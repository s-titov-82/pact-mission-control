using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Tomlyn;
using Tomlyn.Model;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	private sealed record ClaudeDenyRule(string Path, string? Name, string? Url, string[]? Command);

	/// <summary>Lists file-backed declarations for one profile and scope, without returning credentials or launching servers.</summary>
	public static IReadOnlyList<NativeMcpServer> ReadNative(AgentProfileRecord profile, string? projectDirectory, Action<string>? reportDiagnostic = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		if (!Supports(profile))
		{
			return [];
		}
		var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var customHome = SkillActivationService.NativeDirectoryOverride(profile, userHome);
		if (string.IsNullOrWhiteSpace(customHome))
		{
			customHome = null;
		}
		else if (!Path.IsPathFullyQualified(customHome))
		{
			reportDiagnostic?.Invoke("The profile's native configuration directory must be absolute.");
			return [];
		}
		var project = NormalizeProject(projectDirectory);
		List<NativeMcpServer> result = [];
		if (profile.Kind == AgentKind.Codex)
		{
			var trusted = false;
			Read(Path.Combine(customHome ?? Path.Combine(userHome, ".codex"), "config.toml"), (source, content) =>
			{
				var model = Toml.ToModel(content);
				trusted = project is not null && IsCodexProjectTrusted(model, project);
				AddCodex(source, model, null, true);
			});
			if (project is not null)
			{
				Read(Path.Combine(project, ".codex", "config.toml"), (source, content) => AddCodex(source, Toml.ToModel(content), project, trusted));
			}
		}
		else
		{
			var settingsPaths = project is null ? new[] { ClaudeNativeSettingsPath(profile, null) } :
				[ClaudeNativeSettingsPath(profile, null), Path.Combine(project, ".claude", "settings.json"), ClaudeNativeSettingsPath(profile, project)];
			var parsed = settingsPaths.Select(path => ReadClaudeDenials(path, reportDiagnostic)).ToArray();
			var denies = parsed.OfType<ClaudeDenyRule[]>().SelectMany(rules => rules).ToArray();
			var allReadable = parsed.All(rules => rules is not null);
			Read(Path.Combine(customHome ?? userHome, ".claude.json"), (source, content) =>
			{
				var root = JsonNode.Parse(content) as JsonObject;
				AddClaude(source, root?["mcpServers"], null, denies, allReadable);
				if (project is not null)
				{
					AddClaude(source, ClaudeProject(root, project)?["mcpServers"], project, denies, allReadable);
				}
			});
			if (project is not null)
			{
				Read(Path.Combine(project, ".mcp.json"), (source, content) => AddClaude(source, (JsonNode.Parse(content) as JsonObject)?["mcpServers"], project, denies, allReadable));
			}
		}
		return result.Distinct().OrderBy(server => server.Name, StringComparer.OrdinalIgnoreCase).ToArray();

		void Read(string path, Action<string, string> parse)
		{
			try
			{
				if (ReadFile(path) is { } content)
				{
					parse(path, content);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or TomlException or ArgumentException)
			{
				reportDiagnostic?.Invoke($"Could not read MCP declarations in {path}.");
			}
		}

		void AddCodex(string source, TomlTable model, string? scope, bool trusted)
		{
			if (model.TryGetValue("mcp_servers", out var table) && table is TomlTable servers)
			{
				foreach (var (name, definition) in servers)
				{
					if (definition is TomlTable server)
					{
						var enabled = server.TryGetValue("enabled", out var value) ? value as bool? : true;
						result.Add(new(name, server.ContainsKey("url") ? "HTTP" : server.ContainsKey("command") ? "stdio" : "Unknown", source)
						{
							ProjectDirectory = scope,
							IsEnabled = enabled,
							IsProjectConfigurationTrusted = trusted
						});
					}
				}
			}
		}

		void AddClaude(string source, JsonNode? node, string? scope, ClaudeDenyRule[] denials, bool allReadable)
		{
			if (node is JsonObject servers)
			{
				foreach (var (name, definition) in servers)
				{
					if (definition is JsonObject server)
					{
						var type = server["type"] is JsonValue value && value.TryGetValue<string>(out var label) ? label : null;
						var transport = type switch
						{
							"http" => "HTTP",
							"sse" => "SSE",
							"stdio" => "stdio",
							null when server.ContainsKey("command") => "stdio",
							_ => "Other"
						};
						var matches = denials.Where(denial => MatchesClaudeDenial(denial, name, server)).ToArray();
						var switchPath = ClaudeNativeSettingsPath(profile, scope);
						result.Add(new(name, transport, source)
						{
							ProjectDirectory = scope,
							IsEnabled = matches.Length > 0 ? false : allReadable ? true : null,
							BlockingSettings = matches.Length == 0 ? null : string.Join(", ", matches.Select(rule => rule.Path).Distinct()),
							CanEnable = allReadable && matches.All(rule => rule.Name == name && StringComparer.OrdinalIgnoreCase.Equals(rule.Path, switchPath))
						});
					}
				}
			}
		}
	}

	private static bool IsCodexProjectTrusted(TomlTable model, string projectDirectory) => model.TryGetValue("projects", out var value) &&
		value is TomlTable projects && projects.Any(entry => Path.IsPathFullyQualified(entry.Key) &&
			StringComparer.OrdinalIgnoreCase.Equals(NormalizeProject(entry.Key), projectDirectory) &&
			entry.Value is TomlTable settings && settings.TryGetValue("trust_level", out var trust) && trust is "trusted");

	private static bool IsCodexWorkingDirectoryInProject(string? projectDirectory, string? workingDirectory)
	{
		var project = NormalizeProject(projectDirectory);
		var directory = NormalizeProject(workingDirectory) ?? project;
		if (project is null || directory is null || !IsUnderFolder(directory, project))
		{
			return false;
		}
		for (var parent = directory; !StringComparer.OrdinalIgnoreCase.Equals(parent, project); parent = Path.GetDirectoryName(parent)!)
		{
			var marker = Path.Combine(parent, ".git");
			if (Directory.Exists(marker) || File.Exists(marker))
			{
				return false;
			}
		}
		return true;
	}

	private static ClaudeDenyRule[]? ReadClaudeDenials(string path, Action<string>? reportDiagnostic)
	{
		try
		{
			return ParseNativeJson(ReadFile(path))["deniedMcpServers"] switch
			{
				null => [],
				JsonArray denied => denied.OfType<JsonObject>().Select(rule => new ClaudeDenyRule(path, ClaudeDenialName(rule),
					NativeString(rule["serverUrl"]), ReadNativeStringArray(rule["serverCommand"]))).ToArray(),
				_ => throw new InvalidOperationException("The existing Claude MCP deny list is not an array.")
			};
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			reportDiagnostic?.Invoke($"Could not read the native MCP switch in {path}.");
			return null;
		}
	}

	private static bool MatchesClaudeDenial(ClaudeDenyRule denial, string name, JsonObject server)
	{
		if (denial.Name == name)
		{
			return true;
		}
		if (denial.Url is { } pattern && NativeString(server["url"]) is { } url)
		{
			return MatchesClaudeUrl(pattern, url);
		}
		return denial.Command is { Length: > 0 } blocked && NativeString(server["command"]) == blocked[0] &&
			(blocked.Length == 1 && server["args"] is null || ReadNativeStringArray(server["args"]) is { } args && blocked.Skip(1).SequenceEqual(args));
	}

	private static bool MatchesClaudeUrl(string pattern, string url)
	{
		var parts = pattern.Split('*');
		if (parts.Length == 1)
		{
			return pattern == url;
		}
		var prefix = parts[0];
		var suffix = parts[^1];
		if (prefix.Length + suffix.Length > url.Length || !url.StartsWith(prefix, StringComparison.Ordinal) ||
			!url.EndsWith(suffix, StringComparison.Ordinal))
		{
			return false;
		}
		var remaining = url.AsSpan(prefix.Length, url.Length - prefix.Length - suffix.Length);
		foreach (var part in parts.AsSpan(1, parts.Length - 2))
		{
			var index = remaining.IndexOf(part.AsSpan(), StringComparison.Ordinal);
			if (index < 0)
			{
				return false;
			}
			remaining = remaining[(index + part.Length)..];
		}
		return true;
	}

	private static string? NativeString(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

	private static string[]? ReadNativeStringArray(JsonNode? node) => node is JsonArray list && list.All(item => NativeString(item) is not null)
		? list.Select(item => NativeString(item)!).ToArray() : null;

	private static JsonObject? ClaudeProject(JsonObject? root, string project) => (root?["projects"] as JsonObject)?
		.FirstOrDefault(pair => Path.IsPathFullyQualified(pair.Key) &&
			StringComparer.OrdinalIgnoreCase.Equals(NormalizeProject(pair.Key), project)).Value as JsonObject;
}
