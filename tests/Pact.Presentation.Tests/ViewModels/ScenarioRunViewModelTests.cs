using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ScenarioRunViewModelTests : IDisposable
{
	private static readonly ScenarioBlueprint Blueprint = new(
		"test-scenario",
		"Test scenario",
		["reviewer"],
		[
			new ScenarioStepMetadata("send", "reviewer", null, "Send prompt", ScenarioStepKind.Send),
			new ScenarioStepMetadata("capture", "reviewer", null, "Capture response", ScenarioStepKind.Capture)
		],
		DefaultMaxIterations: 2,
		DefaultTarget: "start");

	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();

	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	[Test]
	public async Task JournalEntriesAppearInObservableCollection()
	{
		FakeGateway gateway = new();
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.SendAsync("send", "reviewer", "hello", ct);
				await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, _) => Task.FromResult("done"),
					ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		viewModel.Journal.ShouldContain(entry => entry.StepId == "send");
	}

	[Test]
	public async Task JournalMarkdownAggregatesEntriesAndNotifiesOnAppend()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (context, ct) =>
			{
				await release.Task.WaitAsync(ct);
				await context.SendAsync("send", "reviewer", "hello", ct);
				await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, _) => Task.FromResult("done"),
					ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());
		List<string?> changedProperties = [];
		viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);
		release.TrySetResult();

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		changedProperties.ShouldContain(nameof(ScenarioRunViewModel.JournalMarkdown));
		viewModel.JournalMarkdown.Contains("### ", StringComparison.Ordinal).ShouldBeTrue();
		viewModel.JournalMarkdown.Contains(" · Info · send", StringComparison.Ordinal).ShouldBeTrue();
		viewModel.JournalMarkdown.Contains("hello", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task StatePropertiesUpdateWhenHandleStateChanges()
	{
		FakeGateway gateway = new();
		gateway.Responses.Enqueue(_ => Task.FromException<string>(new ScenarioStepTimeoutException()));
		TaskCompletionSource<string> resumedResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
		gateway.Responses.Enqueue(_ => resumedResponse.Task);
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					(_, waitCancellationToken) => gateway.Responses.Dequeue()(waitCancellationToken),
					ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());
		List<string?> changedProperties = [];
		viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		viewModel.State.ShouldBe(ScenarioRunState.Paused);
		viewModel.StateGlyph.ShouldBe("⏸");
		viewModel.NeedsAttention.ShouldBeTrue();
		viewModel.IsRunning.ShouldBeFalse();
		viewModel.CanSoftStop.ShouldBeFalse();
		viewModel.CanAbort.ShouldBeTrue();
		viewModel.CanResume.ShouldBeTrue();
		viewModel.CurrentStepId.ShouldBe("capture");
		changedProperties.ShouldContain(nameof(ScenarioRunViewModel.State));
		changedProperties.ShouldContain(nameof(ScenarioRunViewModel.CanResume));
		changedProperties.ShouldContain(nameof(ScenarioRunViewModel.NeedsAttention));

		viewModel.Resume();
		resumedResponse.SetResult("done");
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		viewModel.State.ShouldBe(ScenarioRunState.Completed);
		viewModel.StateGlyph.ShouldBe("✓");
		viewModel.CanResume.ShouldBeFalse();
		viewModel.CanSoftStop.ShouldBeFalse();
		viewModel.CanAbort.ShouldBeFalse();
	}

	[Test]
	public async Task ManualPauseProjectsPauseActionProgressAndIconState()
	{
		FakeGateway gateway = new();
		TaskCompletionSource waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (context, ct) =>
			{
				await context.WaitForResponseAsync(
					"capture",
					"reviewer",
					async (_, waitCancellationToken) =>
					{
						waitStarted.TrySetResult();
						await Task.Delay(Timeout.InfiniteTimeSpan, waitCancellationToken);
						return string.Empty;
					},
					ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());

		await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
		viewModel.RequestPause();
		await WaitForStateAsync(handle, ScenarioRunState.Paused);

		viewModel.Title.ShouldBe("Test scenario (step 1/1)");
		viewModel.CanPause.ShouldBeFalse();
		viewModel.CanResume.ShouldBeTrue();
		viewModel.ShowPauseIcon.ShouldBeTrue();

		viewModel.Abort();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(2));
	}

	[Test]
	public async Task ActionMethodsForwardToHandle()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (_, ct) =>
			{
				await release.Task.WaitAsync(ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());

		viewModel.RequestSoftStop();

		viewModel.State.ShouldBe(ScenarioRunState.StoppingAfterStep);
		viewModel.StateGlyph.ShouldBe("▶");
		viewModel.IsRunning.ShouldBeTrue();
		viewModel.CanAbort.ShouldBeTrue();
		viewModel.CanSoftStop.ShouldBeFalse();

		viewModel.Abort();
		release.SetResult();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		viewModel.State.ShouldBe(ScenarioRunState.Aborted);
		viewModel.StateGlyph.ShouldBe("■");
	}

	private static ScenarioRunHandle CreateHandle(FakeGateway gateway, IScenarioProgram program)
	{
		ScenarioRunService service = new(gateway);
		return service.Start(
			Blueprint,
			program,
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start",
			maxIterations: 1);
	}

	[Test]
	public async Task TitleReportsExecutedStepCountOnceTheRunIsTerminal()
	{
		FakeGateway gateway = new();
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram((_, _) => Task.FromResult(true)));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());

		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));

		viewModel.IsTerminal.ShouldBeTrue();
		viewModel.Title.ShouldBe("Test scenario (finished, 1 steps)");
	}

	[Test]
	public async Task TitleKeepsReportingProgressWhileTheRunIsExecuting()
	{
		FakeGateway gateway = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var handle = CreateHandle(
			gateway,
			new ScriptedProgram(async (_, ct) =>
			{
				await release.Task.WaitAsync(ct);
				return true;
			}));
		using ScenarioRunViewModel viewModel = new(handle, dispatch: action => action());

		await WaitForStateAsync(handle, ScenarioRunState.Running);

		viewModel.Title.ShouldBe("Test scenario (step 1/1)");
		release.TrySetResult();
		await handle.Completion.WaitAsync(TimeSpan.FromSeconds(5));
	}

	private static async Task WaitForStateAsync(ScenarioRunHandle handle, ScenarioRunState state)
	{
		if (handle.State == state)
		{
			return;
		}

		TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStateChanged(object? _, EventArgs __)
		{
			if (handle.State == state)
			{
				reached.TrySetResult();
			}
		}

		handle.StateChanged += OnStateChanged;
		try
		{
			if (handle.State == state)
			{
				return;
			}

			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			handle.StateChanged -= OnStateChanged;
		}
	}

	private sealed class FakeGateway : IScenarioTerminalGateway
	{
		public Queue<Func<CancellationToken, Task<string>>> Responses { get; } = new();

		public Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken) =>
			Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true));

		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

		public bool IsSessionAlive(string sessionId) => true;

		public string GetSessionLabel(string sessionId) => sessionId;
	}

	private sealed class ScriptedProgram(
		Func<ScenarioIterationContext, CancellationToken, Task<bool>> runAsync) : IScenarioProgram
	{
		private readonly Func<ScenarioIterationContext, CancellationToken, Task<bool>> _runAsync = runAsync;

		public Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken) => _runAsync(context, cancellationToken);
	}
}
