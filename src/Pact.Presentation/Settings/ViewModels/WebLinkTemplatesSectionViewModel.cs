using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Editable web-link-templates.json: web link buttons shown from projects and ROOT.</summary>
public sealed class WebLinkTemplatesSectionViewModel : FileSectionViewModel<WebLinkTemplateItemViewModel>
{
	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public WebLinkTemplatesSectionViewModel(SettingsFileStore store)
		: base(
			store,
			SettingsSection.WebLinkTemplates,
			"Web link templates",
			"Web link templates shown from projects and ROOT, supporting %gitLabRepoId% and %teamCityProjectId% placeholders.",
			"web-link-templates.json")
	{
	}

	/// <inheritdoc />
	protected override WebLinkTemplateItemViewModel? TryCreateItem(JsonObject node)
		=> node["id"] is null ? null : new WebLinkTemplateItemViewModel(node);

	/// <inheritdoc />
	protected override WebLinkTemplateItemViewModel CreateNewItem(JsonObject node) => new(node);

	/// <inheritdoc />
	protected override string? Validate()
	{
		var links = Items.OfType<WebLinkTemplateItemViewModel>().ToList();

		foreach (var link in links)
		{
			if (string.IsNullOrWhiteSpace(link.Id))
			{
				return "Every web link template needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(link.Title))
			{
				return $"Web link template '{link.Id}' needs a title.";
			}

			if (string.IsNullOrWhiteSpace(link.StartUrl))
			{
				return $"Web link template '{link.Id}' needs a start URL.";
			}
		}

		var uniqueIdCount = links.Select(link => link.Id).Distinct(StringComparer.Ordinal).Count();
		if (uniqueIdCount != links.Count)
		{
			return "Web link template ids must be unique.";
		}

		return null;
	}
}
