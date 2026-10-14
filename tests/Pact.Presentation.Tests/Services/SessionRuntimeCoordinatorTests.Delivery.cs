using System.Diagnostics;
using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed partial class SessionRuntimeCoordinatorTests
{
	private const string Pasted = "\u001b[200~review this\u001b[201~";

	[Test]
	public void Delivery_test_screens_classify_as_intended()
	{
		CodexScreenProfile.Instance.Classify(ComposerScreen(empty: true)).PromptIsEmpty.ShouldBe(true);
		CodexScreenProfile.Instance.Classify(ComposerScreen(empty: false)).PromptIsEmpty.ShouldBe(false);
		var question = CodexScreenProfile.Instance.Classify(QuestionScreen());
		question.State.ShouldBe(TerminalScreenVerdictState.InputRequested);
		question.Description.ShouldBe("Approve this edit?");
	}

	[Test]
	public async Task A_fresh_empty_composer_after_enter_confirms()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task An_unknown_composer_never_confirms()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = static _ => TerminalScreenSnapshot.FromText("x");

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Unconfirmed);
		result.SubmitAttempted.ShouldBeTrue();
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task Busy_agent_and_text_in_the_composer_do_not_refuse()
	{
		var (coordinator, backend, host, facts, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		facts.Screen = Busy(epoch: 1) with { PromptIsEmpty = false };
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[TestCase(PromptSender.Outsider)]
	[TestCase(PromptSender.ScenarioOwner)]
	public async Task A_pending_question_refuses_every_sender(PromptSender sender)
	{
		var (coordinator, backend, _, facts, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		facts.Screen = Question("Approve this edit?");

		var result = await DeliverAsync(coordinator, sender: sender);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.RefusedQuestion);
		result.StatusLine.ShouldBe("Approve this edit?");
		result.WriteAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task A_scenario_lock_refuses_only_outsiders()
	{
		var (coordinator, backend, host, facts, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		facts.Locked = true;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));

		var outsider = await DeliverAsync(coordinator, sender: PromptSender.Outsider);
		backend.InputWrites.ShouldBeEmpty();
		var owner = await DeliverAsync(coordinator, sender: PromptSender.ScenarioOwner);

		outsider.Outcome.ShouldBe(PromptDeliveryOutcome.RefusedLocked);
		owner.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task A_paste_that_never_shows_is_not_entered()
	{
		var (coordinator, backend, _, _, controller) = await CreateDeliverySessionAsync(echoPastes: false);
		await using var _ = controller;

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.PasteNotShown);
		result.SubmitAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBe([Pasted]);
	}

	[Test]
	public async Task A_question_that_opens_while_the_paste_lands_stops_enter()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = static _ => QuestionScreen();

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.RefusedQuestion);
		result.StatusLine.ShouldBe("Approve this edit?");
		result.WriteAttempted.ShouldBeTrue();
		result.SubmitAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBe([Pasted]);
	}

	[Test]
	public async Task A_capture_that_never_answers_writes_no_enter()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreenHandler = static async (_, token) =>
		{
			await Task.Delay(Timeout.Infinite, token);
			return null;
		};
		var startedAt = Stopwatch.GetTimestamp();

		var result = await DeliverAsync(coordinator);

		Stopwatch.GetElapsedTime(startedAt).ShouldBeLessThan(TimeSpan.FromSeconds(2));
		result.Outcome.ShouldBe(PromptDeliveryOutcome.Unconfirmed);
		result.SubmitAttempted.ShouldBeFalse();
		backend.InputWrites.ShouldBe([Pasted]);
	}

	[Test]
	public async Task Text_left_in_the_composer_gets_exactly_one_more_enter()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Count(write => write == PlainEnter) >= 2);

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter, PlainEnter]);
	}

	[Test]
	public async Task Text_that_stays_after_the_repair_ends_unconfirmed()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = static _ => ComposerScreen(empty: false);

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Unconfirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter, PlainEnter]);
	}

	[Test]
	public async Task A_question_before_the_repair_stops_the_second_enter()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		long? enteredAt = null;
		host.CaptureScreen = _ =>
		{
			if (!backend.InputWrites.Contains(PlainEnter))
			{
				return ComposerScreen(empty: false);
			}

			enteredAt ??= Stopwatch.GetTimestamp();
			return Stopwatch.GetElapsedTime(enteredAt.Value) < TimeSpan.FromMilliseconds(30)
				? ComposerScreen(empty: false)
				: QuestionScreen();
		};

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.RefusedQuestion);
		result.SubmitAttempted.ShouldBeTrue();
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task Keyboard_input_during_confirmation_ends_the_delivery_without_repair()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		var typed = false;
		host.CaptureScreenHandler = async (_, _) =>
		{
			if (backend.InputWrites.Contains(PlainEnter) && !typed)
			{
				typed = true;
				await coordinator.WriteInputAsync(
					"session-1",
					"x",
					static _ => false,
					static (_, _) => Task.CompletedTask);
			}

			return ComposerScreen(empty: false);
		};

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Unconfirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter, "x"]);
	}

	[Test]
	public async Task A_session_without_a_composer_model_is_submitted()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync(kind: AgentKind.Pwsh);
		await using var _ = controller;
		host.CaptureScreen = static _ => throw new InvalidOperationException("no capture expected");

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Submitted);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task A_second_delivery_waits_for_the_first_result()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource firstConfirming = new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.CaptureScreenHandler = async (_, token) =>
		{
			var enters = backend.InputWrites.Count(write => write == PlainEnter);
			if (enters == 1 && !release.Task.IsCompleted)
			{
				firstConfirming.TrySetResult();
				await release.Task.WaitAsync(token);
			}

			return ComposerScreen(empty: enters > 0);
		};

		var first = DeliverAsync(coordinator);
		await firstConfirming.Task;
		var second = coordinator.DeliverPromptAsync(
			"session-1", "second text", true, PromptSender.Outsider, false, CancellationToken.None);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
		release.SetResult();
		var results = await Task.WhenAll(first, second);

		results.ShouldAllBe(result => result.Outcome == PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter, "\u001b[200~second text\u001b[201~", PlainEnter]);
		await controller.DisposeAsync();
	}

	[Test]
	public async Task A_stopped_session_is_not_started_without_permission()
	{
		var coordinator = CreateFastCoordinator();
		FakeFacts facts = new() { Start = static _ => throw new InvalidOperationException("no start expected") };
		coordinator.SessionFacts = facts;

		var result = await DeliverAsync(coordinator);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.NotRunning);
		result.WriteAttempted.ShouldBeFalse();
	}

	[Test]
	public async Task A_stopped_session_is_started_and_waits_for_readiness()
	{
		RecordingHost host = new();
		var coordinator = CreateFastCoordinator(host);
		RecordingBackend backend = new();
		await using TerminalController controller = new(backend);
		FakeFacts facts = new() { Screen = Question(AgentScreenProfileBase.TrustPromptDescription) };
		facts.Start = async _ =>
		{
			await controller.StartAsync("fake", Environment.CurrentDirectory);
			var runtime = coordinator.GetOrCreateRuntime("session-1");
			runtime.AttachController(controller);
			runtime.Kind = AgentKind.Codex;
			EchoPastes(backend, runtime, facts);
			runtime.NotifyOutputRendered();
		};
		coordinator.SessionFacts = facts;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Count(write => write == PlainEnter) >= 2);

		var result = await DeliverAsync(coordinator, startIfNotRunning: true);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([PlainEnter, Pasted, PlainEnter]);
	}

	[Test]
	public async Task A_start_that_never_gets_ready_refuses_with_its_reason()
	{
		var coordinator = CreateFastCoordinator();
		RecordingBackend backend = new();
		await using TerminalController controller = new(backend);
		FakeFacts facts = new() { Screen = Question("Pick a model") };
		facts.Start = async _ =>
		{
			await controller.StartAsync("fake", Environment.CurrentDirectory);
			var runtime = coordinator.GetOrCreateRuntime("session-1");
			runtime.AttachController(controller);
			runtime.Kind = AgentKind.Codex;
			runtime.NotifyOutputRendered();
		};
		coordinator.SessionFacts = facts;

		var result = await DeliverAsync(coordinator, startIfNotRunning: true);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.NotRunning);
		result.StatusLine.ShouldBe("Pick a model");
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task A_delivery_queued_behind_an_exit_returns_not_running()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource firstConfirming = new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.CaptureScreenHandler = async (_, token) =>
		{
			if (backend.InputWrites.Contains(PlainEnter))
			{
				firstConfirming.TrySetResult();
				await release.Task.WaitAsync(token);
				return ComposerScreen(empty: true);
			}

			return ComposerScreen(empty: false);
		};

		var first = DeliverAsync(coordinator);
		await firstConfirming.Task;
		var second = coordinator.DeliverPromptAsync(
			"session-1", "second text", true, PromptSender.Outsider, false, CancellationToken.None);
		coordinator.GetOrCreateRuntime("session-1").DetachController();
		release.SetResult();

		(await first).Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		(await second).Outcome.ShouldBe(PromptDeliveryOutcome.NotRunning);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
		await controller.DisposeAsync();
	}

	[Test]
	public async Task Every_delivery_writes_one_trace_line()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		List<string> lines = [];
		coordinator.DeliveryTrace = lines.Add;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));

		await DeliverAsync(coordinator);

		lines.Count.ShouldBe(1);
		lines[0].ShouldStartWith("delivery session=session-1 sender=Outsider");
		lines[0].ShouldContain("outcome=Confirmed");
		lines[0].ShouldNotContain("review this");
	}

	private static Task<PromptDeliveryResult> DeliverAsync(
		SessionRuntimeCoordinator coordinator,
		PromptSender sender = PromptSender.Outsider,
		bool startIfNotRunning = false) =>
		coordinator.DeliverPromptAsync(
			"session-1",
			"review this",
			submit: true,
			sender,
			startIfNotRunning,
			CancellationToken.None);

	// A session whose backend echoes pastes into the recent output, as a TUI shows them.
	private static async Task<(
		SessionRuntimeCoordinator Coordinator,
		RecordingBackend Backend,
		RecordingHost Host,
		FakeFacts Facts,
		TerminalController Controller)> CreateDeliverySessionAsync(
		AgentKind kind = AgentKind.Codex,
		bool echoPastes = true)
	{
		RecordingHost host = new();
		RecordingBackend backend = new();
		TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory);
		var coordinator = CreateFastCoordinator(host);
		var runtime = coordinator.GetOrCreateRuntime("session-1");
		runtime.AttachController(controller);
		runtime.Kind = kind;
		FakeFacts facts = new();
		coordinator.SessionFacts = facts;
		if (echoPastes)
		{
			EchoPastes(backend, runtime, facts);
		}

		return (coordinator, backend, host, facts, controller);
	}

	// Echoes pastes and, like a launched agent, settles once its trust dialog is answered.
	private static void EchoPastes(RecordingBackend backend, SessionRuntime runtime, FakeFacts facts) =>
		backend.InputWritten = input =>
		{
			if (input.StartsWith("\u001b[200~", StringComparison.Ordinal))
			{
				runtime.AppendRecentOutput(input[6..^6]);
			}
			else if (input == PlainEnter && facts.Screen?.StatusLine == AgentScreenProfileBase.TrustPromptDescription)
			{
				facts.Screen = Idle();
			}
		};

	private static TerminalScreenSnapshot ComposerScreen(bool empty) =>
		TerminalScreenSnapshot.FromRows(
			[
				new("Worked for 1s", false, []),
				new(empty ? "› " : "› review this", false, []),
				new("  GPT-6.1-Sol medium · main · Context 30% used", false, [])],
			80,
			true);

	private static TerminalScreenSnapshot QuestionScreen() =>
		TerminalScreenSnapshot.FromText(
			"""
			Question 1/1 (1 unanswered)
			  Approve this edit?

			  › 1. Yes
			    2. No

			  tab to add notes | enter to submit answer | esc to interrupt
			""");

	private sealed class FakeFacts : ISessionFacts
	{
		public SessionScreenState? Screen { get; set; } = Idle();
		public bool Locked { get; set; }
		public Func<string, Task>? Start { get; set; }

		public SessionScreenState? ReadScreen(string sessionId) => Screen;

		public bool IsScenarioLocked(string sessionId) => Locked;

		public Task StartAsync(string sessionId, CancellationToken cancellationToken) =>
			Start?.Invoke(sessionId) ?? Task.CompletedTask;
	}
}
