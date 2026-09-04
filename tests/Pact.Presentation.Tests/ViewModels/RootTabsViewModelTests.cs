using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Presentation.ViewModels;
namespace Pact.Presentation.Tests.ViewModels;

public sealed class RootTabsViewModelTests
{
	[Test]
	public void Move_updates_flattened_tree_order_without_replacing_items()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord firstSession = new(
			"session-1", AgentKind.Codex, "One", "C:\\", "codex", null,
			SessionStatus.Stopped, now, now);
		SessionRecord secondSession = firstSession with { Id = "session-2", Title = "Two" };
		WebPageRecord firstPage = new(
			"web-1", "One", "https://one.test", "https://one.test", now, now);
		WebPageRecord secondPage = firstPage with
		{
			Id = "web-2",
			Title = "Two",
			StartUrl = "https://two.test",
			ResumeUrl = "https://two.test"
		};
		RootTabsViewModel viewModel = new(new RootTabsRecord(
			1,
			null,
			[firstSession, secondSession],
			[firstPage, secondPage],
			[]));
		var firstSessionViewModel = viewModel.Sessions[0];
		var secondSessionViewModel = viewModel.Sessions[1];
		var firstPageViewModel = viewModel.WebPages[0];
		var secondPageViewModel = viewModel.WebPages[1];

		viewModel.Sessions.Move(1, 0);
		viewModel.WebPages.Move(1, 0);

