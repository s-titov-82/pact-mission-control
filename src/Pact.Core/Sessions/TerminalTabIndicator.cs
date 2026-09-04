namespace Pact.Core.Sessions;

/// <summary>
/// The badge shown on a terminal tab, derived by <see cref="TerminalTabStatusEngine"/> from
/// observed evidence. This reflects UI state only: it must never be read as scenario progress
/// or used to gate a scenario step.
/// </summary>
public enum TerminalTabIndicator
{
	/// <summary>Nothing to report; the tab is idle and read.</summary>
	None,

	/// <summary>The agent appears to be working, based on screen activity evidence.</summary>
	Busy,

	/// <summary>The agent is waiting for a human answer and cannot receive a prompt.</summary>
	InputRequested,

	/// <summary>Output arrived while the tab was unselected and has not been seen yet.</summary>
	Unread,

	/// <summary>The session's project is parked.</summary>
	Paused,

	/// <summary>The session's process failed to start or ended abnormally.</summary>
	Failed
}
