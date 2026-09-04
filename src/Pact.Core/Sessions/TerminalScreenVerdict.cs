namespace Pact.Core.Sessions;

/// <summary>Structural, content-free evidence used to classify an agent composer.</summary>
/// <param name="PromptFound">Whether the profile found its prompt glyph.</param>
/// <param name="BoundaryFound">Whether the profile found the composer boundary after that glyph.</param>
/// <param name="NonWhitespaceCharacterCount">
/// Number of non-whitespace characters between the prompt and boundary.
/// </param>
/// <param name="SeparatorSharesLogicalLine">
/// Whether xterm reconstructed the separator onto the same logical line as the prompt.
/// </param>
public sealed record TerminalPromptEvidence(
	bool PromptFound,
	bool BoundaryFound,
	int NonWhitespaceCharacterCount,
	bool SeparatorSharesLogicalLine)
{
	/// <summary>
	/// Returns whether the recognized composer is empty, or null when its boundary was not found.
	/// </summary>
	public bool? IsEmpty => BoundaryFound ? NonWhitespaceCharacterCount == 0 : null;
}

/// <summary>Result of classifying the latest stable terminal screen.</summary>
/// <param name="State">State inferred from the screen.</param>
/// <param name="Description">Short classifier text suitable for display.</param>
/// <param name="LastMessage">
/// The agent's most recent message extracted from the same screen. Empty when the profile
/// cannot recognize a message; callers must not replace it with arbitrary screen text.
/// </param>
/// <param name="PromptIsEmpty">
/// Whether the agent's input field is on screen and blank. <see langword="false"/> means it
/// holds unsent text, which is what tells a dropped submit from a dropped paste.
/// <see langword="null"/> means the screen cannot answer — scrolled, no composer, or a profile
/// that does not recognize this agent's input field — and must never be read as either answer.
/// This is deliberately a value rather than a state: what the composer holds is orthogonal to
/// whether the agent is working, and a working agent can show an empty field.
/// </param>
/// <param name="PromptEvidence">
/// Structural prompt evidence suitable for diagnostics without exposing terminal text.
/// </param>
public sealed record TerminalScreenVerdict(
	TerminalScreenVerdictState State,
	string Description = "",
	string LastMessage = "",
	bool? PromptIsEmpty = null,
	TerminalPromptEvidence? PromptEvidence = null);

/// <summary>
/// Describes what a stable terminal screen reveals about the current activity.
/// </summary>
public enum TerminalScreenVerdictState
{
	/// <summary>
	/// The screen contains evidence that the terminal is processing an activity.
	/// </summary>
	Busy,

	/// <summary>
	/// The screen contains evidence that the latest activity completed.
	/// </summary>
	Done,

	/// <summary>
	/// The screen does not contain enough evidence to determine activity state.
	/// </summary>
	Unknown,

	/// <summary>
	/// The agent is waiting for a human answer: a trust question, a permission question, or
	/// another actionable selection list. It is neither working nor finished, and it outranks
	/// both, because nothing may be written into a terminal holding someone else's question.
	/// </summary>
	InputRequested
}
