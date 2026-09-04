using Pact.Core.Projects;
using Pact.Core.Workspaces;

namespace Pact.Presentation.Services;

/// <summary>
/// Builds the parked form of a project.
/// </summary>
public static class WorkspacePauseService
{
	/// <summary>
	/// Returns <paramref name="workspace"/> marked paused, remembering
	/// <paramref name="activeItemId"/> so the same item is reselected on restore.
	/// </summary>
	/// <remarks>
	/// Nested sessions and pages are deliberately carried over untouched: pausing parks the
	/// project's layout for later restore rather than discarding it.
	/// </remarks>
	public static ProjectRecord CreatePausedWorkspace(ProjectRecord workspace, string? activeItemId)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		return workspace with
		{
			Status = WorkspaceStatus.Paused,
			LastActiveAt = DateTimeOffset.UtcNow,
			ActiveItemId = activeItemId
		};
	}
}