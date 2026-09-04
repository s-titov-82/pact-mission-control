using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class ProjectsSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _tempRoot => _temporaryDirectory.Path;
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Load_builds_one_item_per_workspace_with_sessions()
	{
		var dir = CreateSubDir("proj-1");
		var workspace = CreateWorkspace("proj-1", "Proj One", dir);
		workspace.Sessions.Add(CreateSession("sess-1", "Main", dir, "codex"));
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());

		await section.LoadAsync(CancellationToken.None);

		var item = section.Items.ShouldHaveSingleItem();
		item.Id.ShouldBe("proj-1");
		item.Name.ShouldBe("Proj One");
		item.RootPath.ShouldBe(dir);
		var sessionItem = item.Sessions.ShouldHaveSingleItem();
		sessionItem.Id.ShouldBe("sess-1");
		sessionItem.Title.ShouldBe("Main");
		section.IsDirty.ShouldBeFalse();
	}

	[Test]
	public async Task Editing_name_marks_item_and_section_dirty_and_edit_contains_only_name()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir);
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());
		await section.LoadAsync(CancellationToken.None);
		var item = section.Items[0];

		item.Name = "Renamed";

		item.IsItemDirty.ShouldBeTrue();
		section.IsDirty.ShouldBeTrue();
		var edit = item.BuildProjectEdit();
		edit.Name.ShouldBe("Renamed");
		edit.RootPath.ShouldBeNull();
		edit.Notes.ShouldBeNull();
		edit.GitLabRepoId.ShouldBeNull();
		edit.ClearGitLabRepoId.ShouldBeFalse();
	}

	[Test]
	public async Task Project_InfoLine_combines_id_status_created_and_last_active()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Proj", dir);
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());

		await section.LoadAsync(CancellationToken.None);

		var item = section.Items[0];
		item.InfoLine.ShouldBe($"{item.Id} · {item.StatusDisplay} · created {item.CreatedAtDisplay} · active {item.LastActiveAtDisplay}");
	}

	[Test]
	public async Task Session_InfoLine_combines_id_kind_and_status()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Proj", dir);
		workspace.Sessions.Add(CreateSession("s1", "Main", dir, "codex"));
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());

		await section.LoadAsync(CancellationToken.None);

		var item = section.Items[0].Sessions[0];
		item.InfoLine.ShouldBe($"{item.Id} · {item.KindDisplay} · {item.StatusDisplay}");
	}

	[Test]
	public async Task HasSessions_is_false_for_a_project_without_sessions_and_true_with_them()
	{
		var dirA = CreateSubDir("a");
		var dirB = CreateSubDir("b");
		var withSession = CreateWorkspace("a", "A", dirA);
		withSession.Sessions.Add(CreateSession("sa", "Sess A", dirA, "cmd"));
		var withoutSession = CreateWorkspace("b", "B", dirB);
		var section = CreateSection(() => [withSession, withoutSession], new FakeProjectSettingsEditor());

		await section.LoadAsync(CancellationToken.None);

		section.Items[0].HasSessions.ShouldBeTrue();
		section.Items[1].HasSessions.ShouldBeFalse();
	}

	[Test]
	[TestCase(AgentKind.Codex, false)]
	[TestCase(AgentKind.Claude, false)]
	[TestCase(AgentKind.Hermes, false)]
	[TestCase(AgentKind.Pwsh, true)]
	[TestCase(AgentKind.Custom, true)]
	public async Task ShowWorkingDirectorySetting_is_true_only_for_pwsh_and_custom_kinds(AgentKind kind, bool expected)
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Proj", dir);
		workspace.Sessions.Add(CreateSession("s1", "Main", dir, "cmd", kind: kind));
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());

		await section.LoadAsync(CancellationToken.None);

		section.Items[0].Sessions[0].ShowWorkingDirectorySetting.ShouldBe(expected);
	}

	[Test]
	public async Task Agent_kind_session_skips_working_directory_validation_but_pwsh_kind_does_not()
	{
		var dir = CreateSubDir("p1");
		var missing = Path.Combine(_tempRoot, "gone-zzz");
		var workspace = CreateWorkspace("p1", "Proj", dir);
		workspace.Sessions.Add(CreateSession("s1", "Agent sess", missing, "codex", kind: AgentKind.Codex));
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspace], editor);
		await section.LoadAsync(CancellationToken.None);
		section.Items[0].Sessions[0].Title = "Agent sess 2";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		editor.SessionUpdates.ShouldHaveSingleItem();

		var pwshWorkspace = CreateWorkspace("p2", "Proj 2", dir);
		pwshWorkspace.Sessions.Add(CreateSession("s2", "Shell sess", missing, "pwsh", kind: AgentKind.Pwsh));
		var pwshSection = CreateSection(() => [pwshWorkspace], new FakeProjectSettingsEditor());
		await pwshSection.LoadAsync(CancellationToken.None);

		(await pwshSection.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		pwshSection.StatusText!.Contains("working directory", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public async Task Emptying_a_previously_set_GitLabRepoId_sets_the_clear_flag()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir, gitLabRepoId: "42");
		var section = CreateSection(() => [workspace], new FakeProjectSettingsEditor());
		await section.LoadAsync(CancellationToken.None);
		var item = section.Items[0];
		item.GitLabRepoId.ShouldBe("42");

		item.GitLabRepoId = "";

		var edit = item.BuildProjectEdit();
		edit.ClearGitLabRepoId.ShouldBeTrue();
		edit.GitLabRepoId.ShouldBeNull();
	}

	[Test]
	public async Task Save_updates_only_dirty_items_and_clean_items_never_reach_the_editor()
	{
		var dirA = CreateSubDir("a");
		var dirB = CreateSubDir("b");
		var workspaceA = CreateWorkspace("a", "A", dirA);
		workspaceA.Sessions.Add(CreateSession("sa", "Sess A", dirA, "cmdA"));
		var workspaceB = CreateWorkspace("b", "B", dirB);
		workspaceB.Sessions.Add(CreateSession("sb", "Sess B", dirB, "cmdB"));
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspaceA, workspaceB], editor);
		await section.LoadAsync(CancellationToken.None);

		section.Items[0].Name = "A2";
		section.Items[0].Sessions[0].Title = "Sess A2";
		// workspaceB / its session are left untouched (clean).

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeTrue();
		editor.ProjectUpdates.ShouldHaveSingleItem();
		editor.ProjectUpdates[0].ProjectId.ShouldBe("a");
		editor.SessionUpdates.ShouldHaveSingleItem();
		editor.SessionUpdates[0].SessionId.ShouldBe("sa");
		section.Items[0].IsItemDirty.ShouldBeFalse();
		section.Items[1].IsItemDirty.ShouldBeFalse();
		section.IsDirty.ShouldBeFalse();
		section.StatusText.ShouldBe($"Saved {section.Label} (2 items).");
	}

	[Test]
	public async Task Successful_save_sets_confirmation_status_with_applied_item_count()
	{
		var dirA = CreateSubDir("a");
		var dirB = CreateSubDir("b");
		var workspaceA = CreateWorkspace("a", "A", dirA);
		workspaceA.Sessions.Add(CreateSession("sa", "Sess A", dirA, "cmdA"));
		var workspaceB = CreateWorkspace("b", "B", dirB);
		workspaceB.Sessions.Add(CreateSession("sb", "Sess B", dirB, "cmdB"));
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspaceA, workspaceB], editor);
		await section.LoadAsync(CancellationToken.None);

		// Dirty project A (its own field) and dirty session B (nested); workspaceB's own fields
		// and workspaceA's session stay clean, so only 2 of the 4 possible edits are applied.
		section.Items[0].Name = "A2";
		section.Items[1].Sessions[0].Title = "Sess B2";

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeTrue();
		section.StatusText.ShouldBe($"Saved {section.Label} (2 items).");
	}

	[Test]
	public async Task Successful_save_with_nothing_dirty_reports_zero_items_applied()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir);
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspace], editor);
		await section.LoadAsync(CancellationToken.None);

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeTrue();
		editor.ProjectUpdates.ShouldBeEmpty();
		section.StatusText.ShouldBe($"Saved {section.Label} (0 items).");
	}

	[Test]
	public async Task Failing_editor_keeps_section_dirty_and_sets_status_text()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir);
		FakeProjectSettingsEditor editor = new() { ThrowOnProjectUpdate = true };
		var section = CreateSection(() => [workspace], editor);
		await section.LoadAsync(CancellationToken.None);
		section.Items[0].Name = "Changed";

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeFalse();
		section.IsDirty.ShouldBeTrue();
		section.Items[0].IsItemDirty.ShouldBeTrue();
		section.StatusText.ShouldNotBeNull();
		section.StatusText.Contains("p1", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public async Task Invalid_root_path_blocks_save_and_never_calls_the_editor()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir);
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspace], editor);
		await section.LoadAsync(CancellationToken.None);
		section.Items[0].RootPath = Path.Combine(_tempRoot, "does-not-exist-zzz");

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeFalse();
		section.StatusText.ShouldNotBeNull();
		editor.ProjectUpdates.ShouldBeEmpty();
		section.IsDirty.ShouldBeTrue();
	}

	[Test]
	public async Task Locked_session_produces_an_empty_edit_and_is_skipped_on_save()
	{
		var dir = CreateSubDir("p1");
		var workspace = CreateWorkspace("p1", "Original", dir);
		var session = CreateSession("s1", "Title", dir, "cmd");
		session.LockForScenario("run-1");
		workspace.Sessions.Add(session);
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [workspace], editor);
		await section.LoadAsync(CancellationToken.None);
		var sessionItem = section.Items[0].Sessions[0];

		sessionItem.IsLocked.ShouldBeTrue();
		sessionItem.BuildSessionEdit().ShouldBe(new SessionSettingsEdit());

		var result = await section.SaveAsync(CancellationToken.None);

		result.ShouldBeTrue();
		editor.SessionUpdates.ShouldBeEmpty();
	}

	[Test]
	public async Task SelectItem_selects_project_tab_and_session_unknown_ids_are_noop()
	{
		var dirA = CreateSubDir("a");
		var dirB = CreateSubDir("b");
		var workspaceA = CreateWorkspace("a", "A", dirA);
		workspaceA.Sessions.Add(CreateSession("sa1", "S1", dirA, "cmd"));
		var workspaceB = CreateWorkspace("b", "B", dirB);
		var section = CreateSection(() => [workspaceA, workspaceB], new FakeProjectSettingsEditor());
		await section.LoadAsync(CancellationToken.None);

		section.SelectItem("b", null);
		section.SelectedItem.ShouldBeSameAs(section.Items[1]);

		section.SelectItem("a", "sa1");
		section.SelectedItem.ShouldBeSameAs(section.Items[0]);
		section.Items[0].SelectedSession.ShouldBeSameAs(section.Items[0].Sessions[0]);

		var selectedBefore = section.SelectedItem;
		section.SelectItem("unknown-project", null);
		section.SelectedItem.ShouldBeSameAs(selectedBefore);

		var selectedSessionBefore = section.Items[0].SelectedSession;
		section.SelectItem("a", "unknown-session");
		section.SelectedItem.ShouldBeSameAs(section.Items[0]);
		section.Items[0].SelectedSession.ShouldBeSameAs(selectedSessionBefore);
	}

	[Test]
	public async Task AddProjectAsync_cancel_is_noop()
	{
		FakeProjectSettingsEditor editor = new();
		var section = CreateSection(() => [], editor, pickDirectoryAsync: () => Task.FromResult<string?>(null));

		await section.AddProjectAsync(CancellationToken.None);

		section.Items.ShouldBeEmpty();
		editor.CreatedDirectory.ShouldBeNull();
	}

	[Test]
	public async Task AddProjectAsync_creates_reloads_and_selects_the_new_tab()
	{
		var newDir = CreateSubDir("new-proj");
		List<WorkspaceViewModel> workspaces = [];
		FakeProjectSettingsEditor editor = new()
		{
			OnCreateProjectForDirectory = directory =>
			{
				var added = CreateWorkspace("new-id", "New", directory);
				workspaces.Add(added);
				return "new-id";
			}
		};
		var section = CreateSection(() => workspaces, editor, pickDirectoryAsync: () => Task.FromResult<string?>(newDir));

		await section.AddProjectAsync(CancellationToken.None);

		editor.CreatedDirectory.ShouldBe(newDir);
		var added = section.Items.ShouldHaveSingleItem();
		added.Id.ShouldBe("new-id");
		section.SelectedItem.ShouldBeSameAs(added);
	}

	private string CreateSubDir(string name)
	{
		var path = Path.Combine(_tempRoot, name);
		Directory.CreateDirectory(path);
		return path;
	}

	private static ProjectsSectionViewModel CreateSection(
		Func<IReadOnlyList<WorkspaceViewModel>> workspacesProvider,
		IProjectSettingsEditor editor,
		Func<Task<string?>>? pickDirectoryAsync = null)
		=> new(workspacesProvider, editor, pickDirectoryAsync ?? (() => Task.FromResult<string?>(null)), @"C:\x\projects.json");

	private static WorkspaceViewModel CreateWorkspace(
		string id,
		string name,
		string rootPath,
		string? notes = null,
		string? gitLabRepoId = null,
		string? teamCityProjectId = null)
	{
		var now = DateTimeOffset.UtcNow;
		ProjectRecord record = new(id, name, rootPath, now, now, notes)
		{
			GitLabRepoId = gitLabRepoId,
			TeamCityProjectId = teamCityProjectId
		};
		return new WorkspaceViewModel(record, _ => false);
	}

	private static SessionViewModel CreateSession(
		string id,
		string title,
		string workingDirectory,
		string launchCommand,
		string? resumeCommand = null,
		AgentKind kind = AgentKind.Codex)
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord record = new(
			id,
			kind,
			title,
			workingDirectory,
			launchCommand,
			resumeCommand,
			SessionStatus.Running,
			now,
			now);
		return new SessionViewModel(record);
	}

	private sealed class FakeProjectSettingsEditor : IProjectSettingsEditor
	{
		public List<(string ProjectId, ProjectSettingsEdit Edit)> ProjectUpdates { get; } = [];
		public List<(string SessionId, SessionSettingsEdit Edit)> SessionUpdates { get; } = [];
		public string? CreatedDirectory { get; private set; }
		public bool ThrowOnProjectUpdate { get; set; }
		public bool ThrowOnSessionUpdate { get; set; }
		public Func<string, string?>? OnCreateProjectForDirectory { get; set; }

		public Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct)
		{
			ProjectUpdates.Add((projectId, edit));
			if (ThrowOnProjectUpdate)
			{
				throw new InvalidOperationException($"boom-{projectId}");
			}

			return Task.CompletedTask;
		}

		public Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct)
		{
			SessionUpdates.Add((sessionId, edit));
			if (ThrowOnSessionUpdate)
			{
				throw new InvalidOperationException($"boom-{sessionId}");
			}

			return Task.CompletedTask;
		}

		public Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct)
		{
			CreatedDirectory = directory;
			return Task.FromResult(OnCreateProjectForDirectory?.Invoke(directory));
		}
	}
}