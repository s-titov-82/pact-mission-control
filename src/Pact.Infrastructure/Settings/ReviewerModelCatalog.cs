using System.Text.Json;
using Pact.Core.Agents;

namespace Pact.Infrastructure.Settings;

/// <summary>Reviewer model choices from the local CLI caches, without starting an agent or requesting credentials.</summary>
public sealed class ReviewerModelCatalog
{
	private static readonly JsonElement EmptyArray = JsonSerializer.SerializeToElement(Array.Empty<string>());
	private readonly Dictionary<string, string[]> _codex = new(StringComparer.Ordinal);
	private readonly List<string> _claude = ["opus", "sonnet", "haiku"];

	/// <summary>Lists locally known model identifiers for a reviewer.</summary>
	public IReadOnlyList<string> Models(AgentKind kind) => kind == AgentKind.Codex ? [.. _codex.Keys] : _claude;

	/// <summary>Lists effort levels for the selected model. An unknown Codex model uses conservative CLI choices.</summary>
	public IReadOnlyList<string> Efforts(AgentKind kind, string model) => kind == AgentKind.Claude
		? ["low", "medium", "high", "xhigh", "max"]
		: _codex.TryGetValue(model, out var levels) ? levels : ["low", "medium", "high"];

	/// <summary>Reads only local metadata. Missing, oversized and malformed caches yield the built-in choices.</summary>
	public static async Task<ReviewerModelCatalog> LoadAsync(string codexHome, string claudeHome,
		string claudeStatePath, CancellationToken cancellationToken)
	{
		ReviewerModelCatalog catalog = new();
		using var codex = await ReadAsync(Path.Combine(codexHome, "models_cache.json"), cancellationToken).ConfigureAwait(false);
		foreach (var model in ArrayValues(codex?.RootElement, "models"))
		{
			var slug = StringValue(model, "slug");
			if (IsCommandValue(slug) && StringValue(model, "visibility") == "list")
			{
				catalog._codex[slug!] = ArrayValues(model, "supported_reasoning_levels")
					.Select(level => StringValue(level, "effort")).OfType<string>().Where(IsCommandValue).Distinct().ToArray();
			}
		}

		using var claude = await ReadAsync(claudeStatePath, cancellationToken).ConfigureAwait(false);
		using var settings = await ReadAsync(Path.Combine(claudeHome, "settings.json"), cancellationToken).ConfigureAwait(false);
		var extra = ArrayValues(claude?.RootElement, "additionalModelOptionsCache")
			.Select(option => StringValue(option, "value")).Append(StringValue(settings?.RootElement, "model"));
		foreach (var model in extra.OfType<string>().Where(IsCommandValue))
		{
			if (!catalog._claude.Contains(model))
			{
				catalog._claude.Add(model);
			}
		}

		return catalog;
	}

	/// <summary>Limits values inserted into generated shell commands to CLI identifier characters.</summary>
	public static bool IsCommandValue(string? value) =>
		value is { Length: > 0 and <= 200 } && char.IsAsciiLetterOrDigit(value[0])
		&& value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ':' or '[' or ']');

	private static JsonElement.ArrayEnumerator ArrayValues(JsonElement? parent, string name) =>
		parent is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var array)
		&& array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : EmptyArray.EnumerateArray();

	private static string? StringValue(JsonElement? parent, string name) =>
		parent is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
		&& value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static async Task<JsonDocument?> ReadAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length > 4 * 1024 * 1024)
			{
				return null;
			}

			return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
		{
			return null;
		}
	}
}
