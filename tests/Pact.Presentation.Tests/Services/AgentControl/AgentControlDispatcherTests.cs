using Pact.Core.AgentControl;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class AgentControlDispatcherTests
{
	private const string ProjectSessionId = "session-project";
	private const string RootSessionId = "session-root";

	[Test]
	public async Task GetNotesAsync_returns_the_callers_project_snapshot()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.GetNotesAsync(
			ProjectSessionId,
			CancellationToken.None);

		result.Payload.ShouldNotBeNull().ShouldContain("\"text\":\"project notes\"");
		result.Payload.ShouldContain("\"revision\":");
		host.ReadProjectIds.ShouldBe(["project-1"]);
	}

	[Test]
	public async Task GetNotesAsync_refuses_a_root_session()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.GetNotesAsync(
			RootSessionId,
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("owner-not-a-project");
		host.ReadProjectIds.ShouldBeEmpty();
	}

	[Test]
	public async Task ReplaceNotesAsync_reports_a_conflict_without_claiming_success()
	{
		FakeAgentControlHost host = new()
		{
			ReplaceResult = new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText("current"),
				ProjectNotesMutationStatus.Conflict)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.ReplaceNotesAsync(
			ProjectSessionId,
			new ReplaceNoteRequest("new", "stale"),
			CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe("notes-conflict");
	}

	[Test]
	public async Task ReplaceNotesAsync_reports_changed_but_unpersisted_content()
	{
		FakeAgentControlHost host = new()
		{
			ReplaceResult = new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText("new"),
				ProjectNotesMutationStatus.AppliedButNotPersisted)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.ReplaceNotesAsync(
			ProjectSessionId,
			new ReplaceNoteRequest("new", "revision"),
			CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe("notes-save-failed");
		result.Failure.Message.ShouldContain("buffer changed");
	}

	[Test]
	public async Task AppendNoteAsync_RoutesTextToOwningProject()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			ProjectSessionId,
			new AppendNoteRequest("captured decision"),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.AppendedNotes.ShouldHaveSingleItem();
		host.AppendedNotes[0].ProjectId.ShouldBe("project-1");
		host.AppendedNotes[0].Text.ShouldBe("captured decision");
	}

	[Test]
	public async Task AppendNoteAsync_RefusesUnknownSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			"ghost",
			new AppendNoteRequest("text"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("unknown-session");
		host.AppendedNotes.ShouldBeEmpty();
	}

	[Test]
	public async Task AppendNoteAsync_RefusesRootSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			RootSessionId,
			new AppendNoteRequest("text"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("owner-not-a-project");
		host.AppendedNotes.ShouldBeEmpty();
	}

	[Test]
	public async Task OpenWebTabAsync_CreatesTabUnderProjectOwner()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			ProjectSessionId,
			new OpenWebTabRequest("https://example.com/mr/42", "MR 42"),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.WebTabs.ShouldHaveSingleItem();
		host.WebTabs[0].Owner.ProjectId.ShouldBe("project-1");
		host.WebTabs[0].Url.ShouldBe("https://example.com/mr/42");
		host.WebTabs[0].Title.ShouldBe("MR 42");
	}

	[Test]
	public async Task OpenWebTabAsync_CreatesTabForRootSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			RootSessionId,
			new OpenWebTabRequest("https://example.com", null),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.WebTabs[0].Owner.IsRoot.ShouldBeTrue();
	}

	[Test]
	public async Task OpenWebTabAsync_RefusesNonHttpUrl()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			ProjectSessionId,
			new OpenWebTabRequest("file:///c:/secret.txt", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("invalid-argument");
		host.WebTabs.ShouldBeEmpty();
	}

	[Test]
	public async Task RequestReviewAsync_StartsRunAndReturnsRunId()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.RequestReviewAsync(
			ProjectSessionId,
			new RequestReviewRequest("plan-review", "claude-opus", "docs/plan.md", null),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		result.Payload.ShouldBe("run-1");
		host.StartedReviews.ShouldHaveSingleItem();
		host.StartedReviews[0].ProjectId.ShouldBe("project-1");
		host.StartedReviews[0].AuthorSessionId.ShouldBe(ProjectSessionId);
		host.StartedReviews[0].Request.ReviewProfileId.ShouldBe("claude-opus");
	}

	[Test]
	public async Task RequestReviewAsync_ReportsTheConflictingRunWhenOneIsActive()
	{
		FakeAgentControlHost host = new()
		{
			ReviewOutcome = _ => new ReviewStartOutcome(
				null,
				new ProjectSlotConflict("run-existing"),
				null)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.RequestReviewAsync(
			ProjectSessionId,
			new RequestReviewRequest("plan-review", "claude-opus", "docs/plan.md", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("run-already-active");
		result.Failure.Message.ShouldContain("run-existing");
	}

	[Test]
	public async Task RequestReviewAsync_ReportsAStartAlreadyInProgressWithoutInventingARunId()
	{
		FakeAgentControlHost host = new()
		{
			ReviewOutcome = _ => new ReviewStartOutcome(
				null,
				new ProjectSlotConflict(ActiveRunId: null),
				null)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.RequestReviewAsync(
			ProjectSessionId,
			new RequestReviewRequest("plan-review", "claude-opus", "docs/plan.md", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("review-already-starting");
		result.Failure.Message.ShouldContain("already starting");
		result.Failure.Message.ShouldNotContain("''");
	}

	[Test]
	public async Task RequestReviewAsync_RefusesRootSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.RequestReviewAsync(
			RootSessionId,
			new RequestReviewRequest("plan-review", "claude-opus", "docs/plan.md", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("owner-not-a-project");
		host.StartedReviews.ShouldBeEmpty();
	}

	[Test]
	public async Task RequestReviewAsync_ReturnsRefusalWhenStartFails()
	{
		FakeAgentControlHost host = new()
		{
			ReviewOutcome = _ => new ReviewStartOutcome(
				null,
				Conflict: null,
				FailureMessage: "Review profile 'claude-opus' was not found.")
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.RequestReviewAsync(
			ProjectSessionId,
			new RequestReviewRequest("plan-review", "claude-opus", "docs/plan.md", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("review-start-failed");
		result.Failure.Message.ShouldContain("claude-opus");
	}

	private sealed class FakeAgentControlHost : IAgentControlHost
	{
		public List<(string ProjectId, string Text)> AppendedNotes { get; } = [];

		public List<string> ReadProjectIds { get; } = [];

		public ProjectNotesSnapshot ReadSnapshot { get; set; } =
			ProjectNotesSnapshot.FromText("project notes");

		public ProjectNotesMutationResult ReplaceResult { get; set; } =
			new(
				ProjectNotesSnapshot.FromText("replacement"),
				ProjectNotesMutationStatus.Applied);

		public List<(AgentControlOwner Owner, string Url, string? Title)> WebTabs { get; } = [];

		public List<(string ProjectId, string AuthorSessionId, RequestReviewRequest Request)>
			StartedReviews
		{ get; } = [];

		public Func<RequestReviewRequest, ReviewStartOutcome>? ReviewOutcome { get; set; }

		public bool TryGetOwner(string sessionId, out AgentControlOwner owner)
		{
			switch (sessionId)
			{
				case ProjectSessionId:
					owner = new AgentControlOwner(IsRoot: false, ProjectId: "project-1");
					return true;
				case RootSessionId:
					owner = new AgentControlOwner(IsRoot: true, ProjectId: null);
					return true;
				default:
					owner = new AgentControlOwner(IsRoot: false, ProjectId: null);
					return false;
			}
		}

		public Task<ProjectNotesSnapshot> ReadProjectNotesAsync(
			string projectId,
			CancellationToken cancellationToken)
		{
			ReadProjectIds.Add(projectId);
			return Task.FromResult(ReadSnapshot);
		}

		public Task<ProjectNotesMutationResult> ReplaceProjectNotesAsync(
			string projectId,
			ReplaceNoteRequest request,
			CancellationToken cancellationToken) =>
			Task.FromResult(ReplaceResult);

		public Task<ProjectNotesMutationResult> AppendToProjectNotesAsync(
			string projectId,
			string text,
			CancellationToken cancellationToken)
		{
			AppendedNotes.Add((projectId, text));
			return Task.FromResult(new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText(text),
				ProjectNotesMutationStatus.Applied));
		}

		public Task CreateWebTabAsync(
			AgentControlOwner owner,
			string url,
			string? title,
			CancellationToken cancellationToken)
		{
			WebTabs.Add((owner, url, title));
			return Task.CompletedTask;
		}

		public Task<ReviewStartOutcome> StartReviewIfIdleAsync(
			string projectId,
			string authorSessionId,
			RequestReviewRequest request,
			CancellationToken cancellationToken)
		{
			StartedReviews.Add((projectId, authorSessionId, request));
			return Task.FromResult(ReviewOutcome?.Invoke(request)
				?? new ReviewStartOutcome("run-1", Conflict: null, FailureMessage: null));
		}
	}
}
