namespace Pact.Core.Updates;

/// <summary>
/// Describes the externally observable stage of the update lifecycle.
/// </summary>
public enum UpdateState
{
	/// <summary>No update operation is active.</summary>
	Idle,
	/// <summary>A release check is in progress.</summary>
	Checking,
	/// <summary>A newer stable release is available.</summary>
	Available,
	/// <summary>The user-approved package is being downloaded and verified.</summary>
	Downloading,
	/// <summary>A verified package is waiting for active work to become safe.</summary>
	ReadyWaitingForSafeState,
	/// <summary>A verified package can be applied by restarting Pact.</summary>
	ReadyToRestart,
	/// <summary>Pact is handing the prepared update to the updater.</summary>
	Applying,
	/// <summary>The most recent update operation failed.</summary>
	Failed
}
