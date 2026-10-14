using System.Collections.Concurrent;
using System.Diagnostics;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Runtime and backend ownership is transferred to SessionRuntimeCoordinator and exercised by its stop/disposal tests.")]
public sealed partial class SessionRuntimeCoordinatorTests
{
	[TestCase(false, PromptDeliveryOutcome.BlockedByPendingInput)]
	[TestCase(true, PromptDeliveryOutcome.Written)]
	[TestCase(null, PromptDeliveryOutcome.Written)]
	public async Task Styled_composer_uses_existing_delivery_gate(bool? dim, PromptDeliveryOutcome expected)
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;
		var snapshot = TerminalScreenSnapshot.FromRows([
			new("Worked for 1s", false, []), new("› hint", false, dim is null ? null : dim == true ? [new(2, 4)] : []),
			new("GPT-6.1-Sol medium · main · Context 30% used", false, [])], 80, true);
		var verdict = CodexScreenProfile.Instance.Classify(snapshot);
		var state = Idle() with { PromptIsEmpty = verdict.PromptIsEmpty };
		var result = await coordinator.WriteScenarioPromptAndSubmitAsync("session-1", "task-path", confirmDelivery: false, () => state, TestContext.CurrentContext.CancellationToken);
		result.Outcome.ShouldBe(expected);
		backend.InputWrites.Count.ShouldBe(expected == PromptDeliveryOutcome.Written ? 2 : 0);
	}
	private const string PastedTrigger = "\u001b[200~task-path\u001b[201~";
	private const string PlainEnter = "\r";
	private const string MouseTrackingOn = "\u001b[?1003;1006h";
	private const string RightClick = "\u001b[<2;1;1M\u001b[<2;1;1m";

	[Test]
	public void Active_session_snapshot_includes_only_runtimes_with_attached_controllers()
	{
		using SessionRuntimeCoordinator coordinator = CreateFastCoordinator();
		coordinator.GetOrCreateRuntime("live").AttachController(new TerminalController());
		coordinator.GetOrCreateRuntime("empty");

		var snapshot = coordinator.GetActiveSessionIds();
		coordinator.GetOrCreateRuntime("later").AttachController(new TerminalController());

		snapshot.ShouldBe(["live"]);
		coordinator.GetActiveSessionIds().ShouldBe(["later", "live"], ignoreOrder: true);
	}

	[Test]
	public async Task ActivateSessionAsync_SupersededActivation_DoesNotShowStaleSession()
	{
		RecordingHost host = new();
		SessionRuntimeCoordinator coordinator = new(host);
		var first = CreateSession("first");
		var second = CreateSession("second");
		TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

		var firstActivation = coordinator.ActivateSessionAsync(first, async (_, _) =>
		{
			entered.SetResult();
			await release.Task;
		}, NoScope, NoopSession, Noop, NoopSession);
		await entered.Task;
		var secondActivation = coordinator.ActivateSessionAsync(second, NoopSession, NoScope, NoopSession, Noop, NoopSession);
		release.SetResult();
		await Task.WhenAll(firstActivation, secondActivation);

		host.Shown.ShouldBe(["second"]);
		coordinator.ActiveSessionId.ShouldBe("second");
	}

	[Test]
	public async Task ActivateSessionAsync_Failure_RaisesStatusAndMarksFailed()
	{
		SessionRuntimeCoordinator coordinator = new(new RecordingHost());
		string? status = null;
		var markedFailed = false;
		coordinator.StatusMessage += (_, message) => status = message;

		await coordinator.ActivateSessionAsync(
			CreateSession("broken"),
			(_, _) => throw new InvalidOperationException("boom"),
			NoScope,
			NoopSession,
			Noop,
			(_, _) => { markedFailed = true; return Task.CompletedTask; });

		status.ShouldBe("Session failed: boom");
		markedFailed.ShouldBeTrue();
	}

	[Test]
	public async Task ActivateSessionAsync_ThrowingStatusMessageHandler_DoesNotPropagateAndStillMarksFailed()
	{
		SessionRuntimeCoordinator coordinator = new(new RecordingHost());
		var markedFailed = false;
		coordinator.StatusMessage += (_, _) => throw new InvalidOperationException("handler boom");

		await coordinator.ActivateSessionAsync(
			CreateSession("broken"),
			(_, _) => throw new InvalidOperationException("boom"),
			NoScope,
			NoopSession,
			Noop,
			(_, _) => { markedFailed = true; return Task.CompletedTask; });

		markedFailed.ShouldBeTrue();
	}

	[Test]
	[TestCase(false, true, "\n")]
	[TestCase(true, false, "\n")]
	public void RewriteInput_NonCodexOrInactiveMode_LeavesNewlineUntouched(bool active, bool codex, string input) => AgentKeyEncoder.RewriteTyped(input, codex ? AgentKind.Codex : AgentKind.Claude, active).ShouldBe(input);

	[Test]
	public void RewriteInput_CodexWin32Mode_EncodesShiftEnter() => AgentKeyEncoder.RewriteTyped("\n", AgentKind.Codex, true).ShouldBe(Win32InputEncoder.ShiftEnter);

	[Test]
	public async Task A_new_busy_cycle_confirms_delivery()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() => backend.InputWrites.Contains(PlainEnter) ? Busy(epoch: 2) : Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter]);
	}

	[Test]
	public async Task A_busy_session_refuses_delivery_without_writing()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync(kind: AgentKind.Codex);
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			static () => Busy(epoch: 1),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.BlockedByBusy);
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task A_programmatic_prompt_is_one_bulk_bracketed_paste()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		await coordinator.SendPromptAsync(
			CreateSession("session-1"),
			"first line\nsecond line",
			submit: false,
			startIfNeeded: false,
			enforceScenarioLock: false,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);

		backend.InputWrites.ShouldBe(
		[
			"\u001b[200~first line\nsecond line\u001b[201~"
		]);
	}

	[Test]
	public async Task A_submitted_prompt_is_pasted_then_entered_separately()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		await coordinator.SendPromptAsync(
			CreateSession("session-1"),
			"review this",
			submit: true,
			startIfNeeded: false,
			enforceScenarioLock: false,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);

		backend.InputWrites.ShouldBe(["\u001b[200~review this\u001b[201~", "\r"]);
	}

	[Test]
	public async Task A_submitted_prompt_to_codex_in_win32_mode_uses_the_encoded_enter()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync(win32InputMode: true, kind: AgentKind.Codex);
		await using var _ = controller;

		await coordinator.SendPromptAsync(
			CreateSession("session-1", AgentKind.Codex),
			"review this",
			submit: true,
			startIfNeeded: false,
			enforceScenarioLock: false,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);

		backend.InputWrites.ShouldBe(["\u001b[200~review this\u001b[201~", Win32InputEncoder.EnterKey]);
	}

	[Test]
	public async Task A_mouse_tracking_client_pastes_from_the_clipboard_and_is_entered_once_it_shows()
	{
		var (coordinator, backend, controller, clipboard) = await CreateClipboardPasteSessionAsync(
			"older text",
			clickOutput: "\u001b[37;3H\u001b[38;5;6m[Pasted Content 1200 chars]\u001b[0m");
		await using var _ = controller;

		await SendPromptAsync(coordinator, "review this\r\nplease", submit: true);

		clipboard.Writes.ShouldBe(["review this\r\nplease"]);
		backend.InputWrites.ShouldBe([RightClick, PlainEnter]);
	}

	[Test]
	public async Task A_clipboard_paste_is_entered_when_the_pasted_text_shows_wrapped()
	{
		var (coordinator, backend, controller, _) = await CreateClipboardPasteSessionAsync(
			string.Empty,
			clickOutput: "\u001b[38;3H> check the parser e\u001b[39;3Hdge cases");
		await using var _ = controller;

		await SendPromptAsync(coordinator, "check the parser edge cases", submit: true);

		backend.InputWrites.ShouldBe([RightClick, PlainEnter]);
	}

	[Test]
	public async Task Text_already_on_the_clipboard_is_not_written_again()
	{
		var (coordinator, backend, controller, clipboard) =
			await CreateClipboardPasteSessionAsync("selected\r\ntext");
		await using var _ = controller;

		await SendPromptAsync(coordinator, "selected\ntext", submit: false);

		clipboard.Writes.ShouldBeEmpty();
		backend.InputWrites.ShouldBe([RightClick]);
	}

	[Test]
	public async Task A_client_without_mouse_tracking_gets_a_bracketed_paste_and_the_clipboard_is_untouched()
	{
		var (coordinator, backend, controller, clipboard) =
			await CreateClipboardPasteSessionAsync("older text", mouseTracking: false);
		await using var _ = controller;

		await SendPromptAsync(coordinator, "review this", submit: false);

		clipboard.Writes.ShouldBeEmpty();
		backend.InputWrites.ShouldBe(["\u001b[200~review this\u001b[201~"]);
	}

	[Test]
	public async Task A_scenario_trigger_to_a_mouse_tracking_client_is_entered_after_its_paste_shows()
	{
		var (coordinator, backend, controller, _) =
			await CreateClipboardPasteSessionAsync(string.Empty, clickOutput: "> task-path");
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: false,
			static () => Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Written);
		backend.InputWrites.ShouldBe([RightClick, PlainEnter]);
	}

	// A session whose client reports the mouse in SGR encoding when mouseTracking is set, and
	// prints clickOutput, its pasted text, when it receives a right click.
	private static async Task<(
		SessionRuntimeCoordinator Coordinator,
		RecordingBackend Backend,
		TerminalController Controller,
		RecordingClipboard Clipboard)> CreateClipboardPasteSessionAsync(
		string clipboardText,
		string? clickOutput = null,
		bool mouseTracking = true)
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		var runtime = coordinator.GetOrCreateRuntime("session-1");
		if (mouseTracking)
		{
			runtime.InputModes.Scan(MouseTrackingOn);
		}

		RecordingClipboard clipboard = new(clipboardText);
		coordinator.ClientPasteClipboard = clipboard;
		backend.InputWritten = input =>
		{
			if (input == RightClick && clickOutput is not null)
			{
				runtime.AppendRecentOutput(clickOutput);
			}
		};
		return (coordinator, backend, controller, clipboard);
	}

	[Test]
	public async Task Typed_input_waits_until_a_paste_and_its_enter_are_written()
	{
		var (coordinator, backend, _) = await CreateAttachedSessionAsync();
		Task? typed = null;
		backend.InputWritten = input =>
		{
			if (typed is null && input.StartsWith("\u001b[200~", StringComparison.Ordinal))
			{
				typed = coordinator.WriteInputAsync(
					"session-1",
					"x",
					static _ => false,
					static (_, _) => Task.CompletedTask);
			}
		};

		await SendPromptAsync(coordinator, "review this", submit: true);
		await typed.ShouldNotBeNull();

		backend.InputWrites.ShouldBe(["\u001b[200~review this\u001b[201~", PlainEnter, "x"]);
	}

	private static Task SendPromptAsync(SessionRuntimeCoordinator coordinator, string prompt, bool submit) =>
		coordinator.SendPromptAsync(
			CreateSession("session-1"),
			prompt,
			submit,
			startIfNeeded: false,
			enforceScenarioLock: false,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);

	[TestCase('\u0015', 85, 22)]
	[TestCase('\u000b', 75, 37)]
	[TestCase('\u0001', 65, 30)]
	[TestCase('\u0003', 67, 46)]
	public void RewriteInput_CodexWin32Mode_EncodesCtrlLetters(char control, int virtualKey, int scanCode)
	{
		var esc = (char)0x1b;
		var code = (int)control;

		AgentKeyEncoder.RewriteTyped(control.ToString(), AgentKind.Codex, true).ShouldBe(
			$"{esc}[{virtualKey};{scanCode};{code};1;8;1_{esc}[{virtualKey};{scanCode};{code};0;8;1_");
	}

	[TestCase("\t")]
	[TestCase("\r")]
	[TestCase("\b")]
	public void RewriteInput_CodexWin32Mode_LeavesKeysWithTheirOwnMeaningAlone(string input) =>
		AgentKeyEncoder.RewriteTyped(input, AgentKind.Codex, true).ShouldBe(input);

	[Test]
	public async Task Codex_win32_mode_uses_encoded_enter_and_confirms_delivery()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync(win32InputMode: true, kind: AgentKind.Codex);
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() => backend.InputWrites.Count == 2 ? Busy(epoch: 2) : Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([PastedTrigger, Win32InputEncoder.EnterKey]);
	}

	[Test]
	public async Task A_dropped_submit_is_repaired_with_enter_alone()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() =>
			{
				var enters = backend.InputWrites.Count(write => write == PlainEnter);
				return backend.InputWrites.Count == 0 ? Idle()
					: enters >= 2 ? Busy(epoch: 2)
					: HoldingText();
			},
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter, PlainEnter]);
	}

	[Test]
	public async Task A_dropped_paste_is_repaired_after_the_composer_stays_empty_and_quiet()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() => backend.InputWrites.Count(write => write == PastedTrigger) >= 2
				? Busy(epoch: 2)
				: Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter, PastedTrigger, PlainEnter]);
	}

	[Test]
	public async Task An_agent_that_never_reacts_ends_the_step_written()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() => backend.InputWrites.Count == 0 ? Idle() : Busy(epoch: 1),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Written);
		result.SubmitAttempted.ShouldBeTrue();
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter]);
	}

	[Test]
	public async Task A_question_appearing_after_the_paste_blocks_the_repair()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			() => backend.InputWrites.Count == 0
				? Idle()
				: Question("Approve this edit?"),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.BlockedByInputRequest);
		result.StatusLine.ShouldBe("Approve this edit?");
		result.WriteAttempted.ShouldBeTrue();
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter]);
	}

	[Test]
	[TestCase(true)]
	[TestCase(false)]
	public async Task A_composer_holding_text_is_refused_without_writing(bool confirmDelivery)
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery,
			static () => HoldingText(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.BlockedByPendingInput);
		result.WriteAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	[TestCase(true)]
	[TestCase(false)]
	public async Task A_pending_question_is_refused_without_writing(bool confirmDelivery)
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery,
			static () => Question(AgentScreenProfileBase.TrustPromptDescription),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.BlockedByInputRequest);
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task An_unconfirmed_send_writes_exactly_once()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"notice",
			confirmDelivery: false,
			static () => Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Written);
		result.IsSent.ShouldBeTrue();
		backend.InputWrites.Count.ShouldBe(2);
	}

	[Test]
	public async Task An_unreadable_state_is_written_once_without_repair()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WriteScenarioPromptAndSubmitAsync(
			"session-1",
			"task-path",
			confirmDelivery: true,
			static () => null,
			TestContext.CurrentContext.CancellationToken);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Written);
		backend.InputWrites.ShouldBe([PastedTrigger, PlainEnter]);
	}

	[Test]
	public async Task Readiness_waits_for_a_done_verdict()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;
		var polls = 0;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			() => ++polls < 3 ? Busy(epoch: 1) : Idle(),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeTrue();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task Readiness_does_not_treat_an_empty_composer_as_a_done_verdict()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			static () => new SessionScreenState(
				"screen",
				string.Empty,
				false,
				PromptIsEmpty: true,
				IsBusy: false,
				VerdictState: TerminalScreenVerdictState.Unknown),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task Readiness_accepts_a_done_verdict_when_the_composer_is_unknown()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			static () => new SessionScreenState(
				"screen",
				string.Empty,
				false,
				PromptIsEmpty: null,
				IsBusy: false,
				VerdictState: TerminalScreenVerdictState.Done),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeTrue();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task Readiness_answers_the_trust_dialog_once_and_then_proceeds()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			() => backend.InputWrites.Contains(PlainEnter)
				? Idle()
				: Question(AgentScreenProfileBase.TrustPromptDescription),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeTrue();
		backend.InputWrites.ShouldBe([PlainEnter]);
	}

	[Test]
	public async Task Readiness_never_answers_the_same_dialog_twice()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			static () => Question(AgentScreenProfileBase.TrustPromptDescription),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeFalse();
		backend.InputWrites.Count.ShouldBe(1);
	}

	[Test]
	public async Task Readiness_refuses_any_other_question_without_writing()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			static () => Question("Approve this edit?"),
			TestContext.CurrentContext.CancellationToken);

		result.IsReady.ShouldBeFalse();
		result.StatusLine.ShouldBe("Approve this edit?");
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task Readiness_spends_one_budget_across_all_of_its_phases()
	{
		var (coordinator, backend, controller) = await CreateAttachedSessionAsync();
		await using var _ = controller;
		var startedAt = Stopwatch.GetTimestamp();

		var result = await coordinator.WaitForSessionReadyAsync(
			"session-1",
			static () => null,
			TestContext.CurrentContext.CancellationToken);

		Stopwatch.GetElapsedTime(startedAt).ShouldBeLessThan(TimeSpan.FromSeconds(1));
		result.IsReady.ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task WriteScenarioPromptAndSubmitAsync_ControllerReplacedDuringSettle_DoesNotSubmitToEitherController()
	{
		RecordingBackend firstBackend = new();
		RecordingBackend replacementBackend = new();
		await using TerminalController first = new(firstBackend);
		await using TerminalController replacement = new(replacementBackend);
		await first.StartAsync("fake", Environment.CurrentDirectory);
		await replacement.StartAsync("fake", Environment.CurrentDirectory);
		SessionRuntimeCoordinator coordinator = new(new RecordingHost());
		var runtime = coordinator.GetOrCreateRuntime("session-1");
		runtime.AttachController(first);
		firstBackend.InputWritten = _ => runtime.AttachController(replacement);

		await Should.ThrowAsync<InvalidOperationException>(() =>
			coordinator.WriteScenarioPromptAndSubmitAsync(
				"session-1",
				"task-path",
				confirmDelivery: true,
				static () => Idle(),
				TestContext.CurrentContext.CancellationToken));

		firstBackend.InputWrites.ShouldBe(["\u001b[200~task-path\u001b[201~"]);
		replacementBackend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task GetOrCreateRuntime_ConcurrentSameSession_ReturnsOneRuntime()
	{
		SessionRuntimeCoordinator coordinator = new(new RecordingHost());
		var calls = Enumerable.Range(0, 64)
			.Select(_ => Task.Run(() => coordinator.GetOrCreateRuntime("session-1")))
			.ToArray();

		var runtimes = await Task.WhenAll(calls);

		runtimes.Distinct(ReferenceEqualityComparer.Instance).Count().ShouldBe(1);
		coordinator.Runtimes.Count.ShouldBe(1);
	}

	[Test]
	public async Task HandleControllerExitedAsync_DetachesAndDisposesOnlyCurrentController()
	{
		RecordingBackend backend = new();
		SessionRuntimeCoordinator coordinator = new(
			new RecordingHost(),
			() => new TerminalController(backend));
		TerminalStartOptions options = new(
			"fake",
			Environment.CurrentDirectory,
			80,
			24);
		var controller = await coordinator.StartAsync(
			"session-1",
			AgentKind.Pwsh,
			options,
			static (_, _) => { },
			outputHandler: null,
			inputWritingHandler: null,
			inputWrittenHandler: null,
			viewportChangedHandler: null,
			CancellationToken.None);

		var detached = await coordinator.HandleControllerExitedAsync(
			"session-1",
			controller);

		detached.ShouldBeTrue();
		coordinator.Runtimes.ShouldNotContainKey("session-1");
		backend.DisposeCount.ShouldBe(1);
	}

	[Test]
	public async Task StartAsync_DeliversEnvironmentVariablesToTheBackend()
	{
		RecordingBackend backend = new();
		SessionRuntimeCoordinator coordinator = new(
			new RecordingHost(),
			() => new TerminalController(backend));
		TerminalStartOptions options = new(
			"pwsh",
			@"C:\repo",
			80,
			24,
			new Dictionary<string, string> { ["PACT_SESSION_ID"] = "session-1" });

		await coordinator.StartAsync(
			"session-1",
			AgentKind.Pwsh,
			options,
			static (_, _) => { },
			outputHandler: null,
			inputWritingHandler: null,
			inputWrittenHandler: null,
			viewportChangedHandler: null,
			CancellationToken.None);

		backend.StartOptions!.EnvironmentVariables!["PACT_SESSION_ID"].ShouldBe("session-1");
		await coordinator.StopAsync("session-1");
	}

	[Test]
	public async Task HandleControllerExitedAsync_OldControllerCannotDetachOrDisposeReplacement()
	{
		RecordingBackend oldBackend = new();
		RecordingBackend replacementBackend = new();
		Queue<RecordingBackend> backends = new([oldBackend, replacementBackend]);
		SessionRuntimeCoordinator coordinator = new(
			new RecordingHost(),
			() => new TerminalController(backends.Dequeue()));
		TerminalStartOptions options = new(
			"fake",
			Environment.CurrentDirectory,
			80,
			24);
		var oldController = await coordinator.StartAsync(
			"session-1",
			AgentKind.Pwsh,
			options,
			static (_, _) => { },
			outputHandler: null,
			inputWritingHandler: null,
			inputWrittenHandler: null,
			viewportChangedHandler: null,
			CancellationToken.None);
		var replacement = await coordinator.StartAsync(
			"session-1",
			AgentKind.Pwsh,
			options,
			static (_, _) => { },
			outputHandler: null,
			inputWritingHandler: null,
			inputWrittenHandler: null,
			viewportChangedHandler: null,
			CancellationToken.None);

		var detached = await coordinator.HandleControllerExitedAsync(
			"session-1",
			oldController);

		detached.ShouldBeFalse();
		coordinator.TryGetActiveController(
			"session-1",
			out _,
			out var current).ShouldBeTrue();
		current.ShouldBeSameAs(replacement);
		replacementBackend.DisposeCount.ShouldBe(0);
		await coordinator.StopAsync("session-1");
	}

	[Test]
	public async Task ConcurrentStartReplacementExitAndStopDisposesEveryControllerAtMostOnce()
	{
		ConcurrentQueue<RecordingBackend> available = new(
			Enumerable.Range(0, 16).Select(_ => new RecordingBackend()));
		ConcurrentBag<RecordingBackend> created = [];
		SessionRuntimeCoordinator coordinator = new(
			new RecordingHost(),
			() =>
			{
				available.TryDequeue(out var backend).ShouldBeTrue();
				created.Add(backend);
				return new TerminalController(backend);
			});
		TerminalStartOptions options = new(
			"fake",
			Environment.CurrentDirectory,
			80,
			24);

		var starts = Enumerable.Range(0, 16)
			.Select(_ => Task.Run(async () =>
			{
				try
				{
					await coordinator.StartAsync(
						"session-1",
						AgentKind.Pwsh,
						options,
						static (_, _) => { },
						outputHandler: null,
						inputWritingHandler: null,
						inputWrittenHandler: null,
						viewportChangedHandler: null,
						CancellationToken.None);
				}
				catch (Exception exception) when (
					exception is InvalidOperationException or ObjectDisposedException)
				{
					// A concurrent replacement is allowed to supersede this start.
				}
			}))
			.ToArray();
		await Task.WhenAll(starts);
		coordinator.TryGetRuntime("session-1", out var runtime).ShouldBeTrue();
		runtime.TryGetController(out var current).ShouldBeTrue();

		await Task.WhenAll(
			coordinator.HandleControllerExitedAsync("session-1", current),
			coordinator.StopAsync("session-1"));

		coordinator.Runtimes.ShouldBeEmpty();
		created.Count.ShouldBe(16);
		created.ShouldAllBe(backend => backend.DisposeCount == 1);
	}

	[Test]
	public async Task StopAllAsync_BlocksNewRuntimeCreationAndDisposesEachDetachedControllerOnce()
	{
		RecordingBackend firstBackend = new();
		RecordingBackend secondBackend = new();
		TerminalController first = new(firstBackend);
		TerminalController second = new(secondBackend);
		await first.StartAsync("fake", Environment.CurrentDirectory);
		await second.StartAsync("fake", Environment.CurrentDirectory);
		SessionRuntimeCoordinator coordinator = new(new RecordingHost());
		coordinator.GetOrCreateRuntime("first").AttachController(first);
		coordinator.GetOrCreateRuntime("second").AttachController(second);

		var stop = coordinator.StopAllAsync(
			static (_, _) => Task.CompletedTask,
			static () => { });

		Should.Throw<InvalidOperationException>(
			() => coordinator.GetOrCreateRuntime("late"));
		await stop.WaitAsync(TimeSpan.FromSeconds(5));

		firstBackend.DisposeCount.ShouldBe(1);
		secondBackend.DisposeCount.ShouldBe(1);
		coordinator.Runtimes.ShouldBeEmpty();
	}

	private static SessionRuntimeCoordinator CreateFastCoordinator(RecordingHost? host = null) => new(
		host ?? new RecordingHost(),
		static () => new TerminalController(),
		TimeSpan.Zero,
		TimeSpan.Zero,
		TimeSpan.FromMilliseconds(50),
		TimeSpan.FromMilliseconds(20),
		TimeSpan.FromMilliseconds(1),
		TimeSpan.FromMilliseconds(200),
		pasteLandingTimeout: TimeSpan.FromMilliseconds(200),
		preEnterCaptureTimeout: TimeSpan.FromMilliseconds(100));

	private static async Task<(
		SessionRuntimeCoordinator Coordinator,
		RecordingBackend Backend,
		TerminalController Controller)> CreateAttachedSessionAsync(
		bool win32InputMode = false,
		AgentKind kind = AgentKind.Pwsh)
	{
		RecordingBackend backend = new();
		TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory);
		var coordinator = CreateFastCoordinator();
		var runtime = coordinator.GetOrCreateRuntime("session-1");
		runtime.AttachController(controller);
		runtime.Kind = kind;
		if (win32InputMode)
		{
			runtime.InputModes.Scan($"{(char)0x1b}[?9001h");
		}

		return (coordinator, backend, controller);
	}

	private static SessionScreenState Idle(long epoch = 1) =>
		new(
			"screen",
			string.Empty,
			false,
			false,
			string.Empty,
			true,
			epoch,
			false,
			TerminalScreenVerdictState.Done);

	private static SessionScreenState HoldingText(long epoch = 1) =>
		new(
			"screen",
			string.Empty,
			false,
			false,
			string.Empty,
			false,
			epoch,
			false,
			TerminalScreenVerdictState.Done);

	private static SessionScreenState Busy(long epoch) =>
		new(
			"screen",
			string.Empty,
			false,
			false,
			string.Empty,
			null,
			epoch,
			true,
			TerminalScreenVerdictState.Busy);

	private static SessionScreenState Question(string statusLine) =>
		new(
			"screen",
			string.Empty,
			false,
			true,
			statusLine,
			null,
			1,
			false,
			TerminalScreenVerdictState.InputRequested);

	private static Task NoopSession(SessionViewModel _, CancellationToken __) => Task.CompletedTask;
	private static Task<IDisposable?> NoScope(SessionViewModel _, CancellationToken __) => Task.FromResult<IDisposable?>(null);
	private static Task Noop(CancellationToken _) => Task.CompletedTask;

	private static SessionViewModel CreateSession(string id, AgentKind kind = AgentKind.Pwsh) => new(new SessionRecord(
		id, kind, id, Environment.CurrentDirectory, "pwsh", null,
		SessionStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

	private sealed class RecordingHost : ITerminalWebViewHost
	{
		public List<string> Shown { get; } = [];
		public event EventHandler<(string SessionId, string Data)>? InputReceived { add { } remove { } }
		public event EventHandler<(string SessionId, int Columns, int Rows)>? ResizeReceived { add { } remove { } }
		public event EventHandler<(string SessionId, TerminalScreenSnapshot Snapshot)>? ScreenSnapshotReceived { add { } remove { } }
		public event EventHandler<(string SessionId, bool HasSelection)>? SelectionChanged { add { } remove { } }
		public event EventHandler<TerminalSelectionCompleted>? SelectionCompleted { add { } remove { } }
		public event EventHandler<string>? SelectionDismissed { add { } remove { } }
		public event EventHandler<TerminalMouseReleased>? AgentMouseReleased { add { } remove { } }
		public event EventHandler<(string SessionId, Uri Uri)>? LinkRequested { add { } remove { } }
		public event EventHandler? PasteRequested { add { } remove { } }
		public event EventHandler<TerminalCopyRequest>? CopyRequested { add { } remove { } }
		public event EventHandler? BusyOverlayActionRequested { add { } remove { } }
		public Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken) => Task.CompletedTask;
		public (int Columns, int Rows) GetCurrentSize(string sessionId) => (80, 24);
		public Task CreateTerminalAsync(string sessionId) => Task.CompletedTask;
		public Task ShowTerminalAsync(string sessionId) { Shown.Add(sessionId); return Task.CompletedTask; }
		public Task WriteOutputAsync(string sessionId, string text) => Task.CompletedTask;
		public Task ResetSnapshotBaselineAsync(string sessionId) => Task.CompletedTask;
		public Func<string, TerminalScreenSnapshot?> CaptureScreen { get; set; } = static _ => null;
		public Func<string, CancellationToken, Task<TerminalScreenSnapshot?>>? CaptureScreenHandler { get; set; }
		public Task<TerminalScreenSnapshot?> CaptureScreenAsync(string sessionId, CancellationToken cancellationToken) =>
			CaptureScreenHandler?.Invoke(sessionId, cancellationToken) ?? Task.FromResult(CaptureScreen(sessionId));
		public Task DisposeTerminalAsync(string sessionId) => Task.CompletedTask;
		public Task<string> GetSelectedTextAsync() => Task.FromResult(string.Empty);
		public Task FitAsync() => Task.CompletedTask;
		public Task FocusAsync() => Task.CompletedTask;
		public Task SetBusyOverlayAsync(string message, bool isVisible, bool dimBackground, string? actionLabel = null) => Task.CompletedTask;
	}

	private sealed class RecordingClipboard(string text) : IClipboardService
	{
		private string _text = text;

		public List<string> Writes { get; } = [];

		public uint ChangeSequence => (uint)Writes.Count;

		public Task<string> GetTextAsync() => Task.FromResult(_text);

		public Task<bool> TrySetTextAsync(string text)
		{
			Writes.Add(text);
			_text = text;
			return Task.FromResult(true);
		}
	}

	private sealed class RecordingBackend : ITerminalBackend
	{
		private readonly TaskCompletionSource _stopped =
			new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<string> InputWrites { get; } = [];
		public Action<string>? InputWritten { get; set; }
		private int _disposeCount;
		public int DisposeCount => Volatile.Read(ref _disposeCount);
		public TerminalStartOptions? StartOptions { get; private set; }

		public Task<TerminalSession> StartAsync(
			TerminalStartOptions options,
			CancellationToken cancellationToken)
		{
			StartOptions = options;
			return Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));
		}

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken)
		{
			var text = System.Text.Encoding.UTF8.GetString(input);
			InputWrites.Add(text);
			InputWritten?.Invoke(text);
			return Task.CompletedTask;
		}

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[System.Runtime.CompilerServices.EnumeratorCancellation]
			CancellationToken cancellationToken)
		{
			await _stopped.Task.WaitAsync(cancellationToken);
			yield break;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			_stopped.TrySetResult();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			Interlocked.Increment(ref _disposeCount);
			_stopped.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}
}
