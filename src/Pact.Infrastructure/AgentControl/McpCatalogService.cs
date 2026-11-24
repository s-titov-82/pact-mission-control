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

	/// <summary>Tests whether a rule applies to the specified saved profile and exact project scope.</summary>
	public static bool Matches(McpCatalogSelection selection, AgentProfileRecord profile, string? projectDirectory)
	{
		ArgumentNullException.ThrowIfNull(selection);
		ArgumentNullException.ThrowIfNull(profile);
		return selection.Kind == profile.Kind && selection.ProfileId == profile.Id &&
			StringComparer.OrdinalIgnoreCase.Equals(NormalizeProject(selection.ProjectDirectory), NormalizeProject(projectDirectory));
	}

	/// <summary>Appends configured servers while leaving the agent's native loading and permissions in effect.</summary>
	public LaunchInjection ComposeLaunch(AgentProfileRecord profile, string? projectDirectory, LaunchInjection injection)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(injection);
		if (!Supports(profile))
		{
			return injection;
		}
		var settings = Read().Settings;
		var selected = settings.Selections.Where(rule => Matches(rule, profile, null)).ToDictionary(rule => rule.ServerId, rule => rule.Enabled);
		if (projectDirectory is not null)
		{
			foreach (var rule in settings.Selections.Where(rule => Matches(rule, profile, projectDirectory)))
			{
				selected[rule.ServerId] = rule.Enabled;
			}
		}
		var servers = settings.Servers.Where(server => selected.GetValueOrDefault(server.Id)).ToArray();
		if (servers.Length == 0)
		{
			return injection;
		}
		var args = injection.Arguments.ToList();
		if (profile.Kind == AgentKind.Codex)
		{
			foreach (var server in servers)
			{
				args.Add("-c");
				args.Add($"mcp_servers.{server.AgentName}={CodexDefinition(server)}");
			}
		}
		else
		{
			var definitions = new JsonObject();
			foreach (var server in servers)
			{
				definitions.Add(server.AgentName, ClaudeDefinition(server));
			}
			var json = new JsonObject { ["mcpServers"] = definitions }.ToJsonString();
			var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
			args.Add("--mcp-config");
			args.Add(AgentControlLaunchInjection.WriteConfiguration(_launchDirectory, hash + ".json", json, overwrite: false));
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
		var selections = new HashSet<(string, AgentKind, string?, string)>();
		foreach (var rule in settings.Selections)
		{
			if (rule is null || string.IsNullOrWhiteSpace(rule.ProfileId) || !ids.Contains(rule.ServerId) ||
				rule.Kind is not (AgentKind.Codex or AgentKind.Claude) ||
				!selections.Add((rule.ProfileId, rule.Kind, NormalizeProject(rule.ProjectDirectory)?.ToUpperInvariant(), rule.ServerId)))
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
