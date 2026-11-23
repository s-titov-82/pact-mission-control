using Pact.Core.Reviews;

namespace Pact.Presentation.Services.Reviews;

/// <summary>Builds MCP turn instructions for a review using the rules and mode captured at startup.</summary>
public sealed class ReviewMessageRenderer(string reviewRules, bool strict, string target)
{
	/// <summary>Renders a turn. The relay exposes the other participant's reply separately in previousTurn.</summary>
	public string Render(ReviewRole to, int iteration)
	{
		const string submit = "\n\nExchange replies only through Pact's MCP tools. Ignore any file or completion-marker transport instructions in legacy review criteria. Call `pact_submit_review_turn` with this message's `turnId` and your complete answer as `text`. Keep your terminal reply to one short line.";
		if (to == ReviewRole.Author)
		{
			return $"Review target:\n{target}\n\nRead the reviewer's findings in `previousTurn`. Verify each one against the target. Fix valid findings using your judgment and explain each change. Dispute invalid findings with evidence. Address every finding." + submit;
		}

		var task = iteration == 1
			? $"Review target:\n{target}\n\nInspect referenced artifacts directly, or review pasted text as supplied.\n\nReview rules:\n{reviewRules}"
			: "Read the author's reply in `previousTurn`. Recheck the current target and verify each fix or dispute. Report remaining or newly introduced issues.";
		var approval = strict
			? "Approve only when findings of every severity have been resolved or validly disputed."
			: "Approve once all critical and major findings are resolved or validly disputed. List any remaining minor or nit findings as advisory.";
		return task + "\n\nReport numbered findings with severity (critical, major, minor, or nit), location, problem, and suggested fix. "
			+ approval + " On approval, set `approved` to true and provide a final run summary with totals by severity, each finding and its resolution or accepted dispute in one or two sentences, and any open questions."
			+ submit;
	}
}
