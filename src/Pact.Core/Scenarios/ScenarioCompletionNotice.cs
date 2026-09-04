namespace Pact.Core.Scenarios;

/// <summary>
/// Builds the one-line notice telling an author session that its review run ended. Automatically
/// started runs otherwise leave the author waiting after its final reply.
/// </summary>
public static class ScenarioCompletionNotice
{
	/// <summary>Builds a notice only for a terminal run state.</summary>
	/// <param name="state">State in which the run finished.</param>
	/// <param name="iterationsUsed">Number of review passes consumed.</param>
	/// <param name="message">Single-line notice, or empty for a non-terminal state.</param>
	/// <returns><see langword="true"/> only when a terminal-state notice was produced.</returns>
	public static bool TryBuild(
		ScenarioRunState state,
		int iterationsUsed,
		out string message)
	{
		message = state switch
		{
			ScenarioRunState.Completed =>
				$"Review loop finished: the reviewer approved your work after {iterationsUsed} pass(es). Continue with your task.",
			ScenarioRunState.MaxIterationsReached =>
				$"Review loop ended after {iterationsUsed} pass(es) without agreement; the iteration budget ran out. Continue with your task.",
			ScenarioRunState.Aborted =>
				$"Review loop was stopped after {iterationsUsed} pass(es). Continue with your task.",
			ScenarioRunState.Failed =>
				$"Review loop failed after {iterationsUsed} pass(es) and cannot continue. Continue with your task.",
			_ => string.Empty
		};

		return message.Length > 0;
	}
}
