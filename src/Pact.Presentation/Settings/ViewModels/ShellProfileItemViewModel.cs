using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One launch-profile tab, backed by an entry in shell-profiles.json.</summary>
public sealed class ShellProfileItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private AgentKind _kind;
	private string _displayName;
	private string _commandTemplate;
	private string? _resumeCommandTemplate;
	private string _defaultShell;

	/// <summary>Creates an item over its JSON node.</summary>
	public ShellProfileItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_kind = ParseKind((string?)node["kind"]);
		_displayName = (string?)node["displayName"] ?? string.Empty;
		_commandTemplate = (string?)node["commandTemplate"] ?? string.Empty;
		_resumeCommandTemplate = (string?)node["resumeCommandTemplate"];
		_defaultShell = (string?)node["defaultShell"] ?? string.Empty;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Options for the "Agent kind" combo box; same list for every instance.</summary>
	public IReadOnlyList<AgentKind> KindOptions { get; } = Enum.GetValues<AgentKind>();

	/// <summary>Stable key referenced by sessions; must survive profile edits.</summary>
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

	/// <summary>
	/// Agent this profile launches, which selects its terminal compatibility behavior.
	/// </summary>
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

	/// <summary>Label shown in launch menus.</summary>
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

	/// <summary>Command line used to start a fresh session.</summary>
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

	/// <summary>Empty means "not set"; <see cref="WriteTo"/> removes the property in that case.</summary>
	public string? ResumeCommandTemplate
	{
		get => _resumeCommandTemplate;
		set
		{
			if (SetField(ref _resumeCommandTemplate, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Shell executable the command is launched through.</summary>
	public string DefaultShell
	{
		get => _defaultShell;
		set
		{
			if (SetField(ref _defaultShell, value))
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
		if (string.IsNullOrEmpty(ResumeCommandTemplate))
		{
			_node.Remove("resumeCommandTemplate");
		}
		else
		{
			_node["resumeCommandTemplate"] = ResumeCommandTemplate;
		}

		_node["defaultShell"] = DefaultShell;
		// The launch arguments that connect a session to Pact follow the agent kind, so a
		// stale per-profile override must not survive here.
		_node.Remove("agentControlArgumentTemplate");
	}

	private static AgentKind ParseKind(string? value)
		=> !string.IsNullOrEmpty(value) && Enum.TryParse(value, ignoreCase: true, out AgentKind kind)
			? kind
			: AgentKind.Custom;
}
