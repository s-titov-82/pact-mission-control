using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One reviewer-only launch profile backed by its original JSON object.</summary>
public sealed class ReviewProfileItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private readonly ReviewerModelCatalog _catalog;
	private const string DefaultChoice = "Agent default";
	private string _model = DefaultChoice;
	private string _effort = DefaultChoice;
	private bool _useCustomCommand;
	private string _customCommand = string.Empty;
	private string _id;
	private AgentKind _kind;
	private string _displayName;
	private string _commandTemplate;

	/// <summary>Creates an editable item over <paramref name="node"/>.</summary>
	public ReviewProfileItemViewModel(JsonObject node, ReviewerModelCatalog? catalog = null)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_catalog = catalog ?? new();
		_id = (string?)node["id"] ?? string.Empty;
		_kind = ParseKind((string?)node["kind"]);
		_displayName = (string?)node["displayName"] ?? string.Empty;
		_commandTemplate = (string?)node["commandTemplate"] ?? string.Empty;
		KindOptions = _kind is AgentKind.Claude or AgentKind.Codex or AgentKind.Custom
			? [AgentKind.Claude, AgentKind.Codex, AgentKind.Custom]
			: [AgentKind.Claude, AgentKind.Codex, AgentKind.Custom, _kind];
		_useCustomCommand = !TryReadChoices();
		if (_useCustomCommand)
		{
			_customCommand = _commandTemplate;
		}
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Agent kinds available to the settings combo box.</summary>
	public IReadOnlyList<AgentKind> KindOptions { get; }

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
				if (_useCustomCommand)
				{
					_customCommand = CommandTemplate;
				}

				_model = _effort = DefaultChoice;
				_useCustomCommand = !SupportsChoices;
				UpdateCommand();
				OnPropertyChanged(nameof(SupportsChoices));
				NotifyChoices();
				RaiseChanged();
			}
		}
	}

	/// <summary>Whether Pact can generate a command for this reviewer kind.</summary>
	public bool SupportsChoices => Kind is AgentKind.Claude or AgentKind.Codex;

	/// <summary>Whether the model and effort fields own the launch command.</summary>
	public bool ShowChoices => SupportsChoices && !UseCustomCommand;

	/// <summary>Preserves an arbitrary executable or additional arguments in the raw command editor.</summary>
	public bool UseCustomCommand
	{
		get => _useCustomCommand;
		set
		{
			if (!SupportsChoices || !SetField(ref _useCustomCommand, value))
			{
				return;
			}

			if (!value || string.IsNullOrEmpty(_customCommand))
			{
				_customCommand = CommandTemplate;
			}

			UpdateCommand();
			NotifyChoices();
			RaiseChanged();
		}
	}

	/// <summary>Available models including the current profile's saved choice.</summary>
	public IReadOnlyList<string> ModelOptions => [.. _catalog.Models(Kind).Prepend(DefaultChoice).Append(_model).Distinct()];

	/// <summary>Available effort levels including the current profile's saved choice.</summary>
	public IReadOnlyList<string> EffortOptions => [.. _catalog.Efforts(Kind, _model).Prepend(DefaultChoice).Append(_effort).Distinct()];

	/// <summary>The model override, or the agent's configured default.</summary>
	public string SelectedModel
	{
		get => _model;
		set
		{
			if (!ShowChoices || !ModelOptions.Contains(value) || !SetField(ref _model, value))
			{
				return;
			}

			if (!_catalog.Efforts(Kind, value).Contains(_effort))
			{
				_effort = DefaultChoice;
			}

			UpdateCommand();
			OnPropertyChanged(nameof(EffortOptions));
			OnPropertyChanged(nameof(SelectedEffort));
		}
	}

	/// <summary>The reasoning effort override, or the agent's configured default.</summary>
	public string SelectedEffort
	{
		get => _effort;
		set
		{
			if (ShowChoices && EffortOptions.Contains(value) && SetField(ref _effort, value))
			{
				UpdateCommand();
			}
		}
	}

	private void NotifyChoices()
	{
		OnPropertyChanged(nameof(UseCustomCommand));
		OnPropertyChanged(nameof(ShowChoices));
		OnPropertyChanged(nameof(ModelOptions));
		OnPropertyChanged(nameof(EffortOptions));
		OnPropertyChanged(nameof(SelectedModel));
		OnPropertyChanged(nameof(SelectedEffort));
	}

	private string Executable => Kind == AgentKind.Codex ? "codex" : "claude";

	private void UpdateCommand()
	{
		if (UseCustomCommand)
		{
			CommandTemplate = _customCommand;
			return;
		}

		var command = Executable;
		if (_model != DefaultChoice)
		{
			command += $" --model {_model}";
		}
		if (_effort != DefaultChoice)
		{
			command += Kind == AgentKind.Codex ? $" -c model_reasoning_effort={_effort}" : $" --effort {_effort}";
		}

		CommandTemplate = command;
	}

	private bool TryReadChoices()
	{
		var tokens = CommandTemplate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
		if (!SupportsChoices || tokens.Length % 2 == 0 || tokens[0] != Executable)
		{
			return false;
		}

		var model = DefaultChoice;
		var effort = DefaultChoice;
		const string prefix = "model_reasoning_effort=";
		for (var index = 1; index < tokens.Length; index += 2)
		{
			var option = tokens[index];
			var value = tokens[index + 1];
			if ((option == "--model" || (Kind == AgentKind.Codex && option == "-m")) && model == DefaultChoice && ReviewerModelCatalog.IsCommandValue(value))
			{
				model = value;
			}
			else if (Kind == AgentKind.Claude && option == "--effort" && effort == DefaultChoice && ReviewerModelCatalog.IsCommandValue(value))
			{
				effort = value;
			}
			else if (Kind == AgentKind.Codex && option == "-c" && effort == DefaultChoice && value.StartsWith(prefix, StringComparison.Ordinal)
				&& ReviewerModelCatalog.IsCommandValue(value[prefix.Length..]))
			{
				effort = value[prefix.Length..];
			}
			else
			{
				return false;
			}
		}

		_model = model;
		_effort = effort;
		return true;
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
