namespace Pact.Core.Orchestrator;

/// <summary>
/// Decides whether a workstation lock or unlock produces a prompt for the orchestrator slot.
/// </summary>
/// <remarks>
/// Kept as a pure decision so both switches, the provisioning state, and blank prompt text are
/// testable without a window or a Win32 subscription.
/// </remarks>
public static class WorkstationLockPolicy
{
	/// <summary>Builds the prompt for a lock-state change.</summary>
	/// <param name="record">Current slot configuration.</param>
	/// <param name="locked">Whether the workstation just locked.</param>
	/// <param name="prompt">Text to submit, or empty when nothing should be sent.</param>
	/// <returns><see langword="false"/> when the change must be ignored.</returns>
	public static bool TryBuildPrompt(
		OrchestratorRecord record,
		bool locked,
		out string prompt)
	{
		ArgumentNullException.ThrowIfNull(record);

		prompt = string.Empty;
		if (!record.Enabled || !record.LockDetectionEnabled || !record.IsProvisioned)
		{
			return false;
		}

		var candidate = locked ? record.LockPrompt : record.UnlockPrompt;
		if (string.IsNullOrWhiteSpace(candidate))
		{
			return false;
		}

		prompt = candidate;
		return true;
	}
}
