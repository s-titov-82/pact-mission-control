namespace Pact.Core.Sessions;

/// <summary>
/// Lifecycle state of a terminal session's backing process, as persisted in
/// <c>projects.json</c>.
/// </summary>
/// <remarks>
/// <see cref="Starting"/> and <see cref="Running"/> are transient: a process cannot survive
/// an application restart, so any session still recorded in those states on load is
/// normalized back to <see cref="Stopped"/>.
/// </remarks>
public enum SessionStatus
{
	/// <summary>No process is attached; the session is a saved placeholder.</summary>
	Stopped,

	/// <summary>Launch has begun but the backend has not confirmed the process yet.</summary>
	Starting,

	/// <summary>A live process is attached.</summary>
	Running,

	/// <summary>The process ended on its own.</summary>
	Exited,

	/// <summary>The process could not be started, or ended abnormally.</summary>
	Failed
}