using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>One popup action nested inside a git helper's actions array.</summary>
public sealed class GitHelperActionItemViewModel : SettingsObservableObject
{
	private string _slot;
	private string _label;
	private string _argumentsText;

	/// <summary>Creates an action row.</summary>
	public GitHelperActionItemViewModel(string slot, string label, string argumentsText)
	{
		_slot = slot;
		_label = label;
		_argumentsText = argumentsText;
	}

	/// <summary>Raised whenever an editable field changes; the owning helper marks itself dirty.</summary>
	public event EventHandler? Changed;

	/// <summary>Git panel slot this action fills, such as history or resolve.</summary>
	public string Slot
	{
		get => _slot;
		set
		{
			if (SetField(ref _slot, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>Text shown for the action.</summary>
	public string Label
	{
		get => _label;
		set
		{
			if (SetField(ref _label, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>One argument per line; blank lines are dropped when written back on save.</summary>
	public string ArgumentsText
	{
		get => _argumentsText;
		set
		{
			if (SetField(ref _argumentsText, value))
			{
				Changed?.Invoke(this, EventArgs.Empty);
			}
		}
	}

	/// <summary>Splits <see cref="ArgumentsText"/> into trimmed, non-blank lines.</summary>
	public IReadOnlyList<string> ToArguments()
		=> ArgumentsText
			.Split('\n')
			.Select(line => line.Trim())
			.Where(line => line.Length > 0)
			.ToArray();

	/// <summary>Joins arguments read from JSON into the multiline text this VM edits.</summary>
	public static string JoinArguments(IEnumerable<string> arguments) => string.Join('\n', arguments);
}

/// <summary>One external git GUI helper tab, backed by an entry in git-helpers.json.</summary>
public sealed class GitHelperItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private string _name;
	private string _executable;
	private string _registryKey;
	private string _registryValue;
	private GitHelperActionItemViewModel? _selectedAction;

	/// <summary>Creates an item over its JSON node.</summary>
	public GitHelperItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_name = (string?)node["name"] ?? string.Empty;
		_executable = (string?)node["executable"] ?? string.Empty;

		_registryKey = string.Empty;
		_registryValue = string.Empty;
		if (node["windowsRegistryProbe"] is JsonObject probe)
		{
			_registryKey = (string?)probe["key"] ?? string.Empty;
			_registryValue = (string?)probe["value"] ?? string.Empty;
		}

		Actions = [];
		if (node["actions"] is JsonArray actions)
		{
			foreach (var actionNode in actions.OfType<JsonObject>())
			{
				var argumentsText = actionNode["arguments"] is JsonArray arguments
					? GitHelperActionItemViewModel.JoinArguments(arguments.Select(argument => (string?)argument ?? string.Empty))
					: string.Empty;

				AttachAction(new GitHelperActionItemViewModel(
					(string?)actionNode["slot"] ?? string.Empty,
					(string?)actionNode["label"] ?? string.Empty,
					argumentsText));
			}
		}

		_selectedAction = Actions.Count > 0 ? Actions[0] : null;
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>Stable key surviving edits to the name or executable.</summary>
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

	/// <summary>Label shown in the git menu.</summary>
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

	/// <summary>Executable name or full path; the helper is hidden when it cannot be resolved.</summary>
	public string Executable
	{
		get => _executable;
		set
		{
			if (SetField(ref _executable, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Empty together with <see cref="RegistryValue"/> means "no registry probe".</summary>
	public string RegistryKey
	{
		get => _registryKey;
		set
		{
			if (SetField(ref _registryKey, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Empty together with <see cref="RegistryKey"/> means "no registry probe".</summary>
	public string RegistryValue
	{
		get => _registryValue;
		set
		{
			if (SetField(ref _registryValue, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Actions this helper contributes.</summary>
	public ObservableCollection<GitHelperActionItemViewModel> Actions { get; }

	/// <summary>Selected action row, or <see langword="null"/> when none is selected.</summary>
	public GitHelperActionItemViewModel? SelectedAction
	{
		get => _selectedAction;
		set => SetField(ref _selectedAction, value);
	}

	/// <summary>Adds a new, empty popup action and selects it.</summary>
	public void AddAction()
	{
		var action = new GitHelperActionItemViewModel(string.Empty, string.Empty, string.Empty);
		AttachAction(action);
		SelectedAction = action;
		RaiseChanged();
	}

	/// <summary>Removes <paramref name="action"/> from <see cref="Actions"/>.</summary>
	public void RemoveAction(GitHelperActionItemViewModel action)
	{
		ArgumentNullException.ThrowIfNull(action);

		if (!Actions.Remove(action))
		{
			return;
		}

		DetachAction(action);

		if (ReferenceEquals(SelectedAction, action))
		{
			SelectedAction = Actions.Count > 0 ? Actions[0] : null;
		}

		RaiseChanged();
	}

	/// <inheritdoc />
	public override string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Name)
				? Name
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new helper)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["name"] = Name;
		_node["executable"] = Executable;

		if (string.IsNullOrWhiteSpace(RegistryKey) && string.IsNullOrWhiteSpace(RegistryValue))
		{
			_node.Remove("windowsRegistryProbe");
		}
		else
		{
			_node["windowsRegistryProbe"] = new JsonObject
			{
				["key"] = RegistryKey,
				["value"] = RegistryValue
			};
		}

		var actions = new JsonArray();
		foreach (var action in Actions)
		{
			var argumentsArray = new JsonArray();
			foreach (var argument in action.ToArguments())
			{
				argumentsArray.Add(argument);
			}

			actions.Add(new JsonObject
			{
				["slot"] = action.Slot,
				["label"] = action.Label,
				["arguments"] = argumentsArray
			});
		}

		_node["actions"] = actions;
	}

	private void AttachAction(GitHelperActionItemViewModel action)
	{
		action.Changed += OnActionChanged;
		Actions.Add(action);
	}

	private void DetachAction(GitHelperActionItemViewModel action) => action.Changed -= OnActionChanged;

	private void OnActionChanged(object? sender, EventArgs e) => RaiseChanged();
}