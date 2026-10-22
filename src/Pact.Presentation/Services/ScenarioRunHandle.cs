using System.Diagnostics.CodeAnalysis;
using Pact.Core.Scenarios;

namespace Pact.Presentation.Services;

/// <summary>
/// Observes and controls one scenario run: its state, journal, and stop/abort/resume actions.
/// </summary>
/// <remarks>
/// Members are safe to call from any thread. Reaching a terminal state releases the run's session
/// locks and deletes its exchange directory exactly once, however that state was reached.
/// </remarks>
public sealed class ScenarioRunHandle : IDisposable
{
	private readonly Lock _sync = new();
	private readonly IScenarioProgram _program;
	private readonly string _startPrompt;
	private readonly IScenarioTerminalGateway _gateway;
	private readonly Func<Exception, Task>? _reportCleanupFailureAsync;
	private readonly Func<string, Exception?, Task>? _reportDiagnosticAsync;
	private readonly TimeSpan _stepWatchdogTimeout;
	private readonly TimeSpan _completionNoticeTimeout;
	private readonly Action<ScenarioRunHandle> _releaseRun;
	private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
	private readonly CancellationTokenSource _cancellation = new();
	private readonly List<ScenarioJournalEntry> _journal = [];
	private TaskCompletionSource? _resumeRequested;
	private CancellationTokenSource? _pauseInterruption;
	private ScenarioPauseState _pause = ScenarioPauseState.Initial;
	private string? _currentStepId;
	private string? _failureMessage;
	private ScenarioExpectedResponse? _expectedResponse;
	private bool _abortRequested;
	private bool _failureRequested;
	private bool _finalized;

	internal ScenarioRunHandle(
		string runId,
		ScenarioBlueprint blueprint,
		IScenarioProgram program,
		IReadOnlyDictionary<string, string> roleBindings,
		string startPrompt,
		int maxIterations,
		IScenarioTerminalGateway gateway,
		Func<Exception, Task>? reportCleanupFailureAsync,
		Func<string, Exception?, Task>? reportDiagnosticAsync,
		TimeSpan stepWatchdogTimeout,
		TimeSpan completionNoticeTimeout,
		Action<ScenarioRunHandle> releaseRun)
	{
		RunId = runId;
		Blueprint = blueprint;
		RoleBindings = new Dictionary<string, string>(roleBindings, StringComparer.Ordinal);
		_program = program;
		_startPrompt = startPrompt;
		MaxIterations = maxIterations;
		_gateway = gateway;
		_reportCleanupFailureAsync = reportCleanupFailureAsync;
		_reportDiagnosticAsync = reportDiagnosticAsync;
		_stepWatchdogTimeout = stepWatchdogTimeout;
		_completionNoticeTimeout = completionNoticeTimeout;
		_releaseRun = releaseRun;
		Completion = _completion.Task;
	}

	/// <summary>Identifier of this run; its short form names the exchange directory.</summary>
	public string RunId { get; }

	/// <summary>Shape of the scenario being run.</summary>
	public ScenarioBlueprint Blueprint { get; }

	/// <summary>Role name to session id for this run.</summary>
	public IReadOnlyDictionary<string, string> RoleBindings { get; }

	/// <summary>One-based review pass currently executing.</summary>
	public int CurrentIteration
	{
		get
		{
			lock (_sync)
			{
				return field;
			}
		}

		private set;
	} = 1;

	/// <summary>Maximum review passes configured for this run.</summary>
	public int MaxIterations { get; }

	/// <summary>Current run state.</summary>
	public ScenarioRunState State
	{
		get
		{
			lock (_sync)
			{
				return _pause.State;
			}
		}
	}

	/// <summary>
	/// Step currently executing, or <see langword="null"/> before the first step.
	/// </summary>
	public string? CurrentStepId
	{
		get
		{
			lock (_sync)
			{
				return _currentStepId;
			}
		}
	}

