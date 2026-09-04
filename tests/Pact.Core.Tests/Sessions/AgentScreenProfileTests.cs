using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class AgentScreenProfileTests
{
	[Test]
	[TestCase("Cogitating… (12s · esc to interrupt)", "Cogitating")]
	[TestCase("Cogitating… (12s)", "Cogitating")]
	[TestCase("Working… (1m 12s)", "Working")]
	[TestCase("Churned for 12s · 1 shell still running", "1 shell still running")]
	[TestCase("Caramelized for 12s · 1 monitor still running", "1 monitor still running")]
	[TestCase("✻ Baked for 11m 22s · 2 shells still running", "2 shells still running")]
	[TestCase("Sautéed for 12s · Waiting for 1 background agent", "Waiting for 1 background agent")]
	[TestCase("Beboppin'… (2m 10s)", "Beboppin'")]
	[TestCase("Restructuring build (CPM/slnx/props)… (1m 12s)", "Restructuring build (CPM/slnx/props)")]
	[TestCase("✻ Делаю GitLab v2… (1m 48s · ↓ 3.4k tokens · thinking with medium effort)", "Делаю GitLab v2")]
	public void Claude_busy_screen_is_busy(string marker, string descr)
	{
		var screen = $"● Reading files...\n✻ {marker}\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.Description.ShouldBe(descr);
	}

	[Test]
	[TestCase("Worked for 2m 30s", "Worked for 2m")]
	[TestCase("Cooked for 12s", "Cooked for 12s")]
	[TestCase("Sautéed for 3s", "Sautéed for 3s")]
	[TestCase("Brewed for 45s", "Brewed for 45s")]
	public void Claude_completion_summary_above_prompt_is_done(string marker, string descr)
	{
		var screen = $"● Done. Updated 3 files.\n✻ {marker}\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe(descr);
	}

	[TestCase("⎿ Interrupted", "Interrupted")]
	[TestCase("✻ 529 Overloaded", "529 Overloaded")]
	[TestCase("✻ Unable to connect to API (ConnectionRefused)", "Unable to connect to API")]
	public void Claude_terminate_summary_above_prompt_is_done(string marker, string descr)
	{
		var screen = $"● Done. Updated 3 files.\n✻ {marker}\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe(descr);
	}

	[TestCase("───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n [ ] Select options", "[ ] Select")]
	[TestCase("Some options selectors √ Submit \n", "√ Submit")]
	[TestCase("Some options selectors\n\n option 1\n\n option 2\n\n Enter to select \n", "Enter to select")]
	public void Claude_question_summary_detected(string marker, string descr)
	{
		var screen = $"● Done. Updated 3 files.\n✻ {marker}\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		verdict.Description.ShouldBe(descr);
	}

	[Test]
	public void Claude_marker_closest_to_prompt_decides_busy()
	{
		var screen = "✻ Cooked for 12s\nsome older output\n✻ Cogitating… (esc to interrupt · 3s)\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.Description.ShouldBe("Cogitating");
	}

	[Test]
	public void Claude_marker_closest_to_prompt_decides_done()
	{
		var screen = "transcript quoting esc to interrupt hint\n✻ Worked for 5s\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe("Worked for 5s");
	}

	[Test]
	public void Claude_tail_anchors_to_last_prompt_char()
	{
		// The decoy '>' sits more than 1000 characters before the real input
		// box, so a search anchored to the first match window misses the
		// completion summary next to the actual prompt.
		var history = new string('x', 1100);
		var screen = $"$ dotnet build > build.log\n{history}\n✻ Worked for 5s\n╭──╮\n│ > │\n╰──╯";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe("Worked for 5s");
	}

	[Test]
	public void Claude_transitional_screen_is_unknown()
	{
		var verdict = ClaudeScreenProfile.Instance.Classify("partial redraw text");
		verdict.State.ShouldBe(TerminalScreenVerdictState.Unknown);
		verdict.Description.ShouldBeEmpty();
	}

	[Test]
	public void Claude_last_assistant_message_is_extracted()
	{
		var verdict = ClaudeScreenProfile.Instance.Classify(
			ClaudeScreenWithAssistantText("Done. I updated the plan and re-ran the tests."));

		verdict.LastMessage.ShouldContain("re-ran the tests");
	}

	[TestCase("")]
	[TestCase(" [Pasted text #1 +16 lines]")]
	[TestCase(" Some text")]
	public void Claude_treats_any_visible_prompt_as_empty(string prompt)
	{
		var verdict = ClaudeScreenProfile.Instance.Classify($"● Some info \n ✻ Sautéed for 3s\n───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n>{prompt}\n────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n╰──╯");
		verdict.PromptIsEmpty.ShouldBe(true);
	}

	[Test]
	public void Clude_reaal_screen_with_empty_prompt_is_done()
	{
		var screen = "  Called pravotech\n\nВижу подключение в логах: > Client (claude-code 2.1.243) вызвал server/discover и tools/list, обработчики отработали за 7 мс, отказов авторизации нет. До этого в 08:22:45 — проверка токена самим скриптом (tools/list без имени клиента, 212 мс — холодное соединение, ретрай не понадобился).\n\nЗаписи CookieTokenAuth ... Access token is missed — штатный шум: браузерная схема не находит куки и уступает MCP-фильтру, дальше решает Bearer.\n\nТак что первый прогон инструкции целиком прошёл: скрипт получил рабочий адрес, проверил токен, записал сервер, клиент подключился и увидел инструменты. Осталось из инструкции — второй профиль (если выбирал только один) и разделы для Codex/Cursor/Hermes, если захочешь проверить и их.\n\n✻ Baked for 27s\n\n───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n>\n───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n  Opus 5 medium · Context 29% left · 5h 49% (→12:40) · weekly 89% (→31.08 12:00)";
		var verdict = ClaudeScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe("Baked for 27s");
		verdict.PromptIsEmpty.GetValueOrDefault().ShouldBeTrue();
	}

	[Test]
	public void Claude_wrapped_separator_after_an_empty_prompt_is_empty()
	{
		var screen = "✻ Baked for 3s\n>          ──────────────────────────────";

		var verdict = ClaudeScreenProfile.Instance.Classify(screen);

		verdict.PromptIsEmpty.ShouldBe(true);
		verdict.PromptEvidence.ShouldNotBeNull().BoundaryFound.ShouldBeTrue();
		verdict.PromptEvidence.NonWhitespaceCharacterCount.ShouldBe(0);
	}

	[Test]
	public void Claude_prompt_without_a_separator_is_still_treated_as_empty()
	{
		var verdict = ClaudeScreenProfile.Instance.Classify("✻ Baked for 3s\n>");

		verdict.PromptIsEmpty.ShouldBe(true);
		verdict.PromptEvidence.ShouldNotBeNull().BoundaryFound.ShouldBeTrue();
	}

	[Test]
	public void Claude_ignores_text_that_looks_like_pending_input()
	{
		var screen = "✻ Baked for 3s\n> compare a >\n──────────────────────────────";

		var verdict = ClaudeScreenProfile.Instance.Classify(screen);

		verdict.PromptIsEmpty.ShouldBe(true);
		verdict.PromptEvidence.ShouldNotBeNull().NonWhitespaceCharacterCount.ShouldBe(0);
	}

	[Test]
	public void Claude_unrecognised_screen_has_no_last_message()
	{
		var verdict = ClaudeScreenProfile.Instance.Classify("garbled ▒▒▒ nothing familiar here");

		verdict.LastMessage.ShouldBeEmpty();
	}

	[Test]
	public void Codex_busy_screen_is_busy()
	{
		var screen = "Working (12s • Esc to interrupt)";
		var verdict = CodexScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Busy);
		verdict.Description.ShouldBe("Working");
	}

	[Test]
	[TestCase("──────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────", "")]
	[TestCase("Worked for 1m 3s", "Worked for 1m")]
	public void Codex_completion_summary_above_prompt_is_done(string marker, string descr)
	{
		var screen = $"● Done. Updated 3 files.\n{marker}\n❯ ";

		var verdict = CodexScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe(descr);
	}

	[Test]
	public void Codex_done_screen_keeps_inferred_empty_composer()
	{
		var verdict = CodexScreenProfile.Instance.Classify("\n──────────────────────────────\n❯");

		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.PromptIsEmpty.ShouldBe(true);
		verdict.PromptEvidence.ShouldBeNull();
	}

	[Test]
	public void Codex_structured_question_requests_input()
	{
		const string screen =
			"""
			Question 1/1 (1 unanswered)
			  Какой режим повторной доставки Codex-подсказки использовать?

			  › 1. Один повтор (Recommended)  Повторить отправку один раз, если новая activity не появилась.
			    2. Повторять до Busy          Продолжать попытки до появления подтверждённой активности.
			    3. Без повторов               Оставить доставку неподтверждённой после первой попытки.
			    4. None of the above          Optionally, add details in notes (tab).

			  tab to add notes | enter to submit answer | esc to interrupt
			""";

		var verdict = CodexScreenProfile.Instance.Classify(screen);

		verdict.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		verdict.Description.ShouldBe("Какой режим повторной доставки Codex-подсказки использовать?");
		verdict.PromptIsEmpty.ShouldBeNull();
	}

	[Test]
	public void Codex_last_assistant_message_is_extracted()
	{
		var verdict = CodexScreenProfile.Instance.Classify(
			CodexScreenWithAssistantText("Applied the patch to three files."));

		verdict.LastMessage.ShouldContain("Applied the patch");
	}

	[Test]
	public void Last_message_extraction_keeps_state_and_description()
	{
		var verdict = ClaudeScreenProfile.Instance.Classify(ClaudeScreenWithAssistantText("Done."));

		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBe("Sautéed for 3s");
	}

	[Test]
	public void Pwsh_prompt_on_last_line_is_done()
	{
		var screen = "some output\nPS D:\\Personal\\Pact> ";
		var verdict = PwshScreenProfile.Instance.Classify(screen);
		verdict.State.ShouldBe(TerminalScreenVerdictState.Done);
		verdict.Description.ShouldBeEmpty();
		verdict.LastMessage.ShouldBeEmpty();
	}

	[Test]
	public void Pwsh_without_prompt_is_unknown()
	{
		var verdict = PwshScreenProfile.Instance.Classify("building...\n[1/4] compile");
		verdict.State.ShouldBe(TerminalScreenVerdictState.Unknown);
		verdict.Description.ShouldBeEmpty();
	}

	[Test]
	public void Quiescence_profile_always_done()
	{
		var verdict1 = QuiescenceScreenProfile.Instance.Classify("anything");
		verdict1.State.ShouldBe(TerminalScreenVerdictState.Done);
		var verdict2 = QuiescenceScreenProfile.Instance.Classify(string.Empty);
		verdict2.State.ShouldBe(TerminalScreenVerdictState.Done);
	}

	[Test]
	[TestCase(AgentKind.Claude, typeof(ClaudeScreenProfile))]
	[TestCase(AgentKind.Codex, typeof(CodexScreenProfile))]
	[TestCase(AgentKind.Pwsh, typeof(PwshScreenProfile))]
	[TestCase(AgentKind.Hermes, typeof(QuiescenceScreenProfile))]
	[TestCase(AgentKind.Custom, typeof(QuiescenceScreenProfile))]
	public void ForKind_maps_agent_to_profile(AgentKind kind, Type expected) => AgentScreenProfileSelector.ForKind(kind).GetType().ShouldBe(expected);

	private static string ClaudeScreenWithAssistantText(string message) =>
		$"● Some info \n {message}\n✻ Sautéed for 3s\n╭──╮\n───────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n>\n────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────\n╰──╯";

	private static string CodexScreenWithAssistantText(string message) =>
		$"• Some info {message}\n\n─ Worked for 3s ────────────────────────────────────────────────────────────────────────────────────────\n› ";
}
