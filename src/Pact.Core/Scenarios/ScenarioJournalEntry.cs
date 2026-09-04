namespace Pact.Core.Scenarios;

/// <summary>
/// Severity of a scenario journal entry, controlling how it is styled in the journal panel.
/// </summary>
public enum ScenarioJournalLevel
{
	/// <summary>Normal progress.</summary>
	Info,

	/// <summary>Something needing attention that did not stop the run, such as a watchdog pause.</summary>
	Warning,

	/// <summary>A failure that ended the run or a step.</summary>
	Error,

	/// <summary>A step or run completed on its own terms.</summary>
	Success
}

/// <summary>
/// One line in a run's journal, recording the published task, trigger, waiting state, and
/// exchange file contents.
/// </summary>
/// <remarks>
/// Journals exist only in memory for as long as their pseudo-node can be shown, and are
/// discarded when the run is closed; they are never written to disk.
/// </remarks>
/// <param name="Timestamp">When the entry was recorded.</param>
/// <param name="StepId">Step it belongs to, or <c>run</c> for run-level entries.</param>
/// <param name="Message">Entry text.</param>
/// <param name="Level">Severity.</param>
public sealed record ScenarioJournalEntry(
	DateTimeOffset Timestamp,
	string StepId,
	string Message,
	ScenarioJournalLevel Level);