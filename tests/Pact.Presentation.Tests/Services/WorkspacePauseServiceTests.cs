using Pact.Core.Projects;
using Pact.Core.Workspaces;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class WorkspacePauseServiceTests
{
	[Test]
	public void CreatePausedWorkspace_maps_active_session_argument_to_active_item()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-5);
		ProjectRecord active = new(
			"project-1",
			"Pact",
			@"D:\Personal\Pact",
			createdAt,
			createdAt,
			Notes: null);
		var paused = WorkspacePauseService.CreatePausedWorkspace(
			active,
			activeItemId: "session-2");

		paused.Status.ShouldBe(WorkspaceStatus.Paused);
		paused.ActiveItemId.ShouldBe("session-2");
		(paused.LastActiveAt >= active.LastActiveAt).ShouldBeTrue();
	}
}