using Pact.Core.Agents;

namespace Pact.Core.Sessions;

/// <summary>Immutable classifier and delivery facts for one visible terminal session.</summary>
/// <param name="SessionId">Session whose evidence was classified.</param>
/// <param name="TerminalKind">Agent profile used for classification.</param>
/// <param name="LifecycleStatus">Current child-process lifecycle state.</param>
/// <param name="VerdictState">Latest stable classifier verdict, or null before classification.</param>
/// <param name="VerdictDescription">Description returned with the latest stable verdict.</param>
/// <param name="Indicator">Tab indicator derived from retained lifecycle and classifier evidence.</param>
/// <param name="IndicatorDescription">Description retained alongside the derived indicator.</param>
/// <param name="PromptIsEmpty">Whether the visible composer is empty, or null when unknown.</param>
/// <param name="InputRequested">Whether the agent is waiting for a human answer.</param>
/// <param name="StatusLine">Description of the pending input request, or an empty string.</param>
/// <param name="ActivityInProgress">Whether the engine retains an active work cycle.</param>
/// <param name="ActivityEpoch">Monotonic activity-cycle counter.</param>
/// <param name="HasUnreadCompletion">Whether a completed cycle has not been acknowledged.</param>
/// <param name="Columns">Latest viewport width in cells, or null before a resize report.</param>
/// <param name="Rows">Latest viewport height in cells, or null before a resize report.</param>
/// <param name="LastClassificationAt">Time of the latest stable classification.</param>
/// <param name="PromptEvidence">
/// Structural composer evidence suitable for diagnostics without exposing terminal text.
/// </param>
public sealed record TerminalClassifierDiagnostics(
	string SessionId,
	AgentKind TerminalKind,
	SessionStatus LifecycleStatus,
	TerminalScreenVerdictState? VerdictState,
	string VerdictDescription,
	TerminalTabIndicator Indicator,
	string IndicatorDescription,
	bool? PromptIsEmpty,
	bool InputRequested,
	string StatusLine,
	bool ActivityInProgress,
	long ActivityEpoch,
	bool HasUnreadCompletion,
	int? Columns,
	int? Rows,
	DateTimeOffset? LastClassificationAt,
	TerminalPromptEvidence? PromptEvidence = null);

/// <summary>Reports a change to one session's classifier diagnostics.</summary>
public sealed class TerminalClassifierDiagnosticsChangedEventArgs : EventArgs
{
	/// <summary>Creates an event carrying the complete replacement snapshot.</summary>
	public TerminalClassifierDiagnosticsChangedEventArgs(TerminalClassifierDiagnostics diagnostics)
	{
		Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
	}

	/// <summary>Complete diagnostic state after the change.</summary>
	public TerminalClassifierDiagnostics Diagnostics { get; }
}
