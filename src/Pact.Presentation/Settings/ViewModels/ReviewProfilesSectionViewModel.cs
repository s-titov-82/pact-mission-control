using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Edits reviewer-only launch profiles while preserving unknown JSON fields.</summary>
public sealed class ReviewProfilesSectionViewModel : FileSectionViewModel<ReviewProfileItemViewModel>
{
	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public ReviewProfilesSectionViewModel(SettingsFileStore store)
		: base(
			store,
			SettingsSection.ReviewProfiles,
			"Review profiles",
			"Reviewer-only launch profiles used by agent-requested review runs.",
			"review-profiles.json")
	{
	}

	/// <inheritdoc />
	protected override ReviewProfileItemViewModel? TryCreateItem(JsonObject node)
		=> node["id"] is null ? null : new ReviewProfileItemViewModel(node);

	/// <inheritdoc />
	protected override ReviewProfileItemViewModel CreateNewItem(JsonObject node) => new(node);

	/// <inheritdoc />
	protected override string? Validate()
	{
		var profiles = Items.OfType<ReviewProfileItemViewModel>().ToList();
		foreach (var profile in profiles)
		{
			if (string.IsNullOrWhiteSpace(profile.Id))
			{
				return "Every review profile needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(profile.CommandTemplate))
			{
				return $"Review profile '{profile.Id}' needs a command template.";
			}
		}

		return profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count()
			== profiles.Count
			? null
			: "Review profile ids must be unique.";
	}
}
