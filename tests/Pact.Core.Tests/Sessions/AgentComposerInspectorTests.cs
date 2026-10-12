using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class AgentComposerInspectorTests
{
	[Test]
	public void Unknown_styling_in_empty_row_is_not_empty_evidence()
	{
		var screen = TerminalScreenSnapshot.FromRows([
			new("› ", false, null), new("gpt-6 · 100% context left", false, [])], 80, true);
		AgentComposerInspector.Inspect(screen, '›', false, []).ShouldNotBeNull().IsEmpty.ShouldBeNull();
	}

	[TestCase('❯')]
	[TestCase('›')]
	public void Current_numbered_permission_frame_is_a_question_not_a_composer(char marker)
	{
		var screen = TerminalScreenSnapshot.FromRows([
			new("Allow this command?", false, []), new($"{marker} 1. Yes", false, []),
			new("  2. No", false, []), new("Press enter to confirm · esc to cancel", false, [])], 80, true);
		var profile = marker == '❯' ? (IAgentScreenProfile)ClaudeScreenProfile.Instance : CodexScreenProfile.Instance;
		var verdict = profile.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		verdict.PromptIsEmpty.ShouldBeNull();
	}

	[Test]
	public void Old_menu_before_new_composer_does_not_request_input()
	{
		var screen = TerminalScreenSnapshot.FromRows([
			new("❯ 1. Yes", false, []), new("  2. No", false, []), new("Enter to select", false, []),
			new("Worked for 1s", false, []), new(Rule, false, []), new("❯ hint", false, [new(2, 4)]), new(Rule, false, [])], 80, true);
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	[Test]
	public void Lower_interrupt_hint_requires_current_spinner_chrome()
	{
		var screen = TerminalScreenSnapshot.FromRows([
			new("✳ Рассуждаю", false, []), new(Rule, false, []), new("❯ hint", false, [new(2, 4)]),
			new(Rule, false, []), new("esc to interrupt", false, [])], 80, true);
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	private const string Rule = "────────────────────────────────────────────────";
}
