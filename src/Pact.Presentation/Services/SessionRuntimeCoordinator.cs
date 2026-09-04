using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Pact.Core.Presentation;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns the live <see cref="SessionRuntime"/> per session and serializes tab activation.
/// </summary>
/// <remarks>
/// Activation is gated and versioned: switching tabs quickly cancels the in-flight activation
/// rather than letting two run concurrently and leave the wrong terminal showing. Switching
/// never stops a background session's process.
/// </remarks>
public sealed class SessionRuntimeCoordinator : IDisposable
{
	private static readonly TimeSpan DefaultStartupSettleDelay = TimeSpan.FromMilliseconds(600);
	private static readonly TimeSpan DefaultScenarioSubmitSettleDelay = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan DefaultConfirmationTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan DefaultQuietComposerPeriod = TimeSpan.FromSeconds(2);
	private static readonly TimeSpan DefaultStatePollInterval = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan DefaultSessionReadyTimeout = TimeSpan.FromSeconds(30);
	private readonly ITerminalWebViewHost _terminalHost;
	private readonly Func<TerminalController> _controllerFactory;
	private readonly TimeSpan _scenarioSubmitSettleDelay;
	private readonly TimeSpan _startupSettleDelay;
	private readonly TimeSpan _confirmationTimeout;
	private readonly TimeSpan _quietComposerPeriod;
	private readonly TimeSpan _statePollInterval;
	private readonly TimeSpan _sessionReadyTimeout;
	private readonly SemaphoreSlim _activationGate = new(1, 1);
	private readonly Lock _lifecycleGate = new();
	private readonly Dictionary<string, SessionRuntime> _runtimes = new(StringComparer.Ordinal);
	private int _activationVersion;
	private string? _activeSessionId;
	private bool _stoppingAll;

	/// <summary>Live runtimes by session id.</summary>
	public IReadOnlyDictionary<string, SessionRuntime> Runtimes
	{
		get
		{
			lock (_lifecycleGate)
			{
				return new Dictionary<string, SessionRuntime>(_runtimes, StringComparer.Ordinal);
			}
		}
	}

	/// <summary>
	/// Creates a coordinator over the terminal host that renders the sessions.
	/// </summary>
	public SessionRuntimeCoordinator(ITerminalWebViewHost terminalHost)
		: this(
			terminalHost,
			static () => new TerminalController(),
			DefaultScenarioSubmitSettleDelay,
			DefaultConfirmationTimeout,
			DefaultStatePollInterval)
	{
	}

	/// <summary>
	/// Creates a coordinator with an explicit controller factory. The coordinator takes
	/// ownership of every controller returned by the factory.
	/// </summary>
	public SessionRuntimeCoordinator(
		ITerminalWebViewHost terminalHost,
		Func<TerminalController> controllerFactory)
		: this(
			terminalHost,
			controllerFactory,
			DefaultScenarioSubmitSettleDelay,
			DefaultConfirmationTimeout,
			DefaultStatePollInterval)
	{
	}

	internal SessionRuntimeCoordinator(
		ITerminalWebViewHost terminalHost,
		Func<TerminalController> controllerFactory,
		TimeSpan scenarioSubmitSettleDelay,
		TimeSpan confirmationTimeout,
		TimeSpan statePollInterval,
		TimeSpan? sessionReadyTimeout = null)
		: this(
			terminalHost,
			controllerFactory,
			scenarioSubmitSettleDelay,
			DefaultStartupSettleDelay,
			confirmationTimeout,
			DefaultQuietComposerPeriod,
			statePollInterval,
			sessionReadyTimeout)
	{
	}

