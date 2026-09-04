using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.Settings;
using Pact.Presentation.Tests.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class ProjectSettingsEditorTests
{
	[Test]
	public async Task CreateProjectForDirectoryAsync_delegates_to_EnsureWorkspaceForDirectoryAsync()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var mainWindowViewModel = MainWindowViewModelTestFactory.Create(store);
		await mainWindowViewModel.LoadAsync(CancellationToken.None);
		ProjectSettingsEditor editor = new(mainWindowViewModel);

		var workspaceId = await editor.CreateProjectForDirectoryAsync(@"D:\Work\NewProj", CancellationToken.None);

		var workspace = mainWindowViewModel.Workspaces.ShouldHaveSingleItem();
		workspaceId.ShouldBe(workspace.Id);
	}

	[Test]
	public async Task UpdateProjectSettingsAsync_delegates_to_the_main_window_view_model()
	{
		var now = DateTimeOffset.UtcNow;
		ProjectRecord project = new("p1", "Original", @"D:\Work\Original", now, now, null);
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var mainWindowViewModel = MainWindowViewModelTestFactory.Create(store);
		await mainWindowViewModel.LoadAsync(CancellationToken.None);
		ProjectSettingsEditor editor = new(mainWindowViewModel);

		await editor.UpdateProjectSettingsAsync("p1", new ProjectSettingsEdit(Name: "Renamed"), CancellationToken.None);

		store.Document.Projects.ShouldHaveSingleItem().Name.ShouldBe("Renamed");
	}

	[Test]
	public async Task UpdateSessionSettingsAsync_delegates_to_the_main_window_view_model()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord session = new("s1", AgentKind.Codex, "Title", @"D:\Work", "codex", null, SessionStatus.Running, now, now);
		var project = new ProjectRecord("p1", "Original", @"D:\Work", now, now, null) with { Sessions = [session] };
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var mainWindowViewModel = MainWindowViewModelTestFactory.Create(store);
		await mainWindowViewModel.LoadAsync(CancellationToken.None);
		ProjectSettingsEditor editor = new(mainWindowViewModel);

		await editor.UpdateSessionSettingsAsync("s1", new SessionSettingsEdit(Title: "Renamed"), CancellationToken.None);

		var saved = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		saved.Title.ShouldBe("Renamed");
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		public ProjectsDocument Document { get; private set; } = document;

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Document);

		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken)
		{
			Document = document;
			return Task.CompletedTask;
		}

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken)
		{
			Document = update(Document);
			return Task.FromResult(Document);
		}
	}
}