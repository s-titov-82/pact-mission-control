using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One web-link-template tab, backed by an entry in web-link-templates.json.</summary>
public sealed class WebLinkTemplateItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private string _title;
	private string _startUrl;

	/// <summary>Creates an item over its JSON node.</summary>
	public WebLinkTemplateItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_title = (string?)node["title"] ?? string.Empty;
		_startUrl = (string?)node["startUrl"] ?? string.Empty;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Stable key surviving edits to the title or URL.</summary>
	public string Id
	{
		get => _id;
		set
		{
			if (SetField(ref _id, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Label shown in the web link menu.</summary>
	public string Title
	{
		get => _title;
		set
		{
			if (SetField(ref _title, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>
	/// URL template, supporting the <c>%gitLabRepoId%</c> and <c>%teamCityProjectId%</c>
	/// project placeholders.
	/// </summary>
	public string StartUrl
	{
		get => _startUrl;
		set
		{
			if (SetField(ref _startUrl, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <inheritdoc />
	public override string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Title)
				? Title
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new link)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["title"] = Title;
		_node["startUrl"] = StartUrl;
	}
}