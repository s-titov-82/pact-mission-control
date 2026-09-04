using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Editable scenarios.json: review-loop scenario definitions shown in the right panel.
/// Loading is pure <see cref="Mapping.JsonSettingsArray"/> parsing — it never delegates to
/// <see cref="ScenarioDefinitionStore"/>, which silently reseeds malformed files.
/// </summary>
public sealed class ScenariosSectionViewModel : FileSectionViewModel<ScenarioItemViewModel>
{
	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public ScenariosSectionViewModel(SettingsFileStore store)
		: base(
			store,
			SettingsSection.Scenarios,
			"Scenarios",
			"Workspace scenario definitions shown in the right panel. Edit ids, iteration limits, prompt templates, reviewer instructions, and stop markers carefully; invalid values can break future scenario automation.",
			"scenarios.json")
	{
	}

	/// <inheritdoc />
	protected override ScenarioItemViewModel? TryCreateItem(JsonObject node)
		=> string.Equals((string?)node["kind"], "reviewLoop", StringComparison.OrdinalIgnoreCase)
			? new ScenarioItemViewModel(node)
			: null;

	/// <inheritdoc />
	protected override ScenarioItemViewModel CreateNewItem(JsonObject node) => new(node);

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

			if (!int.TryParse(scenario.MaxIterationsText, out var maxIterations) || maxIterations < 1)
			{
				return $"Scenario '{scenario.Id}' needs a max iterations value of at least 1.";
			}

			if (string.IsNullOrWhiteSpace(scenario.StopMarker))
			{
				return $"Scenario '{scenario.Id}' needs a stop marker.";
			}

			if (string.IsNullOrWhiteSpace(scenario.StartPromptTemplate))
			{
				return $"Scenario '{scenario.Id}' needs a start prompt template.";
			}

			if (string.IsNullOrWhiteSpace(scenario.FirstFeedbackTemplate))
			{
				return $"Scenario '{scenario.Id}' needs a first feedback template.";
			}

			if (string.IsNullOrWhiteSpace(scenario.AuthorReturnTemplate))
			{
				return $"Scenario '{scenario.Id}' needs an author return template.";
			}

			if (string.IsNullOrWhiteSpace(scenario.FeedbackTemplate))
			{
				return $"Scenario '{scenario.Id}' needs a feedback template.";
			}

			if (scenario.ReviewerInstructions.Count == 0)
			{
				return $"Scenario '{scenario.Id}' needs at least one reviewer instruction.";
			}

			foreach (var instruction in scenario.ReviewerInstructions)
			{
				if (string.IsNullOrWhiteSpace(instruction.Id))
				{
					return $"Scenario '{scenario.Id}' has a reviewer instruction with an empty id.";
				}
			}

			var uniqueInstructionIdCount = scenario.ReviewerInstructions
				.Select(instruction => instruction.Id)
				.Distinct(StringComparer.Ordinal)
				.Count();
			if (uniqueInstructionIdCount != scenario.ReviewerInstructions.Count)
			{
				return $"Scenario '{scenario.Id}' has duplicate reviewer instruction ids.";
			}

			if (!scenario.ReviewerInstructions.Any(instruction =>
					string.Equals(instruction.Id, scenario.DefaultReviewerInstructionId, StringComparison.Ordinal)))
			{
				return $"Scenario '{scenario.Id}' has a defaultReviewerInstructionId that does not match any instruction.";
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
