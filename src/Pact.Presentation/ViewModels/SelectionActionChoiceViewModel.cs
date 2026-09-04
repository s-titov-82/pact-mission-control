using Pact.Core.Prompting;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// One entry in the selection-action list. The list mixes selectable actions with
/// non-selectable group headers, so <paramref name="IsSelectable"/> distinguishes them.
/// </summary>
/// <param name="Name">Text shown in the list.</param>
/// <param name="IsSelectable">Whether this entry can be chosen; <see langword="false"/> for headers.</param>
/// <param name="Template">
/// Template applied to the selection, or <see langword="null"/> to send the selection unchanged.
/// </param>
public sealed record SelectionActionChoiceViewModel(
	string Name,
	bool IsSelectable,
	PromptTemplateRecord? Template = null)
{
	/// <summary>Whether this entry sends the selection as-is, with no template applied.</summary>
	public bool IsRaw => IsSelectable && Template is null;

	/// <summary>The built-in entry that sends the selection unchanged.</summary>
	public static SelectionActionChoiceViewModel Raw { get; } = new("Raw", true);

	/// <summary>Creates a non-selectable group header.</summary>
	public static SelectionActionChoiceViewModel Header(string name) => new(name, false);

	/// <summary>Creates a selectable entry that applies <paramref name="template"/>.</summary>
	public static SelectionActionChoiceViewModel ForTemplate(PromptTemplateRecord template)
	{
		ArgumentNullException.ThrowIfNull(template);

		return new(template.Name, true, template);
	}
}