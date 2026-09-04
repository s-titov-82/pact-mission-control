using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.Settings;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class MainWindowViewModelProjectSettingsTests
{
	[Test]
	public void Project_edit_trims_values_and_preserves_nested_records()
	{
		var now = DateTimeOffset.UtcNow;
		var project = CreateProjectRecord("project-1", "Original") with
		{
			Sessions =
			[
				new SessionRecord(
					"session-1",
					AgentKind.Codex,
					"Session",
					@"D:\Work",
					"codex",
					null,
					SessionStatus.Stopped,
					now.AddMinutes(-1),
					now.AddMinutes(-1))
			]
		};
		ProjectSettingsEdit edit = new(
			Name: "  New title  ",
			GitLabRepoId: "  group/project  ");

		var result = edit.ApplyTo(project, now);

		result.Name.ShouldBe("New title");
		result.GitLabRepoId.ShouldBe("group/project");
		result.Sessions.ShouldBeSameAs(project.Sessions);
		result.LastActiveAt.ShouldBe(now);
	}

	[Test]
	public void Session_edit_trims_launch_values_and_preserves_runtime_state()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var now = DateTimeOffset.UtcNow;
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Original",
			@"D:\Old",
			"codex",
			"codex resume existing",
			SessionStatus.Running,
			createdAt,
			createdAt);

		var result = new SessionSettingsEdit(
			Title: "  New title  ",
			WorkingDirectory: @"  D:\New  ",
			LaunchCommand: "  codex --full-auto  ")
			.ApplyTo(session, now);

		result.Title.ShouldBe("New title");
		result.WorkingDirectory.ShouldBe(@"D:\New");
		result.LaunchCommand.ShouldBe("codex --full-auto");
		result.Status.ShouldBe(SessionStatus.Running);
		result.CreatedAt.ShouldBe(createdAt);
		result.LastActiveAt.ShouldBe(now);
	}

	[Test]
	public async Task UpdateProjectSettingsAsync_applies_only_non_null_fields()
	{
		var project = CreateProjectRecord("project-1", "Original", notes: "keep", gitLabRepoId: "42");
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateProjectSettingsAsync(
			"project-1",
			new ProjectSettingsEdit(Name: "NewName"),
			CancellationToken.None);

		var saved = store.Document.Projects.ShouldHaveSingleItem();
		saved.Name.ShouldBe("NewName");
		saved.Notes.ShouldBe("keep");
		saved.GitLabRepoId.ShouldBe("42");
		var workspace = viewModel.Workspaces.ShouldHaveSingleItem();
		workspace.Record.Name.ShouldBe("NewName");
	}

	[Test]
	public async Task Clear_flag_nulls_the_GitLab_field()
	{
		var project = CreateProjectRecord(
			"project-1",
			"Original",
			gitLabRepoId: "42");
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateProjectSettingsAsync(
			"project-1",
			new ProjectSettingsEdit(ClearGitLabRepoId: true),
			CancellationToken.None);

		var saved = store.Document.Projects.ShouldHaveSingleItem();
		saved.GitLabRepoId.ShouldBeNull();
	}

	[Test]
	public async Task UpdateSessionSettingsAsync_edits_launch_fields_and_preserves_runtime_fields()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Session Title",
			@"D:\Old",
			"codex",
			null,
			SessionStatus.Running,
			createdAt,
			createdAt);
		var project = CreateProjectRecord("project-1", "Original") with { Sessions = [session] };
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var before = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();

		await viewModel.UpdateSessionSettingsAsync(
			"session-1",
			new SessionSettingsEdit(WorkingDirectory: @"C:\new", LaunchCommand: "codex --full-auto"),
			CancellationToken.None);

		var after = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		after.WorkingDirectory.ShouldBe(@"C:\new");
		after.LaunchCommand.ShouldBe("codex --full-auto");
		after.Status.ShouldBe(before.Status);
		after.CreatedAt.ShouldBe(before.CreatedAt);
		after.ResumeCommand.ShouldBe(before.ResumeCommand);
	}

	[Test]
	public async Task ClearResumeCommand_nulls_it()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Session Title",
			@"D:\Work",
			"codex",
			"codex resume session-1",
			SessionStatus.Running,
			createdAt,
			createdAt);
		var project = CreateProjectRecord("project-1", "Original") with { Sessions = [session] };
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateSessionSettingsAsync(
			"session-1",
			new SessionSettingsEdit(ClearResumeCommand: true),
			CancellationToken.None);

		var after = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		after.ResumeCommand.ShouldBeNull();
	}

	[Test]
	public async Task Empty_session_settings_edit_does_not_bump_LastActiveAt()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Session Title",
			@"D:\Work",
			"codex",
			"codex resume session-1",
			SessionStatus.Running,
			createdAt,
			createdAt);
		var project = CreateProjectRecord("project-1", "Original") with { Sessions = [session] };
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with { Projects = [project] });
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		var sessionBefore = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		var projectLastActiveAtBefore = store.Document.Projects.ShouldHaveSingleItem().LastActiveAt;
		var sessionViewModel = viewModel.Workspaces.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		var viewModelRecordBefore = sessionViewModel.Record;

		await viewModel.UpdateSessionSettingsAsync(
			"session-1",
			new SessionSettingsEdit(),
			CancellationToken.None);

		var sessionAfter = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		var projectLastActiveAtAfter = store.Document.Projects.ShouldHaveSingleItem().LastActiveAt;
		sessionAfter.ShouldBe(sessionBefore);
		projectLastActiveAtAfter.ShouldBe(projectLastActiveAtBefore);
		sessionViewModel.Record.ShouldBeSameAs(viewModelRecordBefore);
	}

	[Test]
	public async Task Unknown_ids_are_no_ops()
	{
		var project = CreateProjectRecord("project-1", "Original", notes: "keep", gitLabRepoId: "42");
		var original = ProjectsDocument.CreateDefault() with { Projects = [project] };
		InMemoryProjectStore store = new(original);
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateProjectSettingsAsync("missing", new ProjectSettingsEdit(Name: "x"), CancellationToken.None);
		await viewModel.UpdateSessionSettingsAsync("missing", new SessionSettingsEdit(Title: "x"), CancellationToken.None);

		var unchanged = store.Document.Projects.ShouldHaveSingleItem();
		unchanged.Name.ShouldBe("Original");
		unchanged.Notes.ShouldBe("keep");
		unchanged.GitLabRepoId.ShouldBe("42");
	}

	private static ProjectRecord CreateProjectRecord(
		string id,
		string name,
		string? notes = null,
		string? gitLabRepoId = null)
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		return new ProjectRecord(
			id,
			name,
			$@"D:\Work\{name}",
			createdAt,
			createdAt,
			notes)
		{
			GitLabRepoId = gitLabRepoId
		};
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