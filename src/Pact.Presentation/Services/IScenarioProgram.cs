namespace Pact.Presentation.Services;

/// <summary>Removes temporary files owned by a scenario program after a run reaches a terminal state.</summary>
public interface IScenarioRunArtifactCleaner
{
	/// <summary>Deletes only artifacts owned by the supplied run identifier.</summary>
	Task CleanupRunArtifactsAsync(string runId, CancellationToken cancellationToken);
}

/// <summary>
/// The step logic of one scenario kind, driven iteration by iteration.
/// </summary>
public interface IScenarioProgram
{
	/// <summary>
	/// Runs one iteration.
	/// </summary>
	/// <returns>
	/// <see langword="true"/> to continue with another iteration, <see langword="false"/> when
	/// the program reached its own completion — for the review loop, when the stop marker was
	/// observed.
	/// </returns>
	Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken);
}
