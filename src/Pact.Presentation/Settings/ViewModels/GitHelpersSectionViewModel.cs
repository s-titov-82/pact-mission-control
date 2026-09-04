using System.Collections.ObjectModel;
using System.Text.Json;
using Pact.Core.Git;
using Pact.Presentation.Settings.Mapping;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Editable git-helpers.json: the whole git popup — button commands (the "commands" array:
/// built-in buttons plus custom entries, shown under the "Buttons" top-level tab) and external
/// git GUI helpers (the "helpers" array, shown under "External helpers"). Both arrays live in one
/// JSON object root and are saved together with one Save button. Unlike the other sections this
/// one manages two <see cref="JsonSettingsArray"/> views over the same document, so it does not
/// derive from <see cref="FileSectionViewModel{TItem}"/>.
/// </summary>
public sealed class GitHelpersSectionViewModel : SettingsSectionViewModelBase
{
	private readonly SettingsFileStore _store;
	private JsonSettingsArray? _commands;
	private JsonSettingsArray? _helpers;

	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public GitHelpersSectionViewModel(SettingsFileStore store)
		: base(
			SettingsSection.GitHelpers,
			"Git popup",
			"The project's git popup: button commands (edit what Pull/Stash run, add fixed flags to dialog buttons, disable buttons, add custom command buttons, reorder them) on the Buttons tab, and external git GUI helpers (TortoiseGit and friends; helpers that cannot be resolved on this machine stay hidden) on the External helpers tab.",
			"git-helpers.json",
			ResolvePath(store))
	{
		ArgumentNullException.ThrowIfNull(store);
		_store = store;
	}

	/// <summary>Command (button) tabs, in popup order — this is also the "commands" JSON array order.</summary>
	public ObservableCollection<SettingsItemViewModelBase> CommandItems { get; } = [];

	/// <summary>External GUI helper tabs.</summary>
	public ObservableCollection<SettingsItemViewModelBase> HelperItems { get; } = [];

	/// <summary>Selected button tab, or <see langword="null"/> when none is selected.</summary>
	public SettingsItemViewModelBase? SelectedCommandItem
	{
		get;
		set
		{
			if (SetField(ref field, value))
			{
				OnPropertyChanged(nameof(CanMoveSelectedCommandLeft));
				OnPropertyChanged(nameof(CanMoveSelectedCommandRight));
			}
		}
	}

	/// <summary>Whether the selected button can move earlier in popup order.</summary>
	public bool CanMoveSelectedCommandLeft =>
		SelectedCommandItem is not null && CommandItems.IndexOf(SelectedCommandItem) > 0;

	/// <summary>Whether the selected button can move later in popup order.</summary>
	public bool CanMoveSelectedCommandRight
	{
		get
		{
			var index = SelectedCommandItem is null ? -1 : CommandItems.IndexOf(SelectedCommandItem);
			return index >= 0 && index < CommandItems.Count - 1;
		}
	}

	/// <summary>Selected external helper tab, or <see langword="null"/> when none is selected.</summary>
	public SettingsItemViewModelBase? SelectedHelperItem
	{
		get;
		set => SetField(ref field, value);
	}

	/// <summary>0 = Buttons tab, 1 = External helpers tab.</summary>
	public int ActiveTabIndex
	{
		get;
		set => SetField(ref field, value);
	}

	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		DetachAllItems();
		CommandItems.Clear();
		HelperItems.Clear();
		SelectedCommandItem = null;
		SelectedHelperItem = null;
		_commands = null;
		_helpers = null;
		StatusText = null;

		// No ConfigureAwait(false): this method mutates UI-bound state afterwards.
		var json = await _store.ReadAsync(FileName, cancellationToken);

		JsonSettingsArray commands;
		JsonSettingsArray helpers;
		try
		{
			commands = JsonSettingsArray.Parse(json, "commands");
			helpers = commands.SiblingArray("helpers");
		}
		catch (JsonException ex)
		{
			StatusText = $"Failed to load {FileName}: {ex.Message}";
			ClearDirty();
			return;
		}

		_commands = commands;
		_helpers = helpers;
		BackfillMissingBuiltInCommands(commands);

		foreach (var node in commands.Items)
		{
			AddItem(CommandItems, node["id"] is null ? new UnrecognizedItemViewModel(node) : new GitCommandItemViewModel(node));
		}

		foreach (var node in helpers.Items)
		{
			AddItem(HelperItems, node["id"] is null ? new UnrecognizedItemViewModel(node) : new GitHelperItemViewModel(node));
		}

		SelectedCommandItem = CommandItems.Count > 0 ? CommandItems[0] : null;
		SelectedHelperItem = HelperItems.Count > 0 ? HelperItems[0] : null;
		ActiveTabIndex = 0;
		ClearDirty();
	}

	/// <inheritdoc />
	public override async Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		if (_commands is null)
		{
			StatusText = "Nothing loaded to save.";
			return false;
		}

		var error = Validate();
		if (error is not null)
		{
			StatusText = error;
			return false;
		}

		foreach (var item in AllItems)
		{
			item.WriteTo();
		}

		// No ConfigureAwait(false): the loop below mutates each item's (UI-bound) dirty state.
		await _store.SaveAsync(FileName, _commands.ToJsonString(), cancellationToken);
		foreach (var item in AllItems)
		{
			item.ClearItemDirty();
		}

		ClearDirty();
		StatusText = $"Saved {Label} ({CommandItems.Count + HelperItems.Count} items).";
		return true;
	}

	/// <summary>Finds a command or helper by id in either collection and activates its top-level
	/// tab; unknown ids no-op. <paramref name="subItemId"/> is currently unused for this
	/// section.</summary>
	public override void SelectItem(string? itemId, string? subItemId)
	{
		if (itemId is null)
		{
			return;
		}

		var command = CommandItems.OfType<GitCommandItemViewModel>()
			.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
		if (command is not null)
		{
			SelectedCommandItem = command;
			ActiveTabIndex = 0;
			return;
		}

		var helper = HelperItems.OfType<GitHelperItemViewModel>()
			.FirstOrDefault(item => string.Equals(item.Id, itemId, StringComparison.Ordinal));
		if (helper is not null)
		{
			SelectedHelperItem = helper;
			ActiveTabIndex = 1;
		}
	}

	/// <summary>Adds a new custom command entry after the last command tab and selects it.</summary>
	public void AddNewCommand()
	{
		if (_commands is null)
		{
			return;
		}

		var node = _commands.AddNew();
		GitCommandItemViewModel item = new(node);
		AttachItem(item);
		CommandItems.Add(item);
		SelectedCommandItem = item;
		MarkDirty();
	}

	/// <summary>Adds a new external helper entry at the end and selects it.</summary>
	public void AddNewItem()
	{
		if (_helpers is null)
		{
			return;
		}

		var node = _helpers.AddNew();
		GitHelperItemViewModel item = new(node);
		AttachItem(item);
		HelperItems.Add(item);
		SelectedHelperItem = item;
		MarkDirty();
	}

	/// <summary>Moves the selected command tab one slot left (<paramref name="delta"/> -1) or right
	/// (+1) within the "commands" JSON array; a no-op at either end or with nothing selected.</summary>
	public void MoveSelectedCommand(int delta)
	{
		if (_commands is null || SelectedCommandItem is null)
		{
			return;
		}

		var oldIndex = CommandItems.IndexOf(SelectedCommandItem);
		if (oldIndex < 0)
		{
			return;
		}

		var newIndex = oldIndex + delta;
		if (newIndex < 0 || newIndex >= CommandItems.Count)
		{
			return;
		}

		var selectedItem = SelectedCommandItem;
		_commands.Move(selectedItem.Node, delta);
		CommandItems.Move(oldIndex, newIndex);
		SelectedCommandItem = selectedItem;
		OnPropertyChanged(nameof(CanMoveSelectedCommandLeft));
		OnPropertyChanged(nameof(CanMoveSelectedCommandRight));
		MarkDirty();
	}

	/// <summary>Removes a helper or a custom command; built-in commands are never removable.</summary>
	public void RemoveItem(SettingsItemViewModelBase item)
	{
		ArgumentNullException.ThrowIfNull(item);

		if (item is GitCommandItemViewModel { IsBuiltIn: true })
		{
			return;
		}

		var isCommand = item is GitCommandItemViewModel;
		var owner = isCommand ? _commands : _helpers;
		var collection = isCommand ? CommandItems : HelperItems;
		if (owner is null || !collection.Remove(item))
		{
			return;
		}

		DetachItem(item);
		owner.Remove(item.Node);

		if (isCommand)
		{
			if (ReferenceEquals(SelectedCommandItem, item))
			{
				SelectedCommandItem = CommandItems.Count > 0 ? CommandItems[0] : null;
			}
		}
		else if (ReferenceEquals(SelectedHelperItem, item))
		{
			SelectedHelperItem = HelperItems.Count > 0 ? HelperItems[0] : null;
		}

		MarkDirty();
	}

	/// <summary>
	/// A deleted-by-hand built-in entry silently behaves as its default in the popup; recreate it
	/// here (from the defaults catalog) so every built-in button always has a tab to edit.
	/// </summary>
	private static void BackfillMissingBuiltInCommands(JsonSettingsArray commands)
	{
		var presentIds = commands.Items
			.Select(node => (string?)node["id"])
			.OfType<string>()
			.ToHashSet(StringComparer.Ordinal);

		foreach (var record in GitButtonCommandSet.Defaults)
		{
			if (presentIds.Contains(record.Id))
			{
				continue;
			}

			var node = commands.AddNew();
			node["id"] = record.Id;
			node["label"] = record.Label;
			node["enabled"] = record.Enabled;
			if (record.Command is not null)
			{
				node["command"] = record.Command;
			}

			node["description"] = record.Description;
			node["docUrl"] = record.DocUrl;
		}
	}

	private string? Validate()
	{
		var commandsError = ValidateCommands(CommandItems.OfType<GitCommandItemViewModel>().ToList());
		return commandsError ?? ValidateHelpers(HelperItems.OfType<GitHelperItemViewModel>().ToList());
	}

	private static string? ValidateCommands(List<GitCommandItemViewModel> commands)
	{
		foreach (var command in commands)
		{
			if (string.IsNullOrWhiteSpace(command.Id))
			{
				return "Every git command needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(command.Label))
			{
				return $"Git command '{command.Id}' needs a label.";
			}

			if (command.IsDialog)
			{
				if (!GitCommandLine.TrySplit(command.ExtraArgs, out _))
				{
					return $"Git command '{command.Id}' has unbalanced quotes in its extra flags.";
				}
			}
			else if (!GitCommandLine.TrySplit(command.Command, out var arguments) || arguments.Count == 0)
			{
				return $"Git command '{command.Id}' needs a non-empty, well-quoted command.";
			}
		}

		var uniqueIdCount = commands.Select(command => command.Id).Distinct(StringComparer.Ordinal).Count();
		if (uniqueIdCount != commands.Count)
		{
			return "Git command ids must be unique.";
		}

		return null;
	}

	private static string? ValidateHelpers(List<GitHelperItemViewModel> helpers)
	{
		foreach (var helper in helpers)
		{
			if (string.IsNullOrWhiteSpace(helper.Id))
			{
				return "Every git helper needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(helper.Name))
			{
				return $"Git helper '{helper.Id}' needs a name.";
			}

			var keyFilled = !string.IsNullOrWhiteSpace(helper.RegistryKey);
			var valueFilled = !string.IsNullOrWhiteSpace(helper.RegistryValue);
			if (keyFilled != valueFilled)
			{
				return $"Git helper '{helper.Id}' needs both a registry key and value, or neither.";
			}

			var hasRegistryProbe = keyFilled && valueFilled;
			if (string.IsNullOrWhiteSpace(helper.Executable) && !hasRegistryProbe)
			{
				return $"Git helper '{helper.Id}' needs an executable, or a registry probe.";
			}

			foreach (var action in helper.Actions)
			{
				if (string.IsNullOrWhiteSpace(action.Slot))
				{
					return $"Git helper '{helper.Id}' has an action with an empty slot.";
				}

				if (string.IsNullOrWhiteSpace(action.Label))
				{
					return $"Git helper '{helper.Id}' has an action with an empty label.";
				}
			}
		}

		var uniqueIdCount = helpers.Select(helper => helper.Id).Distinct(StringComparer.Ordinal).Count();
		if (uniqueIdCount != helpers.Count)
		{
			return "Git helper ids must be unique.";
		}

		return null;
	}

	private IEnumerable<SettingsItemViewModelBase> AllItems => CommandItems.Concat(HelperItems);

	private void AddItem(ObservableCollection<SettingsItemViewModelBase> collection, SettingsItemViewModelBase item)
	{
		AttachItem(item);
		collection.Add(item);
	}

	private void AttachItem(SettingsItemViewModelBase item) => item.Changed += OnItemChanged;

	private void DetachItem(SettingsItemViewModelBase item) => item.Changed -= OnItemChanged;

	private void DetachAllItems()
	{
		foreach (var item in AllItems)
		{
			DetachItem(item);
		}
	}

	private void OnItemChanged(object? sender, EventArgs e) => MarkDirty();

	private static string ResolvePath(SettingsFileStore store)
	{
		ArgumentNullException.ThrowIfNull(store);
		return store.Files
			.First(file => string.Equals(file.FileName, "git-helpers.json", StringComparison.OrdinalIgnoreCase))
			.Path;
	}
}
