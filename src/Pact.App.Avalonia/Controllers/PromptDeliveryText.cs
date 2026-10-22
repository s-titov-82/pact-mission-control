using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Controllers;

/// <summary>Words an undelivered prompt for a status message.</summary>
internal static class PromptDeliveryText
{
	/// <summary>Why a delivery did not land, or null when it did.</summary>
	public static string? DescribeUndelivered(PromptDeliveryResult result) => result.Outcome switch
	{
		PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Submitted => null,
		PromptDeliveryOutcome.RefusedQuestion when result.WriteAttempted =>
			$"a question opened after the text was pasted ({result.StatusLine}); it was not submitted",
		PromptDeliveryOutcome.RefusedQuestion => $"waiting for an answer ({result.StatusLine}); nothing was sent",
		PromptDeliveryOutcome.RefusedLocked => "locked by a running scenario; nothing was sent",
		PromptDeliveryOutcome.NotRunning => $"not running ({result.StatusLine}); nothing was sent",
		PromptDeliveryOutcome.PasteNotShown => "the pasted text never showed, so it was not submitted",
		PromptDeliveryOutcome.Unconfirmed => "sent, but the agent was not seen taking it",
		_ => $"delivery ended as {result.Outcome}"
	};
}
