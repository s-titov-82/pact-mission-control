using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Pact.Infrastructure.AgentControl;

public sealed partial class McpCatalogService
{
	/// <summary>Locates the user-scoped file that the selected profile uses for native MCP declarations.</summary>
	public static string NativeUserConfigurationPath(AgentProfileRecord profile)
	{
		if (!Supports(profile))
		{
			throw new ArgumentException("This profile does not use a supported agent command.", nameof(profile));
		}
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var directory = SkillActivationService.NativeDirectoryOverride(profile, home);
		if (string.IsNullOrWhiteSpace(directory))
		{
			directory = profile.Kind == AgentKind.Codex ? Path.Combine(home, ".codex") : home;
		}
		if (!Path.IsPathFullyQualified(directory))
		{
			throw new ArgumentException("The profile's native configuration directory must be absolute.", nameof(profile));
		}
		return Path.GetFullPath(Path.Combine(directory, profile.Kind == AgentKind.Codex ? "config.toml" : ".claude.json"));
	}

	/// <summary>Previews enabling, disabling or removing only the selected native declaration.</summary>
	public static McpNativeEditPreview PreviewNativeEdit(AgentProfileRecord profile, NativeMcpServer source, McpNativeEditAction action)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var original = ReadNativeSource(profile, source);
		if (profile.Kind == AgentKind.Codex)
		{
			var updated = action == McpNativeEditAction.Remove ? EditCodexDeclaration(original, source.Name, (table, name) =>
			{
				if (!table.Remove(name))
				{
					throw MissingNativeDeclaration();
				}
				return true;
			}) : EditCodexSwitch(original, source.Name, action == McpNativeEditAction.Enable);
			return NativePreview(source, original, SkillCatalogService.ResolveManifest(source.SourcePath), original, updated);
		}
		if (action == McpNativeEditAction.Remove)
		{
			var root = ParseNativeJson(original);
			var servers = ClaudeDeclarations(root, source);
			if (servers?.Remove(source.Name) != true)
			{
				throw MissingNativeDeclaration();
			}
			return NativePreview(source, original, SkillCatalogService.ResolveManifest(source.SourcePath), original, FormatNativeJson(root, original));
		}
		var settingsPath = SkillCatalogService.ResolveManifest(ClaudeNativeSettingsPath(profile, source.ProjectDirectory));
		var previous = ReadFile(settingsPath);
		var settings = ParseNativeJson(previous);
		var changed = ChangeClaudeNativeDenial(settings, source.Name, action == McpNativeEditAction.Disable);
		return NativePreview(source, original, settingsPath, previous, changed ? FormatNativeJson(settings, previous) : previous);
	}

	/// <summary>Applies a reviewed native edit and rejects changed inputs before writing. A failed batch rolls back its own writes.</summary>
	public static async Task<bool> ApplyNativeEditAsync(McpNativeEditPreview preview, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(preview);
		await SaveGate.WaitAsync(cancellationToken);
		List<McpNativeFileEdit> written = [];
		try
		{
			if (ReadFile(preview.SourcePath) != preview.SourceContent || preview.Edits.Any(edit => ReadFile(edit.Path) != edit.Expected))
			{
				return false;
			}
			foreach (var edit in preview.Edits)
			{
				if (ReadFile(edit.Path) != edit.Expected)
				{
					await UndoNativeEditsAsync(written);
					return false;
				}
				await AtomicFileWriter.WriteTextAsync(edit.Path, edit.Updated, cancellationToken);
				written.Add(edit);
			}
			return true;
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
		{
			await UndoNativeEditsAsync(written);
			throw;
		}
		finally
		{
			SaveGate.Release();
		}
	}

	private static async Task UndoNativeEditsAsync(List<McpNativeFileEdit> written)
	{
		List<string> failed = [];
		foreach (var edit in written.AsEnumerable().Reverse())
		{
			try
			{
				if (ReadFile(edit.Path) != edit.Updated)
				{
					failed.Add(edit.Path);
					continue;
				}
				if (edit.Expected is null)
				{
					File.Delete(edit.Path);
				}
				else
				{
					await AtomicFileWriter.WriteTextAsync(edit.Path, edit.Expected, CancellationToken.None);
				}
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				failed.Add(edit.Path);
			}
		}
		written.Clear();
		if (failed.Count > 0)
		{
			throw new IOException($"Some native files changed or could not be restored. Review {string.Join(", ", failed)}.");
		}
	}

	private static McpNativeEditPreview NativePreview(NativeMcpServer source, string sourceContent, string path, string? before, string? after) =>
		new(source.SourcePath, sourceContent, after is null || after == before ? [] : [new(path, before, after)]);

	private static string ReadNativeSource(AgentProfileRecord profile, NativeMcpServer source)
	{
		ArgumentNullException.ThrowIfNull(source);
		if (!IsConfigurableNativeName(source.Name) || !ReadNative(profile, source.ProjectDirectory)
			.Any(current => current.MatchesDeclaration(source) && current.Transport == source.Transport) ||
			ReadFile(source.SourcePath) is not { } original)
		{
			throw MissingNativeDeclaration();
		}
		return original;
	}

	private static InvalidOperationException MissingNativeDeclaration() => new("The selected native MCP declaration is unavailable. Refresh the catalog and try again.");

	private static string ClaudeNativeSettingsPath(AgentProfileRecord profile, string? project)
	{
		if (project is not null)
		{
			return Path.Combine(project, ".claude", "settings.local.json");
		}
		var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var directory = SkillActivationService.NativeDirectoryOverride(profile, home);
		return Path.Combine(string.IsNullOrWhiteSpace(directory) ? Path.Combine(home, ".claude") : directory, "settings.json");
	}

	private static JsonObject ParseNativeJson(string? content)
	{
		try
		{
			return content is null ? new JsonObject() : JsonNode.Parse(content) as JsonObject ??
				throw new InvalidOperationException("The native MCP configuration is not a JSON object.");
		}
		catch (JsonException exception)
		{
			throw new InvalidOperationException("The native MCP configuration is invalid JSON.", exception);
		}
	}

	private static JsonObject? ClaudeDeclarations(JsonObject root, NativeMcpServer source)
	{
		var owner = source.ProjectDirectory is not null && Path.GetFileName(source.SourcePath) == ".claude.json"
			? ClaudeProject(root, source.ProjectDirectory) : root;
		return owner?["mcpServers"] as JsonObject;
	}

	private static string FormatNativeJson(JsonObject document, string? original)
	{
		var newline = original?.Contains("\r\n", StringComparison.Ordinal) == true ? "\r\n" : "\n";
		var options = new JsonSerializerOptions
		{
			Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
			NewLine = newline,
			WriteIndented = original is null || original.TrimEnd('\r', '\n').Contains('\n', StringComparison.Ordinal)
		};
		var updated = document.ToJsonString(options);
		return original?.EndsWith('\n') == true ? updated + newline : updated;
	}

	private static string EditCodexSwitch(string content, string name, bool enabled)
	{
		var document = Toml.Parse(content);
		var root = ReadCodexModel(document);
		if (!root.TryGetValue("mcp_servers", out var subtree) || subtree is not TomlTable servers ||
			!servers.TryGetValue(name, out var value) || value is not TomlTable definition)
		{
			throw MissingNativeDeclaration();
		}
		var current = !definition.TryGetValue("enabled", out var configured) || configured is true;
		if (current == enabled)
		{
			return content;
		}
		var table = document.Tables.OfType<TableSyntax>().FirstOrDefault(candidate => IsCodexServerTable(candidate, name) &&
			candidate.Name!.DotKeys.Count() == 1);
		if (table is null || HasCodexRootDefinition(document))
		{
			return EditCodexDeclaration(content, name, (entries, key) =>
			{
				((TomlTable)entries[key])["enabled"] = enabled;
				return true;
			});
		}
		var field = table.Items.FirstOrDefault(item => TomlKey(item.Key?.Key) == "enabled" && !item.Key!.DotKeys.Any());
		var replacement = new BooleanValueSyntax(enabled);
		if (field?.Value is BooleanValueSyntax { Token: { } token } previous)
		{
			replacement.Token!.LeadingTrivia = token.LeadingTrivia;
			replacement.Token.TrailingTrivia = token.TrailingTrivia;
			replacement.LeadingTrivia = previous.LeadingTrivia;
			replacement.TrailingTrivia = previous.TrailingTrivia;
			field.Value = replacement;
		}
		else if (field is not null)
		{
			field.Value = replacement;
		}
		else
		{
			var newline = new SyntaxToken(TokenKind.NewLine, content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
			var last = table.Items.LastOrDefault();
			var needsLineBreak = (last is null ? table.EndOfLineToken : last.EndOfLineToken) is null;
			if (needsLineBreak)
			{
				if (last is null)
				{
					table.EndOfLineToken = newline;
				}
				else
				{
					last.EndOfLineToken = newline;
				}
			}
			table.Items.Add(new KeyValueSyntax("enabled", replacement) { EndOfLineToken = needsLineBreak ? null : newline });
		}
		var updated = document.ToString();
		_ = ReadCodexModel(Toml.Parse(updated));
		return updated;
	}

	private static string EditCodexDeclaration(string? content, string name, Func<TomlTable, string, bool> edit, string? replacementText = null)
	{
		var document = Toml.Parse(content ?? "");
		var model = ReadCodexModel(document);
		var servers = (model.TryGetValue("mcp_servers", out var value) ? value as TomlTable : new TomlTable()) ??
			throw new InvalidOperationException("The Codex mcp_servers setting is not a table.");
		if (!edit(servers, name))
		{
			return content ?? "";
		}
		var keys = document.KeyValues.Where(entry => TomlKey(entry.Key?.Key) == "mcp_servers").ToArray();
		var tables = document.Tables.Where(table => TomlKey(table.Name?.Key) == "mcp_servers").ToArray();
		var hasRootDefinition = HasCodexRootDefinition(document);
		foreach (var key in keys)
		{
			document.KeyValues.RemoveChild(key);
		}
		foreach (var table in tables.Where(table => hasRootDefinition || IsCodexServerTable(table, name)))
		{
			document.Tables.RemoveChild(table);
		}
		var remaining = document.ToString();
		if (!hasRootDefinition && replacementText is not null)
		{
			return AppendCodexDeclaration(remaining, replacementText);
		}
		var replacement = hasRootDefinition ? servers : new TomlTable();
		if (!hasRootDefinition && servers.TryGetValue(name, out var declaration))
		{
			replacement[name] = declaration;
		}
		if (replacement.Count == 0)
		{
			return remaining;
		}
		var fragment = Toml.Parse(Toml.FromModel(new TomlTable { ["mcp_servers"] = replacement }));
		foreach (var parent in fragment.Tables.Where(table => !table.Name!.DotKeys.Any() && !table.Items.Any()).ToArray())
		{
			fragment.Tables.RemoveChild(parent);
		}
		return AppendCodexDeclaration(remaining, fragment.ToString());
	}

	private static string AppendCodexDeclaration(string remaining, string fragment)
	{
		var updated = remaining + Environment.NewLine + fragment;
		_ = ReadCodexModel(Toml.Parse(updated));
		return updated;
	}

	private static TomlTable ReadCodexModel(DocumentSyntax document)
	{
		if (document.HasErrors)
		{
			throw new InvalidOperationException("The native MCP configuration is invalid TOML.");
		}
		try
		{
			return document.ToModel();
		}
		catch (TomlException exception)
		{
			throw new InvalidOperationException("The native MCP configuration is invalid TOML.", exception);
		}
	}

	private static bool HasCodexRootDefinition(DocumentSyntax document) =>
		document.KeyValues.Any(entry => TomlKey(entry.Key?.Key) == "mcp_servers") || document.Tables.Any(table =>
			TomlKey(table.Name?.Key) == "mcp_servers" && !table.Name!.DotKeys.Any() && table.Items.Any());

	private static bool IsCodexServerTable(TableSyntaxBase table, string name) => TomlKey(table.Name?.Key) == "mcp_servers" &&
		TomlKey(table.Name!.DotKeys.FirstOrDefault()?.Key) == name;

	private static string? TomlKey(BareKeyOrStringValueSyntax? key) => key switch
	{
		BareKeySyntax bare => bare.Key?.Text,
		StringValueSyntax quoted => quoted.Value,
		_ => null
	};
}
