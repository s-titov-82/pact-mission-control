namespace Pact.Core.Scenarios;

/// <summary>Result of asking a live scenario run to enter a manual pause.</summary>
public enum ScenarioPauseRequestStatus
{
	/// <summary>The pause was retained and will apply at the current safe boundary.</summary>
	Requested,

	/// <summary>An attention pause was converted into a manual pause.</summary>
	Escalated,

	/// <summary>The run was already manually paused or already had a pending request.</summary>
	Unchanged,

	/// <summary>The run is stopping or terminal and cannot be paused.</summary>
	NotPausable
}
