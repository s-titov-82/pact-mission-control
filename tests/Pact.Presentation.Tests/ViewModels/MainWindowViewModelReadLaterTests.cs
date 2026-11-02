using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class MainWindowViewModelReadLaterTests
{
	[Test]
	public async Task Two_toggles_while_the_first_save_waits_preserve_the_final_unmarked_choice()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord record = new("s", AgentKind.Codex, "Agent", @"C:\", "codex", null, SessionStatus.Stopped, now, now);
		GatedStore store = new(new TestProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [new ProjectRecord("p", "Project", @"C:\", now, now, null) { Sessions = [record] }]
		}));
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		store.BlockNext = true;
		var session = viewModel.Sessions.Single();
		var first = viewModel.ToggleReadLaterAsync(session);
		await store.Entered.Task;
		var second = viewModel.ToggleReadLaterAsync(session);
		second.IsCompleted.ShouldBeFalse();
		store.Release.SetResult();
		await Task.WhenAll(first, second);
		session.IsMarkedForLater.ShouldBeFalse();
		(await store.LoadAsync(CancellationToken.None)).Projects.Single().Sessions.Single().IsMarkedForLater.ShouldBeFalse();
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Bookmark_toggle_round_trips_through_the_owner_and_clears_again(bool root)
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord record = new("s", AgentKind.Codex, "Agent", @"C:\", "codex", null, SessionStatus.Stopped, now, now);
		TestProjectStore projects = new(ProjectsDocument.CreateDefault() with
		{
			Projects = root ? [] : [new ProjectRecord("p", "Project", @"C:\", now, now, null) { Sessions = [record] }]
		});
		InMemoryRootTabsStore roots = new(new(1, root ? "s" : null, root ? [record] : [], [], []));
		var first = MainWindowViewModelTestFactory.Create(projects, rootTabsStore: roots);
		await first.LoadAsync(CancellationToken.None);
		var session = root ? first.RootTabs.Sessions.Single() : first.Sessions.Single();
		await first.ToggleReadLaterAsync(session);
		session.IsMarkedForLater.ShouldBeTrue();
		var second = MainWindowViewModelTestFactory.Create(projects, rootTabsStore: roots);
		await second.LoadAsync(CancellationToken.None);
		var reloaded = root ? second.RootTabs.Sessions.Single() : second.Sessions.Single();
		reloaded.IsMarkedForLater.ShouldBeTrue();
		await Task.WhenAll(second.ToggleReadLaterAsync(reloaded), second.ToggleReadLaterAsync(reloaded));
		reloaded.IsMarkedForLater.ShouldBeTrue();
		await second.ToggleReadLaterAsync(reloaded);
		reloaded.IsMarkedForLater.ShouldBeFalse();
		(root ? (await roots.LoadAsync(CancellationToken.None)).Sessions.Single()
			: (await projects.LoadAsync(CancellationToken.None)).Projects.Single().Sessions.Single())
			.IsMarkedForLater.ShouldBeFalse();
	}

	private sealed class GatedStore(TestProjectStore inner) : IProjectStore
	{
		public bool BlockNext { get; set; }
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) => inner.SaveAsync(document, cancellationToken);
		public async Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> mutate, CancellationToken cancellationToken)
		{
			if (BlockNext)
			{
				BlockNext = false;
				Entered.SetResult();
				await Release.Task;
			}
			return await inner.UpdateAsync(mutate, cancellationToken);
		}
	}
}
