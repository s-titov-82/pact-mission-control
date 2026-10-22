namespace Pact.Core.Scenarios;

/// <summary>What the run handle must do after a pause transition.</summary>
[Flags]
public enum ScenarioPauseEffects
{
	/// <summary>Nothing changed.</summary>
	None = 0,

	/// <summary>Observers must be told the state changed.</summary>
	Changed = 1,

	/// <summary>Journal that the user paused the run.</summary>
	JournalManualPause = 2,

	/// <summary>Journal why the run needs attention.</summary>
	JournalAttention = 4,

	/// <summary>Release whatever waits for the run to resume.</summary>
	SignalResume = 8,

	/// <summary>Interrupt a response wait so the pause applies at once.</summary>
	InterruptWait = 16,

	/// <summary>Keep the arrived response until the user resumes.</summary>
	HoldResponse = 32
}

/// <summary>The run state and the reason it stands still, as one value.</summary>
/// <param name="State">Current run state.</param>
/// <param name="PauseKind">Kind of the current pause; <see cref="ScenarioPauseKind.None"/> unless paused.</param>
/// <param name="AttentionReason">Why Pact paused; set exactly when <paramref name="PauseKind"/> is Attention.</param>
/// <param name="StuckSessionId">Session needing the user during an attention pause.</param>
/// <param name="PauseRequested">A manual pause waits for the next safe boundary.</param>
/// <param name="StopRequested">The user asked the run to stop after the current step.</param>
public sealed record ScenarioPauseState(
	ScenarioRunState State,
	ScenarioPauseKind PauseKind,
	ScenarioAttentionReason? AttentionReason,
	string? StuckSessionId,
	bool PauseRequested,
	bool StopRequested)
{
	/// <summary>A run that has just started.</summary>
	public static ScenarioPauseState Initial { get; } =
		new(ScenarioRunState.Running, ScenarioPauseKind.None, null, null, false, false);

	/// <summary>Whether the run is paused.</summary>
	public bool IsPaused => State == ScenarioRunState.Paused;
}

/// <summary>A pause transition: the new state, what to do, and the answer to a pause request.</summary>
public readonly record struct ScenarioPauseTransition(
	ScenarioPauseState State,
	ScenarioPauseEffects Effects,
	ScenarioPauseRequestStatus? Status = null);

/// <summary>Pure transitions of a scenario run's pause state.</summary>
public static class ScenarioPauseMachine
{
	/// <summary>
	/// The user pressed Pause. A running run retains the request for its next safe boundary
	/// and interrupts a response wait; an attention pause becomes a manual one.
	/// </summary>
	public static ScenarioPauseTransition RequestPause(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state switch
		{
			{ State: ScenarioRunState.Running, PauseRequested: false } => new(
				state with { PauseRequested = true },
				ScenarioPauseEffects.Changed | ScenarioPauseEffects.InterruptWait,
				ScenarioPauseRequestStatus.Requested),
			{ State: ScenarioRunState.Running } or { PauseKind: ScenarioPauseKind.Manual } =>
				new(state, ScenarioPauseEffects.None, ScenarioPauseRequestStatus.Unchanged),
			{ PauseKind: ScenarioPauseKind.Attention } => new(
				ManualPause(state),
				ScenarioPauseEffects.Changed,
				ScenarioPauseRequestStatus.Escalated),
			_ => new(state, ScenarioPauseEffects.None, ScenarioPauseRequestStatus.NotPausable)
		};
	}

	/// <summary>The run reached a safe boundary, where a pending manual pause takes effect.</summary>
	public static ScenarioPauseTransition ReachBoundary(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state is { State: ScenarioRunState.Running, PauseRequested: true }
			? new(ManualPause(state), ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalManualPause)
			: new(state, ScenarioPauseEffects.None);
	}

	/// <summary>
	/// Pact needs the user. A pending manual request wins; a running run pauses for attention;
	/// an attention pause takes a different reason; a manual pause is left alone.
	/// </summary>
	public static ScenarioPauseTransition NeedAttention(
		ScenarioPauseState state,
		string? sessionId,
		ScenarioAttentionReason reason)
	{
		ArgumentNullException.ThrowIfNull(state);
		if (state.PauseRequested && state.State == ScenarioRunState.Running)
		{
			return ReachBoundary(state);
		}

		return state switch
		{
			{ State: ScenarioRunState.Running } => new(
				state with
				{
					State = ScenarioRunState.Paused,
					PauseKind = ScenarioPauseKind.Attention,
					AttentionReason = reason,
					StuckSessionId = sessionId
				},
				ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalAttention),
			{ PauseKind: ScenarioPauseKind.Attention } when state.AttentionReason != reason => new(
				state with { AttentionReason = reason },
				ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalAttention),
			_ => new(state, ScenarioPauseEffects.None)
		};
	}

