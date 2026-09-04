using System.Collections.Specialized;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class WorkspaceViewModelTests
{
	[Test]
	public void ProjectNotePseudoTabUsesDocsAndNotesLabel()
	{
		ProjectNoteViewModel note = new(
			new NotesTabRecord("notes-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			@"C:\repo");

		ProjectNoteViewModel.Title.ShouldBe("Docs & Notes");
		ProjectNoteViewModel.PageKind.ShouldBe("docs");
	}

	[Test]
	public void TreeItems_PlaceNotesAfterWebPages()
	{
		WorkspaceViewModel workspace = new(CreateProject());
		ProjectNoteViewModel note = new(new NotesTabRecord("n1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), workspace.RootPath);
		SessionViewModel session = new(CreateSession());
		WebPageViewModel webPage = new(CreateWebPage());
		workspace.Notes.Add(note);
		workspace.Sessions.Add(session);
		workspace.WebPages.Add(webPage);
		workspace.TreeItems.ShouldBe([session, webPage, note]);
	}

	[Test]
	public void IsNotesTabOpen_TracksNotesCollection()
	{
		WorkspaceViewModel workspace = new(CreateProject());
		List<string?> raised = [];
		workspace.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
		workspace.IsNotesTabOpen.ShouldBeFalse();
		ProjectNoteViewModel note = new(new NotesTabRecord("n1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), workspace.RootPath);
		workspace.Notes.Add(note);
		workspace.IsNotesTabOpen.ShouldBeTrue();
		raised.ShouldContain(nameof(WorkspaceViewModel.IsNotesTabOpen));
		workspace.Notes.Remove(note);
		workspace.IsNotesTabOpen.ShouldBeFalse();
	}

	[Test]
	public void TreeItems_are_sessions_then_scenario_runs_then_web_pages()
	{
		WorkspaceViewModel workspace = new(CreateProject());
		SessionViewModel session = new(CreateSession());
		WebPageViewModel webPage = new(CreateWebPage());
		var run = CreateScenarioRunViewModel();

		workspace.WebPages.Add(webPage);
		workspace.ScenarioRuns.Add(run);
		workspace.Sessions.Add(session);

		workspace.TreeItems.ShouldBe([session, run, webPage]);
	}

	[Test]
	public void TreeItems_adds_web_page_incrementally_without_rebuilding_existing_items()
	{
		WorkspaceViewModel workspace = new(CreateProject());
		SessionViewModel session = new(CreateSession());
		var run = CreateScenarioRunViewModel();
		WebPageViewModel webPage = new(CreateWebPage());
		List<NotifyCollectionChangedAction> actions = [];

		workspace.Sessions.Add(session);
		workspace.ScenarioRuns.Add(run);
		workspace.TreeItems.CollectionChanged += (_, e) => actions.Add(e.Action);

		workspace.WebPages.Add(webPage);

		actions.ShouldBe([NotifyCollectionChangedAction.Add]);
		workspace.TreeItems.ShouldBe([session, run, webPage]);
	}

	[Test]
	public void Move_updates_flattened_tree_order_without_replacing_items()
	{
		WorkspaceViewModel workspace = new(CreateProject());
		SessionViewModel firstSession = new(CreateSession() with { Id = "session-1" });
		SessionViewModel secondSession = new(CreateSession() with { Id = "session-2" });
		WebPageViewModel firstPage = new(CreateWebPage() with { Id = "web-1" });
		WebPageViewModel secondPage = new(CreateWebPage() with { Id = "web-2" });
		workspace.Sessions.Add(firstSession);
		workspace.Sessions.Add(secondSession);
		workspace.WebPages.Add(firstPage);
		workspace.WebPages.Add(secondPage);

		workspace.Sessions.Move(1, 0);
		workspace.WebPages.Move(1, 0);

		workspace.TreeItems.ShouldBe([secondSession, firstSession, secondPage, firstPage]);
	}

	[Test]
	public void IsGitRepository_uses_detector_for_project_root()
	{
		WorkspaceViewModel workspace = new(
			CreateProject(rootPath: @"D:\Work\Repo"),
			path => path is not null && path.EndsWith("Repo", StringComparison.Ordinal));

		workspace.IsGitRepository.ShouldBeTrue();
	}

	[Test]
	public void UpdateRecord_recomputes_git_repository_state()
	{
		WorkspaceViewModel workspace = new(
			CreateProject(rootPath: @"D:\Work\NotRepo"),
			path => path is not null && path.EndsWith("Repo", StringComparison.Ordinal));

		workspace.UpdateRecord(CreateProject(rootPath: @"D:\Work\Repo"));

		workspace.IsGitRepository.ShouldBeTrue();
	}

	[Test]
	public void WebPageViewModel_tracks_loaded_browser_state_for_pause_indicator()
	{
		WebPageViewModel webPage = new(CreateWebPage());
		List<string?> changedProperties = [];
		webPage.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		webPage.IsBrowserLoaded.ShouldBeFalse();
		webPage.IsBrowserPaused.ShouldBeTrue();

		webPage.SetBrowserLoaded(true);

		webPage.IsBrowserLoaded.ShouldBeTrue();
		webPage.IsBrowserPaused.ShouldBeFalse();
		changedProperties.ShouldContain(nameof(WebPageViewModel.IsBrowserLoaded));
		changedProperties.ShouldContain(nameof(WebPageViewModel.IsBrowserPaused));

		webPage.SetBrowserLoaded(false);

		webPage.IsBrowserLoaded.ShouldBeFalse();
		webPage.IsBrowserPaused.ShouldBeTrue();
	}

	[Test]
	public void WebPageViewModel_exposes_compact_address_and_english_copy_tooltip()
	{
		WebPageViewModel webPage = new(new WebPageRecord(
			"web-1",
			"GitLab",
			"https://gitlab.example.com/group/project/-/tags",
			"https://gitlab.example.com/group/project/-/tags",
			DateTimeOffset.UtcNow,
			DateTimeOffset.UtcNow));

		webPage.DisplayAddress.ShouldBe("GIT:group/project/-/tags");
		webPage.AddressToolTip.ShouldBe("https://gitlab.example.com/group/project/-/tags" + Environment.NewLine + "Right-click → Copy");
	}

	[Test]
	public void WebPageViewModel_tracks_current_browser_state_for_active_indicator()
	{
		WebPageViewModel webPage = new(CreateWebPage());
		List<string?> changedProperties = [];
		webPage.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		webPage.SetCurrentBrowser(true);

		webPage.IsCurrentBrowser.ShouldBeTrue();
		changedProperties.ShouldContain(nameof(WebPageViewModel.IsCurrentBrowser));

		webPage.SetCurrentBrowser(false);

		webPage.IsCurrentBrowser.ShouldBeFalse();
	}

	private static ProjectRecord CreateProject(string rootPath = @"D:\Work\Project")
	{
		var now = DateTimeOffset.UtcNow;
		return new ProjectRecord("project-1", "Project", rootPath, now, now, Notes: null);
	}

	private static SessionRecord CreateSession()
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionRecord("session-1", AgentKind.Codex, "Codex", @"D:\Work\Project", "codex", "codex resume", SessionStatus.Stopped, now, now);
	}

	private static WebPageRecord CreateWebPage()
	{
		var now = DateTimeOffset.UtcNow;
		return new WebPageRecord("web-1", "GitLab", "https://gitlab/group/repo", "https://gitlab/group/repo/-/merge_requests/1", now, now);
	}

	private static ScenarioRunViewModel CreateScenarioRunViewModel()
	{
		ScenarioRunService service = new(new ImmediateScenarioGateway());
		ScenarioBlueprint blueprint = new(
			"test-scenario",
			"Test scenario",
			["reviewer"],
			[new ScenarioStepMetadata("run", "reviewer", null, "Run", ScenarioStepKind.Decision)],
			DefaultMaxIterations: 1,
			DefaultTarget: "start");
		var handle = service.Start(
			blueprint,
			new ImmediateScenarioProgram(),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start",
			maxIterations: 1);
		return new ScenarioRunViewModel(handle, action => action());
	}

	private sealed class ImmediateScenarioGateway : IScenarioTerminalGateway
	{
		public Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken) =>
			Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true));
		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
		public bool IsSessionAlive(string sessionId) => true;

		public string GetSessionLabel(string sessionId) => sessionId;
	}

	private sealed class ImmediateScenarioProgram : IScenarioProgram
	{
		public Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken) => Task.FromResult(true);
	}
}
