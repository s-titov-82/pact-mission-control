using System.Text.Json;
using Pact.Core.Scenarios;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>
/// Reads and writes <c>scenarios.json</c>, falling back to the shipped defaults when the file is
/// absent or an entry is unusable.
/// </summary>
public sealed class ScenarioDefinitionStore
{
	private const string DefaultsDirectoryName = "Defaults";
	private const string DefaultDefinitionsFileName = "scenarios-default.json";

	private static readonly string[] RequiredTemplateProperties =
	[
		"startPromptTemplate",
		"firstFeedbackTemplate",
		"authorReturnTemplate",
		"feedbackTemplate",
		"defaultTarget"
	];

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
	/// The definitions, or the shipped defaults when the file is missing. An entry lacking a
	/// required prompt template is completed from the defaults rather than dropped, so a
	/// partially hand-edited file still yields runnable scenarios.
	/// </returns>
	public async Task<IReadOnlyList<ScenarioDefinition>> LoadAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_path))
		{
			return LoadDefaultDefinitions();
		}

		var json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
		using var document = JsonDocument.Parse(json);

		List<ScenarioDefinition> definitions = [];
		foreach (var element in document.RootElement.EnumerateArray())
		{
			if (!IsKnownKind(element))
			{
				continue;
			}

			if (IsOldFormat(element))
			{
				var defaults = LoadDefaultDefinitions();
				await SaveAsync(defaults, cancellationToken).ConfigureAwait(false);
				return defaults;
			}

			var definition = element.Deserialize<ScenarioDefinition>(SettingsFileStore.JsonOptions);
			if (definition is not null)
			{
				definitions.Add(definition);
			}
		}

		return definitions;
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

	private static bool IsKnownKind(JsonElement element) => element.TryGetProperty("kind", out var kindElement)
			&& kindElement.ValueKind == JsonValueKind.String
			&& string.Equals(kindElement.GetString(), "reviewLoop", StringComparison.OrdinalIgnoreCase);

	private static bool IsOldFormat(JsonElement element) => RequiredTemplateProperties.Any(propertyName =>
																		 !element.TryGetProperty(propertyName, out var property)
																		 || property.ValueKind != JsonValueKind.String)
			|| HasMissingOrInvalidReviewerInstructions(element)
			|| HasMissingOrInvalidDefaultReviewerInstructionId(element);

	private static bool HasMissingOrInvalidReviewerInstructions(JsonElement element) => !element.TryGetProperty("reviewerInstructions", out var property)
			|| property.ValueKind != JsonValueKind.Array
			|| property.GetArrayLength() == 0;

	private static bool HasMissingOrInvalidDefaultReviewerInstructionId(JsonElement element) => !element.TryGetProperty("defaultReviewerInstructionId", out var property)
			|| property.ValueKind != JsonValueKind.String;
}
