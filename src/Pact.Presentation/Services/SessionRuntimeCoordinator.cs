using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Pact.Core.Agents;
using Pact.Core.Platform;
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
	private static readonly TimeSpan DefaultStatePollInterval = TimeSpan.FromMilliseconds(250);
	private static readonly TimeSpan DefaultSessionReadyTimeout = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ClientPasteLandingTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan DefaultPreEnterCaptureTimeout = TimeSpan.FromSeconds(2);

	// Right button press and release at the top-left cell, SGR-encoded. A client tracking the
	// mouse pastes the clipboard on it; only the button matters, not the cell.
	private const string ClientPasteClick = "\u001b[<2;1;1M\u001b[<2;1;1m";
	private readonly ITerminalWebViewHost _terminalHost;
	private readonly Func<TerminalController> _controllerFactory;
	private readonly TimeSpan _scenarioSubmitSettleDelay;
	private readonly TimeSpan _startupSettleDelay;
	private readonly TimeSpan _confirmationTimeout;
	private readonly TimeSpan _statePollInterval;
	private readonly TimeSpan _sessionReadyTimeout;
	private readonly TimeSpan _pasteLandingTimeout;
	private readonly TimeSpan _preEnterCaptureTimeout;
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
	/// Copies the ids whose runtime currently owns an attached terminal controller.
	/// </summary>
	public IReadOnlyList<string> GetActiveSessionIds()
	{
		lock (_lifecycleGate)
		{
			return _runtimes
				.Where(pair => pair.Value.TryGetController(out _))
				.Select(pair => pair.Key)
				.Order(StringComparer.Ordinal)
				.ToArray();
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
		TimeSpan statePollInterval,
		TimeSpan? sessionReadyTimeout = null,
		TimeSpan? pasteLandingTimeout = null,
		TimeSpan? preEnterCaptureTimeout = null)
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
		_statePollInterval = statePollInterval;
		_sessionReadyTimeout = resolvedReadyTimeout;
		_pasteLandingTimeout = pasteLandingTimeout ?? ClientPasteLandingTimeout;
		_preEnterCaptureTimeout = preEnterCaptureTimeout ?? DefaultPreEnterCaptureTimeout;
	}

	/// <summary>
	/// Clipboard through which prompts are handed to a client that captures the mouse, which
	/// then pastes them itself on a right click. When <see langword="null"/>, every prompt is
	/// written as a bracketed paste.
	/// </summary>
	public IClipboardService? ClientPasteClipboard { get; set; }

	/// <summary>
	/// Creates, attaches, and starts a controller as the current lifecycle identity for a
	/// session. Replaced controllers are disposed before this method returns.
	/// </summary>
	public async Task<TerminalController> StartAsync(
		string sessionId,
		AgentKind kind,
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
			runtime.Kind = kind;
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
		AgentKind kind,
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
			kind,
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
	/// Waits until a launched session can receive a prompt: its retained stable classifier
	/// verdict is done, with no question or activity. The folder-trust dialog is answered with
	/// Enter, which is its own default, once per launched session; every other question, and a
	/// session that never becomes ready, end the wait as not ready.
	/// </summary>
	/// <remarks>
	/// One budget covers first output, the settle delay, and trust handling, so a review request
	/// cannot be blocked for a multiple of it. Readiness is a positive classifier fact: "no
	/// question" is not enough, because a starting TUI shows no question either and discards what
	/// it is given.
	/// </remarks>
	public async Task<SessionReadiness> WaitForSessionReadyAsync(
		string sessionId,
		Func<SessionScreenState?> readScreenState,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(readScreenState);

		var startedAt = Stopwatch.GetTimestamp();
		var trustAnswerSent = false;
		var lastStatusLine = string.Empty;
		var initialOutput = "none";
		var screenStates = 0;
		SessionScreenState? lastState = null;

		TimeSpan Remaining()
		{
			var remaining = _sessionReadyTimeout - Stopwatch.GetElapsedTime(startedAt);
			return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
		}

		if (TryGetRuntime(sessionId, out var runtime)
			&& runtime.InitialOutputTask is { } initialOutputTask
			&& Remaining() > TimeSpan.Zero)
		{
			try
			{
				await initialOutputTask.WaitAsync(Remaining(), cancellationToken).ConfigureAwait(false);
				initialOutput = "arrived";
			}
			catch (TimeoutException)
			{
				initialOutput = "timed-out";
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
				screenStates++;
				lastState = state;
				if (!string.IsNullOrWhiteSpace(state.StatusLine))
				{
					lastStatusLine = state.StatusLine;
				}

				if (state.VerdictState == TerminalScreenVerdictState.Done
					&& !state.IsBusy
					&& !state.InputRequested)
				{
					return Traced(new SessionReadiness(IsReady: true, StatusLine: string.Empty));
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

					var trustRuntime = runtime;
					if (!await trustRuntime.RunInputExclusiveAsync(
							() => controller.WriteInputAsync(trustRuntime.EncodeEnter()),
							cancellationToken).ConfigureAwait(false))
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
			return Traced(new SessionReadiness(
				IsReady: false,
				string.IsNullOrWhiteSpace(lastStatusLine)
					? "the session never became ready to receive input"
					: lastStatusLine));
		}

		// Facts only: no screen or status text, so the log never holds what the agent shows.
		SessionReadiness Traced(SessionReadiness readiness)
		{
			DeliveryTrace?.Invoke(
				$"readiness session={sessionId} ready={(readiness.IsReady ? "true" : "false")} "
					+ $"elapsedMs={(long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds} "
					+ $"initialOutput={initialOutput} screenStates={screenStates} "
					+ $"verdict={lastState?.VerdictState?.ToString() ?? "none"} "
					+ $"busy={(lastState?.IsBusy == true ? "true" : "false")} "
					+ $"question={(lastState?.InputRequested == true ? "true" : "false")} "
					+ $"trustAnswered={(trustAnswerSent ? "true" : "false")}");
			return readiness;
		}
	}

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

	/// <summary>Session facts that prompt delivery reads; set once by the shell.</summary>
	public ISessionFacts? SessionFacts { get; set; }

	/// <summary>Receives one metadata-only line per prompt delivery.</summary>
	public Action<string>? DeliveryTrace { get; set; }

	/// <summary>
	/// Delivers <paramref name="text"/> to a session's visible TUI: admission, paste, a check
	/// that the paste showed, Enter, and confirmation from a fresh screen capture. The whole
	/// delivery holds the session against other programmatic senders. Text is never pasted
	/// twice; Enter is repeated once only when a fresh capture shows the text still waiting.
	/// </summary>
	/// <param name="sessionId">Session receiving the prompt.</param>
	/// <param name="text">Prompt text, pasted as one paste.</param>
	/// <param name="submit">Whether to press Enter after the paste shows.</param>
	/// <param name="sender">Who sends it, which decides whether a scenario lock refuses it.</param>
	/// <param name="startIfNotRunning">
	/// Whether a stopped session may be started and awaited until ready; otherwise it is
	/// reported as <see cref="PromptDeliveryOutcome.NotRunning"/>.
	/// </param>
	/// <param name="cancellationToken">Cancels the delivery.</param>
	public async Task<PromptDeliveryResult> DeliverPromptAsync(
		string sessionId,
		string text,
		bool submit,
		PromptSender sender,
		bool startIfNotRunning,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(text);
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		DeliveryTrace trace = new(sessionId, sender);
		var runtime = GetOrCreateRuntime(sessionId);
		var result = await runtime.RunDeliveryExclusiveAsync(
			() =>
			{
				trace.GateWait = trace.Elapsed();
				return DeliverUnderGateAsync(
					sessionId,
					text,
					submit,
					sender,
					startIfNotRunning,
					facts,
					trace,
					cancellationToken);
			},
			cancellationToken).ConfigureAwait(false);
		DeliveryTrace?.Invoke(trace.Format(result));
		return result;
	}

	private async Task<PromptDeliveryResult> DeliverUnderGateAsync(
		string sessionId,
		string text,
		bool submit,
		PromptSender sender,
		bool startIfNotRunning,
		ISessionFacts facts,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		if (!TryGetActiveController(sessionId, out _, out _))
		{
			if (!startIfNotRunning)
			{
				return new(PromptDeliveryOutcome.NotRunning, "the session is not running");
			}

			var readiness = await StartAndWaitReadyCoreAsync(sessionId, facts, cancellationToken)
				.ConfigureAwait(false);
			trace.Start = readiness.IsReady ? "ok" : "not-ready";
			if (!readiness.IsReady)
			{
				return new(PromptDeliveryOutcome.NotRunning, readiness.StatusLine);
			}
		}

		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			return new(PromptDeliveryOutcome.NotRunning, "the session is not running");
		}

		var screen = facts.ReadScreen(sessionId);
		if (screen?.InputRequested == true)
		{
			trace.Admission = "question";
			return new(PromptDeliveryOutcome.RefusedQuestion, screen.StatusLine);
		}

		if (sender == PromptSender.Outsider && facts.IsScenarioLocked(sessionId))
		{
			trace.Admission = "locked";
			return new(PromptDeliveryOutcome.RefusedLocked);
		}

		trace.Admission = "ok";
		var profile = AgentScreenProfileSelector.ForKind(runtime.Kind);
		var keyboardAtEnter = 0L;
		var first = await runtime.RunInputExclusiveAsync<PromptDeliveryResult?>(
			async () =>
			{
				if (!await WritePasteAsync(runtime, controller, text, trace, cancellationToken).ConfigureAwait(false))
				{
					return new PromptDeliveryResult(PromptDeliveryOutcome.PasteNotShown, WriteAttempted: true);
				}

				if (!submit)
				{
					return new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true);
				}

				if (_scenarioSubmitSettleDelay > TimeSpan.Zero)
				{
					await Task.Delay(_scenarioSubmitSettleDelay, cancellationToken).ConfigureAwait(false);
				}

				// The paste can take seconds to show, and an agent that was already working may
				// open a question meanwhile; Enter would answer it instead of submitting.
				if (profile.ReadsComposer)
				{
					var check = await CaptureAsync(sessionId, profile, _preEnterCaptureTimeout, cancellationToken)
						.ConfigureAwait(false);
					if (check is not { } beforeEnter)
					{
						trace.Enter = "no-capture";
						return new PromptDeliveryResult(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true);
					}

					if (beforeEnter.Question is { } question)
					{
						return new PromptDeliveryResult(
							PromptDeliveryOutcome.RefusedQuestion,
							question,
							WriteAttempted: true);
					}
				}

				EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
				keyboardAtEnter = runtime.KeyboardInputCount;
				await WriteRequiredInputAsync(controller, runtime.EncodeEnter()).ConfigureAwait(false);
				trace.Enter = trace.Elapsed();
				return null;
			},
			cancellationToken).ConfigureAwait(false);
		if (first is { } stopped)
		{
			return stopped;
		}

		if (!profile.ReadsComposer)
		{
			trace.Confirmed = "no-model";
			return Sent(PromptDeliveryOutcome.Submitted);
		}

		var observed = await ConfirmAsync(sessionId, runtime, profile, keyboardAtEnter, trace, cancellationToken)
			.ConfigureAwait(false);
		if (observed is { } settled)
		{
			return settled;
		}

		// Timed out. Enter is repeated only on fresh evidence that the text is still waiting.
		var repaired = await runtime.RunInputExclusiveAsync<PromptDeliveryResult?>(
			async () =>
			{
				if (runtime.KeyboardInputCount != keyboardAtEnter)
				{
					trace.Keyboard = true;
					return Sent(PromptDeliveryOutcome.Unconfirmed);
				}

				var fresh = await CaptureAsync(sessionId, profile, _preEnterCaptureTimeout, cancellationToken)
					.ConfigureAwait(false);
				if (fresh is { Question: { } question })
				{
					return Sent(PromptDeliveryOutcome.RefusedQuestion, question);
				}

				if (fresh is { PromptIsEmpty: true })
				{
					trace.Confirmed = trace.Elapsed();
					return Sent(PromptDeliveryOutcome.Confirmed);
				}

				if (fresh is not { PromptIsEmpty: false })
				{
					return Sent(PromptDeliveryOutcome.Unconfirmed);
				}

				EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
				await WriteRequiredInputAsync(controller, runtime.EncodeEnter()).ConfigureAwait(false);
				trace.Repair = "enter";
				return null;
			},
			cancellationToken).ConfigureAwait(false);
		if (repaired is { } repairStopped)
		{
			return repairStopped;
		}

		return await ConfirmAsync(sessionId, runtime, profile, keyboardAtEnter, trace, cancellationToken)
				.ConfigureAwait(false)
			?? Sent(PromptDeliveryOutcome.Unconfirmed);
	}

	private static PromptDeliveryResult Sent(PromptDeliveryOutcome outcome, string statusLine = "") =>
		new(outcome, statusLine, WriteAttempted: true, SubmitAttempted: true);

	// Polls fresh captures until the composer is seen empty, a question opens, the user types,
	// or a capture goes unanswered; null when the confirmation timeout passes first.
	private async Task<PromptDeliveryResult?> ConfirmAsync(
		string sessionId,
		SessionRuntime runtime,
		IAgentScreenProfile profile,
		long keyboardAtEnter,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		var startedAt = Stopwatch.GetTimestamp();
		while (true)
		{
			var remaining = _confirmationTimeout - Stopwatch.GetElapsedTime(startedAt);
			if (remaining <= TimeSpan.Zero)
			{
				trace.Confirmed = "timeout";
				return null;
			}

			if (runtime.KeyboardInputCount != keyboardAtEnter)
			{
				trace.Keyboard = true;
				return Sent(PromptDeliveryOutcome.Unconfirmed);
			}

			var reading = await CaptureAsync(sessionId, profile, remaining, cancellationToken).ConfigureAwait(false);
			if (reading is not { } seen)
			{
				trace.Confirmed = "no-capture";
				return Sent(PromptDeliveryOutcome.Unconfirmed);
			}

			if (seen.Question is { } question)
			{
				return Sent(PromptDeliveryOutcome.RefusedQuestion, question);
			}

			if (seen.PromptIsEmpty == true)
			{
				trace.Confirmed = trace.Elapsed();
				return Sent(PromptDeliveryOutcome.Confirmed);
			}

			var wait = _confirmationTimeout - Stopwatch.GetElapsedTime(startedAt);
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait < _statePollInterval ? wait : _statePollInterval, cancellationToken)
					.ConfigureAwait(false);
			}
		}
	}

	private readonly record struct ScreenReading(string? Question, bool? PromptIsEmpty);

	// One capture, bounded by its own budget; null when the web view did not answer in time or
	// the session has no terminal. A late answer is dropped by the host.
	private async Task<ScreenReading?> CaptureAsync(
		string sessionId,
		IAgentScreenProfile profile,
		TimeSpan budget,
		CancellationToken cancellationToken)
	{
		var snapshot = await CaptureSnapshotAsync(sessionId, budget, cancellationToken).ConfigureAwait(false);
		if (snapshot is null)
		{
			return null;
		}

		var verdict = profile.Classify(snapshot);
		return new ScreenReading(
			verdict.State == TerminalScreenVerdictState.InputRequested ? verdict.Description : null,
			verdict.PromptIsEmpty);
	}

	private async Task<TerminalScreenSnapshot?> CaptureSnapshotAsync(
		string sessionId,
		TimeSpan budget,
		CancellationToken cancellationToken)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(budget);
		try
		{
			// The host may not honour the token, so the wait itself is bounded too.
			return await _terminalHost.CaptureScreenAsync(sessionId, limit.Token)
				.WaitAsync(limit.Token)
				.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	/// <summary>
	/// Starts a stopped session through <see cref="SessionFacts"/> and waits until it can
	/// receive a prompt, as <see cref="WaitForSessionReadyAsync(string, CancellationToken)"/>
	/// does. A running session is reported ready without waiting.
	/// </summary>
	public Task<SessionReadiness> StartAndWaitReadyAsync(string sessionId, CancellationToken cancellationToken)
	{
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		// Under the delivery gate, so it cannot race a delivery that is starting the same session.
		return GetOrCreateRuntime(sessionId).RunDeliveryExclusiveAsync(
			() => TryGetActiveController(sessionId, out _, out _)
				? Task.FromResult(new SessionReadiness(true, string.Empty))
				: StartAndWaitReadyCoreAsync(sessionId, facts, cancellationToken),
			cancellationToken);
	}

	private async Task<SessionReadiness> StartAndWaitReadyCoreAsync(
		string sessionId,
		ISessionFacts facts,
		CancellationToken cancellationToken)
	{
		GetOrCreateRuntime(sessionId).PrepareForControllerStart();
		await facts.StartAsync(sessionId, cancellationToken).ConfigureAwait(false);
		return await WaitForSessionReadyAsync(sessionId, () => facts.ReadScreen(sessionId), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>Waits for readiness, reading the screen through <see cref="SessionFacts"/>.</summary>
	public Task<SessionReadiness> WaitForSessionReadyAsync(string sessionId, CancellationToken cancellationToken)
	{
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		return WaitForSessionReadyAsync(sessionId, () => facts.ReadScreen(sessionId), cancellationToken);
	}

	/// <summary>
	/// Pastes text the way a keyboard paste would: no admission, no Enter, no confirmation. It
	/// counts as keyboard input for a delivery in progress.
	/// </summary>
	/// <exception cref="InvalidOperationException">The session is not running or refused the input.</exception>
	public async Task PasteAsKeyboardAsync(string sessionId, string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			throw new InvalidOperationException("Target session is not running.");
		}

		await runtime.RunInputExclusiveAsync(
			async () =>
			{
				// Counted in its turn: a key that waited behind a delivery's Enter is seen as typed after it.
				runtime.NoteKeyboardInput();
				await WriteRequiredInputAsync(controller, BuildPastedInput(text)).ConfigureAwait(false);
				return true;
			},
			CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>
	/// Pastes <paramref name="text"/> into a session and waits until the session shows it. A client
	/// that captures the mouse is handed the text through the clipboard and asked to paste it with
	/// a right click, so it receives the whole text as one paste: a client reading console key
	/// records otherwise gets a typed paste one key at a time, and on Windows Codex splits long
	/// text that way. Every other client gets a bracketed paste. Either way an Enter written before
	/// the paste shows submits nothing, and a key press can cancel a clipboard paste.
	/// </summary>
	/// <returns><see langword="false"/> when the paste did not show within the landing timeout.</returns>
	private async Task<bool> WritePasteAsync(
		SessionRuntime runtime,
		TerminalController controller,
		string text,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		var viaClipboard = ClientPasteClipboard is { } clipboard
			&& runtime.InputModes.IsSgrMouseActive
			&& await OfferOnClipboardAsync(clipboard, text).ConfigureAwait(false);
		trace.Paste = viaClipboard ? "clipboard" : "bracketed";
		var input = viaClipboard
			? await BuildClientPasteClicksAsync(runtime, cancellationToken).ConfigureAwait(false)
			: BuildPastedInput(text);

		// The wait starts before the write, so output from a fast paste cannot be missed.
		using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		var landed = runtime.WaitForClientPasteAsync(text, _pasteLandingTimeout, stopWaiting.Token);
		try
		{
			await WriteRequiredInputAsync(controller, input).ConfigureAwait(false);
		}
		catch
		{
			await stopWaiting.CancelAsync().ConfigureAwait(false);
			await landed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			throw;
		}

		try
		{
			await landed.ConfigureAwait(false);
			trace.Shown = trace.Elapsed();
			return true;
		}
		catch (TimeoutException)
		{
			trace.Shown = "timeout";
			return false;
		}
	}

	/// <summary>
	/// The clicks that paste the clipboard. While text is selected in its history, an agent
	/// does not paste on a right click (Codex checks this, Claude behaves the same), so a left
	/// click right after the draft first
	/// clears the selection and puts the cursor at the end of the draft, where the paste then
	/// adds to it. Without a recognized composer the right click goes to the corner.
	/// </summary>
	private async Task<string> BuildClientPasteClicksAsync(SessionRuntime runtime, CancellationToken cancellationToken)
	{
		var profile = AgentScreenProfileSelector.ForKind(runtime.Kind);
		var screen = profile.ReadsComposer
			? await CaptureSnapshotAsync(runtime.SessionId, _preEnterCaptureTimeout, cancellationToken).ConfigureAwait(false)
			: null;
		if (screen is null || profile.FindDraftEnd(screen) is not { } end)
		{
			return ClientPasteClick;
		}

		var at = FormattableString.Invariant($"{end.Column};{end.Row}");
		return $"\u001b[<0;{at}M\u001b[<0;{at}m\u001b[<2;{at}M\u001b[<2;{at}m";
	}

	/// <summary>
	/// Writes raw input to a session, applying the agent-specific rewrites that depend on its
	/// profile and current win32-input-mode state.
	/// </summary>
	public async Task WriteInputAsync(
		string sessionId,
		string input,
		Func<string, bool> isLocked,
		Func<string, string, Task> showIgnoredAsync)
	{
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(isLocked);
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
		var written = await runtime.RunInputExclusiveAsync(
			() =>
			{
				// Counted in its turn: a key that waited behind a delivery's Enter is seen as typed after it.
				runtime.NoteKeyboardInput();
				return controller.WriteInputAsync(runtime.RewriteTypedInput(input));
			},
			CancellationToken.None).ConfigureAwait(false);
		if (!written)
		{
			await showIgnoredAsync(sessionId, "Input write failed; session may have exited.").ConfigureAwait(false);
		}
	}

	/// <summary>Presses Esc in a live session.</summary>
	/// <exception cref="InvalidOperationException">The session is not running or refused the key.</exception>
	public async Task SendEscapeAsync(string sessionId)
	{
		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			throw new InvalidOperationException($"Session '{sessionId}' is not running.");
		}

		await runtime.RunInputExclusiveAsync(
			async () =>
			{
				await WriteRequiredInputAsync(controller, runtime.EncodeEscape()).ConfigureAwait(false);
				return true;
			},
			CancellationToken.None).ConfigureAwait(false);
	}

	/// <summary>
	/// Types a slash command into a live session's composer and presses Enter.
	/// </summary>
	/// <returns><see langword="false"/> when the session is not running or refused the input.</returns>
	public async Task<bool> SubmitCommandAsync(
		string sessionId,
		string command,
		TimeSpan delayBeforeEnter,
		TimeProvider timeProvider,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(timeProvider);
		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			return false;
		}

		return await runtime.RunInputExclusiveAsync(
			async () =>
			{
				if (!await controller.WriteInputAsync(command).ConfigureAwait(false))
				{
					return false;
				}

				await Task.Delay(delayBeforeEnter, timeProvider, cancellationToken).ConfigureAwait(false);
				return await controller.WriteInputAsync(runtime.EncodeEnter()).ConfigureAwait(false);
			},
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task WriteRequiredInputAsync(TerminalController controller, string input)
	{
		if (!await controller.WriteInputAsync(input).ConfigureAwait(false))
		{
			throw new InvalidOperationException("Target session input write failed.");
		}
	}

	// Text already on the clipboard, such as an agent's own copy of a selection, is not rewritten.
	private static async Task<bool> OfferOnClipboardAsync(IClipboardService clipboard, string text)
	{
		var current = await clipboard.GetTextAsync().ConfigureAwait(false);
		return string.Equals(NormalizeLineEndings(current), NormalizeLineEndings(text), StringComparison.Ordinal)
			|| await clipboard.TrySetTextAsync(text).ConfigureAwait(false);
	}

	private static string NormalizeLineEndings(string text) =>
		text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);

	private static string BuildPastedInput(string text)
	{
		return $"\u001b[200~{NormalizeLineEndings(text)}\u001b[201~";
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
