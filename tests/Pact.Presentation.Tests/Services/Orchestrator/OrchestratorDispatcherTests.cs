using System.Text.Json;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Core.Sessions;
using Pact.Presentation.Services.Orchestrator;

namespace Pact.Presentation.Tests.Services.Orchestrator;

public sealed class OrchestratorDispatcherTests
{
	[Test]
	public void List_workspaces_returns_every_session_under_its_owner()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.ListWorkspaces();

		result.Succeeded.ShouldBeTrue();
		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("project-a");
		payload.ShouldContain("session-a");
		payload.ShouldContain("ROOT");
	}

	[Test]
	public void Get_session_returns_the_extracted_message()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetSession("session-a", content: "message");

		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("finished the refactor");
		payload.ShouldNotContain("full screen text");
	}

	[Test]
	public void Get_session_returns_the_whole_screen_when_requested()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetSession("session-a", content: "screen");

		result.Payload.ShouldNotBeNull().ShouldContain("full screen text");
	}

	[Test]
	public void Get_session_does_not_substitute_screen_text_for_an_unrecognised_message()
	{
		FakeOrchestratorHost host = new();
		host.SetScreen(
			"session-a",
			new SessionScreenState(
				"full screen text",
				string.Empty,
				LastMessageIsCurrent: false));
		OrchestratorDispatcher dispatcher = new(host);

		var result = dispatcher.GetSession("session-a", content: "message");

		result.Succeeded.ShouldBeTrue();
		result.Payload.ShouldNotBeNull().ShouldNotContain("full screen text");
	}

	[Test]
	public void Get_session_flags_a_message_older_than_the_current_screen()
	{
		FakeOrchestratorHost host = new();
		host.SetScreen(
			"session-a",
			new SessionScreenState(
				"a working spinner",
				"earlier words",
				LastMessageIsCurrent: false));
		OrchestratorDispatcher dispatcher = new(host);

		var result = dispatcher.GetSession("session-a", content: "message");

		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("earlier words");
		payload.ShouldContain("lastMessageIsCurrent");
		payload.ShouldContain("false");
	}

	[Test]
	public void Get_session_refuses_an_unknown_session()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetSession("ghost", content: "message");

		result.Failure!.Code.ShouldBe("unknown-session");
	}

	[Test]
	public void Get_session_refuses_an_unknown_content_mode()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetSession("session-a", content: "everything");

		result.Failure!.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public void List_active_runs_reports_runs_with_their_participants()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.ListActiveRuns();

		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("run-1");
		payload.ShouldContain("session-a");
		payload.ShouldContain("pause-requested");
		payload.ShouldContain("pass-002-reviewer-response.md");
	}

	[Test]
	public void Get_review_run_reports_current_step_expected_file_and_journal()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetReviewRun("run-1");

		result.Succeeded.ShouldBeTrue();
		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("Wait for reviewer response file");
		payload.ShouldContain("pass-002-reviewer-response.md");
		payload.ShouldContain("published task");
	}

	[Test]
	public void Pause_review_returns_an_explicit_applied_outcome()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.PauseReview("run-1");

		result.Succeeded.ShouldBeTrue();
		result.Payload.ShouldNotBeNull().ShouldContain("\"status\":\"applied\"");
	}

	[Test]
	public void Resume_review_returns_unchanged_while_pause_is_only_requested()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.ResumeReview("run-1");

		result.Succeeded.ShouldBeTrue();
		result.Payload.ShouldNotBeNull().ShouldContain("\"status\":\"unchanged\"");
	}

	[TestCase("ghost", "unknown-review-run")]
	[TestCase("stopping", "review-not-pausable")]
	public void Pause_review_maps_control_failures_to_stable_codes(
		string runId,
		string expectedCode)
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.PauseReview(runId);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe(expectedCode);
	}

	[Test]
	public void Get_subscription_usage_reports_every_profile()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.GetSubscriptionUsage();

		result.Payload.ShouldNotBeNull().ShouldContain("claude");
	}

	[Test]
	public async Task Project_notes_can_be_read_replaced_with_empty_text_and_read_again()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);
		var before = await dispatcher.GetProjectNotesAsync(
			"project-a",
			CancellationToken.None);
		var revision = host.Notes.Revision;

		var replaced = await dispatcher.ReplaceProjectNotesAsync(
			"project-a",
			new ReplaceNoteRequest(string.Empty, revision),
			CancellationToken.None);
		var after = await dispatcher.GetProjectNotesAsync(
			"project-a",
			CancellationToken.None);

		before.Payload.ShouldNotBeNull().ShouldContain("project notes");
		replaced.Succeeded.ShouldBeTrue();
		after.Payload.ShouldNotBeNull().ShouldContain("\"text\":\"\"");
	}

	[TestCase("ROOT", "owner-not-a-project")]
	[TestCase("paused-project", "unknown-workspace")]
	[TestCase("missing-project", "unknown-workspace")]
	public async Task Project_notes_reject_non_running_project_owners(
		string workspaceId,
		string expectedCode)
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = await dispatcher.GetProjectNotesAsync(
			workspaceId,
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe(expectedCode);
	}

	[Test]
	public async Task Project_notes_map_conflict_and_save_failure()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);

		var conflict = await dispatcher.ReplaceProjectNotesAsync(
			"project-a",
			new ReplaceNoteRequest("replacement", "stale"),
			CancellationToken.None);
		host.FailNextNotesSave = true;
		var saveFailure = await dispatcher.AppendProjectNoteAsync(
			"project-a",
			"append",
			CancellationToken.None);

		conflict.Failure.ShouldNotBeNull().Code.ShouldBe("notes-conflict");
		saveFailure.Failure.ShouldNotBeNull().Code.ShouldBe("notes-save-failed");
	}

	[Test]
	public void List_web_tabs_includes_running_workspaces_and_root_but_not_paused_projects()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = dispatcher.ListWebTabs();

		var payload = result.Payload.ShouldNotBeNull();
		payload.ShouldContain("project-paused-page");
		payload.ShouldContain("root-page");
		payload.ShouldNotContain("paused-project-page");
	}

	[Test]
	public async Task Resume_web_tab_is_idempotent_and_rejects_unknown_pages()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);

		var first = await dispatcher.ResumeWebTabAsync(
			"project-paused-page",
			CancellationToken.None);
		var second = await dispatcher.ResumeWebTabAsync(
			"project-paused-page",
			CancellationToken.None);
		var unknown = await dispatcher.ResumeWebTabAsync(
			"paused-project-page",
			CancellationToken.None);

		first.Succeeded.ShouldBeTrue();
		second.Succeeded.ShouldBeTrue();
		host.ResumeCalls.ShouldBe(2);
		unknown.Failure.ShouldNotBeNull().Code.ShouldBe("unknown-web-tab");
	}

	[TestCase(-1, 1)]
	[TestCase(0, 200_001)]
	public async Task Get_web_tab_html_rejects_invalid_ranges(
		int offset,
		int maxChars)
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var result = await dispatcher.GetWebTabHtmlAsync(
			"project-active-page",
			offset,
			maxChars,
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("invalid-argument");
	}

	[Test]
	public async Task Get_web_tab_html_distinguishes_paused_and_returns_utf16_fragment()
	{
		OrchestratorDispatcher dispatcher = new(new FakeOrchestratorHost());

		var paused = await dispatcher.GetWebTabHtmlAsync(
			"project-paused-page",
			offset: 0,
			maxChars: 10,
			CancellationToken.None);
		var active = await dispatcher.GetWebTabHtmlAsync(
			"project-active-page",
			offset: 1,
			maxChars: 2,
			CancellationToken.None);

		paused.Failure.ShouldNotBeNull().Code.ShouldBe("web-tab-paused");
		using var payload = JsonDocument.Parse(active.Payload.ShouldNotBeNull());
		var root = payload.RootElement;
		root.GetProperty("html").GetString().ShouldBe("😀");
		root.GetProperty("totalLength").GetInt32().ShouldBe(4);
		root.GetProperty("nextOffset").GetInt32().ShouldBe(3);
	}

	[Test]
	public async Task Send_message_delivers_to_the_target_session()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			"session-a",
			"status please",
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.Sent.ShouldHaveSingleItem();
		host.Sent[0].ShouldBe(("session-a", "status please"));
	}

	[Test]
	public async Task Send_message_refuses_a_session_that_is_not_alive()
	{
		FakeOrchestratorHost host = new();
		host.SetAlive("session-a", alive: false);
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			"session-a",
			"hello",
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("session-not-alive");
		host.Sent.ShouldBeEmpty();
	}

	[Test]
	public async Task Send_message_refuses_a_scenario_locked_session()
	{
		FakeOrchestratorHost host = new();
		host.SetScenarioLocked("session-a", "run-1");
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			"session-a",
			"hello",
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("session-scenario-locked");
		result.Failure.Message.ShouldContain("run-1");
		host.Sent.ShouldBeEmpty();
	}

	[Test]
	public async Task Send_message_refuses_a_session_waiting_for_a_human_answer()
	{
		FakeOrchestratorHost host = new();
		host.SetScreen(
			"session-a",
			new SessionScreenState(
				"question screen",
				string.Empty,
				LastMessageIsCurrent: false,
				InputRequested: true,
				StatusLine: "Approve this edit?"));
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			"session-a",
			"hello",
			CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe("input-requested");
		result.Failure.Message.ShouldContain("Approve this edit?");
		host.Sent.ShouldBeEmpty();
	}

	[Test]
	public async Task Send_message_refuses_the_orchestrator_itself()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			host.OrchestratorSessionId!,
			"loop",
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("self-target");
		host.Sent.ShouldBeEmpty();
	}

	[Test]
	public async Task Send_message_refuses_blank_text()
	{
		FakeOrchestratorHost host = new();
		OrchestratorDispatcher dispatcher = new(host);

		var result = await dispatcher.SendMessageAsync(
			"session-a",
			"   ",
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("invalid-argument");
		host.Sent.ShouldBeEmpty();
	}

	private sealed class FakeOrchestratorHost : IOrchestratorHost
	{
		private readonly ReviewRunDetails _review = new(
			new ActiveRunSummary(
				"run-1",
				"project-a",
				"session-a",
				"reviewer-a",
				Iteration: 2,
				DateTimeOffset.UnixEpoch,
				State: "pause-requested",
				PauseKind: null,
				CurrentStepId: "capture-review",
				CurrentStepName: "Wait for reviewer response file",
				PauseRequested: true,
				ExpectedRole: "reviewer",
				ExpectedSessionId: "reviewer-a",
				ExpectedTaskPath: @"C:\project\.pact-reviews\run-1\pass-002-reviewer-task.md",
				ExpectedResponsePath: @"C:\project\.pact-reviews\run-1\pass-002-reviewer-response.md"),
			[
				new ReviewJournalSummary(
					DateTimeOffset.UnixEpoch,
					"info",
					"send-review-brief",
					"published task")
			]);
		private readonly Dictionary<string, WebTabSummary> _webTabs = new(
			StringComparer.Ordinal)
		{
			["project-active-page"] = new(
				"project-a",
				"Project A",
				IsRoot: false,
				"project-active-page",
				"Active page",
				"https://example.test/active",
				"active",
				IsSelected: false),
			["project-paused-page"] = new(
				"project-a",
				"Project A",
				IsRoot: false,
				"project-paused-page",
				"Paused page",
				"https://example.test/paused",
				"paused",
				IsSelected: false),
			["root-page"] = new(
				"ROOT",
				"ROOT",
				IsRoot: true,
				"root-page",
				"Root page",
				"https://example.test/root",
				"active",
				IsSelected: true)
		};

		private readonly Dictionary<string, bool> _alive = new()
		{
			["session-a"] = true
		};

		private readonly Dictionary<string, string> _scenarioLocks = [];

		private readonly Dictionary<string, SessionScreenState> _screens = new()
		{
			["session-a"] = new SessionScreenState(
				"full screen text",
				"finished the refactor",
				LastMessageIsCurrent: true)
		};

		private readonly WorkspaceSummary[] _workspaces =
		[
			new(
				"project-a",
				"Project A",
				IsRoot: false,
				[
					new SessionSummary(
						"session-a",
						"Claude",
						"Claude",
						"Running",
						"None",
						"Worked for 3s",
						DateTimeOffset.UnixEpoch)
				]),
			new(
				"ROOT",
				"ROOT",
				IsRoot: true,
				[
					new SessionSummary(
						"root-session",
						"PowerShell",
						"Pwsh",
						"Running",
						"None",
						string.Empty,
						null)
				])
		];

		public string? OrchestratorSessionId => "orchestrator-session";

		public List<(string SessionId, string Text)> Sent { get; } = [];
		public ProjectNotesSnapshot Notes { get; private set; } =
			ProjectNotesSnapshot.FromText("project notes");
		public bool FailNextNotesSave { get; set; }
		public int ResumeCalls { get; private set; }

		public IReadOnlyList<WorkspaceSummary> ListWorkspaces() => _workspaces;

		public bool TryGetSession(string sessionId, out SessionSummary summary)
		{
			var match = _workspaces.SelectMany(workspace => workspace.Sessions)
				.FirstOrDefault(session => session.SessionId == sessionId);
			summary = match!;
			return match is not null;
		}

		public bool TryGetScreen(string sessionId, out SessionScreenState state) =>
			_screens.TryGetValue(sessionId, out state!);

		public IReadOnlyList<ActiveRunSummary> ListActiveRuns() => [_review.Run];

		public bool IsRunningWorkspace(string workspaceId) =>
			string.Equals(workspaceId, "project-a", StringComparison.Ordinal);

		public Task<ProjectNotesSnapshot?> ReadProjectNotesAsync(
			string workspaceId,
			CancellationToken cancellationToken) =>
			Task.FromResult<ProjectNotesSnapshot?>(
				IsRunningWorkspace(workspaceId) ? Notes : null);

		public Task<ProjectNotesMutationResult?> ReplaceProjectNotesAsync(
			string workspaceId,
			ReplaceNoteRequest request,
			CancellationToken cancellationToken)
		{
			if (!IsRunningWorkspace(workspaceId))
			{
				return Task.FromResult<ProjectNotesMutationResult?>(null);
			}

			if (!string.Equals(
					request.ExpectedRevision,
					Notes.Revision,
					StringComparison.Ordinal))
			{
				return Task.FromResult<ProjectNotesMutationResult?>(new(
					Notes,
					ProjectNotesMutationStatus.Conflict));
			}

			Notes = ProjectNotesSnapshot.FromText(request.Text);
			return Task.FromResult<ProjectNotesMutationResult?>(new(
				Notes,
				ProjectNotesMutationStatus.Applied));
		}

		public Task<ProjectNotesMutationResult?> AppendProjectNoteAsync(
			string workspaceId,
			string text,
			CancellationToken cancellationToken)
		{
			if (!IsRunningWorkspace(workspaceId))
			{
				return Task.FromResult<ProjectNotesMutationResult?>(null);
			}

			Notes = ProjectNotesSnapshot.FromText(Notes.Text + text);
			var status = FailNextNotesSave
				? ProjectNotesMutationStatus.AppliedButNotPersisted
				: ProjectNotesMutationStatus.Applied;
			FailNextNotesSave = false;
			return Task.FromResult<ProjectNotesMutationResult?>(new(Notes, status));
		}

		public IReadOnlyList<WebTabSummary> ListWebTabs() => _webTabs.Values.ToArray();

		public bool TryGetWebTab(string pageId, out WebTabSummary summary) =>
			_webTabs.TryGetValue(pageId, out summary!);

		public Task<bool> ResumeWebTabAsync(
			string pageId,
			CancellationToken cancellationToken)
		{
			if (!_webTabs.TryGetValue(pageId, out var summary))
			{
				return Task.FromResult(false);
			}

			ResumeCalls++;
			_webTabs[pageId] = summary with { State = "active" };
			return Task.FromResult(true);
		}

		public Task<WebPageDocumentFragment?> ReadWebTabHtmlAsync(
			string pageId,
			WebPageDocumentRange range,
			CancellationToken cancellationToken)
		{
			if (!_webTabs.TryGetValue(pageId, out var summary)
				|| summary.State != "active")
			{
				return Task.FromResult<WebPageDocumentFragment?>(null);
			}

			const string html = "A😀B";
			var length = Math.Min(range.MaxChars, html.Length - range.Offset);
			return Task.FromResult<WebPageDocumentFragment?>(
				WebPageDocumentFragment.Create(
					html.Substring(range.Offset, length),
					html.Length,
					range));
		}

		public bool TryGetActiveRun(string runId, out ReviewRunDetails details)
		{
			details = _review;
			return string.Equals(runId, _review.Run.RunId, StringComparison.Ordinal);
		}

		public ReviewControlOutcome RequestReviewPause(string runId) => runId switch
		{
			"run-1" => new(ReviewControlStatus.Applied, _review.Run),
			"stopping" => new(ReviewControlStatus.NotPausable, null),
			_ => new(ReviewControlStatus.UnknownRun, null)
		};

		public ReviewControlOutcome ResumeReview(string runId) =>
			string.Equals(runId, "run-1", StringComparison.Ordinal)
				? new(ReviewControlStatus.Unchanged, _review.Run)
				: new(ReviewControlStatus.UnknownRun, null);

		public IReadOnlyList<UsageSummary> ListUsage() =>
			[
				new(
					"claude",
					"Claude",
					"Available",
					"5h 80%",
					"Weekly 60%")
			];

		public Task SendMessageAsync(
			string sessionId,
			string text,
			CancellationToken cancellationToken)
		{
			Sent.Add((sessionId, text));
			return Task.CompletedTask;
		}

		public bool IsScenarioLocked(string sessionId, out string runId)
		{
			return _scenarioLocks.TryGetValue(sessionId, out runId!);
		}

		public bool IsSessionAlive(string sessionId) =>
			_alive.TryGetValue(sessionId, out var alive) && alive;

		public void SetAlive(string sessionId, bool alive) => _alive[sessionId] = alive;

		public void SetScenarioLocked(string sessionId, string runId) =>
			_scenarioLocks[sessionId] = runId;

		public void SetScreen(string sessionId, SessionScreenState state) =>
			_screens[sessionId] = state;
	}
}
