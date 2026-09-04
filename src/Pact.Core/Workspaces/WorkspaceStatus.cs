namespace Pact.Core.Workspaces;

/// <summary>
/// Whether a project participates in the running cockpit or is parked.
/// </summary>
public enum WorkspaceStatus
{
	/// <summary>Project is open; its sessions and pages are live.</summary>
	Active,

	/// <summary>
	/// Project is parked. Its nested sessions and pages stay in storage so the layout can be
	/// restored later, but nothing runs for it.
	/// </summary>
	Paused
}