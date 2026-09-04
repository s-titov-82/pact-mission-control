namespace Pact.Core.Orchestrator;

/// <summary>Bounds automatic restarts of the dedicated orchestrator slot.</summary>
/// <remarks>
/// A run of at least one minute is treated as healthy and resets backoff. Short-lived
/// failures back off from two seconds, and five consecutive failures exhaust the budget.
/// Giving up leaves a visible stopped slot instead of continuously spawning a broken command.
/// </remarks>
public static class OrchestratorRestartPolicy
{
	private static readonly TimeSpan HealthyRunThreshold = TimeSpan.FromMinutes(1);
	private const int FailureBudget = 5;

	/// <summary>Returns the delay before restarting, or no value when restart should stop.</summary>
	/// <param name="consecutiveFailures">Number of consecutive short-lived failures.</param>
	/// <param name="ranFor">Duration of the just-finished slot process.</param>
	public static TimeSpan? NextDelay(int consecutiveFailures, TimeSpan ranFor)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);
		ArgumentOutOfRangeException.ThrowIfLessThan(ranFor, TimeSpan.Zero);

		if (ranFor >= HealthyRunThreshold)
		{
			return TimeSpan.Zero;
		}

		if (consecutiveFailures >= FailureBudget)
		{
			return null;
		}

		var exponent = Math.Max(0, consecutiveFailures - 1);
		return TimeSpan.FromSeconds(2 * Math.Pow(2, exponent));
	}
}
