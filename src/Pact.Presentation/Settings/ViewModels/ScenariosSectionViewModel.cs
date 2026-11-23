using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Edits the named review criteria and defaults stored in scenarios.json.
/// </summary>
public sealed class ScenariosSectionViewModel : FileSectionViewModel<ScenarioItemViewModel>
{
	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public ScenariosSectionViewModel(SettingsFileStore store)
		: base(
			store,
			SettingsSection.Scenarios,
			"Review types",
			"Choose what a reviewer checks and when it may approve the work.",
			"scenarios.json")
	{
	}

	/// <inheritdoc />
	protected override ScenarioItemViewModel? TryCreateItem(JsonObject node)
		=> node["kind"] is JsonValue value && value.TryGetValue<string>(out var kind)
			&& string.Equals(kind, "reviewLoop", StringComparison.OrdinalIgnoreCase)
			? new ScenarioItemViewModel(node)
			: null;

	/// <inheritdoc />
	protected override ScenarioItemViewModel CreateNewItem(JsonObject node)
	{
		node["kind"] = "reviewLoop";
		node["reviewRules"] = string.Empty;
		node["defaultStrict"] = true;
		node["id"] = $"review-{Guid.NewGuid():N}";
		node["name"] = "New review";
		return new(node);
	}

	/// <inheritdoc />
	protected override string? Validate()
	{
		var reviewTypes = Items.OfType<ScenarioItemViewModel>().ToArray();
		foreach (var reviewType in reviewTypes.Where(item => item.IdChanged))
		{
			if (string.IsNullOrWhiteSpace(reviewType.Id))
			{
				return "A review type needs a non-empty ID.";
			}
			if (reviewTypes.Any(item => !ReferenceEquals(item, reviewType) && item.Id == reviewType.Id))
			{
				return "Review type IDs must be unique.";
			}
		}

		return null;
	}
}
