using Pact.Core.Prompting;
namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// A selectable delivery mode in the prompt template editor.
/// </summary>
/// <param name="Value">Action type this option sets.</param>
/// <param name="Label">Text shown in the picker.</param>
public sealed record PromptTemplateTypeOption(PromptActionType Value, string Label)
{
	/// <summary>
	/// The offered options. The legacy <see cref="PromptActionType.SelectionTemplate"/> is
	/// deliberately absent: it carries no distinct behavior and must not be written to new
	/// templates.
	/// </summary>
	public static IReadOnlyList<PromptTemplateTypeOption> All { get; } =
	[new(PromptActionType.Prompt, "Prompt"), new(PromptActionType.TerminalCommand, "Shell command")];
}