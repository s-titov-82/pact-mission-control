using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;
using Tomlyn.Syntax;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Stores the reviewed catalog and adds selected connections to ordinary agent launches.</summary>
public sealed partial class McpCatalogService(AppPaths paths)
{
	private static readonly SemaphoreSlim SaveGate = new(1);
	private readonly string _launchDirectory = Path.Combine(paths.SessionTempDirectory, "McpCatalog");
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
	{
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter<AgentKind>() }
	};

	/// <summary>The only file written when applying a catalog edit.</summary>
	public string SettingsPath { get; } = Path.Combine(paths.SettingsDirectory, "mcp-catalog.json");

	/// <summary>Reads the current file without provisioning any connections.</summary>
	public McpCatalogSnapshot Read()
	{
		var content = ReadFile(SettingsPath);
		var settings = content is null ? new McpCatalogSettings() : JsonSerializer.Deserialize<McpCatalogSettings>(content, Json)
			?? throw new InvalidOperationException("Expected an MCP catalog object.");
		Validate(settings);
		return new(settings, content);
	}

	/// <summary>Applies the reviewed settings atomically. Returns false when the preview is stale.</summary>
	public async Task<bool> SaveAsync(McpCatalogSnapshot expected, McpCatalogSettings settings, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(expected);
		ArgumentNullException.ThrowIfNull(settings);
		Validate(settings);
		await SaveGate.WaitAsync(cancellationToken);
		try
		{
			if (!string.Equals(ReadFile(SettingsPath), expected.Content, StringComparison.Ordinal))
			{
				return false;
			}
			await AtomicFileWriter.WriteTextAsync(SettingsPath, JsonSerializer.Serialize(settings, Json), cancellationToken);
			return true;
		}
		finally
		{
			SaveGate.Release();
		}
	}

	/// <summary>Returns whether the catalog can pass arguments directly to this agent's CLI.</summary>
	public static bool Supports(AgentProfileRecord profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		return SkillActivationService.MatchesNativeExecutable(profile);
	}

	/// <summary>Tests whether a rule belongs to the saved profile and exact scope being edited.</summary>
	public static bool Matches(McpCatalogSelection selection, AgentProfileRecord profile, string? projectDirectory, bool isFolder = false)
	{
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(profile);
		return selection.Kind == profile.Kind && selection.ProfileId == profile.Id && selection.IsFolder == isFolder &&
			StringComparer.OrdinalIgnoreCase.Equals(NormalizeProject(selection.ProjectDirectory), NormalizeProject(projectDirectory));
	}

	/// <summary>Returns the choice for one server, preferring the exact project, nearest working-directory ancestor, then default.</summary>
	public static McpCatalogSelection? EffectiveSelection(IEnumerable<McpCatalogSelection> selections, AgentProfileRecord profile,
		string? projectDirectory, string? workingDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var project = NormalizeProject(projectDirectory);
		var directory = NormalizeProject(workingDirectory) ?? project;
		McpCatalogSelection? effective = null;
		var priority = -1;
		foreach (var selection in selections.Where(selection => selection.Kind == profile.Kind && selection.ProfileId == profile.Id))
		{
			var scope = NormalizeProject(selection.ProjectDirectory);
			var candidate = scope is null ? 0 : selection.IsFolder
				? (directory is not null && IsUnderFolder(directory, scope) ? scope.Length : -1)
				: (StringComparer.OrdinalIgnoreCase.Equals(scope, project) ? int.MaxValue : -1);
			if (candidate > priority)
			{
				effective = selection;
				priority = candidate;
			}
		}
		return effective;
	}

	private static bool IsUnderFolder(string directory, string folder) =>
		StringComparer.OrdinalIgnoreCase.Equals(directory, folder) || directory.StartsWith(
			Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

	/// <summary>Appends configured servers while leaving the agent's native loading and permissions in effect.</summary>
	public LaunchInjection ComposeLaunch(AgentProfileRecord profile, string? projectDirectory, LaunchInjection injection, string? workingDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(injection);
		if (!Supports(profile))
		{
			return injection;
		}
		var settings = Read().Settings;
		var rules = settings.Selections.Where(rule => rule.ProfileId == profile.Id && rule.Kind == profile.Kind)
			.ToLookup(rule => (rule.IsNative, rule.ServerId));
		var native = SupportsNativeManagement(profile) && (rules.Any(group => group.Key.IsNative) ||
			settings.Servers.Any(server => server.NativeOrigin?.Kind == profile.Kind)) ? ReadNative(profile, projectDirectory) : [];
		if (profile.Kind == AgentKind.Codex && native.Any(declaration => declaration.ProjectDirectory is not null))
		{
			var projectInScope = IsCodexWorkingDirectoryInProject(projectDirectory, workingDirectory);
			native = native.Where(declaration => declaration.ProjectDirectory is null || declaration.IsProjectConfigurationTrusted && projectInScope).ToArray();
		}
		Dictionary<string, bool> nativeChoices = new(StringComparer.Ordinal);
		foreach (var name in native.Select(server => server.Name).Distinct())
		{
			if (EffectiveSelection(rules[(true, name)], profile, projectDirectory, workingDirectory) is { } choice)
			{
				nativeChoices[name] = choice.Enabled;
			}
		}
		List<McpCatalogServer> servers = [];
		foreach (var server in settings.Servers)
		{
			var choice = EffectiveSelection(rules[(false, server.Id)], profile, projectDirectory, workingDirectory);
			if (UsesNativeOrigin(server, profile, native))
			{
				var name = server.NativeOrigin!.Name;
				choice = EffectiveSelection(rules[(false, server.Id)].Concat(rules[(true, name)]), profile, projectDirectory, workingDirectory);
				if (choice is not null)
				{
					nativeChoices[name] = choice.Enabled;
				}
			}
			else if (choice?.Enabled == true)
			{
				servers.Add(server);
			}
		}
		if (servers.Count == 0 && (nativeChoices.Count == 0 ||
			profile.Kind == AgentKind.Claude && nativeChoices.Values.All(enabled => enabled)))
		{
			return injection;
		}
		var args = injection.Arguments.ToList();
		if (profile.Kind == AgentKind.Codex)
		{
			foreach (var (name, enabled) in nativeChoices)
			{
				args.Add("-c");
				args.Add($"mcp_servers.{name}.enabled={(enabled ? "true" : "false")}");
			}
			foreach (var server in servers)
			{
				args.Add("-c");
				args.Add($"mcp_servers.{server.AgentName}={CodexDefinition(server)}");
			}
		}
		else
		{
			var denied = nativeChoices.Where(choice => !choice.Value).Select(choice => choice.Key).ToArray();
			if (denied.Length > 0)
			{
				AddClaudeNativeDenials(args, denied);
			}
			if (servers.Count > 0)
			{
				var definitions = new JsonObject(servers.Select(server => KeyValuePair.Create<string, JsonNode?>(server.AgentName, ClaudeDefinition(server))));
				var json = new JsonObject { ["mcpServers"] = definitions }.ToJsonString();
				var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
				args.Add("--mcp-config");
				args.Add(AgentControlLaunchInjection.WriteConfiguration(_launchDirectory, hash + ".json", json, overwrite: false));
			}
		}
		return injection with { Arguments = args };
	}

	/// <summary>Validates a catalog definition before showing it in an Apply preview.</summary>
	public static void ValidateServer(McpCatalogServer server)
	{
		ArgumentNullException.ThrowIfNull(server);
		if (server.Id is null || !IdPattern().IsMatch(server.Id))
		{
			throw new ArgumentException("Use 1–40 lowercase letters, digits, hyphens, or underscores for the server ID. Start with a letter.");
		}
		if (server.NativeOrigin is { } origin && (origin.Kind is not (AgentKind.Codex or AgentKind.Claude) ||
			!IsConfigurableNativeName(origin.Name) || !Path.IsPathFullyQualified(origin.SourcePath)))
		{
			throw new ArgumentException("The shared MCP server has an invalid native source.");
		}
		if (server.Arguments is null || server.Arguments.Any(arg => arg is null || arg.Contains('\0', StringComparison.Ordinal)) ||
			server.EnvironmentVariables is null || server.EnvironmentVariables.Any(name => name is null || !VariablePattern().IsMatch(name)))
		{
			throw new ArgumentException("Check the stdio arguments and environment variable names.");
		}
		if (server.Url is not null)
		{
			if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
				uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
			{
				throw new ArgumentException("Enter an HTTP or HTTPS endpoint without user information or a fragment.");
			}
			if (server.Command is not null || server.Arguments.Length != 0 || server.EnvironmentVariables.Length != 0)
			{
				throw new ArgumentException("Choose either HTTP or stdio for this server.");
			}
			if (server.BearerTokenVariable is { } token && (!VariablePattern().IsMatch(token) || uri.Scheme == "http" && !uri.IsLoopback))
			{
				throw new ArgumentException("A bearer token needs an environment variable name and an HTTPS or loopback endpoint.");
			}
		}
		else if (string.IsNullOrWhiteSpace(server.Command) || server.Command.Any(char.IsControl) || server.BearerTokenVariable is not null)
		{
			throw new ArgumentException("Enter the executable for a stdio server.");
		}
	}

	private static void Validate(McpCatalogSettings settings)
	{
		if (settings.Servers is null || settings.Selections is null)
		{
			throw new ArgumentException("The MCP catalog needs server and selection lists.");
		}
		var ids = new HashSet<string>(StringComparer.Ordinal);
		foreach (var server in settings.Servers)
		{
			ArgumentNullException.ThrowIfNull(server);
			ValidateServer(server);
			if (!ids.Add(server.Id))
			{
				throw new ArgumentException("Each MCP server needs a unique ID.");
			}
		}
		var selections = new HashSet<(string, AgentKind, string?, bool, bool, string)>();
		foreach (var rule in settings.Selections)
		{
			if (rule is null || string.IsNullOrWhiteSpace(rule.ProfileId) ||
				(rule.IsNative ? !IsConfigurableNativeName(rule.ServerId) : !ids.Contains(rule.ServerId)) ||
				rule.Kind is not (AgentKind.Codex or AgentKind.Claude) || (rule.IsFolder && rule.ProjectDirectory is null) ||
				!selections.Add((rule.ProfileId, rule.Kind, NormalizeProject(rule.ProjectDirectory)?.ToUpperInvariant(), rule.IsFolder, rule.IsNative, rule.ServerId)))
			{
				throw new ArgumentException("Every MCP selection must uniquely identify a server, launch profile, and scope.");
			}
		}
	}

	private static string CodexDefinition(McpCatalogServer server)
	{
		if (server.Url is { } url)
		{
			var token = server.BearerTokenVariable is { } variable ? $", bearer_token_env_var = {new StringValueSyntax(variable)}" : "";
			return $"{{ url = {new StringValueSyntax(url)}{token} }}";
		}
		var args = string.Join(", ", server.Arguments.Select(arg => new StringValueSyntax(arg).ToString()));
		var env = server.EnvironmentVariables.Length == 0 ? "" :
			$", env_vars = [{string.Join(", ", server.EnvironmentVariables.Select(name => new StringValueSyntax(name).ToString()))}]";
		return $"{{ command = {new StringValueSyntax(server.Command!)}, args = [{args}]{env} }}";
	}

	private static JsonObject ClaudeDefinition(McpCatalogServer server)
	{
		if (server.Url is { } url)
		{
			var remote = new JsonObject { ["type"] = "http", ["url"] = url };
			if (server.BearerTokenVariable is { } variable)
			{
				remote["headers"] = new JsonObject { ["Authorization"] = "Bearer " + EnvironmentReference(variable) };
			}
			return remote;
		}
		var local = new JsonObject { ["type"] = "stdio", ["command"] = server.Command, ["args"] = JsonSerializer.SerializeToNode(server.Arguments) };
		if (server.EnvironmentVariables.Length > 0)
		{
			var env = new JsonObject();
			foreach (var name in server.EnvironmentVariables)
			{
				env[name] = EnvironmentReference(name);
			}
			local["env"] = env;
		}
		return local;
	}

	private static string EnvironmentReference(string name) => "${" + name + "}";

	private static string? NormalizeProject(string? path) => path is null ? null : Path.IsPathFullyQualified(path)
		? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
		: throw new ArgumentException("The MCP project scope requires an absolute path.");

	private static string? ReadFile(string path)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
		catch (FileNotFoundException) { return null; }
		catch (DirectoryNotFoundException) { return null; }
	}

	[GeneratedRegex("^[a-z][a-z0-9_-]{0,39}\\z", RegexOptions.CultureInvariant)]
	private static partial Regex IdPattern();

	[GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*\\z", RegexOptions.CultureInvariant)]
	private static partial Regex VariablePattern();
}
