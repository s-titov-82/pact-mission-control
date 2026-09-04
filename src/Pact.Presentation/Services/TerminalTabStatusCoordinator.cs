using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns one <see cref="TerminalTabStatusEngine"/> per session and projects their derived
/// indicators onto the corresponding view models.
/// </summary>
/// <remarks>
/// Engine state is guarded by an internal lock, while view-model updates are dispatched to the
/// UI thread, so evidence can be reported from background readers without marshalling at every
/// call site.
/// </remarks>
public sealed class TerminalTabStatusCoordinator
{
	private readonly Lock _gate = new();
	private readonly Action<Action> _dispatchToUi;
	private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);
	private string? _selectedSessionId;
	private bool _windowVisible;
	private bool _windowActive;

	/// <summary>
	/// Creates a coordinator.
	/// </summary>
	/// <param name="dispatchToUi">Posts an action to the UI thread.</param>
	public TerminalTabStatusCoordinator(Action<Action> dispatchToUi)
	{
		_dispatchToUi = dispatchToUi ?? throw new ArgumentNullException(nameof(dispatchToUi));
	}

	/// <summary>Raised on the UI dispatcher when a registered terminal's diagnostic facts change.</summary>
	public event EventHandler<TerminalClassifierDiagnosticsChangedEventArgs>? DiagnosticsChanged;

	/// <summary>
	/// Starts tracking a session, seeding the engine with the current selection and window
	/// facts. Re-registering the same id replaces the previous engine and discards its state.
	/// </summary>
	public void RegisterSession(SessionViewModel session)
	{
		ArgumentNullException.ThrowIfNull(session);
		var sessionId = session.Record.Id;
		RemoveSession(sessionId);

		TerminalTabStatusEngine engine;
		Registration registration;
		lock (_gate)
		{
			engine = new TerminalTabStatusEngine(
				sessionId,
				session.Record.Kind,
				AgentScreenProfileSelector.ForKind(session.Record.Kind),
				session.Record.Status,
				string.Equals(_selectedSessionId, sessionId, StringComparison.Ordinal),
				_windowVisible,
				_windowActive);
			registration = new Registration(engine, session);
			_registrations.Add(sessionId, registration);
			engine.IndicatorChanged += OnIndicatorChanged;
			engine.DiagnosticsChanged += OnDiagnosticsChanged;
		}

		DispatchProjection(
			registration,
			engine.CurrentIndicator,
			engine.ActivityStartedAt,
			engine.CurrentDescription);
		DispatchDiagnostics(registration, engine.CurrentDiagnostics);
	}

	/// <summary>
	/// Stops tracking a session and detaches its handlers. Unknown ids are ignored.
	/// </summary>
	public void RemoveSession(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		lock (_gate)
		{
			if (!_registrations.Remove(sessionId, out var removed))
			{
				return;
			}

			removed.Engine.IndicatorChanged -= OnIndicatorChanged;
			removed.Engine.DiagnosticsChanged -= OnDiagnosticsChanged;
		}
	}

	/// <summary>
	/// Moves the selection, notifying both the deselected and newly selected sessions. Pass
	/// <see langword="null"/> when no terminal is selected.
	/// </summary>
	public void SetSelectedSession(string? sessionId, DateTimeOffset occurredAt)
	{
		Registration? previous;
		Registration? next;
		lock (_gate)
		{
			_registrations.TryGetValue(_selectedSessionId ?? string.Empty, out previous);
			_selectedSessionId = sessionId;
			_registrations.TryGetValue(sessionId ?? string.Empty, out next);
		}

		if (previous is not null && !ReferenceEquals(previous, next))
		{
			previous.Engine.SetSelected(false, occurredAt);
		}

		next?.Engine.SetSelected(true, occurredAt);
	}

	/// <summary>
	/// Publishes the complete window visibility/activation fact set without
	/// exposing registrations to a transient mixed pair.
	/// </summary>
	public void SetWindowFacts(bool visible, bool active, DateTimeOffset occurredAt)
	{
		Registration[] registrations;
		lock (_gate)
		{
			_windowVisible = visible;
			_windowActive = active;
			registrations = [.. _registrations.Values];
		}

		foreach (var registration in registrations)
		{
			registration.Engine.SetWindowFacts(visible, active, occurredAt);
		}
	}

	/// <summary>Reports that a session's process was launched. Unknown ids are ignored.</summary>
	public void OnSessionStarted(string sessionId, TerminalStartMode mode, DateTimeOffset occurredAt)
	{
		if (TryGetEngine(sessionId, out var engine))
		{
			engine.OnSessionStarted(mode, occurredAt);
		}
	}

	/// <summary>Reports a session's lifecycle change. Unknown ids are ignored.</summary>
	public void OnLifecycleChanged(string sessionId, SessionStatus status, DateTimeOffset occurredAt)
	{
		if (TryGetEngine(sessionId, out var engine))
		{
			engine.SetLifecycleStatus(status, occurredAt);
		}
	}

	/// <summary>Reports user input sent to a session. Unknown ids are ignored.</summary>
	public void OnUserInput(string sessionId, string input, DateTimeOffset occurredAt)
	{
		ArgumentNullException.ThrowIfNull(input);
		if (TryGetEngine(sessionId, out var engine))
		{
			engine.OnUserInput(input, occurredAt);
		}
	}

	/// <summary>
	/// Routes a visible-screen snapshot to its registered session engine.
	/// Snapshots for unknown sessions are ignored. Snapshots captured while the
	/// screen was still repainting carry <paramref name="stable"/> = false.
	/// </summary>
	public void OnScreenSnapshot(string sessionId, string screenText, DateTimeOffset occurredAt, bool stable = true)
	{
		ArgumentNullException.ThrowIfNull(screenText);
		if (TryGetEngine(sessionId, out var engine))
		{
			engine.OnScreenSnapshot(screenText, occurredAt, stable);
		}
	}

	/// <summary>Reports a viewport resize, which invalidates screen-derived conclusions.</summary>
	public void OnViewportChanged(
		string sessionId,
		int columns,
		int rows,
		DateTimeOffset occurredAt)
	{
		if (TryGetEngine(sessionId, out var engine))
		{
			engine.OnViewportChanged(columns, rows, occurredAt);
		}
	}

	/// <summary>Reads a registered session's retained stable screen and last message.</summary>
	/// <returns><see langword="false"/> when no engine is registered for the session.</returns>
	public bool TryGetScreenState(string sessionId, out SessionScreenState state)
	{
		if (TryGetEngine(sessionId, out var engine))
		{
			var status = engine.CurrentStatus;
			state = new SessionScreenState(
				engine.LastStableScreen,
				engine.LastMessage,
				engine.LastMessageIsCurrent,
				status.InputRequested,
				status.StatusLine,
				status.PromptIsEmpty,
				status.ActivityEpoch,
				status.Indicator == TerminalTabIndicator.Busy);
			return true;
		}

		state = null!;
		return false;
	}

	/// <summary>Reads classifier metadata without exposing a raw terminal-screen snapshot.</summary>
	/// <returns><see langword="false"/> when no engine is registered for the session.</returns>
	public bool TryGetDiagnostics(
		string sessionId,
		out TerminalClassifierDiagnostics diagnostics)
	{
		if (TryGetEngine(sessionId, out var engine))
		{
			diagnostics = engine.CurrentDiagnostics;
			return true;
		}

		diagnostics = null!;
		return false;
	}

	private bool TryGetEngine(string sessionId, out TerminalTabStatusEngine engine)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		lock (_gate)
		{
			if (_registrations.TryGetValue(sessionId, out var registration))
			{
				engine = registration.Engine;
				return true;
			}
		}

		engine = null!;
		return false;
	}

	private void OnIndicatorChanged(object? sender, TerminalTabIndicatorChangedEventArgs args)
	{
		if (sender is not TerminalTabStatusEngine engine)
		{
			return;
		}

		Registration? registration;
		lock (_gate)
		{
			if (!_registrations.TryGetValue(engine.SessionId, out registration)
				|| !ReferenceEquals(registration.Engine, engine))
			{
				return;
			}
		}

		DispatchProjection(
			registration,
			args.Indicator,
			args.ActivityStartedAt,
			args.Description);
	}

	private void OnDiagnosticsChanged(
		object? sender,
		TerminalClassifierDiagnosticsChangedEventArgs args)
	{
		if (sender is not TerminalTabStatusEngine engine)
		{
			return;
		}

		Registration? registration;
		lock (_gate)
		{
			if (!_registrations.TryGetValue(engine.SessionId, out registration)
				|| !ReferenceEquals(registration.Engine, engine))
			{
				return;
			}
		}

		DispatchDiagnostics(registration, args.Diagnostics);
	}

	private void DispatchProjection(
		Registration registration,
		TerminalTabIndicator indicator,
		DateTimeOffset? activityStartedAt,
		string description) => _dispatchToUi(() =>
												   {
													   lock (_gate)
													   {
														   if (!_registrations.TryGetValue(registration.Engine.SessionId, out var current)
															   || !ReferenceEquals(current, registration))
														   {
															   return;
														   }
													   }

													   registration.Session.ApplyTerminalStatus(
														   indicator,
														   activityStartedAt,
														   description);
												   });

	private void DispatchDiagnostics(
		Registration registration,
		TerminalClassifierDiagnostics diagnostics) => _dispatchToUi(() =>
		{
			lock (_gate)
			{
				if (!_registrations.TryGetValue(registration.Engine.SessionId, out var current)
					|| !ReferenceEquals(current, registration))
				{
					return;
				}
			}

			DiagnosticsChanged?.Invoke(
				this,
				new TerminalClassifierDiagnosticsChangedEventArgs(diagnostics));
		});

	private sealed record Registration(
		TerminalTabStatusEngine Engine,
		SessionViewModel Session);
}
