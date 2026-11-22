using System.Collections;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Syntax;

namespace Pact.Infrastructure.Skills;

internal sealed record CodexSkillRule(string Selector, string Value, bool Enabled)
{
	internal bool MatchesPath(string file) => Selector == "path" && Path.IsPathFullyQualified(Value)
		&& (string.Equals(Value, file, StringComparison.OrdinalIgnoreCase)
			|| string.Equals(SkillCatalogService.ResolveManifest(Value), SkillCatalogService.ResolveManifest(file), StringComparison.OrdinalIgnoreCase));

	internal bool Matches(string name, string file) => MatchesPath(file) || Selector == "name" && Value == name;

	internal string Inline => $"{{ {Selector} = {new StringValueSyntax(Value)}, enabled = {Enabled.ToString().ToLowerInvariant()} }}";
}

internal static class CodexSkillRules
{
	internal static List<CodexSkillRule> Read(string? content)
	{
		var model = Toml.ToModel(content ?? string.Empty);
		if (!model.TryGetValue("skills", out var skills))
		{
			return [];
		}

		if (skills is not TomlTable table)
		{
			throw Unsupported();
		}

		if (!table.TryGetValue("config", out var config))
		{
			return [];
		}

		if (config is not IEnumerable entries || config is string)
		{
			throw Unsupported();
		}

		List<CodexSkillRule> rules = [];
		foreach (var entry in entries)
		{
			if (entry is not TomlTable rule || rule.Count != 2
				|| !rule.TryGetValue("enabled", out var enabled) || enabled is not bool flag)
			{
				throw Unsupported();
			}

			var selector = rule.ContainsKey("path") ? "path" : "name";
			if (!rule.TryGetValue(selector, out var value) || value is not string text)
			{
				throw Unsupported();
			}

			rules.Add(new(selector, text, flag));
		}
		return rules;
	}

	internal static bool IsEnabled(IEnumerable<CodexSkillRule> rules, string name, string file) =>
		rules.LastOrDefault(rule => rule.Matches(name, file))?.Enabled ?? true;

	internal static string? Edit(string? original, string name, string file, SkillMode mode)
	{
		var content = original ?? string.Empty;
		var rules = Read(content);
		if (mode == SkillMode.Inherit && !rules.Any(rule => rule.MatchesPath(file)))
		{
			return original;
		}
		var document = Toml.Parse(content);
		var (tables, inline, inlineItems) = SyntaxRows(document, rules.Count);

		if (mode == SkillMode.Inherit)
		{
			for (var i = rules.Count - 1; i >= 0; i--)
			{
				if (!rules[i].MatchesPath(file))
				{
					continue;
				}
				if (inline != null)
				{
					inline.Items.RemoveChildAt(i);
				}
				else
				{
					document.Tables.RemoveChild(tables[i]);
				}
			}
			var remaining = document.ToString();
			_ = Read(remaining);
			return remaining;
		}

		// A later name rule also matches this copy. Append a path exception after it so
		// other copies keep their original visibility.
		var index = rules.FindLastIndex(rule => rule.Matches(name, file));
		var enabled = mode == SkillMode.Enabled;
		if (index >= 0 && rules[index].MatchesPath(file))
		{
			var field = FindField(tables, inlineItems, index, "enabled");
			if (field?.Value is not BooleanValueSyntax { Token: { } token } previous)
			{
				throw Unsupported();
			}

			var replacement = new BooleanValueSyntax(enabled);
			replacement.Token!.LeadingTrivia = token.LeadingTrivia;
			replacement.Token.TrailingTrivia = token.TrailingTrivia;
			replacement.LeadingTrivia = previous.LeadingTrivia;
			replacement.TrailingTrivia = previous.TrailingTrivia;
			field.Value = replacement;
			return document.ToString();
		}

		if (inline != null)
		{
			var last = inline.Items.LastOrDefault();
			if (last is { Comma: null })
			{
				last.Comma = SyntaxFactory.Token(TokenKind.Comma);
			}
			var path = new KeyValueSyntax("path", new StringValueSyntax(file)) { EndOfLineToken = null };
			var state = new KeyValueSyntax("enabled", new BooleanValueSyntax(enabled)) { EndOfLineToken = null };
			inline.Items.Add(new ArrayItemSyntax { Value = new InlineTableSyntax(path, state) });
			var result = document.ToString();
			_ = Read(result);
			return result;
		}

		var updated = content + Environment.NewLine + "[[skills.config]]" + Environment.NewLine
			+ $"path = {new StringValueSyntax(file)}" + Environment.NewLine
			+ $"enabled = {enabled.ToString().ToLowerInvariant()}" + Environment.NewLine;
		// Inline tables and dotted keys can seal their containing table against additions.
		_ = Read(updated);
		return updated;
	}

	internal static string RenamePaths(string content, Func<string, string?> renamed)
	{
		var rules = Read(content);
		var edits = rules.Select((rule, index) => (Index: index, Value: rule.Selector == "path" ? renamed(rule.Value) : null))
			.Where(edit => edit.Value != null).ToArray();
		if (edits.Length == 0)
		{
			return content;
		}
		var document = Toml.Parse(content);
		var (tables, _, inlineItems) = SyntaxRows(document, rules.Count);
		foreach (var edit in edits)
		{
			var field = FindField(tables, inlineItems, edit.Index, "path");
			if (field?.Value is not StringValueSyntax { Token: { } token } old)
			{
				throw Unsupported();
			}
			var updated = new StringValueSyntax(edit.Value!) { LeadingTrivia = old.LeadingTrivia, TrailingTrivia = old.TrailingTrivia };
			updated.Token!.LeadingTrivia = token.LeadingTrivia;
			updated.Token.TrailingTrivia = token.TrailingTrivia;
			field.Value = updated;
		}
		var result = document.ToString();
		_ = Read(result);
		return result;
	}

	private static (TableArraySyntax[] Tables, ArraySyntax? Inline, ArrayItemSyntax[]? Items) SyntaxRows(DocumentSyntax document, int count)
	{
		var tables = document.Tables.OfType<TableArraySyntax>().Where(table => table.Name?.ToString().Trim() == "skills.config").ToArray();
		var inline = document.KeyValues.FirstOrDefault(field => field.Key?.ToString().Trim() == "skills.config")?.Value as ArraySyntax
			?? document.Tables.OfType<TableSyntax>().FirstOrDefault(table => table.Name?.ToString().Trim() == "skills")?
				.Items.FirstOrDefault(field => field.Key?.ToString().Trim() == "config")?.Value as ArraySyntax;
		var items = inline?.Items.ToArray();
		if ((items?.Length ?? tables.Length) != count)
		{
			throw new InvalidOperationException("Pact cannot edit this TOML representation of skills.config.");
		}
		return (tables, inline, items);
	}

	private static KeyValueSyntax? FindField(TableArraySyntax[] tables, ArrayItemSyntax[]? items, int index, string key) =>
		items is null ? tables[index].Items.FirstOrDefault(item => item.Key?.ToString().Trim() == key)
			: (items[index].Value as InlineTableSyntax)?.Items.Select(item => item.KeyValue).FirstOrDefault(item => item?.Key?.ToString().Trim() == key);

	private static InvalidOperationException Unsupported() =>
		new("This skills.config contains rules that Pact cannot preserve in a Codex launch override.");
}