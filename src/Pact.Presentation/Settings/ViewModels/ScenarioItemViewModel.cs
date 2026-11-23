using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>Editable review criteria and defaults for one named scenario.</summary>
public sealed class ScenarioItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private string _name;
	private string _reviewRules;
	private bool _defaultStrict;

	/// <summary>Reads current and legacy settings through the same mapping as the runtime.</summary>
	public ScenarioItemViewModel(JsonObject node)
	{
		_node = node;
		var review = ScenarioDefinitionStore.ReadReview(node);
		_id = review.Id;
		_name = review.Name;
		_reviewRules = review.ReviewRules;
		_defaultStrict = review.DefaultStrict;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Stable key a review request names.</summary>
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

	/// <summary>Label shown in the scenarios list.</summary>
	public string Name
	{
		get => _name;
		set
		{
			if (SetField(ref _name, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Checks included in the first reviewer turn.</summary>
	public string ReviewRules
	{
		get => _reviewRules;
		set
		{
			if (SetField(ref _reviewRules, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Requires resolution of minor findings as well as major ones by default.</summary>
	public bool DefaultStrict
	{
		get => _defaultStrict;
		set
		{
			if (SetField(ref _defaultStrict, value))
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
			var name = !string.IsNullOrWhiteSpace(Name)
				? Name
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new scenario)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["kind"] = "reviewLoop";
		_node["name"] = Name;
		_node["reviewRules"] = ReviewRules;
		_node["defaultStrict"] = DefaultStrict;
	}
}
