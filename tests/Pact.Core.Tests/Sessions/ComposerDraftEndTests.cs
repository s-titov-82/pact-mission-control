using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class ComposerDraftEndTests
{
	[Test]
	public void An_empty_codex_composer_is_clicked_right_after_its_prompt()
	{
		var screen = Codex(new TerminalScreenRow("› ", false, []));

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(3, 2));
	}

	[Test]
	public void A_dim_placeholder_is_not_draft_text()
	{
		var screen = Codex(new TerminalScreenRow("› Ask Codex to do anything", false, [new(2, 24)]));

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(3, 2));
	}

	[Test]
	public void A_draft_is_clicked_one_cell_after_its_last_character()
	{
		var screen = Codex(new TerminalScreenRow("› review this", false, []));

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(14, 2));
	}

	[Test]
	public void A_draft_over_several_rows_is_clicked_on_its_last_row()
	{
		var screen = Codex(new TerminalScreenRow("› first line", false, []), new("  second", false, []));

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(9, 3));
	}

	[Test]
	public void A_claude_draft_between_rules_is_found()
	{
		var screen = TerminalScreenSnapshot.FromRows(
			[new("Worked for 1s", false, []), new(Rule, false, []), new("❯ fix it", false, []), new(Rule, false, [])],
			80,
			true);

		ClaudeScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(9, 3));
	}

	[Test]
	public void A_draft_reaching_the_last_column_is_clicked_on_that_column()
	{
		var screen = TerminalScreenSnapshot.FromRows(
			[new("› abcdefgh", false, []), new(Footer, false, [])],
			10,
			true);

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBe(new TerminalCellPosition(10, 1));
	}

	[Test]
	public void A_question_has_no_draft_end()
	{
		var screen = TerminalScreenSnapshot.FromRows(
			[new("Allow this command?", false, []), new("› 1. Yes", false, []),
			new("  2. No", false, []), new("Press enter to confirm · esc to cancel", false, [])],
			80,
			true);

		CodexScreenProfile.Instance.FindDraftEnd(screen).ShouldBeNull();
	}

	[Test]
	public void A_profile_without_a_composer_has_no_draft_end()
	{
		var screen = Codex(new TerminalScreenRow("› review this", false, []));

		((IAgentScreenProfile)PwshScreenProfile.Instance).FindDraftEnd(screen).ShouldBeNull();
	}

	private static TerminalScreenSnapshot Codex(params TerminalScreenRow[] composer) =>
		TerminalScreenSnapshot.FromRows(
			[new("Worked for 1s", false, []), .. composer, new(Footer, false, [])],
			80,
			true);

	private const string Footer = "  GPT-6.1-Sol medium · main · Context 30% used";
	private const string Rule = "────────────────────────────────────────────────";
}
