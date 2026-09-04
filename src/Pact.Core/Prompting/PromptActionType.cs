namespace Pact.Core.Prompting;

/// <summary>
/// How a template's rendered text is delivered to a terminal session.
/// </summary>
public enum PromptActionType
{
	/// <summary>Text is typed into the agent's composer as a prompt.</summary>
	Prompt,

	/// <summary>Text is sent as a shell command line, for plain shell sessions.</summary>
	TerminalCommand,

	/// <summary>
	/// Legacy value retained only so existing <c>prompt-templates.json</c> files keep
	/// deserializing. It carries no distinct runtime behavior and is normalized to
	/// <see cref="Prompt"/>; do not write it for new templates.
	/// </summary>
	SelectionTemplate
}