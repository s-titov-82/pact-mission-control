using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Tomlyn;
using Tomlyn.Model;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	/// <summary>Lists file-backed declarations for one profile and scope, without returning credentials or launching servers.</summary>
	public IReadOnlyList<NativeMcpServer> ReadNative(AgentProfileRecord profile, string? projectDirectory, Action<string>? reportDiagnostic = null)
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
			Read(Path.Combine(customHome ?? Path.Combine(userHome, ".codex"), "config.toml"), AddCodex);
			if (project is not null)
			{
				Read(Path.Combine(project, ".codex", "config.toml"), AddCodex);
			}
		}
		else
		{
			Read(Path.Combine(customHome ?? userHome, ".claude.json"), (source, content) =>
			{
				var root = JsonNode.Parse(content) as JsonObject;
				AddClaude(source, root?["mcpServers"]);
				if (project is not null && root?["projects"] is JsonObject projects)
				{
					var local = projects.FirstOrDefault(pair => Path.IsPathFullyQualified(pair.Key) &&
						StringComparer.OrdinalIgnoreCase.Equals(NormalizeProject(pair.Key), project)).Value as JsonObject;
					AddClaude(source, local?["mcpServers"]);
				}
			});
			if (project is not null)
			{
				Read(Path.Combine(project, ".mcp.json"), (source, content) => AddClaude(source, (JsonNode.Parse(content) as JsonObject)?["mcpServers"]));
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

		void AddCodex(string source, string content)
		{
			if (Toml.ToModel(content).TryGetValue("mcp_servers", out var table) && table is TomlTable servers)
			{
				foreach (var (name, definition) in servers)
				{
					if (definition is TomlTable server)
					{
						result.Add(new(name, server.ContainsKey("url") ? "HTTP" : server.ContainsKey("command") ? "stdio" : "Unknown", source));
					}
				}
			}
		}

		void AddClaude(string source, JsonNode? node)
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
						result.Add(new(name, transport, source));
					}
				}
			}
		}
	}
}
