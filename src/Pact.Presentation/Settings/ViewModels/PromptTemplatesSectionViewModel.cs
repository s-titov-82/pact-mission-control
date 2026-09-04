using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Nodes;
using Pact.Core.Prompting;
namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Editable prompt-templates.json, split into prompt and shell-command groups so each delivery
/// mode is edited in its own list while sharing one backing file.
/// </summary>
public sealed class PromptTemplatesSectionViewModel : FileSectionViewModel<PromptTemplateItemViewModel>
{
	private PromptTemplateGroupViewModel _selectedGroup;

	/// <summary>Creates the section over <paramref name="store"/>.</summary>
	public PromptTemplatesSectionViewModel(SettingsFileStore store)
		: base(store, SettingsSection.PromptTemplates, "Prompt/Shell templates",
			"Prompt and shell-command templates shown in the right panel. Either type may use {selectedText}.", "prompt-templates.json")
	{ Groups = [Prompts, ShellCommands]; _selectedGroup = Prompts; }
	/// <summary>Templates delivered into an agent's composer.</summary>
	public PromptTemplateGroupViewModel Prompts { get; } = new(PromptActionType.Prompt, "Prompts");

	/// <summary>Templates delivered as shell command lines.</summary>
	public PromptTemplateGroupViewModel ShellCommands { get; } = new(PromptActionType.TerminalCommand, "Shell commands");

	/// <summary>Both groups, for binding the group selector.</summary>
	public ObservableCollection<PromptTemplateGroupViewModel> Groups { get; }

	/// <summary>Group currently shown.</summary>
	public PromptTemplateGroupViewModel SelectedGroup { get => _selectedGroup; set => SetField(ref _selectedGroup, value); }
	/// <inheritdoc />
	public override async Task LoadAsync(CancellationToken cancellationToken)
	{
		DetachTypedItems();
		Prompts.Items.Clear();
		ShellCommands.Items.Clear();
		Prompts.SelectedItem = null;
		ShellCommands.SelectedItem = null;
		await base.LoadAsync(cancellationToken);
		foreach (var item in Items.OfType<PromptTemplateItemViewModel>())
		{ GroupFor(item.Type).Items.Add(item); item.PropertyChanged += OnTemplatePropertyChanged; }
		SelectedGroup = Prompts.Items.Count > 0 ? Prompts : ShellCommands;
		SelectedGroup.SelectedItem = SelectedGroup.Items.FirstOrDefault();
	}
	/// <summary>
	/// Adds a template of the given type, places it in the matching group, and selects it.
	/// </summary>
	public PromptTemplateItemViewModel AddNewTemplate(PromptActionType type)
	{
		var count = Items.Count;
		base.AddNewItem();
		if (Items.Count == count)
		{
			throw new InvalidOperationException("Prompt templates must be loaded before adding an item.");
		}

		var item = (PromptTemplateItemViewModel)Items[^1];
		item.Type = PromptActionPolicy.Normalize(type);
		item.SendByDefault = item.Type == PromptActionType.TerminalCommand;
		Track(item);
		return item;
	}
	/// <summary>Removes a template from its group and from the backing array.</summary>
	public void RemoveTemplate(PromptTemplateItemViewModel item)
	{
		ArgumentNullException.ThrowIfNull(item);

		var group = GroupFor(item.Type);
		var oldIndex = group.Items.IndexOf(item);
		item.PropertyChanged -= OnTemplatePropertyChanged;
		group.Items.Remove(item);
		base.RemoveItem(item);
		if (ReferenceEquals(group.SelectedItem, item))
		{
			group.SelectedItem = group.Items.Count == 0 ? null : group.Items[Math.Min(oldIndex, group.Items.Count - 1)];
		}
	}
	/// <inheritdoc />
	protected override PromptTemplateItemViewModel? TryCreateItem(JsonObject node) => node["id"] is null ? null : new(node);
	/// <inheritdoc />
	protected override PromptTemplateItemViewModel CreateNewItem(JsonObject node) => new(node);
	/// <inheritdoc />
	protected override string? Validate()
	{
		var templates = Items.OfType<PromptTemplateItemViewModel>().ToList();
		foreach (var template in templates)
		{
			if (string.IsNullOrWhiteSpace(template.Id))
			{
				return "Every prompt template needs a non-empty id.";
			}

			if (string.IsNullOrWhiteSpace(template.Name))
			{
				return $"Prompt template '{template.Id}' needs a name.";
			}

			if (string.IsNullOrWhiteSpace(template.Body))
			{
				return $"Prompt template '{template.Id}' needs a body.";
			}
		}
		return templates.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != templates.Count ? "Prompt template ids must be unique." : null;
	}
	private void Track(PromptTemplateItemViewModel item)
	{
		item.PropertyChanged -= OnTemplatePropertyChanged;
		item.PropertyChanged += OnTemplatePropertyChanged;
		InsertInMasterOrder(GroupFor(item.Type), item);
		SelectedGroup = GroupFor(item.Type);
		SelectedGroup.SelectedItem = item;
	}
	private void OnTemplatePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (sender is not PromptTemplateItemViewModel item
			|| e.PropertyName != nameof(PromptTemplateItemViewModel.Type))
		{
			return;
		}

		var oldGroup = Prompts.Items.Contains(item) ? Prompts : ShellCommands;
		var oldIndex = oldGroup.Items.IndexOf(item);
		var wasSelected = ReferenceEquals(oldGroup.SelectedItem, item);

		oldGroup.Items.Remove(item);

		if (wasSelected)
		{
			oldGroup.SelectedItem = oldGroup.Items.Count == 0
				? null
				: oldGroup.Items[Math.Min(oldIndex, oldGroup.Items.Count - 1)];
		}

		var newGroup = GroupFor(item.Type);
		InsertInMasterOrder(newGroup, item);
		SelectedGroup = newGroup;
		newGroup.SelectedItem = item;
	}
	private void InsertInMasterOrder(PromptTemplateGroupViewModel group, PromptTemplateItemViewModel item)
	{
		var masterIndex = Items.IndexOf(item);
		var groupIndex = group.Items.Count(existing => Items.IndexOf(existing) < masterIndex);
		group.Items.Insert(groupIndex, item);
	}
	private PromptTemplateGroupViewModel GroupFor(PromptActionType type) => PromptActionPolicy.Normalize(type) == PromptActionType.TerminalCommand ? ShellCommands : Prompts;
	private void DetachTypedItems()
	{
		foreach (var item in Items.OfType<PromptTemplateItemViewModel>())
		{
			item.PropertyChanged -= OnTemplatePropertyChanged;
		}
	}
}
