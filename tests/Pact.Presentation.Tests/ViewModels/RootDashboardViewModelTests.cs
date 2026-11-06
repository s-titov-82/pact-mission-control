using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using Pact.Core.Agents;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class RootDashboardViewModelTests
{
	[Test]
	public async Task Recent_folders_exclude_active_and_paused_roots_and_keep_history_order()
	{
		ObservableCollection<WorkspaceViewModel> active = [Project("active")];
		ObservableCollection<WorkspaceViewModel> paused = [Project("paused")];
		using var dashboard = new RootDashboardViewModel(active, new Reader(_ => Task.FromResult<int?>(0)),
			_ => false, a => a(), new ManualTimeProvider(), paused,
			_ => Task.FromResult<IReadOnlyList<string>>(["C:/ACTIVE/", "C:/paused", "C:/fresh", "C:/FRESH/", "C:/other"]));
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.PausedProjects.ShouldBe([paused[0]]);
		dashboard.RecentDirectories.ShouldBe(["C:/fresh", "C:/other"]);
		active.RemoveAt(0);
		dashboard.RecentDirectories.ShouldBe(["C:/ACTIVE/", "C:/fresh", "C:/other"]);
		paused[0].UpdateRecord(paused[0].Record with { RootPath = "C:/fresh" });
		dashboard.RecentDirectories.ShouldBe(["C:/ACTIVE/", "C:/paused", "C:/other"]);
		paused.Clear();
		dashboard.PausedProjects.ShouldBeEmpty();
		dashboard.RecentDirectories.ShouldBe(["C:/ACTIVE/", "C:/paused", "C:/fresh", "C:/other"]);
	}

	[Test]
	public async Task A_recent_folder_load_from_a_hidden_dashboard_cannot_replace_a_newer_load()
	{
		TaskCompletionSource<IReadOnlyList<string>> first = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var loads = 0;
		using var dashboard = new RootDashboardViewModel([], new Reader(_ => Task.FromResult<int?>(0)),
			_ => false, a => a(), new ManualTimeProvider(), [],
			_ => ++loads == 1 ? first.Task : Task.FromResult<IReadOnlyList<string>>(["C:/new"]));
		var oldActivation = dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Deactivate();
		await dashboard.ActivateAsync(CancellationToken.None);
		first.SetResult(["C:/old"]);
		await oldActivation;
		dashboard.RecentDirectories.ShouldBe(["C:/new"]);
	}

	[Test]
	public async Task Recent_folder_read_failure_is_visible_and_retried_on_reopening()
	{
		var loads = 0;
		using var dashboard = new RootDashboardViewModel([], new Reader(_ => Task.FromResult<int?>(0)),
			_ => false, a => a(), new ManualTimeProvider(), [],
			_ => ++loads == 1 ? Task.FromException<IReadOnlyList<string>>(new IOException("locked"))
				: Task.FromResult<IReadOnlyList<string>>(["C:/available"]));
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.RecentEmptyText.ShouldBe("Could not load recent folders");
		dashboard.Deactivate();
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.RecentDirectories.ShouldBe(["C:/available"]);
	}
	[Test]
	public async Task Status_and_name_updates_preserve_card_and_agent_item_sources()
	{
		var project = Project("one");
		var session = Session("live");
		project.Sessions.Add(session);
		using var dashboard = new RootDashboardViewModel([project], new Reader(_ => Task.FromResult<int?>(0)),
			_ => true, a => a(), new ManualTimeProvider());
		await dashboard.ActivateAsync(CancellationToken.None);
		var cards = dashboard.Cards;
		var card = cards[0];
		var agents = card.Agents;
		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, null, "Working");
		dashboard.RefreshAgents();
		card.Agents.ShouldBeSameAs(agents);
		project.UpdateRecord(project.Record with { Name = "Renamed" });
		dashboard.Cards.ShouldBeSameAs(cards);
		dashboard.Cards[0].ShouldBeSameAs(card);
		card.Agents.ShouldBeSameAs(agents);
	}
	[Test]
	public async Task Projects_sharing_a_root_share_one_status_query()
	{
		var first = Project("one");
		var second = Project("two");
		second.UpdateRecord(second.Record with { RootPath = first.RootPath });
		var reads = 0;
		using var dashboard = new RootDashboardViewModel([first, second],
			new Reader(_ => { reads++; return Task.FromResult<int?>(4); }), _ => false, a => a(), new ManualTimeProvider());
		await dashboard.ActivateAsync(CancellationToken.None);
		reads.ShouldBe(1);
		dashboard.Cards.Select(card => card.ChangesText).ShouldBe(["Changes · 4", "Changes · 4"]);
	}
	[Test]
	public async Task Git_sweep_has_at_most_two_reads_in_flight()
	{
		ConcurrentQueue<TaskCompletionSource<int?>> gates = new();
		using SemaphoreSlim entered = new(0);
		using var dashboard = new RootDashboardViewModel([Project("a"), Project("b"), Project("c"), Project("d")],
			new Reader(async _ =>
			{
				TaskCompletionSource<int?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
				gates.Enqueue(gate);
				entered.Release();
				return await gate.Task;
			}), _ => false, a => a(), new ManualTimeProvider());
		var refresh = dashboard.ActivateAsync(CancellationToken.None);
		await entered.WaitAsync(TestContext.CurrentContext.CancellationToken);
		await entered.WaitAsync(TestContext.CurrentContext.CancellationToken);
		gates.Count.ShouldBe(2);
		entered.CurrentCount.ShouldBe(0);
		gates.TryDequeue(out var first).ShouldBeTrue();
		first!.SetResult(1);
		await entered.WaitAsync(TestContext.CurrentContext.CancellationToken);
		gates.TryDequeue(out var second).ShouldBeTrue();
		second!.SetResult(2);
		await entered.WaitAsync(TestContext.CurrentContext.CancellationToken);
		while (gates.TryDequeue(out var gate))
		{ gate.SetResult(3); }
		await refresh;
		dashboard.Cards.All(card => card.ChangesText.StartsWith("Changes ·", StringComparison.Ordinal)).ShouldBeTrue();
	}

	[Test]
	public async Task Non_git_project_does_not_issue_status_reads_and_is_still_visible()
	{
		WorkspaceViewModel project = new(Project("plain").Record, _ => false);
		using var dashboard = new RootDashboardViewModel([project], new Reader(_ => throw new InvalidOperationException()),
			_ => false, a => a(), new ManualTimeProvider());
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards.ShouldHaveSingleItem().HasGitRepository.ShouldBeFalse();
		dashboard.IsEmpty.ShouldBeFalse();
	}
	[Test]
	public void Repeated_deactivation_after_disposal_is_safe_for_late_shutdown_presentation()
	{
		var time = new ManualTimeProvider();
		var dashboard = new RootDashboardViewModel([], new Reader(_ => throw new InvalidOperationException()),
			_ => false, a => a(), time);
		dashboard.Dispose();
		Should.NotThrow(dashboard.Deactivate);
		time.Advance(TimeSpan.FromSeconds(10));
	}
	[Test]
	public async Task Visible_dashboard_reconciles_at_five_seconds_and_removes_lost_runtimes()
	{
		var time = new ManualTimeProvider();
		var live = true;
		var reads = 0;
		var project = Project("one");
		project.Sessions.Add(Session("agent"));
		using var dashboard = new RootDashboardViewModel(new ObservableCollection<WorkspaceViewModel> { project },
			new Reader(_ => Task.FromResult<int?>(reads++)), _ => live, a => a(), time);
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards[0].ChangesText.ShouldBe("No uncommitted changes");
		time.Advance(TimeSpan.FromSeconds(4));
		reads.ShouldBe(1);
		time.Advance(TimeSpan.FromSeconds(1));
		dashboard.Cards[0].ChangesText.ShouldBe("Changes · 1");
		live = false;
		dashboard.RefreshAgents();
		dashboard.Cards[0].Agents.ShouldBeEmpty();
	}

	[Test]
	public async Task Root_edit_discards_the_old_result_before_reading_the_new_root()
	{
		TaskCompletionSource<int?> gate = new();
		var project = Project("one");
		using var dashboard = new RootDashboardViewModel(new ObservableCollection<WorkspaceViewModel> { project },
			new Reader(root => root == "C:/one" ? gate.Task : Task.FromResult<int?>(2)), _ => false,
			a => a(), new ManualTimeProvider());
		var refresh = dashboard.ActivateAsync(CancellationToken.None);
		project.UpdateRecord(project.Record with { RootPath = "C:/new" });
		gate.SetResult(90);
		await refresh;
		dashboard.Cards[0].RootPath.ShouldBe("C:/new");
		dashboard.Cards[0].ChangesText.ShouldBe("Changes · 2");
	}
	[Test]
	public async Task Cards_follow_project_order_live_agents_and_status_results()
	{
		var first = Project("one");
		var second = Project("two");
		var live = Session("live");
		first.Sessions.Add(live);
		first.Sessions.Add(Session("saved"));
		ObservableCollection<WorkspaceViewModel> projects = [first, second];
		using var dashboard = new RootDashboardViewModel(projects, new Reader(_ => Task.FromResult<int?>(3)),
			s => s.Record.Id == "live", action => action(), new ManualTimeProvider());
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards.Select(c => c.Workspace.Id).ShouldBe(["one", "two"]);
		dashboard.Cards[0].Agents.ShouldBe([live]);
		dashboard.Cards[1].Agents.ShouldBeEmpty();
		dashboard.Cards[0].ChangesText.ShouldBe("Changes · 3");
		projects.Remove(first);
		dashboard.Cards.ShouldHaveSingleItem().Workspace.ShouldBeSameAs(second);
	}

	[Test]
	public async Task Late_git_result_after_leaving_cannot_publish_into_a_reopened_card()
	{
		TaskCompletionSource<int?> gate = new();
		var project = Project("one");
		var reads = 0;
		using var dashboard = new RootDashboardViewModel(new ObservableCollection<WorkspaceViewModel> { project },
			new Reader(_ => ++reads == 1 ? gate.Task : Task.FromResult<int?>(0)), _ => false,
			action => action(), new ManualTimeProvider());
		var first = dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards[0].ChangesText.ShouldBe("Checking changes…");
		dashboard.Deactivate();
		gate.SetResult(9);
		await first;
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards[0].ChangesText.ShouldBe("No uncommitted changes");
	}

	[Test]
	public async Task Failed_git_is_distinct_from_clean_and_hidden_dashboard_does_not_poll()
	{
		var time = new ManualTimeProvider();
		var reads = 0;
		using var dashboard = new RootDashboardViewModel(new ObservableCollection<WorkspaceViewModel> { Project("one") },
			new Reader(_ => { reads++; return Task.FromResult<int?>(null); }), _ => false, action => action(), time);
		await dashboard.ActivateAsync(CancellationToken.None);
		dashboard.Cards[0].ChangesText.ShouldBe("Changes unavailable");
		dashboard.Deactivate();
		time.Advance(TimeSpan.FromSeconds(30));
		reads.ShouldBe(1);
	}

	private static WorkspaceViewModel Project(string id) => new(
		new ProjectRecord(id, id, "C:/" + id, DateTimeOffset.MinValue, DateTimeOffset.MinValue, null), _ => true);

	private static SessionViewModel Session(string id) => new(new SessionRecord(id, AgentKind.Claude, id,
		"C:/one", "claude", null, SessionStatus.Running, DateTimeOffset.MinValue, DateTimeOffset.MinValue));

	private sealed class Reader(Func<string, Task<int?>> read) : IGitWorkingTreeReader
	{
		public Task<int?> ReadChangedFileCountAsync(string root, CancellationToken cancellationToken) => read(root);
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}
