using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;

namespace Pact.Core.Sessions;

/// <summary>
/// Owns terminal-tab status evidence for one session and classifies stable
/// screen snapshots through the session's agent-specific profile.
/// </summary>
public sealed class TerminalTabStatusEngine
{
	private readonly Lock _gate = new();
	private readonly IAgentScreenProfile _profile;
	private SessionStatus _lifecycleStatus;
	private bool _selected;
	private bool _windowVisible;
	private bool _windowActive;
	private DateTimeOffset? _activityStartedAt;
	private bool _activityInProgress;
	private long _activityEpoch;
	private bool _hasUnreadCompletion;
	private bool _inputRequested;
	private string _inputRequestStatusLine = string.Empty;
	private bool? _promptIsEmpty;
	private TerminalPromptEvidence? _promptEvidence;
	private TerminalTabIndicator _currentIndicator;
	private TerminalScreenVerdictState _currentVerdictState;
	private string _currentDescription = string.Empty;
	private TerminalScreenVerdictState? _lastStableVerdictState;
	private string _lastStableVerdictDescription = string.Empty;
	private DateTimeOffset? _lastClassificationAt;

	/// <summary>
	/// Creates status evidence for one terminal session using the supplied
	/// profile to interpret stable visible-screen snapshots.
	/// </summary>
	public TerminalTabStatusEngine(
		string sessionId,
		AgentKind terminalKind,
		IAgentScreenProfile profile,
		SessionStatus lifecycleStatus,
		bool selected,
		bool windowVisible,
		bool windowActive)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(profile);
		SessionId = sessionId;
		TerminalKind = terminalKind;
		_profile = profile;
		_lifecycleStatus = lifecycleStatus;
		_selected = selected;
		_windowVisible = windowVisible;
		_windowActive = windowActive;
		_currentIndicator = CalculateIndicator();
	}

	/// <summary>Raised when the derived indicator or accepted classifier description changes.</summary>
	public event EventHandler<TerminalTabIndicatorChangedEventArgs>? IndicatorChanged;

	/// <summary>Raised when metadata shown by terminal diagnostics changes.</summary>
	public event EventHandler<TerminalClassifierDiagnosticsChangedEventArgs>? DiagnosticsChanged;

	/// <summary>Session this engine tracks.</summary>
	public string SessionId { get; }

	/// <summary>Agent running in the session, which selects the screen profile.</summary>
	public AgentKind TerminalKind { get; }

	/// <summary>Last reported process lifecycle state.</summary>
	public SessionStatus LifecycleStatus { get { lock (_gate) { return _lifecycleStatus; } } }

	/// <summary>Whether this tab is the selected one.</summary>
	public bool Selected { get { lock (_gate) { return _selected; } } }

	/// <summary>Whether the application window is visible.</summary>
	public bool WindowVisible { get { lock (_gate) { return _windowVisible; } } }

	/// <summary>Whether the application window is active.</summary>
	public bool WindowActive { get { lock (_gate) { return _windowActive; } } }

	/// <summary>Kind of the most recent event, or <see langword="null"/> before any arrives.</summary>
	public TerminalTabEventKind? LastEventKind
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>Timestamp of the most recent event.</summary>
	public DateTimeOffset? LastEventAt
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>When the user last sent input.</summary>
	public DateTimeOffset? LastUserInputAt
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>Last character the user sent, used to distinguish a submit from ordinary typing.</summary>
	public char? LastUserCharacter
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>
	/// When the current activity began, or <see langword="null"/> when the session is not busy.
	/// </summary>
	public DateTimeOffset? ActivityStartedAt { get { lock (_gate) { return _activityStartedAt; } } }

	/// <summary>
	/// Monotonic count of activity cycles, including a new submit into an already-busy session.
	/// </summary>
	public long ActivityEpoch { get { lock (_gate) { return _activityEpoch; } } }

	/// <summary>Most recently reported viewport width in cells.</summary>
	public int? LastColumns
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>Most recently reported viewport height in cells.</summary>
	public int? LastRows
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>Whether the agent is currently judged to be working.</summary>
	public bool ActivityInProgress { get { lock (_gate) { return _activityInProgress; } } }

	/// <summary>
	/// Whether a completion happened that the user has not seen. Cleared by selecting the tab
	/// while the window is visible and active.
	/// </summary>
	public bool HasUnreadCompletion { get { lock (_gate) { return _hasUnreadCompletion; } } }

	/// <summary>Indicator currently derived from the accumulated evidence.</summary>
	public TerminalTabIndicator CurrentIndicator { get { lock (_gate) { return _currentIndicator; } } }

	/// <summary>Latest accepted classifier text shown below the terminal title.</summary>
	public string CurrentDescription { get { lock (_gate) { return _currentDescription; } } }

	/// <summary>Atomically reads classifier and delivery metadata without terminal-screen text.</summary>
	public TerminalClassifierDiagnostics CurrentDiagnostics
	{
		get { lock (_gate) { return CreateDiagnosticsUnsafe(); } }
	}

	/// <summary>
	/// Atomically reads the delivery-relevant status retained from settled screen evidence.
	/// </summary>
	public SessionStatusSnapshot CurrentStatus
	{
		get
		{
			lock (_gate)
			{
				return new(
					_currentIndicator,
					_inputRequested,
					_inputRequestStatusLine,
					_promptIsEmpty,
					_activityEpoch);
			}
		}
	}

	/// <summary>
	/// Gets the latest visible-screen snapshot accepted as evidence.
	/// </summary>
	public string LastScreenSnapshot
	{
		get { lock (_gate) { return field; } }

		private set;
	} = string.Empty;

	/// <summary>
	/// Gets the full text of the last snapshot accepted as stable evidence while the process
	/// was running.
	/// </summary>
	/// <remarks>
	/// This differs from <see cref="LastScreenSnapshot"/>, which also records mid-repaint
	/// snapshots. Only a settled screen is suitable for quoting to a reader.
	/// </remarks>
	public string LastStableScreen
	{
		get { lock (_gate) { return field; } }

		private set;
	} = string.Empty;

	/// <summary>
	/// Gets the agent's last recognized message from the verdict produced while classifying
	/// the stable screen.
	/// </summary>
	/// <remarks>
	/// The value remains until a newer message is recognized. Read
	/// <see cref="LastMessageIsCurrent"/> with it to distinguish retained words from a message
	/// visible on the current stable screen.
	/// </remarks>
	public string LastMessage
	{
		get { lock (_gate) { return field; } }

		private set;
	} = string.Empty;

	/// <summary>
	/// Gets whether <see cref="LastMessage"/> was recognized on
	/// <see cref="LastStableScreen"/>.
	/// </summary>
	public bool LastMessageIsCurrent
	{
		get { lock (_gate) { return field; } }

		private set;
	}

	/// <summary>
	/// Records a lifecycle change. Any state in which the process is no longer running also
	/// ends the current activity, so a tab cannot stay busy after its process is gone.
	/// </summary>
	public void SetLifecycleStatus(SessionStatus status, DateTimeOffset occurredAt) =>
		Process(TerminalTabEventKind.LifecycleChanged, occurredAt, () =>
		{
			_lifecycleStatus = status;
			if (status is SessionStatus.Stopped or SessionStatus.Exited or SessionStatus.Failed)
			{
				EndActivity();
				_inputRequested = false;
				_inputRequestStatusLine = string.Empty;
			}
		});

	/// <summary>
	/// Records that this tab became or stopped being the selected one. Selection is what
	/// acknowledges an unread completion, provided the window is also visible and active.
	/// </summary>
	public void SetSelected(bool selected, DateTimeOffset occurredAt) =>
		Process(TerminalTabEventKind.SelectionChanged, occurredAt, () => _selected = selected);

	/// <summary>
	/// Replaces visibility and activation together so acknowledgement never
	/// observes a transient combination assembled from two view events.
	/// </summary>
	public void SetWindowFacts(bool visible, bool active, DateTimeOffset occurredAt) =>
		Process(TerminalTabEventKind.WindowFactsChanged, occurredAt, () =>
		{
			_windowVisible = visible;
			_windowActive = active;
		});

	/// <summary>
	/// Records a session launch. A resume starts an activity immediately, because the agent
	/// begins replaying its conversation before producing any screen evidence; a normal start
	/// waits for real evidence.
	/// </summary>
	public void OnSessionStarted(TerminalStartMode mode, DateTimeOffset occurredAt) =>
		Process(TerminalTabEventKind.SessionStarted, occurredAt, () =>
		{
			if (mode == TerminalStartMode.Resume)
			{
				StartActivity(occurredAt);
			}
		});

	/// <summary>
	/// Records user input. Only input containing a carriage return starts an activity, so
	/// ordinary typing into the composer does not mark the tab busy.
	/// </summary>
	public void OnUserInput(string input, DateTimeOffset occurredAt)
	{
		ArgumentNullException.ThrowIfNull(input);
		Process(TerminalTabEventKind.UserInput, occurredAt, () =>
		{
			if (input.Length == 0)
			{
				return;
			}

			LastUserInputAt = occurredAt;
			LastUserCharacter = input[^1];
			if (input.Contains('\r', StringComparison.Ordinal))
			{
				StartActivity(occurredAt, restart: true);
			}
		});
	}

	/// <summary>
	/// Classifies a visible-screen snapshot when the session is running;
	/// non-running sessions retain the snapshot without consulting the profile.
	/// A snapshot captured while the screen was still repainting
	/// (<paramref name="stable"/> = false) may be a half-drawn frame, so only
	/// its busy verdict is trusted and done verdicts are ignored.
	/// </summary>
	public void OnScreenSnapshot(string screenText, DateTimeOffset occurredAt, bool stable = true)
	{
		ArgumentNullException.ThrowIfNull(screenText);
		Process(TerminalTabEventKind.ScreenSnapshot, occurredAt, () =>
		{
			LastScreenSnapshot = screenText;
			if (_lifecycleStatus != SessionStatus.Running)
			{
				return;
			}

			var verdict = _profile.Classify(screenText);
			if (stable)
			{
				LastStableScreen = screenText;
				_lastStableVerdictState = verdict.State;
				_lastStableVerdictDescription = verdict.Description;
				_lastClassificationAt = occurredAt;
				_promptIsEmpty = verdict.PromptIsEmpty;
				_promptEvidence = verdict.PromptEvidence;
				var clearedInputRequest =
					_inputRequested && verdict.State != TerminalScreenVerdictState.InputRequested;
				if (verdict.State == TerminalScreenVerdictState.InputRequested)
				{
					_inputRequested = true;
					_inputRequestStatusLine = verdict.Description;
				}
				else
				{
					_inputRequested = false;
					_inputRequestStatusLine = string.Empty;
				}

				if (clearedInputRequest)
				{
					_currentVerdictState = verdict.State;
					_currentDescription = verdict.Description;
				}

				var recognized = !string.IsNullOrEmpty(verdict.LastMessage);
				LastMessageIsCurrent = recognized;
				if (recognized)
				{
					LastMessage = verdict.LastMessage;
				}
			}

			switch (verdict.State)
			{
				case TerminalScreenVerdictState.Busy:
					ApplyVerdict(verdict);
					StartActivity(occurredAt);
					break;
				case TerminalScreenVerdictState.Done when stable && _activityInProgress:
					ApplyVerdict(verdict);
					EndActivity();
					_hasUnreadCompletion = true;
					break;
				case TerminalScreenVerdictState.InputRequested when stable:
					ApplyVerdict(verdict);
					EndActivity();
					break;
				case TerminalScreenVerdictState.Unknown
					when !string.IsNullOrEmpty(verdict.Description):
					_currentDescription = verdict.Description;
					break;
			}

			if (stable && verdict.PromptIsEmpty == true && _activityInProgress)
			{
				EndActivity();
				if (verdict.State == TerminalScreenVerdictState.Done)
				{
					_hasUnreadCompletion = true;
				}
			}
		});
	}

	/// <summary>
	/// Records a viewport resize, which invalidates screen-derived conclusions because the
	/// agent repaints at the new dimensions.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">A dimension is zero or negative.</exception>
	public void OnViewportChanged(int columns, int rows, DateTimeOffset occurredAt)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rows);
		Process(TerminalTabEventKind.ViewportChanged, occurredAt, () =>
		{
			LastColumns = columns;
			LastRows = rows;
		});
	}

	private void Process(TerminalTabEventKind eventKind, DateTimeOffset occurredAt, Action update)
	{
		TerminalTabIndicatorChangedEventArgs? change = null;
		TerminalClassifierDiagnostics? diagnostics = null;
		lock (_gate)
		{
			var previousDiagnostics = CreateDiagnosticsUnsafe();
			var previousDescription = _currentDescription;
			update();
			LastEventKind = eventKind;
			LastEventAt = occurredAt;
			if (_selected && _windowVisible && _windowActive)
			{
				_hasUnreadCompletion = false;
			}

			var next = CalculateIndicator();
			if (next != _currentIndicator || _currentDescription != previousDescription)
			{
				_currentIndicator = next;
				change = new TerminalTabIndicatorChangedEventArgs(
					next,
					_activityStartedAt,
					_currentDescription);
			}

			var currentDiagnostics = CreateDiagnosticsUnsafe();
			if (currentDiagnostics != previousDiagnostics)
			{
				diagnostics = currentDiagnostics;
			}
		}

		if (change is not null)
		{
			IndicatorChanged?.Invoke(this, change);
		}

		if (diagnostics is not null)
		{
			DiagnosticsChanged?.Invoke(
				this,
				new TerminalClassifierDiagnosticsChangedEventArgs(diagnostics));
		}
	}

	private TerminalClassifierDiagnostics CreateDiagnosticsUnsafe() => new(
		SessionId,
		TerminalKind,
		_lifecycleStatus,
		_lastStableVerdictState,
		_lastStableVerdictDescription,
		_currentIndicator,
		_currentDescription,
		_promptIsEmpty,
		_inputRequested,
		_inputRequestStatusLine,
		_activityInProgress,
		_activityEpoch,
		_hasUnreadCompletion,
		LastColumns,
		LastRows,
		_lastClassificationAt,
		_promptEvidence);

	private void ApplyVerdict(TerminalScreenVerdict verdict)
	{
		if (verdict.State != _currentVerdictState)
		{
			_currentVerdictState = verdict.State;
			_currentDescription = verdict.Description;
		}
		else if (!string.IsNullOrEmpty(verdict.Description))
		{
			_currentDescription = verdict.Description;
		}
	}

	private void StartActivity(DateTimeOffset occurredAt, bool restart = false)
	{
		if (_activityInProgress && !restart)
		{
			return;
		}

		_activityInProgress = true;
		_activityStartedAt = occurredAt;
		_activityEpoch++;
	}

	private void EndActivity()
	{
		_activityInProgress = false;
	}

	private TerminalTabIndicator CalculateIndicator()
	{
		if (_lifecycleStatus == SessionStatus.Failed)
		{
			return TerminalTabIndicator.Failed;
		}

		if (_lifecycleStatus is SessionStatus.Stopped or SessionStatus.Exited)
		{
			return TerminalTabIndicator.Paused;
		}

		if (_inputRequested)
		{
			return TerminalTabIndicator.InputRequested;
		}

		if (_activityInProgress)
		{
			return TerminalTabIndicator.Busy;
		}

		return _hasUnreadCompletion
			? TerminalTabIndicator.Unread
			: TerminalTabIndicator.None;
	}

}

/// <summary>
/// Atomically retained status used by prompt delivery and presentation projections.
/// </summary>
/// <param name="Indicator">Current tab indicator.</param>
/// <param name="InputRequested">Whether the agent is waiting for a human answer.</param>
/// <param name="StatusLine">Description of the pending request, or an empty string.</param>
/// <param name="PromptIsEmpty">
/// Whether the visible composer is blank; <see langword="null"/> when the screen cannot say.
/// </param>
/// <param name="ActivityEpoch">Monotonic activity cycle observed for the session.</param>
public sealed record SessionStatusSnapshot(
	TerminalTabIndicator Indicator,
	bool InputRequested,
	string StatusLine,
	bool? PromptIsEmpty,
	long ActivityEpoch);
