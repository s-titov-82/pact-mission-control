using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;

namespace Pact.Core.Tests.Projects;

public sealed class ProjectRecordTests
{
	[Test]
	public void ProjectRecord_stores_project_root_metadata_and_nested_sessions()
	{
		DateTimeOffset createdAt = new(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Review storage",
			@"D:\Personal\Pact",
			"codex",
			"codex resume codex-session-123",
			SessionStatus.Stopped,
			createdAt,
			createdAt);
		ProjectRecord project = new(
			"project-1",
			"Pact",
			@"D:\Personal\Pact",
			createdAt,
			createdAt,
			"Working on project grouping")
		{
			Status = WorkspaceStatus.Paused,
			ActiveItemId = session.Id,
			Sessions = [session]
		};

		project.Id.ShouldBe("project-1");
		project.Name.ShouldBe("Pact");
		project.RootPath.ShouldBe(@"D:\Personal\Pact");
		project.Notes.ShouldBe("Working on project grouping");
		project.Status.ShouldBe(WorkspaceStatus.Paused);
		project.ActiveItemId.ShouldBe("session-1");
		project.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
	}

	[Test]
	public void ProjectRecord_can_store_web_pages_and_web_context()
	{
		var now = DateTimeOffset.UtcNow;
		WebPageRecord webPage = new(
			"web-1",
			"GitLab Requests",
			"https://gitlab/group/repo/-/merge_requests",
			"https://gitlab/group/repo/-/merge_requests/42",
			now,
			now);
		ProjectRecord project = new(
			"project-1",
			"Pact",
			@"D:\Personal\Pact",
			now,
			now,
			Notes: null)
		{
			ActiveItemId = "web-1",
			GitLabRepoId = "group/repo",
			TeamCityProjectId = "Pact_Build",
			WebPages = [webPage]
		};

		project.ActiveItemId.ShouldBe("web-1");
		project.GitLabRepoId.ShouldBe("group/repo");
		project.TeamCityProjectId.ShouldBe("Pact_Build");
		project.WebPages.ShouldHaveSingleItem().ShouldBeSameAs(webPage);
	}
}