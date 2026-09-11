using Pact.App.Avalonia.Controllers;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SoftRestartSnapshotBuilderTests
{
	[Test]
	public void Snapshot_keeps_only_live_unpaused_runtime_surfaces_and_metadata()
	{
		var viewModel = CreateViewModel();
		var project = CreateWorkspace("project-1", "project", "reviewer", "project-web");
		var paused = CreateWorkspace("paused-project", "paused", null, "paused-web");
		viewModel.Workspaces.Add(project);
		viewModel.PausedWorkspaces.Add(paused);
		viewModel.RootTabs.UpdateRecord(new RootTabsRecord(
			1,
			"root",
			[Session("root"), Session("paused-root")],
			[Web("root-web"), Web("paused-root-web")],
			["paused-root", "paused-root-web"]));
		var orchestrator = new SessionViewModel(Session("orchestrator"));
		viewModel.OrchestratorSlot.AttachSession(orchestrator);
		viewModel.OrchestratorSlot.Apply(true, true, true, "Running");
		viewModel.SelectedWorkspace = project;
		viewModel.SelectedSession = project.Sessions[1];

		var diagnostics = new Dictionary<string, TerminalClassifierDiagnostics>(StringComparer.Ordinal)
		{
			["project"] = Diagnostics("project", unread: false),
			["reviewer"] = Diagnostics("reviewer", unread: true),
			["root"] = Diagnostics("root", unread: true),
			["orchestrator"] = Diagnostics("orchestrator", unread: false),
			["paused"] = Diagnostics("paused", unread: true),
			["paused-root"] = Diagnostics("paused-root", unread: true)
		};
		SoftRestartSnapshotBuilder builder = new(
			viewModel,
			() => ["project", "reviewer", "root", "orchestrator", "paused", "paused-root", "stale"],
			() => diagnostics,
			() => ["project-web", "root-web", "paused-web", "paused-root-web", "stale-web"]);

		var snapshot = builder.Capture();

		snapshot.LiveTerminalIds.ShouldBe(["orchestrator", "project", "reviewer", "root"]);
		snapshot.LoadedWebPageIds.ShouldBe(["project-web", "root-web"]);
		snapshot.UnreadTerminalIds.ShouldBe(["reviewer", "root"]);
		snapshot.Selection.ShouldBe(new Pact.Core.Updates.SoftRestartSelection("project-1", "reviewer"));
		snapshot.OrchestratorWasRunning.ShouldBeTrue();
	}

	private static MainWindowViewModel CreateViewModel() => new(
		new EmptyProjectStore(),
		new EmptyNotesStore());

	private static WorkspaceViewModel CreateWorkspace(
		string projectId,
		string sessionId,
		string? reviewerId,
		string webId)
	{
		WorkspaceViewModel workspace = new(
			new ProjectRecord(
				projectId,
				projectId,
				Environment.CurrentDirectory,
				DateTimeOffset.UtcNow,
				DateTimeOffset.UtcNow,
				null),
			static _ => false);
		workspace.Sessions.Add(new SessionViewModel(Session(sessionId)));
		if (reviewerId is not null)
		{
			workspace.Sessions.Add(new SessionViewModel(Session(reviewerId, AgentKind.Claude)));
		}
		workspace.WebPages.Add(new WebPageViewModel(Web(webId)));
		return workspace;
	}

	private static SessionRecord Session(string id, AgentKind kind = AgentKind.Pwsh) => new(
		id,
		kind,
		id,
		Environment.CurrentDirectory,
		"pwsh",
		null,
		SessionStatus.Running,
		DateTimeOffset.UtcNow,
		DateTimeOffset.UtcNow);

	private static WebPageRecord Web(string id) => new(
		id,
		id,
		"https://example.com/",
		"https://example.com/",
		DateTimeOffset.UtcNow,
		DateTimeOffset.UtcNow);

	private static TerminalClassifierDiagnostics Diagnostics(string id, bool unread) => new(
		id,
		AgentKind.Pwsh,
		SessionStatus.Running,
		VerdictState: null,
		VerdictDescription: string.Empty,
		unread ? TerminalTabIndicator.Unread : TerminalTabIndicator.None,
		IndicatorDescription: string.Empty,
		PromptIsEmpty: true,
		InputRequested: false,
		StatusLine: string.Empty,
		ActivityInProgress: false,
		ActivityEpoch: 1,
		unread,
		Columns: null,
		Rows: null,
		LastClassificationAt: null);

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());

		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