	/// <summary>A trigger was delivered; only an attention pause ends.</summary>
	public static ScenarioPauseTransition DeliverySucceeded(ScenarioPauseState state) => EndAttention(state);

	/// <summary>
	/// A valid response arrived. An attention pause ends; a manual pause holds the response
	/// until the user resumes.
	/// </summary>
	public static ScenarioPauseTransition ResponseArrived(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.PauseKind == ScenarioPauseKind.Manual
			? new(state, ScenarioPauseEffects.HoldResponse)
			: EndAttention(state);
	}

	/// <summary>The user resumed. Either pause ends; a pending manual request is kept.</summary>
	public static ScenarioPauseTransition Resume(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state.IsPaused
			? new(Running(state), ScenarioPauseEffects.Changed | ScenarioPauseEffects.SignalResume)
			: new(state, ScenarioPauseEffects.None);
	}

	/// <summary>
	/// The user asked the run to stop after the current step. A running run starts stopping; a
	/// paused run records the stop and keeps its pause.
	/// </summary>
	public static ScenarioPauseTransition RequestSoftStop(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state switch
		{
			{ State: ScenarioRunState.Running } => new(
				state with { State = ScenarioRunState.StoppingAfterStep, StopRequested = true },
				ScenarioPauseEffects.Changed),
			{ State: ScenarioRunState.Paused } => new(state with { StopRequested = true }, ScenarioPauseEffects.None),
			_ => new(state, ScenarioPauseEffects.None)
		};
	}

	/// <summary>The run reached a terminal state; nothing stays paused or unlocked.</summary>
	public static ScenarioPauseTransition Finish(ScenarioPauseState state, ScenarioRunState terminal)
	{
		ArgumentNullException.ThrowIfNull(state);
		return IsTerminal(state.State)
			? new(state, ScenarioPauseEffects.None)
			: new(
				state with
				{
					State = terminal,
					PauseKind = ScenarioPauseKind.None,
					AttentionReason = null,
					StuckSessionId = null,
					PauseRequested = false
				},
				ScenarioPauseEffects.Changed);
	}

	/// <summary>
	/// Sessions open to manual input: every bound one in a manual pause, the stuck one in an
	/// attention pause, none otherwise.
	/// </summary>
	public static IReadOnlyList<string> UnlockedSessions(ScenarioPauseState state, IEnumerable<string> boundSessionIds)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(boundSessionIds);
		return state switch
		{
			{ State: ScenarioRunState.Paused, PauseKind: ScenarioPauseKind.Manual } =>
				[.. boundSessionIds.Distinct(StringComparer.Ordinal)],
			{ State: ScenarioRunState.Paused, PauseKind: ScenarioPauseKind.Attention, StuckSessionId: { } stuck } =>
				[.. boundSessionIds.Where(id => string.Equals(id, stuck, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)],
			_ => []
		};
	}

	private static ScenarioPauseTransition EndAttention(ScenarioPauseState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return state is { State: ScenarioRunState.Paused, PauseKind: ScenarioPauseKind.Attention }
			? new(Running(state), ScenarioPauseEffects.Changed | ScenarioPauseEffects.SignalResume)
			: new(state, ScenarioPauseEffects.None);
	}

	private static ScenarioPauseState ManualPause(ScenarioPauseState state) => state with
	{
		State = ScenarioRunState.Paused,
		PauseKind = ScenarioPauseKind.Manual,
		AttentionReason = null,
		StuckSessionId = null,
		PauseRequested = false
	};

	private static ScenarioPauseState Running(ScenarioPauseState state) => state with
	{
		State = ScenarioRunState.Running,
		PauseKind = ScenarioPauseKind.None,
		AttentionReason = null,
		StuckSessionId = null
	};

	private static bool IsTerminal(ScenarioRunState state) =>
		state is ScenarioRunState.Completed
			or ScenarioRunState.MaxIterationsReached
			or ScenarioRunState.Aborted
			or ScenarioRunState.Failed;
}
