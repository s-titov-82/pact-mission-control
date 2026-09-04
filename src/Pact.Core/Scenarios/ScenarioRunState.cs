namespace Pact.Core.Scenarios;

/// <summary>
/// State of a scenario run. <see cref="Completed"/>, <see cref="MaxIterationsReached"/>,
/// <see cref="Aborted"/>, and <see cref="Failed"/> are terminal; reaching any of them
/// releases the run's session input locks and deletes its exchange directory.
/// </summary>
public enum ScenarioRunState
{
	/// <summary>Steps are executing.</summary>
	Running,

	/// <summary>
	/// The watchdog parked the run because an agent needs the user. Run files are retained,
	/// and only the stuck session is unlocked so the user can answer and resume.
	/// </summary>
	Paused,

	/// <summary>A soft stop was requested; the current step finishes, then the run ends.</summary>
	StoppingAfterStep,

	/// <summary>The stop marker was observed, so the loop finished on its own terms.</summary>
	Completed,

	/// <summary>The iteration budget ran out before the stop marker appeared.</summary>
	MaxIterationsReached,

	/// <summary>The user or application shutdown cancelled the run.</summary>
	Aborted,

	/// <summary>A step could not proceed, typically because an involved terminal exited.</summary>
	Failed
}