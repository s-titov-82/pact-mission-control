namespace Pact.Core.Updates;

/// <summary>Identifies the helper-authored terminal outcome of a restart handoff.</summary>
public enum SoftRestartOutcomeKind
{
	/// <summary>The source Pact process has not yet handed the ticket to the helper.</summary>
	Pending,
	/// <summary>A restart-only helper relaunched Pact.</summary>
	Restarted,
	/// <summary>Setup succeeded and the helper relaunched Pact.</summary>
	UpdateApplied,
	/// <summary>Setup failed but the helper relaunched the old Pact installation.</summary>
	UpdateNotApplied
}

/// <summary>
/// Captures the helper's terminal handoff result without persisting raw exception or process text.
/// </summary>
/// <param name="Kind">The terminal outcome kind.</param>
/// <param name="ErrorCategory">A bounded sanitized category when the update was not applied.</param>
public sealed record SoftRestartOutcome(
	SoftRestartOutcomeKind Kind,
	string? ErrorCategory);