		viewModel.TreeItems.ShouldBe(
			[secondSessionViewModel, firstSessionViewModel, secondPageViewModel, firstPageViewModel]);
	}

	[Test]
	public async Task MoveTreeItemAsync_persists_root_web_page_order_across_reload()
	{
		var now = DateTimeOffset.UtcNow;
		WebPageRecord first = new(
			"web-1", "One", "https://one.test", "https://one.test", now, now);
		WebPageRecord second = new(
			"web-2", "Two", "https://two.test", "https://two.test", now, now);
		InMemoryRootTabsStore rootStore = new(new RootTabsRecord(
			1,
			first.Id,
			[],
			[first, second],
			[]));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(ProjectsDocument.CreateDefault()),
			rootTabsStore: rootStore);
		await viewModel.LoadAsync(CancellationToken.None);
		var selected = viewModel.SelectedWebPage.ShouldNotBeNull();

		var moved = await viewModel.MoveTreeItemAsync(
			viewModel.RootTabs.WebPages[1],
			viewModel.RootTabs.WebPages[0],
			insertAfter: false,
			CancellationToken.None);

		moved.ShouldBeTrue();
		viewModel.RootTabs.WebPages.Select(item => item.Record.Id)
			.ShouldBe(["web-2", "web-1"]);
		viewModel.SelectedWebPage.ShouldBeSameAs(selected);

		var reloaded = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(ProjectsDocument.CreateDefault()),
			rootTabsStore: rootStore);
		await reloaded.LoadAsync(CancellationToken.None);
		reloaded.RootTabs.WebPages.Select(item => item.Record.Id)
			.ShouldBe(["web-2", "web-1"]);
	}

	[Test]
	public void UpdateRecord_preserves_item_identity_and_projects_pause_state()
	{
		var now = DateTimeOffset.UtcNow;
		var session = new SessionRecord(
			"root-session",
			AgentKind.Codex,
			"Hermes",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
		var webPage = new WebPageRecord(
			"root-web",
			"Jira",
			"https://jira.example.test",
			"https://jira.example.test",
			now,
			now);
		RootTabsViewModel viewModel = new(new RootTabsRecord(
			1,
			session.Id,
			[session],
			[webPage],
			[webPage.Id]));
		var originalSession = viewModel.Sessions.Single();
		var originalWebPage = viewModel.WebPages.Single();

		viewModel.UpdateRecord(viewModel.Record with
		{
			Sessions = [session with { Title = "General Hermes" }],
			PausedItemIds = [session.Id]
		});

		viewModel.Sessions.Single().ShouldBeSameAs(originalSession);
		viewModel.WebPages.Single().ShouldBeSameAs(originalWebPage);
		viewModel.TreeItems.ShouldBe([originalSession, originalWebPage]);
		originalSession.Title.ShouldBe("General Hermes");
		originalSession.IsManuallyPaused.ShouldBeTrue();
		originalSession.CanResetAgentSession.ShouldBeFalse();
		originalWebPage.IsManuallyPaused.ShouldBeFalse();
		originalSession.IsRootItem.ShouldBeTrue();
		originalWebPage.IsRootItem.ShouldBeTrue();
	}

	[Test]
	public async Task Main_window_loads_root_items_and_keeps_paused_items_out_of_live_collections()
	{
		var now = DateTimeOffset.UtcNow;
		var active = CreateSession("active", now);
		var paused = CreateSession("paused", now.AddMinutes(1));
		var store = new InMemoryRootTabsStore(new RootTabsRecord(
			1,
			paused.Id,
			[active, paused],
			[],
			[paused.Id]));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(Pact.Core.Projects.ProjectsDocument.CreateDefault()),
			rootTabsStore: store);

		await viewModel.LoadAsync(CancellationToken.None);

		viewModel.RootTabs.Sessions.Select(item => item.Record.Id).ShouldBe(["active", "paused"]);
		viewModel.Sessions.Select(item => item.Record.Id).ShouldBe(["active"]);
		viewModel.SelectedSession?.Record.Id.ShouldBe("paused");
		viewModel.SelectedSession!.IsManuallyPaused.ShouldBeTrue();
	}

	[Test]
	public async Task Pausing_selected_root_session_persists_without_changing_selection()
	{
		var now = DateTimeOffset.UtcNow;
		var session = CreateSession("root-session", now);
		var store = new InMemoryRootTabsStore(new RootTabsRecord(
			1,
			session.Id,
			[session],
			[],
			[]));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(Pact.Core.Projects.ProjectsDocument.CreateDefault()),
			rootTabsStore: store);
		await viewModel.LoadAsync(CancellationToken.None);
		var selected = viewModel.SelectedSession.ShouldNotBeNull();

		await viewModel.SetRootItemPausedAsync(selected.Record.Id, true, CancellationToken.None);

		viewModel.SelectedSession.ShouldBeSameAs(selected);
		viewModel.Sessions.ShouldBeEmpty();
		selected.IsManuallyPaused.ShouldBeTrue();
		(await store.LoadAsync(CancellationToken.None)).IsPaused(selected.Record.Id).ShouldBeTrue();
	}

	[Test]
	public async Task Root_session_is_available_to_manual_targets_but_not_owned_by_a_project()
	{
		var now = DateTimeOffset.UtcNow;
		var rootSession = CreateSession("root-session", now);
		var store = new InMemoryRootTabsStore(new RootTabsRecord(
			1,
			rootSession.Id,
			[rootSession],
			[],
			[]));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(Pact.Core.Projects.ProjectsDocument.CreateDefault()),
			rootTabsStore: store);
		await viewModel.LoadAsync(CancellationToken.None);

		viewModel.SelectedWorkspace.ShouldBeNull();
		viewModel.PromptTemplateTargets.ShouldBe([viewModel.RootTabs.Sessions.Single()]);
	}

	[Test]
	public void UpdateRecord_removes_deleted_items_and_keeps_saved_order()
	{
		var now = DateTimeOffset.UtcNow;
		var first = new SessionRecord(
			"first",
			AgentKind.Pwsh,
			"First",
			@"C:\",
			"pwsh",
			null,
			SessionStatus.Stopped,
			now,
			now);
		var second = first with { Id = "second", Title = "Second" };
		RootTabsViewModel viewModel = new(new RootTabsRecord(1, null, [first, second], [], []));

		viewModel.UpdateRecord(viewModel.Record with { Sessions = [second] });

		viewModel.Sessions.Select(item => item.Record.Id).ShouldBe(["second"]);
		viewModel.TreeItems.ShouldBe([viewModel.Sessions.Single()]);
	}

	private static SessionRecord CreateSession(string id, DateTimeOffset now) => new(
		id,
		AgentKind.Codex,
		id,
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
		"codex",
		null,
		SessionStatus.Stopped,
		now,
		now);
}
