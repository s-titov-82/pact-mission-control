using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Services;

/// <summary>
/// Chooses which sessions can receive the current terminal selection.
/// </summary>
public static class SelectedTextRouter
{
	/// <summary>
	/// Lists the sessions offered as "send selection" targets.
	/// </summary>
	/// <param name="activeSession">Session the selection came from, or <see langword="null"/>.</param>
	/// <param name="workspaces">Open projects to search.</param>
	/// <returns>
	/// The other sessions in the same project, excluding the source session itself. Empty when
	/// there is no active session or it belongs to no open project — sending across projects is
	/// deliberately not offered, since the receiving agent would have the wrong working directory.
	/// </returns>
	public static IReadOnlyList<SessionViewModel> GetTargetSessions(
		SessionViewModel? activeSession,
		IEnumerable<WorkspaceViewModel> workspaces)
	{
		ArgumentNullException.ThrowIfNull(workspaces);

		if (activeSession is null)
		{
			return [];
		}

		var workspace = workspaces.FirstOrDefault(
			item => IsSessionInWorkspace(activeSession, item));
		if (workspace is null)
		{
			return [];
		}

		return workspace.Sessions
			.Where(session => !string.Equals(
				session.Record.Id,
				activeSession.Record.Id,
				StringComparison.Ordinal))
			.ToArray();
	}

	private static bool IsSessionInWorkspace(SessionViewModel session, WorkspaceViewModel workspace) => workspace.Sessions.Any(item => string.Equals(
																												 item.Record.Id,
																												 session.Record.Id,
																												 StringComparison.Ordinal));
}