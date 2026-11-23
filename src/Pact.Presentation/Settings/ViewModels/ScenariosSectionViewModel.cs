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
			"Scenarios",
			"Choose what a reviewer checks and when it may approve the work.",
			"scenarios.json")
	{
	}

	/// <inheritdoc />
	protected override ScenarioItemViewModel? TryCreateItem(JsonObject node)
		=> string.Equals((string?)node["kind"], "reviewLoop", StringComparison.OrdinalIgnoreCase)
			? new ScenarioItemViewModel(node)
			: null;

	/// <inheritdoc />
	protected override ScenarioItemViewModel CreateNewItem(JsonObject node)
	{
		node["kind"] = "reviewLoop";
		node["reviewRules"] = string.Empty;
		node["id"] = $"review-{Guid.NewGuid():N}";
		node["name"] = "New review";
		return new(node);
	}

	/// <inheritdoc />
	protected override string? Validate()
	{
		var scenarios = Items.OfType<ScenarioItemViewModel>().ToList();

		foreach (var scenario in scenarios)
		{
			if (string.IsNullOrWhiteSpace(scenario.Id))
			{
				return "Every scenario needs a non-empty id.";
			}
		}

		var uniqueIdCount = scenarios.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count();
		if (uniqueIdCount != scenarios.Count)
		{
			return "Scenario ids must be unique.";
		}

		return null;
	}
}
