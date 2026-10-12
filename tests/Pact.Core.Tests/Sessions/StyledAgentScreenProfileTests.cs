using System.Collections.Immutable;
using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class StyledAgentScreenProfileTests
{
	[TestCase(AgentKind.Claude, "suggestion", true, true)]
	[TestCase(AgentKind.Codex, "suggestion", true, true)]
	[TestCase(AgentKind.Claude, "draft", false, false)]
	[TestCase(AgentKind.Codex, "draft", false, false)]
	[TestCase(AgentKind.Claude, "", false, true)]
	[TestCase(AgentKind.Codex, "", false, true)]
	[TestCase(AgentKind.Claude, "draft", null, null)]
	[TestCase(AgentKind.Codex, "draft", null, null)]
	public void Composer_uses_dim_evidence_independently_of_work(AgentKind kind, string input, bool? dim, bool? expected)
	{
		ArgumentNullException.ThrowIfNull(input);
		var verdict = AgentScreenProfileSelector.ForKind(kind).Classify(Screen(kind, input, dim));
		verdict.PromptIsEmpty.ShouldBe(expected);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Codex)]
	public void Busy_with_dim_composer_remains_busy(AgentKind kind)
	{
		var verdict = AgentScreenProfileSelector.ForKind(kind).Classify(Screen(kind, "suggestion", true, busy: true));
		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Codex)]
	public void Draft_work_markers_do_not_override_current_idle_chrome(AgentKind kind)
	{
		var verdict = AgentScreenProfileSelector.ForKind(kind).Classify(Screen(kind, "Working (1s · esc to interrupt)", false));
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.PromptIsEmpty.ShouldBe(false);
	}

	[Test]
	public void Wrapped_claude_draft_is_not_a_dim_suggestion()
	{
		var snapshot = TerminalScreenSnapshot.FromRows([
			new("Worked for 1s", false, []), new(Rule, false, []),
			new("❯ hint", false, [new(2, 4)]), new("actual draft", true, []), new(Rule, false, [])], 80, true);
		ClaudeScreenProfile.Instance.Classify(snapshot).PromptIsEmpty.ShouldBe(false);
	}

	[Test]
	public void Cropped_claude_border_does_not_claim_empty_composer()
	{
		var snapshot = TerminalScreenSnapshot.FromRows([new("❯ hint", false, [new(2, 4)])], 80, true);
		ClaudeScreenProfile.Instance.Classify(snapshot).PromptIsEmpty.ShouldBeNull();
	}

	private const string Rule = "────────────────────────────────────────────────";
	[TestCase(true)]
	[TestCase(false)]
	public void Codex_draft_with_separator_is_not_footer_evidence(bool hasFooter)
	{
		var rows = ImmutableArray.CreateBuilder<TerminalScreenRow>();
		rows.Add(new("Worked for 1s", false, []));
		rows.Add(new("› ", false, []));
		rows.Add(new("draft · text", false, []));
		if (hasFooter)
		{
			rows.Add(new("GPT-6.1-Sol medium · main · Context 30% used", false, []));
		}
		var verdict = CodexScreenProfile.Instance.Classify(TerminalScreenSnapshot.FromRows(rows.ToImmutable(), 80, true));
		verdict.PromptIsEmpty.ShouldBe(hasFooter ? false : null);
	}

	[TestCase(true, true)]
	[TestCase(false, false)]
	public void Codex_composer_after_short_exchange_reads_current_prompt(bool dim, bool expected)
	{
		const string placeholder = "Ask Codex to do anything";
		var screen = TerminalScreenSnapshot.FromRows([
			new("› previous request", false, []), new("", false, []),
			new("• Done.", false, []), new("", false, []),
			new("Worked for 12m 40s • 13:06", false, []), new("", false, []),
			new($"› {placeholder}", false, dim ? [new(2, placeholder.Length)] : []), new("", false, []),
			new("GPT-6.1-Sol medium · D:/repo · main · Context 30% used · weekly 84% left", false, []),
			new("? for shortcuts", false, [])], 120, true);
		var verdict = CodexScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.PromptIsEmpty.ShouldBe(expected);
	}

	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Codex, true)]
	[TestCase(AgentKind.Claude, false)]
	[TestCase(AgentKind.Codex, false)]
	public void Historical_trust_or_resume_menu_does_not_override_current_composer(AgentKind kind, bool trust)
	{
		var historical = kind == AgentKind.Claude
			? trust ? "❯ 1. Yes, I trust this folder" : "Ctrl-A to show all projects"
			: trust ? "Do you trust the contents of this directory?" : "Resume a previous session";
		var current = Screen(kind, "", false);
		var screen = TerminalScreenSnapshot.FromRows([new(historical, false, []), .. current.Rows], 80, true);
		var verdict = AgentScreenProfileSelector.ForKind(kind).Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	private static TerminalScreenSnapshot Screen(AgentKind kind, string input, bool? dim, bool busy = false)
	{
		var marker = kind == AgentKind.Claude ? '❯' : '›';
		var rows = ImmutableArray.CreateBuilder<TerminalScreenRow>();
		rows.Add(new(busy ? "✻ Working… (1s · esc to interrupt)" : "Worked for 1s", false, []));
		if (kind == AgentKind.Claude)
		{
			rows.Add(new(Rule, false, []));
		}
		rows.Add(new($"{marker} {input}", false,
			dim is null ? null : dim == true && input.Length > 0 ? [new(2, input.Length)] : []));
		if (kind == AgentKind.Claude)
		{
			rows.Add(new(Rule, false, []));
		}
		rows.Add(new("  GPT-6.1-Sol medium · main · Context 30% used", false, []));
		return TerminalScreenSnapshot.FromRows(rows.ToImmutable(), 80, true);
	}
}
