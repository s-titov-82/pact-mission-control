namespace Pact.Core.Sessions;

/// <summary>How an attempt to deliver a prompt into a terminal ended.</summary>
public enum PromptDeliveryOutcome
{
	/// <summary>A new activity cycle began after the submit: the agent took it.</summary>
	Confirmed,

	/// <summary>Input was written, but no activity followed and the screen never explained why.</summary>
	Written,

	/// <summary>The agent is already working, so nothing was written.</summary>
	BlockedByBusy,

	/// <summary>A question stopped the send; see <see cref="PromptDeliveryResult.WriteAttempted"/>.</summary>
	BlockedByInputRequest,

	/// <summary>The composer already held unsent text, so nothing was written.</summary>
	BlockedByPendingInput
}

/// <summary>Outcome of one prompt delivery, with the status line that explains a refusal.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="StatusLine">
/// The pending question quoted from the screen when <paramref name="Outcome"/> is
/// <see cref="PromptDeliveryOutcome.BlockedByInputRequest"/>; empty otherwise.
/// </param>
/// <param name="WriteAttempted">
/// Whether anything reached the terminal before the outcome was decided. This remains true
/// when a question appears after the paste because the trigger may still be in the composer.
/// </param>
/// <param name="SubmitAttempted">Whether the submit keystroke was written.</param>
public readonly record struct PromptDeliveryResult(
	PromptDeliveryOutcome Outcome,
	string StatusLine = "",
	bool WriteAttempted = false,
	bool SubmitAttempted = false)
{
	/// <summary>Whether the prompt was submitted with nothing refusing it.</summary>
	public bool IsSent =>
		SubmitAttempted
		&& Outcome is PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Written;

	/// <summary>Whether the agent was observed to take the prompt.</summary>
	public bool IsConfirmed => Outcome == PromptDeliveryOutcome.Confirmed;
}

/// <summary>Whether a launched session can receive a prompt, and what blocks it when it cannot.</summary>
/// <param name="IsReady">The session's screen settled into a ready state.</param>
/// <param name="StatusLine">The pending question, or why nothing was concluded; empty when ready.</param>
public readonly record struct SessionReadiness(bool IsReady, string StatusLine);
