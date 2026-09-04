using System.Text.Json.Nodes;
using Pact.Core.Git;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// One git popup button command tab, backed by an entry of the "commands" array in
/// git-helpers.json. Built-in entries (known ids) keep a fixed id, expose an Enabled toggle
/// instead of deletion, and show their description read-only; dialog built-ins edit fixed extra
/// flags instead of the whole command. Custom entries edit everything and are deletable.
/// </summary>
public sealed class GitCommandItemViewModel : SettingsItemViewModelBase
{
	private readonly JsonObject _node;
	private string _id;
	private string _label;
	private string _command;
	private string _extraArgs;
	private bool _enabled;
	private string _description;
	private string _docUrl;

	/// <summary>
	/// Creates an item over its JSON node, classifying it as built-in, dialog, or custom.
	/// </summary>
	public GitCommandItemViewModel(JsonObject node)
	{
		ArgumentNullException.ThrowIfNull(node);
		_node = node;
		_id = (string?)node["id"] ?? string.Empty;
		_label = (string?)node["label"] ?? string.Empty;
		_command = (string?)node["command"] ?? string.Empty;
		_extraArgs = (string?)node["extraArgs"] ?? string.Empty;
		_enabled = (bool?)node["enabled"] ?? true;
		_description = (string?)node["description"] ?? string.Empty;
		_docUrl = (string?)node["docUrl"] ?? string.Empty;

		// Classified once at load: an entry cannot change kind by editing its id (built-in ids
		// are read-only, and every built-in id always exists thanks to load-time backfill).
		IsBuiltIn = GitButtonCommandSet.IsBuiltInId(_id);
		IsDialog = GitButtonCommandSet.IsDialogId(_id);
	}

	/// <inheritdoc />
	public override JsonObject Node => _node;

	/// <summary>
	/// Whether this is a built-in button. Built-ins have a read-only id and cannot be deleted.
	/// </summary>
	public bool IsBuiltIn { get; }

	/// <summary>
	/// Whether the command is composed by a dialog, in which case only extra flags are editable.
	/// </summary>
	public bool IsDialog { get; }

	/// <summary>Whether this is a user-added button, editable and deletable in full.</summary>
	public bool IsCustom => !IsBuiltIn;

	/// <summary>Whether the full command field is editable rather than just extra flags.</summary>
	public bool ShowCommand => !IsDialog;

	/// <summary>Read-only shape of the dialog-generated command; empty for non-dialog entries.</summary>
	public string DialogPreview => GitButtonCommandSet.DialogPreview(_id);

	/// <summary>Description shown for built-ins: the file's text, else the built-in default's.</summary>
	public string DescriptionDisplay => !string.IsNullOrWhiteSpace(_description)
		? _description
		: DefaultRecord?.Description ?? string.Empty;

	/// <summary>Documentation link target: the file's URL, else the built-in default's.</summary>
	public string DocUrlDisplay => !string.IsNullOrWhiteSpace(_docUrl)
		? _docUrl
		: DefaultRecord?.DocUrl ?? string.Empty;

	private GitButtonCommandRecord? DefaultRecord =>
		GitButtonCommandSet.Defaults.FirstOrDefault(record => record.Id == _id);

	/// <summary>
	/// Button id. Read-only for built-ins, since the id is what classifies the entry.
	/// </summary>
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

	/// <summary>Button caption shown in the git popup.</summary>
	public string Label
	{
		get => _label;
		set
		{
			if (SetField(ref _label, value))
			{
				OnPropertyChanged(nameof(TabHeader));
				RaiseChanged();
			}
		}
	}

	/// <summary>The full argument string for simple/custom entries; a leading "git " is tolerated.</summary>
	public string Command
	{
		get => _command;
		set
		{
			if (SetField(ref _command, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Fixed flags inserted after the subcommand of a dialog entry.</summary>
	public string ExtraArgs
	{
		get => _extraArgs;
		set
		{
			if (SetField(ref _extraArgs, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>Disabled built-in buttons are hidden in the git popup.</summary>
	public bool Enabled
	{
		get => _enabled;
		set
		{
			if (SetField(ref _enabled, value))
			{
				RaiseChanged();
			}
		}
	}

	/// <summary>
	/// Explanatory text. Blank falls back to the built-in default's description.
	/// </summary>
	public string Description
	{
		get => _description;
		set
		{
			if (SetField(ref _description, value))
			{
				OnPropertyChanged(nameof(DescriptionDisplay));
				RaiseChanged();
			}
		}
	}

	/// <summary>
	/// Documentation link. Blank falls back to the built-in default's URL.
	/// </summary>
	public string DocUrl
	{
		get => _docUrl;
		set
		{
			if (SetField(ref _docUrl, value))
			{
				OnPropertyChanged(nameof(DocUrlDisplay));
				RaiseChanged();
			}
		}
	}

	/// <inheritdoc />
	public override string TabHeader
	{
		get
		{
			var name = !string.IsNullOrWhiteSpace(Label)
				? Label
				: !string.IsNullOrWhiteSpace(Id) ? Id : "(new command)";
			return IsItemDirty ? $"{name} •" : name;
		}
	}

	internal override void WriteTo()
	{
		_node["id"] = Id;
		_node["label"] = Label;
		_node["enabled"] = Enabled;

		WriteOrRemove("command", IsDialog ? null : Command);
		WriteOrRemove("extraArgs", IsDialog ? ExtraArgs : null);
		WriteOrRemove("description", Description);
		WriteOrRemove("docUrl", DocUrl);
	}

	private void WriteOrRemove(string propertyName, string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			_node.Remove(propertyName);
		}
		else
		{
			_node[propertyName] = value;
		}
	}
}