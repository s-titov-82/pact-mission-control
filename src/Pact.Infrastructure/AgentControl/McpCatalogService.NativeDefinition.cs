using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Tomlyn;
using Tomlyn.Model;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	/// <summary>Opens only the selected declaration in the original agent's native format.</summary>
	public static McpNativeDefinitionDraft OpenNativeDefinition(AgentProfileRecord profile, NativeMcpServer source)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(source);
		var original = ReadNativeSource(profile, source);
		var content = profile.Kind == AgentKind.Codex ? ReadCodexDefinitionText(original, source.Name) :
			FormatNativeJson(ReadNativeDefinition(profile.Kind, source, original), null);
		return new(profile.Kind, source, original, SkillCatalogService.ResolveManifest(source.SourcePath), content);
	}

	/// <summary>Validates and previews replacing the selected declaration against the opened native file revision.</summary>
	public static McpNativeEditPreview PreviewNativeDefinition(McpNativeDefinitionDraft draft, string content)
	{
		ArgumentNullException.ThrowIfNull(draft);
		ArgumentNullException.ThrowIfNull(content);
		if (content == draft.Content)
		{
			return NativePreview(draft.Source, draft.Original, draft.Path, draft.Original, draft.Original);
		}
		if (!StringComparer.OrdinalIgnoreCase.Equals(SkillCatalogService.ResolveManifest(draft.Source.SourcePath), draft.Path) ||
			ReadFile(draft.Path) is not { } current)
		{
			throw new InvalidOperationException("The native configuration moved or disappeared. Copy your edits before resetting the form.");
		}
		if (current != draft.Original && !SelectedDefinitionMatches(draft, current))
		{
			throw new InvalidOperationException("This server changed on disk. Copy your edits before resetting the form.");
		}
		var updated = draft.Kind == AgentKind.Codex ? ReplaceCodexDefinition(current, draft.Source.Name, content) :
			ReplaceClaudeDefinition(current, draft.Source, content);
		return NativePreview(draft.Source, current, draft.Path, current, updated);
	}

	private static bool SelectedDefinitionMatches(McpNativeDefinitionDraft draft, string current) => draft.Kind == AgentKind.Codex
		? ReadCodexDefinitionText(current, draft.Source.Name) == draft.Content
		: JsonNode.DeepEquals(ClaudeDeclarations(ParseNativeJson(current), draft.Source)?[draft.Source.Name], ParseNativeJson(draft.Content));

	private static string ReadCodexDefinitionText(string original, string name)
	{
		var document = Toml.Parse(original);
		var model = ReadCodexModel(document);
		if (!model.TryGetValue("mcp_servers", out var value) || value is not TomlTable servers ||
			!servers.TryGetValue(name, out var item) || item is not TomlTable definition)
		{
			throw MissingNativeDeclaration();
		}
		var tables = document.Tables.Where(table => IsCodexServerTable(table, name)).ToArray();
		return tables.Length > 0 && !HasCodexRootDefinition(document) ? string.Concat(tables).TrimEnd() :
			Toml.FromModel(new TomlTable { ["mcp_servers"] = new TomlTable { [name] = definition } }).TrimEnd();
	}

	private static string ReplaceCodexDefinition(string current, string name, string content)
	{
		var document = Toml.Parse(content);
		var model = ReadCodexModel(document);
		if (model.Count != 1 || !model.TryGetValue("mcp_servers", out var value) || value is not TomlTable { Count: 1 } servers ||
			!servers.TryGetValue(name, out var item) || item is not TomlTable definition)
		{
			throw new InvalidOperationException("The TOML must contain only the selected MCP server. Keep its original name.");
		}
		var plainTables = !document.KeyValues.Any() && document.Tables.All(table => IsCodexServerTable(table, name));
		return EditCodexDeclaration(current, name, (existing, key) =>
		{
			existing[key] = definition;
			return true;
		}, plainTables ? content : null);
	}

	private static string ReplaceClaudeDefinition(string current, NativeMcpServer source, string content)
	{
		var updated = ParseNativeJson(content);
		var root = ParseNativeJson(current);
		var servers = ClaudeDeclarations(root, source) ?? throw MissingNativeDeclaration();
		servers[source.Name] = updated;
		return FormatNativeJson(root, current);
	}
}
