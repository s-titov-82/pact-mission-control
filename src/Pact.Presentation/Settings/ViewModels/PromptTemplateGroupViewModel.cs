using System.Collections.ObjectModel;
using Pact.Core.Prompting;
namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Groups prompt templates by delivery mode in the settings window, so prompts and shell
/// commands are edited in separate lists.
/// </summary>
public sealed class PromptTemplateGroupViewModel : SettingsObservableObject
{

	/// <summary>
	/// Creates a group for one delivery mode. The type is normalized, so legacy values collapse
	/// into the group they actually behave as.
	/// </summary>
	internal PromptTemplateGroupViewModel(PromptActionType type, string label,
		Action<PromptTemplateItemViewModel, PromptTemplateItemViewModel> swapInFile)
	{
		Type = PromptActionPolicy.Normalize(type);
		Label = label;
		Order = new(Items, this, () => SelectedItem, (item, neighbor) =>
		{
			swapInFile(item, neighbor);
			Items.Move(Items.IndexOf(item), Items.IndexOf(neighbor));
		}, item => SelectedItem = item);
	}

	/// <summary>Changes the order within this delivery mode.</summary>
	public SettingsItemOrder<PromptTemplateItemViewModel> Order { get; }

	/// <summary>Normalized delivery mode this group holds.</summary>
	public PromptActionType Type { get; }

	/// <summary>Group heading.</summary>
	public string Label { get; }

	/// <summary>Templates in this group.</summary>
	public ObservableCollection<PromptTemplateItemViewModel> Items { get; } = [];

	/// <summary>Selected template, or <see langword="null"/> when none is selected.</summary>
	public PromptTemplateItemViewModel? SelectedItem { get; set => SetField(ref field, value); }
}