	/// <summary>The durable task/response pair currently awaited by the run, when any.</summary>
	public ScenarioExpectedResponse? ExpectedResponse
	{
		get
		{
			lock (_sync)
			{
				return _expectedResponse;
			}
		}
	}

	/// <summary>
	/// Session the watchdog is waiting on while paused; it is the one session unlocked so the
	/// user can answer. <see langword="null"/> when not paused.
	/// </summary>
	public string? StuckSessionId => PauseState.StuckSessionId;

	/// <summary>Kind of the current pause; <see cref="ScenarioPauseKind.None"/> unless paused.</summary>
	public ScenarioPauseKind PauseKind => PauseState.PauseKind;

	/// <summary>Why Pact paused the run for attention, or <see langword="null"/>.</summary>
	public ScenarioAttentionReason? AttentionReason => PauseState.AttentionReason;

	/// <summary>The run state and the reason it stands still, as one value.</summary>
	public ScenarioPauseState PauseState
	{
		get
		{
			lock (_sync)
			{
				return _pause;
			}
		}
	}

	/// <summary>
	/// Snapshot of the journal. Entries live only in memory and are discarded when the run is
	/// closed.
	/// </summary>
	public IReadOnlyList<ScenarioJournalEntry> Journal
	{
		get
		{
			lock (_sync)
			{
				return _journal.ToArray();
			}
		}
	}

	/// <summary>
	/// Completes when the run reaches a terminal state. It completes rather than faults, even
	/// when the run failed — read <see cref="State"/> for the outcome.
	/// </summary>
	public Task Completion { get; }

	/// <summary>Latest reviewer result file content; after a terminal state this is the run's outcome document.</summary>
	public string? FinalResult
	{
		get
		{
			lock (_sync)
			{
				return field;
			}
		}

		private set;
	}

	internal void SetFinalResult(string result)
	{
		lock (_sync)
		{
			FinalResult = result;
		}

		RaiseStateChanged();
	}

	/// <summary>Raised for each journal entry as it is recorded.</summary>
	public event EventHandler<ScenarioJournalEntry>? JournalEntryAdded;

	/// <summary>Raised whenever the run's state or final result changes.</summary>
	public event EventHandler? StateChanged;

	/// <summary>
	/// Asks the run to finish after the current step rather than stopping mid-exchange.
	/// </summary>
	public void RequestSoftStop()
	{
		Applied applied;
		lock (_sync)
		{
			if (_finalized)
			{
				return;
			}

			applied = ApplyLocked(ScenarioPauseMachine.RequestSoftStop);
		}

		Act(applied, "run");
	}

	/// <summary>
	/// Requests a cooperative pause at the current safe exchange boundary. A response-file wait
	/// is interrupted immediately and resumed against the same file.
	/// </summary>
	public void RequestPause() => RequestManualPause();

	/// <summary>
	/// Requests a manual pause without ever canceling an already-pending request. An existing
	/// attention pause is escalated in place so all bound terminals remain protected until Resume.
	/// </summary>
	public ScenarioPauseRequestStatus RequestManualPause()
	{
		Applied applied;
		lock (_sync)
		{
			if (_finalized)
			{
				return ScenarioPauseRequestStatus.NotPausable;
			}

			applied = ApplyLocked(ScenarioPauseMachine.RequestPause);
		}

		Act(applied, "run");
		return applied.Transition.Status ?? ScenarioPauseRequestStatus.NotPausable;
	}

	/// <summary>
	/// Cancels the run immediately, sending Esc to the involved terminals. Idempotent, and
	/// harmless once the run is already terminal.
	/// </summary>
	public void Abort()
	{
		lock (_sync)
		{
			if (_finalized)
			{
				return;
			}

			_abortRequested = true;
			_resumeRequested?.TrySetCanceled(_cancellation.Token);
		}

		_cancellation.Cancel();
	}

	internal bool RequestFailure(string message)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(message);

		lock (_sync)
		{
			if (_finalized)
			{
				return false;
			}

			_failureRequested = true;
			_failureMessage = message;
			_resumeRequested?.TrySetCanceled(_cancellation.Token);
		}

