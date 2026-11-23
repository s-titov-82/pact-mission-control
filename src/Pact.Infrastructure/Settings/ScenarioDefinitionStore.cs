using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Scenarios;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>
/// Reads and writes <c>scenarios.json</c>, falling back to the shipped defaults when the file is
/// absent. Legacy settings are read without rewriting the file.
/// </summary>
public sealed class ScenarioDefinitionStore
{
	private const string DefaultsDirectoryName = "Defaults";
	private const string DefaultDefinitionsFileName = "scenarios-default.json";

	private readonly string _path;
	private readonly string? _stagingDirectory;

	/// <summary>
	/// Creates a store over <paramref name="path"/>, staging atomic writes in
	/// <paramref name="stagingDirectory"/> when supplied.
	/// </summary>
	public ScenarioDefinitionStore(string path, string? stagingDirectory = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		_path = path;
		_stagingDirectory = stagingDirectory;
	}

	/// <summary>
	/// Reads the configured scenarios.
	/// </summary>
	/// <returns>
	/// The configured definitions, or the shipped defaults when the file is missing.
	/// </returns>
	public async Task<IReadOnlyList<ScenarioDefinition>> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_path))
		{
			return LoadDefaultDefinitions();
		}

		var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
		var entries = JsonNode.Parse(json)?.AsArray() ?? throw new JsonException("Expected a scenario array.");
		return entries.OfType<JsonObject>()
			.Where(node => string.Equals((string?)node["kind"], "reviewLoop", StringComparison.OrdinalIgnoreCase))
			.Select(ReadReview)
			.ToArray();
	}

	/// <summary>Writes the definitions atomically, replacing the whole file.</summary>
	public async Task SaveAsync(IReadOnlyList<ScenarioDefinition> definitions, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(definitions);

		var json = JsonSerializer.Serialize(definitions, SettingsFileStore.JsonOptions);
		Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
		await AtomicFileWriter.WriteTextAsync(_path, json, _stagingDirectory, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Reads the shipped defaults file verbatim, used to seed a new <c>scenarios.json</c> and to
	/// restore individual missing fields.
	/// </summary>
	public static string ReadDefaultDefinitionsJson() => File.ReadAllText(DefaultDefinitionsPath);

	/// <summary>Parses the shipped defaults into definitions.</summary>
	public static IReadOnlyList<ScenarioDefinition> LoadDefaultDefinitions()
	{
		var definitions = JsonSerializer.Deserialize<ScenarioDefinition[]>(
			ReadDefaultDefinitionsJson(),
			SettingsFileStore.JsonOptions);

		return definitions ?? [];
	}

	private static string DefaultDefinitionsPath =>
		Path.Combine(AppContext.BaseDirectory, DefaultsDirectoryName, DefaultDefinitionsFileName);

	/// <summary>Reads one review for both the runtime and settings editor without rewriting its JSON.</summary>
	public static ScenarioDefinition ReadReview(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		var id = (string?)node["id"] ?? string.Empty;
		var rules = (string?)node["reviewRules"];
		if (rules is null)
		{
			// Older shipped prompts enclosed the editable criteria between a common opening
			// and the findings format. Recover those criteria without replaying file transport.
			var prompt = ((string?)node["startPromptTemplate"] ?? string.Empty).ReplaceLineEndings("\n");
			const string openingEnd = "review the text as-is.\n\n";
			var start = prompt.IndexOf(openingEnd, StringComparison.Ordinal);
			if (start >= 0)
			{
				rules = prompt[(start + openingEnd.Length)..];
				var end = rules.IndexOf("\n\nRespond with a numbered list", StringComparison.Ordinal);
				if (end < 0)
				{
					end = rules.IndexOf("\n\nWrite your review as a numbered list", StringComparison.Ordinal);
				}

				if (end >= 0)
				{
					rules = rules[..end];
				}
			}
			else
			{
				// There is no reliable boundary between criteria and framing in a custom
				// opening. Keep its text available to the reviewer and the settings editor.
				rules = prompt;
			}

			var presetId = (string?)node["defaultReviewerInstructionId"];
			if (presetId is not (null or "strict" or "allow-minors") && node["reviewerInstructions"] is JsonArray presets)
			{
				var preset = presets.OfType<JsonObject>().FirstOrDefault(item => (string?)item["id"] == presetId);
				if ((string?)preset?["text"] is { Length: > 0 } criteria)
				{
					rules = string.IsNullOrWhiteSpace(rules) ? criteria : rules + "\n\n" + criteria;
				}
			}
		}

		var strict = (bool?)node["defaultStrict"] ?? ((string?)node["defaultReviewerInstructionId"] != "allow-minors");
		return new(id, ScenarioKind.ReviewLoop, (string?)node["name"] ?? string.Empty, rules, strict);
	}
}
