using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One reviewer-only launch profile backed by its original JSON object.</summary>
public sealed class ReviewProfileItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private AgentKind _kind;
	private string _displayName;
	private string _commandTemplate;

	/// <summary>Creates an editable item over <paramref name="node"/>.</summary>
	public ReviewProfileItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_kind = ParseKind((string?)node["kind"]);
		_displayName = (string?)node["displayName"] ?? string.Empty;
		_commandTemplate = (string?)node["commandTemplate"] ?? string.Empty;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Agent kinds available to the settings combo box.</summary>
	public IReadOnlyList<AgentKind> KindOptions { get; } = Enum.GetValues<AgentKind>();

	/// <summary>Stable identifier named by agent-requested reviews.</summary>
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

	/// <summary>Agent kind selecting terminal compatibility behavior.</summary>
	public AgentKind Kind
	{
		get => _kind;
		set
		{
			if (SetField(ref _kind, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Human-readable reviewer name shown in settings.</summary>
	public string DisplayName
	{
		get => _displayName;
		set
		{
			if (SetField(ref _displayName, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>Command line used to start the reviewer.</summary>
	public string CommandTemplate
	{
		get => _commandTemplate;
		set
		{
			if (SetField(ref _commandTemplate, value))
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
			var name = !string.IsNullOrWhiteSpace(DisplayName)
				? DisplayName
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new profile)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["kind"] = JsonNamingPolicy.CamelCase.ConvertName(Kind.ToString());
		_node["displayName"] = DisplayName;
		_node["commandTemplate"] = CommandTemplate;
		// The launch arguments that connect a session to Pact follow the agent kind, so a
		// stale per-profile override must not survive here.
		_node.Remove("agentControlArgumentTemplate");
	}

	private static AgentKind ParseKind(string? value)
		=> !string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out AgentKind kind)
			? kind
			: AgentKind.Custom;
}
