using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pact.Core.Agents;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	/// <summary>Returns whether the profile uses native inputs the catalog can identify and override for a single launch.</summary>
	public static bool SupportsNativeManagement(AgentProfileRecord profile) =>
		Supports(profile) && !NativeOverridePattern().IsMatch(profile.CommandTemplate);

	/// <summary>Identifies ordinary file-backed server names that can be targeted without affecting Pact's own connection.</summary>
	public static bool IsConfigurableNativeName(string? name) => name is not null && NativeNamePattern().IsMatch(name) &&
		!name.Equals("pact", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("pact_catalog_", StringComparison.OrdinalIgnoreCase);

	/// <summary>Checks whether a profile already loads the declaration behind a shared server in the current scope.</summary>
	public static bool UsesNativeOrigin(McpCatalogServer server, AgentProfileRecord profile, IEnumerable<NativeMcpServer> native)
	{
		ArgumentNullException.ThrowIfNull(server);
		ArgumentNullException.ThrowIfNull(profile);
		if (server.NativeOrigin is not { } origin || origin.Kind != profile.Kind)
		{
			return false;
		}
		var declaration = native.Where(candidate => candidate.Name == origin.Name)
			.OrderByDescending(candidate => candidate.ProjectDirectory is not null)
			.ThenByDescending(candidate => Path.GetFileName(candidate.SourcePath) == ".claude.json").FirstOrDefault();
		return declaration is not null && StringComparer.OrdinalIgnoreCase.Equals(declaration.SourcePath, origin.SourcePath) &&
			StringComparer.OrdinalIgnoreCase.Equals(declaration.ProjectDirectory, origin.ProjectDirectory);
	}

	private static void AddClaudeNativeDenials(List<string> args, string[] names)
	{
		ClaudeSettingsArgument.Edit(args, original =>
		{
			var settings = (JsonObject)JsonNode.Parse(original)!;
			if (settings["deniedMcpServers"] is not (null or JsonArray))
			{
				throw new ClaudeSettingsArgumentException("The Claude --settings argument has a deniedMcpServers value that is not an array.");
			}
			foreach (var name in names)
			{
				ChangeClaudeNativeDenial(settings, name, disabled: true);
			}
			return settings.ToJsonString();
		});
	}

	private static bool ChangeClaudeNativeDenial(JsonObject settings, string name, bool disabled)
	{
		if (settings["deniedMcpServers"] is null)
		{
			if (!disabled)
			{
				return false;
			}
			settings["deniedMcpServers"] = new JsonArray();
		}
		if (settings["deniedMcpServers"] is not JsonArray denied)
		{
			throw new InvalidOperationException("The existing Claude MCP deny list is not an array.");
		}
		var matching = denied.OfType<JsonObject>().Where(rule => ClaudeDenialName(rule) == name).ToArray();
		if (disabled && matching.Length == 0)
		{
			denied.Add(new JsonObject { ["serverName"] = name });
			return true;
		}
		if (!disabled)
		{
			foreach (var entry in matching)
			{
				denied.Remove(entry);
			}
			return matching.Length > 0;
		}
		return false;
	}

	private static string? ClaudeDenialName(JsonObject rule) => rule["serverName"] is JsonValue value &&
		value.TryGetValue<string>(out var name) ? name : null;

	[GeneratedRegex(@"(?:^|\s)(?:--(?:strict-mcp-config|mcp-config|settings|setting-sources|bare|config|profile)|-c|-p)(?:=|\s|$)")]
	private static partial Regex NativeOverridePattern();

	[GeneratedRegex(@"\A[a-zA-Z0-9_-]+\z")]
	private static partial Regex NativeNamePattern();
}
