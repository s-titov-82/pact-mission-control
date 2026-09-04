using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Editable shell-profiles.json: launch buttons for starting agent sessions.</summary>
public sealed class LaunchProfilesSectionViewModel : FileSectionViewModel<ShellProfileItemViewModel>
{
	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public LaunchProfilesSectionViewModel(SettingsFileStore store)
		: base(
			store,
			SettingsSection.LaunchProfiles,
			"Terminal templates",
			"Shell launch profiles; each entry becomes one launch button.",
			"shell-profiles.json")
	{
	}

	/// <inheritdoc />
	protected override ShellProfileItemViewModel? TryCreateItem(JsonObject node)
		=> node["id"] is null ? null : new ShellProfileItemViewModel(node);

	/// <inheritdoc />
	protected override ShellProfileItemViewModel CreateNewItem(JsonObject node) => new(node);

	/// <inheritdoc />
	protected override string? Validate()
	{
		var profiles = Items.OfType<ShellProfileItemViewModel>().ToList();

		foreach (var profile in profiles)
		{
			if (string.IsNullOrWhiteSpace(profile.Id))
			{
				return "Every launch profile needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(profile.CommandTemplate))
			{
				return $"Launch profile '{profile.Id}' needs a command template.";
			}

			if (string.IsNullOrWhiteSpace(profile.DefaultShell))
			{
				return $"Launch profile '{profile.Id}' needs a default shell.";
			}
		}

		var uniqueIdCount = profiles.Select(profile => profile.Id).Distinct(StringComparer.Ordinal).Count();
		if (uniqueIdCount != profiles.Count)
		{
			return "Launch profile ids must be unique.";
		}

		return null;
	}
}