		_cancellation.Cancel();
		return true;
	}

	/// <summary>
	/// Resumes a paused run. The step waits again for the same expected response file, so an
	/// answer the user gave manually while paused is picked up.
	/// </summary>
	public void Resume()
	{
		if (!TryResume())
		{
			throw new InvalidOperationException("Scenario run is not paused.");
		}
	}

	internal void ResumeAfterDelivery() => Transition(ScenarioPauseMachine.DeliverySucceeded);

	internal void ResumeAfterResponse() => Transition(ScenarioPauseMachine.Resume);

	/// <summary>
	/// Resumes an established pause. A pending pause request is deliberately not canceled.
	/// </summary>
	public bool TryResume() => Transition(ScenarioPauseMachine.Resume);

	private bool Transition(Func<ScenarioPauseState, ScenarioPauseTransition> step)
	{
		Applied applied;
		lock (_sync)
		{
			applied = ApplyLocked(step);
		}

		Act(applied, "run");
		return applied.Transition.Effects.HasFlag(ScenarioPauseEffects.Changed);
	}

	// Applies one transition under _sync and takes the plumbing it needs: a fresh resume signal
	// when the run enters a pause, the signal to complete when it leaves one, and the wait to
	// interrupt for a pause request.
	private Applied ApplyLocked(Func<ScenarioPauseState, ScenarioPauseTransition> step)
	{
		var before = _pause;
		var transition = step(before);
		_pause = transition.State;
		if (!before.IsPaused && transition.State.IsPaused)
		{
			_resumeRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		TaskCompletionSource? resumed = null;
		if (transition.Effects.HasFlag(ScenarioPauseEffects.SignalResume))
		{
			resumed = _resumeRequested;
			_resumeRequested = null;
		}

		return new(
			transition,
			resumed,
			transition.Effects.HasFlag(ScenarioPauseEffects.InterruptWait) ? _pauseInterruption : null,
			transition.State.IsPaused ? _resumeRequested?.Task : null);
	}

	// Carries out a transition's effects outside _sync.
	private void Act(Applied applied, string stepId, string? attentionMessage = null)
	{
		var effects = applied.Transition.Effects;
		if (applied.Interrupt is { } interruption)
		{
			try
			{
				interruption.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// The wait completed while the pause request was being published. The retained
				// request is observed at the next safe exchange boundary.
			}
		}

		if (effects.HasFlag(ScenarioPauseEffects.Changed))
		{
			RaiseStateChanged();
		}

		if (effects.HasFlag(ScenarioPauseEffects.JournalManualPause))
		{
			AddJournalEntry(stepId, "paused by user", ScenarioJournalLevel.Info);
		}

		if (effects.HasFlag(ScenarioPauseEffects.JournalAttention) && attentionMessage is not null)
		{
			AddJournalEntry(stepId, attentionMessage, ScenarioJournalLevel.Warning);
		}

		applied.Resumed?.TrySetResult();
	}

	private readonly record struct Applied(
		ScenarioPauseTransition Transition,
		TaskCompletionSource? Resumed,
		CancellationTokenSource? Interrupt,
		Task? ResumeTask);

	internal void Start() => _ = Task.Run(RunLoopAsync);

	internal Task JournalAsync(
		string stepId,
		string message,
		ScenarioJournalLevel level,
		CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		AddJournalEntry(stepId, message, level);
		return Task.CompletedTask;
	}

	private void AddJournalEntry(string stepId, string message, ScenarioJournalLevel level)
	{
		ScenarioJournalEntry entry = new(DateTimeOffset.UtcNow, stepId, message, level);

		lock (_sync)
		{
			_journal.Add(entry);
		}

		JournalEntryAdded?.Invoke(this, entry);
	}

	internal async Task ReportDiagnosticAsync(string phase, Exception? exception = null)
	{
		if (_reportDiagnosticAsync is null)
		{
			return;
		}

		try
		{
			await _reportDiagnosticAsync(phase, exception).ConfigureAwait(false);
		}
		catch
		{
			// Diagnostics are best effort and must never change scenario behavior.
		}
	}

	internal void SetCurrentStepId(string stepId)
	{
		var changed = false;
		lock (_sync)
		{
			if (!string.Equals(_currentStepId, stepId, StringComparison.Ordinal))
			{
				_currentStepId = stepId;
				changed = true;
			}
		}

		if (changed)
		{
			RaiseStateChanged();
		}
	}

	internal void ThrowIfSoftStopRequested()
	{
		if (IsSoftStopRequested())
		{
			throw new ScenarioSoftStopRequestedException();
		}
	}

	internal bool IsPauseRequested => PauseState.PauseRequested;

	/// <summary>Whether a manual pause request is retained for the next safe boundary.</summary>
	public bool PauseRequested => IsPauseRequested;

	internal void SetExpectedResponse(ScenarioExpectedResponse expected)
	{
		lock (_sync)
		{
			_expectedResponse = expected;
		}

		RaiseStateChanged();
	}

	internal void ClearExpectedResponse(ScenarioExpectedResponse expected)
	{
		var changed = false;
		lock (_sync)
		{
			if (Equals(_expectedResponse, expected))
			{
				_expectedResponse = null;
				changed = true;
			}
		}

		if (changed)
		{
			RaiseStateChanged();
		}
	}

	internal async Task WaitWhileManualPauseAsync(CancellationToken cancellationToken)
	{
		if (ManualResumeTask() is { } resumeRequested)
		{
			await resumeRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	// The resume signal of a manual pause, or null when the run is not manually paused.
	private Task? ManualResumeTask()
	{
		lock (_sync)
		{
			return _pause is { IsPaused: true, PauseKind: ScenarioPauseKind.Manual }
				? _resumeRequested?.Task
				: null;
		}
	}

	internal void RegisterPauseInterruption(CancellationTokenSource interruption)
	{
		ArgumentNullException.ThrowIfNull(interruption);
		var cancel = false;
		lock (_sync)
		{
			_pauseInterruption = interruption;
			cancel = _pause.PauseRequested;
		}

		if (cancel)
		{
			try
			{
				interruption.Cancel();
			}
			catch (ObjectDisposedException)
			{
				// The owner may have completed the wait concurrently; the pause request itself
				// remains set and is consumed at the next checkpoint.
			}
		}
	}

	internal void UnregisterPauseInterruption(CancellationTokenSource interruption)
	{
		lock (_sync)
		{
			if (ReferenceEquals(_pauseInterruption, interruption))
			{
				_pauseInterruption = null;
			}
		}
	}

	internal async Task<bool> PauseIfRequestedAsync(
		string stepId,
		CancellationToken cancellationToken,
		bool waitForResume = true)
	{
		Applied applied;
		lock (_sync)
		{
			applied = ApplyLocked(ScenarioPauseMachine.ReachBoundary);
		}

		if (!applied.Transition.Effects.HasFlag(ScenarioPauseEffects.Changed))
		{
			return false;
		}

		Act(applied, stepId);
		if (waitForResume && applied.ResumeTask is { } resumeRequested)
		{
			await resumeRequested.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		return true;
	}

	internal void PauseForTimeout(string stepId, string sessionId) =>
		PauseForAttention(
			stepId,
			sessionId,
			ScenarioAttentionReason.Watchdog,
			"step watchdog elapsed - needs attention");

	// Pauses for attention after a delivery that may have reached the terminal; the step goes on
	// waiting for its response file, which resumes the run.
	internal void PauseForUndelivered(string stepId, string sessionId, string reason) =>
		PauseForAttention(stepId, sessionId, ScenarioAttentionReason.Unconfirmed, reason);

	internal async Task PauseForBlockedDeliveryAsync(
		string stepId,
		string sessionId,
		string reason,
		TimeSpan retryDelay,
		CancellationToken cancellationToken)
	{
		PauseForAttention(stepId, sessionId, ScenarioAttentionReason.NothingSent, reason);
		if (ManualResumeTask() is { } manualResume)
		{
			await manualResume.WaitAsync(cancellationToken).ConfigureAwait(false);
			return;
		}

		await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
	}

	private void PauseForAttention(
		string stepId,
		string sessionId,
		ScenarioAttentionReason reason,
		string journalMessage)
	{
		Applied applied;
		lock (_sync)
		{
			applied = ApplyLocked(state => ScenarioPauseMachine.NeedAttention(state, sessionId, reason));
		}

		Act(applied, stepId, journalMessage);
	}

	private async Task RunLoopAsync()
	{
		var terminalState = ScenarioRunState.Failed;
		var needsFinalize = true;
		string? previousOutput = null;

		try
		{
			for (var iteration = 1; iteration <= MaxIterations; iteration++)
			{
				_cancellation.Token.ThrowIfCancellationRequested();
				SetCurrentIteration(iteration);
				await PauseIfRequestedAsync("run", _cancellation.Token).ConfigureAwait(false);
				ScenarioIterationContext context = new(
					iteration,
					_startPrompt,
					previousOutput,
					RoleBindings,
					_gateway,
					this,
					_stepWatchdogTimeout);

				var completed = await _program.RunIterationAsync(context, _cancellation.Token)
					.ConfigureAwait(false);
				previousOutput = context.PreviousOutput;

				if (IsAbortRequested())
				{
					throw new OperationCanceledException(_cancellation.Token);
				}

				if (completed)
				{
					terminalState = ScenarioRunState.Completed;
					needsFinalize = false;
					await FinalizeAsync(terminalState).ConfigureAwait(false);
					return;
				}

				if (IsSoftStopRequested())
				{
					await JournalAsync(
						"run",
						"stopped by user after step",
						ScenarioJournalLevel.Info,
						CancellationToken.None).ConfigureAwait(false);
					terminalState = ScenarioRunState.Aborted;
					needsFinalize = false;
					await FinalizeAsync(terminalState).ConfigureAwait(false);
					return;
				}
			}

			terminalState = ScenarioRunState.MaxIterationsReached;
			needsFinalize = false;
			await FinalizeAsync(terminalState).ConfigureAwait(false);
		}
		catch (ScenarioSoftStopRequestedException)
		{
			await JournalAsync(
				"run",
				"stopped by user after step",
				ScenarioJournalLevel.Info,
				CancellationToken.None).ConfigureAwait(false);
			terminalState = ScenarioRunState.Aborted;
			needsFinalize = false;
			await FinalizeAsync(terminalState).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (IsAbortRequested())
		{
			await SendEscapesAsync().ConfigureAwait(false);
			terminalState = ScenarioRunState.Aborted;
			needsFinalize = false;
			await FinalizeAsync(terminalState).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (TryGetFailureMessage(out var failureMessage))
		{
			await JournalAsync(
				"run",
				failureMessage,
				ScenarioJournalLevel.Error,
				CancellationToken.None).ConfigureAwait(false);
			terminalState = ScenarioRunState.Failed;
			needsFinalize = false;
			await FinalizeAsync(terminalState).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			try
			{
				await JournalAsync(
					"run",
					ex.Message,
					ScenarioJournalLevel.Error,
					CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				// A broken journal should not leave Completion permanently incomplete.
			}

			terminalState = ScenarioRunState.Failed;
			needsFinalize = false;
			await FinalizeAsync(terminalState).ConfigureAwait(false);
		}
		finally
		{
			if (needsFinalize)
			{
				await FinalizeAsync(terminalState).ConfigureAwait(false);
			}
		}
	}

	private async Task SendEscapesAsync()
	{
		foreach (var sessionId in RoleBindings.Values.Distinct(StringComparer.Ordinal))
		{
			if (!_gateway.IsSessionAlive(sessionId))
			{
				continue;
			}

			try
			{
				await _gateway.SendEscapeAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
			}
			catch
			{
				// Abort must still reach a terminal state if one terminal refuses Esc.
			}
		}
	}

	private void SetCurrentIteration(int iteration)
	{
		var changed = false;
		lock (_sync)
		{
			if (CurrentIteration != iteration)
			{
				CurrentIteration = iteration;
				changed = true;
			}
		}

		if (changed)
		{
			RaiseStateChanged();
		}
	}

	private async Task FinalizeAsync(ScenarioRunState terminalState)
	{
		bool shouldFinalize;
		lock (_sync)
		{
			shouldFinalize = !_finalized;
			if (shouldFinalize)
			{
				_pause = ScenarioPauseMachine.Finish(_pause, terminalState).State;
				_finalized = true;
			}
		}

		if (!shouldFinalize)
		{
			return;
		}

		await SendCompletionNoticeAsync(terminalState, CurrentIteration).ConfigureAwait(false);

		try
		{
			_releaseRun(this);
		}
		catch
		{
			// Completion is the lifecycle signal for UI cleanup and must not fault on lock release.
		}

		if (_program is IScenarioRunArtifactCleaner cleaner)
		{
			try
			{
				await cleaner.CleanupRunArtifactsAsync(RunId, CancellationToken.None).ConfigureAwait(false);
			}
			catch (Exception exception)
			{
				try
				{
					if (_reportCleanupFailureAsync is not null)
					{
						await _reportCleanupFailureAsync(exception).ConfigureAwait(false);
					}
				}
				catch
				{
					// Failure reporting is also best effort during terminal cleanup.
				}
			}
		}

		_completion.TrySetResult();
		_cancellation.Dispose();

		RaiseStateChanged();
	}

	private async Task SendCompletionNoticeAsync(
		ScenarioRunState state,
		int iterationsUsed)
	{
		if (Blueprint.CompletionNoticeRole is not { } role
			|| !RoleBindings.TryGetValue(role, out var sessionId)
			|| !_gateway.IsSessionAlive(sessionId)
			|| !ScenarioCompletionNotice.TryBuild(state, iterationsUsed, out var message))
		{
			return;
		}

		string outcome;
		ScenarioJournalLevel level;
		try
		{
			using CancellationTokenSource timeout = new(_completionNoticeTimeout);
			var delivered = await _gateway
				.SendPromptAsync(sessionId, message, timeout.Token)
				.WaitAsync(timeout.Token)
				.ConfigureAwait(false);
			(outcome, level) = delivered.IsSent
				? (message, ScenarioJournalLevel.Info)
				: (
					$"completion notice was not delivered to '{role}': {message}",
					ScenarioJournalLevel.Warning);
		}
		catch (Exception exception)
		{
			(outcome, level) = (
				$"completion notice to '{role}' failed: {exception.Message}",
				ScenarioJournalLevel.Warning);
		}

		try
		{
			await JournalAsync(
				"completion-notice",
				outcome,
				level,
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Diagnostics must never strand finalization.
		}
	}

	private void RaiseStateChanged()
	{
		try
		{
			StateChanged?.Invoke(this, EventArgs.Empty);
		}
		catch
		{
			// UI dispatchers can disappear during shutdown; lifecycle cleanup must continue.
		}
	}

	private bool IsSoftStopRequested()
	{
		lock (_sync)
		{
			return _pause.StopRequested;
		}
	}

	private bool IsAbortRequested()
	{
		lock (_sync)
		{
			return _abortRequested;
		}
	}

	private bool TryGetFailureMessage(out string message)
	{
		lock (_sync)
		{
			message = _failureMessage ?? "scenario failed";
			return _failureRequested;
		}
	}

	[SuppressMessage(
		"Design",
		"CA1064:Exceptions should be public",
		Justification = "This private sentinel is thrown and caught entirely inside ScenarioRunHandle and is not part of the public failure contract.")]
	private sealed class ScenarioSoftStopRequestedException : Exception;

	/// <summary>Releases the run cancellation source.</summary>
	public void Dispose() => _cancellation.Dispose();
}
