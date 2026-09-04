using System.Text.RegularExpressions;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

/// <summary>
/// Fixes the contract the per-agent profiles are written against: what the pending-question
/// state and the composer reading mean, and how they rank against the existing markers. The
/// profile here is scripted rather than real, so these hold whatever Claude's and Codex's
/// patterns become.
/// </summary>
public sealed partial class AgentScreenProfileContractTests
{
	// Partial because [GeneratedRegex] requires it on the declaring type and its containers.
	private sealed partial class ScriptedProfile : AgentScreenProfileBase
	{
		public const string TrustMarker = "❯ 1. Yes, I trust this folder";

		protected override string[] ScrollMarkers => ["Jump to bottom (ctrl+End)"];

		protected override string[] ResumeSessionMarkers => ["Resume a previous session"];

		protected override string TrustRequestMarker => TrustMarker;

		protected override Regex InterruptedRegex => InterruptedRx();

		protected override Regex WorkingRegex => WorkingRx();

		protected override Regex WorkedForRegex => WorkedForRx();

		protected override Regex LastMessageRegex => LastMessageRx();

		protected override Regex InputRequestedRegex => InputRequestedRx();

		protected override TerminalPromptEvidence InspectPrompt(string window, int promptAt)
		{
			var content = window[(promptAt + 1)..];
			return new(
				PromptFound: true,
				BoundaryFound: true,
				NonWhitespaceCharacterCount: content.Count(character => !char.IsWhiteSpace(character)),
				SeparatorSharesLogicalLine: false);
		}

		[GeneratedRegex(@"(?<descr>WORKING)", RegexOptions.RightToLeft)]
		private static partial Regex WorkingRx();

		[GeneratedRegex(@"(?<descr>DONE)", RegexOptions.RightToLeft)]
		private static partial Regex WorkedForRx();

		[GeneratedRegex(@"(?<descr>INTERRUPTED)", RegexOptions.RightToLeft)]
		private static partial Regex InterruptedRx();

		[GeneratedRegex(@"(?<descr>MESSAGE)", RegexOptions.RightToLeft)]
		private static partial Regex LastMessageRx();

		[GeneratedRegex(@"(?<descr>QUESTION)", RegexOptions.RightToLeft)]
		private static partial Regex InputRequestedRx();

	}

	[Test]
	[TestCase("> ", true)]
	[TestCase("> What is your name?", false)]
	[TestCase("", null)]
	public void The_composer_reading_is_carried_on_every_verdict(string prompt, bool? promptIsEmpty)
	{
		var profile = new ScriptedProfile();
		profile.Classify($"WORKING\n{prompt}").PromptIsEmpty.ShouldBe(promptIsEmpty);
		profile.Classify($"DONE\n{prompt}").PromptIsEmpty.ShouldBe(promptIsEmpty ?? true);
		profile.Classify($"nothing familiar{prompt}").PromptIsEmpty.ShouldBe(promptIsEmpty);
	}

	[Test]
	public void Work_in_progress_still_outranks_a_finished_marker()
	{
		ScriptedProfile profile = new();

		profile.Classify("DONE\nWORKING\n> ").State.ShouldBe(TerminalScreenVerdictState.Busy);
	}

	[Test]
	public void A_pending_question_outranks_working_and_done_markers()
	{
		var profile = new ScriptedProfile();

		var verdict = profile.Classify("WORKING\nDONE\nQUESTION> ");

		verdict.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		verdict.Description.ShouldBe("QUESTION");
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	[Test]
	public void A_trust_request_carries_the_shared_description()
	{
		var profile = new ScriptedProfile();

		var verdict = profile.Classify($"anything\n {ScriptedProfile.TrustMarker} \n something");
		verdict.Description.ShouldBe(AgentScreenProfileBase.TrustPromptDescription);
		verdict.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		verdict.PromptIsEmpty.ShouldBe(null);
	}

	[Test]
	public void A_scrolled_screen_answers_neither()
	{
		var profile = new ScriptedProfile();

		var verdict = profile.Classify("Jump to bottom (ctrl+End)\n> ");

		verdict.State.ShouldBe(TerminalScreenVerdictState.Unknown);
		// A scrolled view shows history, so its composer reading is not the agent's current one.
		verdict.PromptIsEmpty.ShouldBeNull();
	}

	[Test]
	public void A_profile_that_recognizes_nothing_keeps_todays_behaviour()
	{
		// The default seams answer "no request, cannot tell", so an agent whose profile has not
		// been taught the new facts classifies exactly as it does today.
		var profile = new ScriptedProfile();

		var verdict = profile.Classify("WORKING");

		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.PromptIsEmpty.ShouldBeNull();
	}
}