	internal SessionRuntimeCoordinator(
		ITerminalWebViewHost terminalHost,
		Func<TerminalController> controllerFactory,
		TimeSpan scenarioSubmitSettleDelay,
		TimeSpan startupSettleDelay,
		TimeSpan confirmationTimeout,
		TimeSpan quietComposerPeriod,
		TimeSpan statePollInterval,
		TimeSpan? sessionReadyTimeout = null)
	{
		ArgumentNullException.ThrowIfNull(terminalHost);
		ArgumentNullException.ThrowIfNull(controllerFactory);
		ArgumentOutOfRangeException.ThrowIfLessThan(
			scenarioSubmitSettleDelay,
			TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(startupSettleDelay, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			confirmationTimeout,
			TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThan(quietComposerPeriod, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
			statePollInterval,
			TimeSpan.Zero);
		var resolvedReadyTimeout = sessionReadyTimeout ?? DefaultSessionReadyTimeout;
		ArgumentOutOfRangeException.ThrowIfLessThan(resolvedReadyTimeout, TimeSpan.Zero);

		_terminalHost = terminalHost;
		_controllerFactory = controllerFactory;
		_scenarioSubmitSettleDelay = scenarioSubmitSettleDelay;
		_startupSettleDelay = startupSettleDelay;
		_confirmationTimeout = confirmationTimeout;
		_quietComposerPeriod = quietComposerPeriod;
		_statePollInterval = statePollInterval;
		_sessionReadyTimeout = resolvedReadyTimeout;
	}

	/// <summary>
	/// Creates, attaches, and starts a controller as the current lifecycle identity for a
	/// session. Replaced controllers are disposed before this method returns.
	/// </summary>
	public async Task<TerminalController> StartAsync(
		string sessionId,
		TerminalStartOptions options,
		Action<string, TerminalController> exited,
		EventHandler<string>? outputHandler,
		Func<string, Task>? inputWritingHandler,
		EventHandler<string>? inputWrittenHandler,
		EventHandler<TerminalViewportChangedEventArgs>? viewportChangedHandler,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(exited);

		TerminalController controller;
		SessionRuntime runtime;
		TerminalController? replacedController;
		lock (_lifecycleGate)
		{
			if (_stoppingAll)
			{
				throw new InvalidOperationException("Terminal session coordinator is stopping.");
			}

			controller = _controllerFactory();
			void ExitedHandler(object? _, EventArgs __)
			{
				exited(sessionId, controller);
			}
			if (!_runtimes.TryGetValue(sessionId, out runtime!))
			{
				runtime = new SessionRuntime(sessionId);
				_runtimes.Add(sessionId, runtime);
			}

			runtime.PrepareForControllerStart();
			replacedController = runtime.AttachController(
				controller,
				outputHandler,
				ExitedHandler,
				inputWritingHandler,
				inputWrittenHandler,
				viewportChangedHandler);
		}

		try
		{
			if (replacedController is not null)
			{
				await replacedController.DisposeAsync().ConfigureAwait(false);
			}

			await controller.StartAsync(
				options.CommandLine,
				options.WorkingDirectory,
				options.Columns,
				options.Rows,
				options.EnvironmentVariables).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();

			lock (_lifecycleGate)
			{
				if (!_runtimes.TryGetValue(sessionId, out var currentRuntime)
					|| !ReferenceEquals(runtime, currentRuntime)
					|| !runtime.TryGetController(out var currentController)
					|| !ReferenceEquals(controller, currentController))
				{
					throw new InvalidOperationException(
						"Terminal session changed while its controller was starting.");
				}
			}

			return controller;
		}
		catch
		{
			bool detached;
			lock (_lifecycleGate)
			{
				detached = _runtimes.TryGetValue(sessionId, out var currentRuntime)
					&& ReferenceEquals(runtime, currentRuntime)
					&& runtime.DetachControllerIfSame(controller);
				if (detached)
				{
					_runtimes.Remove(sessionId);
				}
			}

			if (detached)
			{
				await controller.DisposeAsync().ConfigureAwait(false);
			}

			throw;
		}
	}

	/// <summary>
	/// Stops the current controller, clears its runtime identity, and starts a replacement.
	/// </summary>
	public async Task<TerminalController> RestartAsync(
		string sessionId,
		TerminalStartOptions options,
		Action<string, TerminalController> exited,
		EventHandler<string>? outputHandler,
		Func<string, Task>? inputWritingHandler,
		EventHandler<string>? inputWrittenHandler,
		EventHandler<TerminalViewportChangedEventArgs>? viewportChangedHandler,
		CancellationToken cancellationToken)
	{
		await StopAsync(sessionId).ConfigureAwait(false);
		return await StartAsync(
			sessionId,
			options,
			exited,
			outputHandler,
			inputWritingHandler,
			inputWrittenHandler,
			viewportChangedHandler,
			cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Removes and disposes the current runtime for one session.
	/// </summary>
	/// <returns><see langword="true"/> when a controller was detached and disposed.</returns>
	public async Task<bool> StopAsync(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		TerminalController? controller;
		lock (_lifecycleGate)
		{
			if (!_runtimes.Remove(sessionId, out var runtime))
			{
				return false;
			}

			controller = runtime.DetachController();
			if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
			{
				_activeSessionId = null;
			}
		}

		if (controller is null)
		{
			return false;
		}

		await controller.DisposeAsync().ConfigureAwait(false);
		return true;
	}

	/// <summary>
	/// Handles an exit only when <paramref name="controller"/> is still the current session
	/// identity. Stale callbacks cannot detach or dispose a replacement.
	/// </summary>
	public async Task<bool> HandleControllerExitedAsync(
		string sessionId,
		TerminalController controller)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(controller);

		lock (_lifecycleGate)
		{
			if (!_runtimes.TryGetValue(sessionId, out var runtime)
				|| !runtime.DetachControllerIfSame(controller))
			{
				return false;
			}

			_runtimes.Remove(sessionId);
			if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
			{
				_activeSessionId = null;
			}
		}

		// Exited is raised from the output pump. Yield before disposal waits for that pump.
		await Task.Yield();
		await controller.DisposeAsync().ConfigureAwait(false);
		return true;
	}

	/// <summary>
	/// Session currently shown, or <see langword="null"/> when no terminal is active.
	/// </summary>
	public string? ActiveSessionId
	{
		get
		{
			lock (_lifecycleGate)
			{
				return _activeSessionId;
			}
		}
		private set
		{
			lock (_lifecycleGate)
			{
				_activeSessionId = value;
			}
		}
	}

	/// <summary>Raised with a message to surface in the status area.</summary>
	public event EventHandler<string>? StatusMessage;

	/// <summary>Raised with the session id when a session's process exits.</summary>
	public event EventHandler<string>? SessionExited;

	/// <summary>
	/// Makes <paramref name="session"/> the visible terminal, running the supplied stages under
	/// the activation gate. A newer activation supersedes this one, in which case the remaining
	/// stages are skipped.
	/// </summary>
	public async Task ActivateSessionAsync(
		SessionViewModel session,
		Func<SessionViewModel, CancellationToken, Task> prepareAsync,
		Func<SessionViewModel, CancellationToken, Task<IDisposable?>> beginActivationScopeAsync,
		Func<SessionViewModel, CancellationToken, Task> activateRuntimeAsync,
		Func<CancellationToken, Task> focusAsync,
		Func<SessionViewModel, CancellationToken, Task> markFailedAsync,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		ArgumentNullException.ThrowIfNull(prepareAsync);
		ArgumentNullException.ThrowIfNull(beginActivationScopeAsync);
		ArgumentNullException.ThrowIfNull(activateRuntimeAsync);
		ArgumentNullException.ThrowIfNull(focusAsync);
		ArgumentNullException.ThrowIfNull(markFailedAsync);

		var activationVersion = Interlocked.Increment(ref _activationVersion);
		// No ConfigureAwait(false) here: the supplied delegates and event
		// subscribers touch WPF controls and must stay on the calling
		// (UI) thread's synchronization context.
		await _activationGate.WaitAsync(cancellationToken);
		try
		{
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			await prepareAsync(session, cancellationToken);
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			using var activationScope = await beginActivationScopeAsync(session, cancellationToken);
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			ActiveSessionId = session.Record.Id;
			await _terminalHost.CreateTerminalAsync(session.Record.Id);
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			await _terminalHost.ShowTerminalAsync(session.Record.Id);
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			await activateRuntimeAsync(session, cancellationToken);
			if (IsSuperseded(activationVersion))
			{
				return;
			}

			await focusAsync(cancellationToken);
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (!IsSuperseded(activationVersion))
			{
				RaiseStatusMessage($"Session failed: {ex.Message}");
				await markFailedAsync(session, cancellationToken);
			}
		}
		finally
		{
			_activationGate.Release();
		}
	}

	/// <summary>
	/// Returns the session's runtime, creating an empty one on first use.
	/// </summary>
	public SessionRuntime GetOrCreateRuntime(string sessionId)
	{
		lock (_lifecycleGate)
		{
			if (_stoppingAll)
			{
				throw new InvalidOperationException("Terminal session coordinator is stopping.");
			}

			if (!_runtimes.TryGetValue(sessionId, out var runtime))
			{
				runtime = new SessionRuntime(sessionId);
				_runtimes.Add(sessionId, runtime);
			}
			return runtime;
		}
	}

	/// <summary>Returns the existing runtime without creating one.</summary>
	public bool TryGetRuntime(
		string sessionId,
		[NotNullWhen(true)] out SessionRuntime? runtime)
	{
		lock (_lifecycleGate)
		{
			return _runtimes.TryGetValue(sessionId, out runtime);
		}
	}

	/// <summary>
	/// Returns the session runtime and its currently attached active controller as one
	/// identity-consistent lookup.
	/// </summary>
	public bool TryGetActiveController(
		string sessionId,
		[NotNullWhen(true)] out SessionRuntime? runtime,
		[NotNullWhen(true)] out TerminalController? controller)
	{
		lock (_lifecycleGate)
		{
			if (!_runtimes.TryGetValue(sessionId, out runtime)
				|| !runtime.TryGetController(out controller)
				|| !controller.IsActive)
			{
				runtime = null;
				controller = null;
				return false;
			}

			return true;
		}
	}

	/// <summary>
	/// Delivers prompt text to a session, optionally submitting it. Delivery targets the visible
	/// TUI through its terminal, never a headless API path.
	/// </summary>
	public async Task SendPromptAsync(
		SessionViewModel targetSession,
		string prompt,
		bool submit,
		bool startIfNeeded,
		bool enforceScenarioLock,
		Func<SessionViewModel, SessionRuntime, Task> startRuntimeAsync,
		Func<SessionRuntime, Task> waitForInitialOutputAsync)
	{
		ArgumentNullException.ThrowIfNull(targetSession);
		ArgumentNullException.ThrowIfNull(prompt);
		ArgumentNullException.ThrowIfNull(startRuntimeAsync);
		ArgumentNullException.ThrowIfNull(waitForInitialOutputAsync);

		if (enforceScenarioLock && targetSession.IsLockedByScenario)
		{
			throw new InvalidOperationException("Target session is locked by a running scenario.");
		}

		var runtime = GetOrCreateRuntime(targetSession.Record.Id);
		if (!runtime.TryGetController(out var controller) || !controller.IsActive)
		{
			if (!startIfNeeded)
			{
				throw new InvalidOperationException("Target session is not running.");
			}

			runtime.PrepareForControllerStart();
			await startRuntimeAsync(targetSession, runtime).ConfigureAwait(false);
			await waitForInitialOutputAsync(runtime).ConfigureAwait(false);
			if (_startupSettleDelay > TimeSpan.Zero)
			{
				await Task.Delay(_startupSettleDelay).ConfigureAwait(false);
			}
		}
		if (!runtime.TryGetController(out controller) || !controller.IsActive)
		{
			throw new InvalidOperationException("Target session is not running.");
		}

		var input = BuildPastedInput(prompt, submit);
		if (!await controller.WriteInputAsync(input).ConfigureAwait(false))
		{
			throw new InvalidOperationException("Target session input write failed.");
		}
	}

	/// <summary>
	/// Waits until a launched session can receive a prompt: its retained state reports no
	/// question, no activity, and an empty composer. The folder-trust dialog is answered with
	/// Enter, which is its own default, once per launched session; every other question, and a
	/// session that never becomes ready, end the wait as not ready.
	/// </summary>
	/// <remarks>
	/// One budget covers first output, the settle delay, and trust handling, so a review request
	/// cannot be blocked for a multiple of it. Readiness is a positive fact: "no question" is not
	/// enough, because a starting TUI shows no question either and discards what it is given.
	/// </remarks>
	public async Task<SessionReadiness> WaitForSessionReadyAsync(
		string sessionId,
		bool isCodex,
		Func<SessionScreenState?> readScreenState,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(readScreenState);

		var startedAt = Stopwatch.GetTimestamp();
		var trustAnswerSent = false;
		var lastStatusLine = string.Empty;

		TimeSpan Remaining()
		{
			var remaining = _sessionReadyTimeout - Stopwatch.GetElapsedTime(startedAt);
			return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
		}

		if (TryGetRuntime(sessionId, out var runtime)
			&& runtime.InitialOutputTask is { } initialOutput
			&& Remaining() > TimeSpan.Zero)
		{
			try
			{
				await initialOutput.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false);
			}
			catch (TimeoutException)
			{
				return NotReady();
			}
		}

		cancellationToken.ThrowIfCancellationRequested();
		var remainingAfterOutput = Remaining();
		if (_startupSettleDelay > TimeSpan.Zero && remainingAfterOutput > TimeSpan.Zero)
		{
			var settleDelay = remainingAfterOutput < _startupSettleDelay
				? remainingAfterOutput
				: _startupSettleDelay;
			await Task.Delay(settleDelay, cancellationToken).ConfigureAwait(false);
		}

		while (Remaining() > TimeSpan.Zero)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var state = readScreenState();
			if (state is not null)
			{
				if (!string.IsNullOrWhiteSpace(state.StatusLine))
				{
					lastStatusLine = state.StatusLine;
				}

				if (state.PromptIsEmpty == true && !state.IsBusy && !state.InputRequested)
				{
					return new SessionReadiness(IsReady: true, StatusLine: string.Empty);
				}

				if (state.InputRequested)
				{
					if (trustAnswerSent
						|| !string.Equals(
							state.StatusLine,
							AgentScreenProfileBase.TrustPromptDescription,
							StringComparison.Ordinal))
					{
						return NotReady();
					}

					if (!TryGetRuntime(sessionId, out runtime)
						|| !runtime.TryGetController(out var controller)
						|| !controller.IsActive)
					{
						return NotReady();
					}

					var submitInput = runtime.Win32InputMode.IsActive && isCodex
						? Win32InputEncoder.EnterKey
						: "\r";
					cancellationToken.ThrowIfCancellationRequested();
					if (!await controller.WriteInputAsync(submitInput).ConfigureAwait(false))
					{
						return NotReady();
					}

					trustAnswerSent = true;
				}
			}

			var remaining = Remaining();
			if (remaining <= TimeSpan.Zero)
			{
				break;
			}

			await Task.Delay(
				remaining < _statePollInterval ? remaining : _statePollInterval,
				cancellationToken).ConfigureAwait(false);
		}

		return NotReady();

		SessionReadiness NotReady()
		{
			return new SessionReadiness(
				IsReady: false,
				string.IsNullOrWhiteSpace(lastStatusLine)
					? "the session never became ready to receive input"
					: lastStatusLine);
		}
	}

	/// <summary>
	/// Pastes a scenario trigger and submits it, refusing a session that is holding a question or
	/// unsent text, and confirming from a new activity cycle.
	/// </summary>
	/// <remarks>
	/// A composer still holding the trigger proves that only the submit was dropped, so Enter
	/// alone is safe. An empty, idle composer is weaker evidence that the paste was discarded;
	/// one bounded repaste is preferred to leaving the run stalled.
	/// </remarks>
	public async Task<PromptDeliveryResult> WriteScenarioPromptAndSubmitAsync(
		string sessionId,
		string trigger,
		bool isCodex,
		bool confirmDelivery,
		Func<SessionScreenState?> readScreenState,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(trigger);
		ArgumentNullException.ThrowIfNull(readScreenState);

		if (!TryGetRuntime(sessionId, out var runtime)
			|| !runtime.TryGetController(out var controller)
			|| !controller.IsActive)
		{
			throw new InvalidOperationException("Target scenario session is not running.");
		}

		var submitInput = runtime.Win32InputMode.IsActive && isCodex
			? Win32InputEncoder.EnterKey
			: "\r";

		var gate = readScreenState();
		if (gate?.IsBusy == true)
		{
			return new PromptDeliveryResult(PromptDeliveryOutcome.BlockedByBusy);
		}

		if (gate?.InputRequested == true)
		{
			return new PromptDeliveryResult(
				PromptDeliveryOutcome.BlockedByInputRequest,
				gate.StatusLine);
		}

		if (gate?.PromptIsEmpty is false)
		{
			return new PromptDeliveryResult(PromptDeliveryOutcome.BlockedByPendingInput);
		}

		var epochBefore = gate?.ActivityEpoch ?? 0;
		await SendAsync().ConfigureAwait(false);
		if (!confirmDelivery)
		{
			return new PromptDeliveryResult(
				PromptDeliveryOutcome.Written,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true);
		}

		var observation = await ObserveAsync(epochBefore).ConfigureAwait(false);
		if (observation.Blocked is { } blocked)
		{
			return blocked with { WriteAttempted = true, SubmitAttempted = true };
		}

		if (observation.Confirmed)
		{
			return new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true);
		}

		if (observation.HoldsText)
		{
			EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
			await WriteAsync(submitInput).ConfigureAwait(false);
		}
		else if (observation.QuietAndEmpty)
		{
			EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
			await SendAsync().ConfigureAwait(false);
		}
		else
		{
			return new PromptDeliveryResult(
				PromptDeliveryOutcome.Written,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true);
		}

		var repaired = await ObserveAsync(epochBefore).ConfigureAwait(false);
		return repaired.Blocked is { } blockedAfterRepair
			? blockedAfterRepair with { WriteAttempted = true, SubmitAttempted = true }
			: new PromptDeliveryResult(
				repaired.Confirmed ? PromptDeliveryOutcome.Confirmed : PromptDeliveryOutcome.Written,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true);

		async Task SendAsync()
		{
			await WriteAsync(BuildPastedInput(trigger, submit: false)).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			if (_scenarioSubmitSettleDelay > TimeSpan.Zero)
			{
				await Task.Delay(_scenarioSubmitSettleDelay, cancellationToken).ConfigureAwait(false);
			}

			cancellationToken.ThrowIfCancellationRequested();
			EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
			await WriteAsync(submitInput).ConfigureAwait(false);
		}

		async Task<Observation> ObserveAsync(long epoch)
		{
			var startedAt = Stopwatch.GetTimestamp();
			var quietSince = default(long?);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (readScreenState() is { } state)
				{
					if (state.InputRequested)
					{
						return new Observation(
							false,
							false,
							false,
							new PromptDeliveryResult(
								PromptDeliveryOutcome.BlockedByInputRequest,
								state.StatusLine));
					}

					if (state.ActivityEpoch > epoch)
					{
						return new Observation(true, false, false, null);
					}

					if (state.PromptIsEmpty is false)
					{
						return new Observation(false, true, false, null);
					}

					if (state.PromptIsEmpty is true && !state.IsBusy)
					{
						quietSince ??= Stopwatch.GetTimestamp();
						if (Stopwatch.GetElapsedTime(quietSince.Value) >= _quietComposerPeriod)
						{
							return new Observation(false, false, true, null);
						}
					}
					else
					{
						quietSince = null;
					}
				}

				var remaining = _confirmationTimeout - Stopwatch.GetElapsedTime(startedAt);
				if (remaining <= TimeSpan.Zero)
				{
					return new Observation(false, false, false, null);
				}

				await Task.Delay(
					remaining < _statePollInterval ? remaining : _statePollInterval,
					cancellationToken).ConfigureAwait(false);
			}
		}

