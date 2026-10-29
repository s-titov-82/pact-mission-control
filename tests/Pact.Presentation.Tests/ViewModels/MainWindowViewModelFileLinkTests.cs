using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class MainWindowViewModelFileLinkTests
{
	private static readonly DateTimeOffset Old = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);
	private static readonly DateTimeOffset Now = new(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public async Task Adding_a_file_link_saves_it_on_the_session()
	{
		var store = CreateStore();
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.AddSessionFileLinkAsync("s1", Link(@"C:\x\a.md"), CancellationToken.None);

		Session(await store.LoadAsync(CancellationToken.None)).FileLinks.ShouldBe([Link(@"C:\x\a.md")]);
		viewModel.Sessions.Single().Record.FileLinks.ShouldBe([Link(@"C:\x\a.md")]);
	}

	[Test]
	public async Task Adding_the_same_file_again_moves_it_to_the_top()
	{
		var store = CreateStore();
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.AddSessionFileLinkAsync("s1", Link(@"C:\x\a.md"), CancellationToken.None);
		await viewModel.AddSessionFileLinkAsync("s1", Link(@"C:\x\b.md"), CancellationToken.None);
		await viewModel.AddSessionFileLinkAsync("s1", Link("c:/x/A.md"), CancellationToken.None);

		Session(await store.LoadAsync(CancellationToken.None)).FileLinks.Select(link => link.Path)
			.ShouldBe(["c:/x/A.md", @"C:\x\b.md"]);
	}

	[Test]
	public async Task Removing_a_file_link_saves_the_list_without_it()
	{
		var store = CreateStore(Link(@"C:\x\a.md"), Link(@"C:\x\b.md"));
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionFileLinkAsync("s1", @"C:\x\a.md", CancellationToken.None);

		Session(await store.LoadAsync(CancellationToken.None)).FileLinks.Select(link => link.Path).ShouldBe([@"C:\x\b.md"]);
	}

	[Test]
	public async Task A_link_survives_reloading_the_projects()
	{
		var store = CreateStore();
		var first = MainWindowViewModelTestFactory.Create(store);
		await first.LoadAsync(CancellationToken.None);
		await first.AddSessionFileLinkAsync("s1", Link(@"C:\x\a.md"), CancellationToken.None);

		var second = MainWindowViewModelTestFactory.Create(store);
		await second.LoadAsync(CancellationToken.None);

		second.Sessions.Single().Record.FileLinks.ShouldBe([Link(@"C:\x\a.md")]);
	}

	[Test]
	public async Task A_root_session_keeps_its_links()
	{
		var root = new SessionRecord("r1", AgentKind.Codex, "Root", @"C:\", "codex", null, SessionStatus.Stopped, Old, Old);
		InMemoryRootTabsStore rootStore = new(new RootTabsRecord(1, root.Id, [root], [], []));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new TestProjectStore(ProjectsDocument.CreateDefault()),
			rootTabsStore: rootStore);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.AddSessionFileLinkAsync("r1", Link(@"C:\x\a.md"), CancellationToken.None);

		(await rootStore.LoadAsync(CancellationToken.None)).Sessions.Single().FileLinks.ShouldBe([Link(@"C:\x\a.md")]);
		viewModel.RootTabs.Sessions.Single().Record.FileLinks.ShouldBe([Link(@"C:\x\a.md")]);
	}

	[Test]
	public async Task A_link_edit_on_an_older_session_keeps_the_projects_activity_time()
	{
		var store = CreateStore();
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.AddSessionFileLinkAsync("s1", Link(@"C:\x\a.md"), CancellationToken.None);
		(await store.LoadAsync(CancellationToken.None)).Projects.Single().LastActiveAt.ShouldBe(Now);
		await viewModel.RemoveSessionFileLinkAsync("s1", @"C:\x\a.md", CancellationToken.None);

		var project = (await store.LoadAsync(CancellationToken.None)).Projects.Single();
		project.LastActiveAt.ShouldBe(Now);
		project.Sessions.Single().LastActiveAt.ShouldBe(Old);
	}

	private static TestProjectStore CreateStore(params AgentFileLink[] links)
	{
		var session = new SessionRecord("s1", AgentKind.Claude, "Claude", @"D:\Work\P", "claude", null, SessionStatus.Stopped, Old, Old)
		{
			FileLinks = links
		};
		return new TestProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [new ProjectRecord("p1", "P", @"D:\Work\P", Old, Now, null) { Sessions = [session] }]
		});
	}

	private static SessionRecord Session(ProjectsDocument document) => document.Projects.Single().Sessions.Single();

	private static AgentFileLink Link(string path) => new(path, null, Now);
}
