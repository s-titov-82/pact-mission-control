using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

public sealed class SelectedTextRouterTests
{
	[Test]
	public void GetTargetSessions_returns_same_workspace_sessions_except_active()
	{
		WorkspaceViewModel currentWorkspace = new(CreateWorkspace("project-1", "Current"));
		SessionViewModel activeSession = new(CreateSession("session-1"));
		SessionViewModel reviewSession = new(CreateSession("session-2"));
		currentWorkspace.Sessions.Add(activeSession);
		currentWorkspace.Sessions.Add(reviewSession);

		WorkspaceViewModel otherWorkspace = new(CreateWorkspace("project-2", "Other"));
		SessionViewModel otherSession = new(CreateSession("session-3"));
		otherWorkspace.Sessions.Add(otherSession);

		var targets = SelectedTextRouter.GetTargetSessions(
			activeSession,
			[currentWorkspace, otherWorkspace]);

		targets.ShouldBe([reviewSession]);
	}

	private static ProjectRecord CreateWorkspace(string id, string name)
	{
		var now = DateTimeOffset.UtcNow;
		return new ProjectRecord(
			id,
			name,
			$@"D:\Work\{name}",
			now,
			now,
			Notes: null);
	}

	private static SessionRecord CreateSession(string id)
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionRecord(
			id,
			AgentKind.Codex,
			id,
			@"D:\Work\Current",
			"codex",
			$"codex resume {id}",
			SessionStatus.Running,
			now,
			now);
	}
}