		async Task WriteAsync(string input)
		{
			if (!await controller.WriteInputAsync(input).ConfigureAwait(false))
			{
				throw new InvalidOperationException("Target session input write failed.");
			}
		}
	}

	private readonly record struct Observation(
		bool Confirmed,
		bool HoldsText,
		bool QuietAndEmpty,
		PromptDeliveryResult? Blocked);

	private void EnsureScenarioControllerIsCurrent(
		string sessionId,
		SessionRuntime runtime,
		TerminalController controller)
	{
		if (!TryGetRuntime(sessionId, out var currentRuntime)
			|| !ReferenceEquals(runtime, currentRuntime)
			|| !runtime.TryGetController(out var currentController)
			|| !ReferenceEquals(controller, currentController)
			|| !controller.IsActive)
		{
			throw new InvalidOperationException("Target scenario session changed before submit.");
		}
	}

	/// <summary>
	/// Writes raw input to a session, applying the agent-specific rewrites that depend on its
	/// profile and current win32-input-mode state.
	/// </summary>
	public async Task WriteInputAsync(
		string sessionId,
		string input,
		Func<string, bool> isLocked,
		Func<string, bool> isCodex,
		Func<string, string, Task> showIgnoredAsync)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(isLocked);
		ArgumentNullException.ThrowIfNull(isCodex);
		ArgumentNullException.ThrowIfNull(showIgnoredAsync);

		if (!TryGetRuntime(sessionId, out var runtime)
			|| !runtime.TryGetController(out var controller)
			|| !controller.IsActive)
		{
			await showIgnoredAsync(sessionId, "Session is not running; input ignored.").ConfigureAwait(false);
			return;
		}
		if (isLocked(sessionId))
		{
			RaiseStatusMessage("Terminal is locked by a running scenario");
			return;
		}
		input = RewriteInput(input, runtime.Win32InputMode.IsActive, isCodex(sessionId));
		if (!await controller.WriteInputAsync(input).ConfigureAwait(false))
		{
			await showIgnoredAsync(sessionId, "Input write failed; session may have exited.").ConfigureAwait(false);
		}
	}

	internal static string RewriteInput(string input, bool win32InputModeActive, bool isCodex)
	{
		if (!win32InputModeActive || !isCodex)
		{
			return input;
		}

		if (string.Equals(input, "\n", StringComparison.Ordinal))
		{
			return Win32InputEncoder.ShiftEnter;
		}

		if (input.Length == 1 && input[0] == (char)0x1b)
		{
			return Win32InputEncoder.EscapeKey;
		}

		if (input.Length == 1 && input[0] == (char)0x03)
		{
			return Win32InputEncoder.CtrlC;
		}

		return input;
	}

	private static string BuildPastedInput(string text, bool submit)
	{
		var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
		var pastedInput = $"\u001b[200~{normalized}\u001b[201~";
		return submit ? pastedInput + "\r" : pastedInput;
	}

	/// <summary>
	/// Supersedes any in-flight activation, so its remaining stages are skipped.
	/// </summary>
	public void CancelActivation() => Interlocked.Increment(ref _activationVersion);

	/// <summary>
	/// Stops every session's process during shutdown, giving each a bounded chance to exit
	/// gracefully. A session that fails to stop does not prevent the others from stopping.
	/// </summary>
	public async Task StopAllAsync(
		Func<string, SessionStatus, Task> updateStatusAsync,
		Action afterStop)
	{
		ArgumentNullException.ThrowIfNull(updateStatusAsync);
		ArgumentNullException.ThrowIfNull(afterStop);

		KeyValuePair<string, TerminalController>[] controllers;
		lock (_lifecycleGate)
		{
			_stoppingAll = true;
			controllers = _runtimes
				.Select(pair => new KeyValuePair<string, TerminalController?>(
					pair.Key,
					pair.Value.DetachController()))
				.Where(pair => pair.Value is not null)
				.Select(pair => new KeyValuePair<string, TerminalController>(
					pair.Key,
					pair.Value!))
				.ToArray();
		}

		foreach ((var sessionId, var controller) in controllers)
		{
			try
			{
				await controller.DisposeAsync().ConfigureAwait(false);
				await updateStatusAsync(sessionId, SessionStatus.Exited).ConfigureAwait(false);
				SessionExited?.Invoke(this, sessionId);
			}
			catch (Exception ex)
			{
				RaiseStatusMessage($"Session shutdown failed for '{sessionId}': {ex.Message}");
			}
		}
		lock (_lifecycleGate)
		{
			_runtimes.Clear();
			_activeSessionId = null;
		}
		afterStop();
	}

	private void RaiseStatusMessage(string message)
	{
		try
		{
			StatusMessage?.Invoke(this, message);
		}
		catch (Exception)
		{
			// Status reporting is best-effort; a faulty subscriber must not break coordination.
		}
	}

	private bool IsSuperseded(int version) => version != Volatile.Read(ref _activationVersion);

	/// <summary>Releases the activation serialization gate.</summary>
	public void Dispose() => _activationGate.Dispose();
}
