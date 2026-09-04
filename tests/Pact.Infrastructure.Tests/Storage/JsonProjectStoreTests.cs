using System.Text.Json;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class JsonProjectStoreTests
{
	[Test]
	public async Task LoadAsync_returns_default_document_when_file_is_missing()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		JsonProjectStore store = new(root);

		var document = await store.LoadAsync(CancellationToken.None);

		document.SchemaVersion.ShouldBe(1);
		document.Projects.ShouldBeEmpty();
	}

	[Test]
	public async Task SaveAsync_persists_projects_with_nested_sessions_and_web_pages()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		JsonProjectStore store = new(root);
		DateTimeOffset createdAt = new(2026, 7, 2, 10, 15, 0, TimeSpan.FromHours(3));
		var lastActiveAt = createdAt.AddMinutes(25);
		SessionRecord session = new(
			"session-1",
			AgentKind.Codex,
			"Reviewer",
			@"D:\Personal\Pact",
			"codex",
			"codex resume codex-session-123",
			SessionStatus.Running,
			createdAt,
			lastActiveAt);
		WebPageRecord webPage = new(
			"web-1",
			"GitLab Requests",
			"https://gitlab/group/repo/-/merge_requests",
			"https://gitlab/group/repo/-/merge_requests/42",
			createdAt,
			lastActiveAt);
		ProjectRecord project = new(
			"project-1",
			"Pact",
			@"D:\Personal\Pact",
			createdAt,
			lastActiveAt,
			"Project notes")
		{
			Status = WorkspaceStatus.Active,
			ActiveItemId = webPage.Id,
			Sessions = [session],
			WebPages = [webPage],
			GitLabRepoId = "group/repo",
			TeamCityProjectId = "Pact_Build"
		};
		ProjectsDocument document = new(SchemaVersion: 1, Projects: [project]);

		await store.SaveAsync(document, CancellationToken.None);
		var json = await File.ReadAllTextAsync(new AppPaths(root).ProjectsPath);
		using var jsonDocument = JsonDocument.Parse(json);
		var rootElement = jsonDocument.RootElement;
		var projectElement = rootElement.GetProperty("projects")[0];
		var sessionElement = projectElement.GetProperty("sessions")[0];

		rootElement.TryGetProperty("sessions", out _).ShouldBeFalse();
		sessionElement.TryGetProperty("profileId", out _).ShouldBeFalse();
		sessionElement.TryGetProperty("name", out _).ShouldBeFalse();
		sessionElement.TryGetProperty("tags", out _).ShouldBeFalse();
		sessionElement.TryGetProperty("notes", out _).ShouldBeFalse();
		sessionElement.TryGetProperty("role", out _).ShouldBeFalse();
		sessionElement.GetProperty("kind").GetString().ShouldBe("codex");
		sessionElement.GetProperty("title").GetString().ShouldBe("Reviewer");
		sessionElement.GetProperty("startCommand").GetString().ShouldBe("codex");
		sessionElement.GetProperty("resumeCommand").GetString().ShouldBe("codex resume codex-session-123");
		projectElement.TryGetProperty("activeSessionId", out _).ShouldBeFalse();
		projectElement.TryGetProperty("sessionOrder", out _).ShouldBeFalse();
		projectElement.GetProperty("activeItemId").GetString().ShouldBe("web-1");
		projectElement.GetProperty("gitLabRepoId").GetString().ShouldBe("group/repo");
		projectElement.GetProperty("teamCityProjectId").GetString().ShouldBe("Pact_Build");

		var webPageElement = projectElement.GetProperty("webPages")[0];
		webPageElement.GetProperty("id").GetString().ShouldBe("web-1");
		webPageElement.GetProperty("title").GetString().ShouldBe("GitLab Requests");
		webPageElement.GetProperty("startUrl").GetString().ShouldBe("https://gitlab/group/repo/-/merge_requests");
		webPageElement.GetProperty("resumeUrl").GetString().ShouldBe("https://gitlab/group/repo/-/merge_requests/42");

		var loaded = await store.LoadAsync(CancellationToken.None);

		var loadedProject = loaded.Projects.ShouldHaveSingleItem();
		var loadedSession = loadedProject.Sessions.ShouldHaveSingleItem();
		loadedProject.Id.ShouldBe(project.Id);
		loadedProject.Name.ShouldBe(project.Name);
		loadedProject.RootPath.ShouldBe(project.RootPath);
		loadedProject.Status.ShouldBe(project.Status);
		loadedProject.ActiveItemId.ShouldBe(project.ActiveItemId);
		loadedProject.GitLabRepoId.ShouldBe(project.GitLabRepoId);
		loadedProject.TeamCityProjectId.ShouldBe(project.TeamCityProjectId);
		var loadedWebPage = loadedProject.WebPages.ShouldHaveSingleItem();
		loadedWebPage.ShouldBe(webPage);
		loadedSession.Id.ShouldBe(session.Id);
		loadedSession.Kind.ShouldBe(session.Kind);
		loadedSession.Title.ShouldBe(session.Title);
		loadedSession.WorkingDirectory.ShouldBe(session.WorkingDirectory);
		loadedSession.LaunchCommand.ShouldBe(session.LaunchCommand);
		loadedSession.ResumeCommand.ShouldBe(session.ResumeCommand);
		loadedSession.Status.ShouldBe(session.Status);
		loadedSession.CreatedAt.ShouldBe(session.CreatedAt);
		loadedSession.LastActiveAt.ShouldBe(session.LastActiveAt);
		File.Exists(new AppPaths(root).ProjectsPath).ShouldBeTrue();
		File.Exists(Path.Combine(root, "registry.json")).ShouldBeFalse();
	}

	[Test]
	public async Task UpdateAsync_serializes_concurrent_project_changes()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		JsonProjectStore store = new(root);
		await store.SaveAsync(ProjectsDocument.CreateDefault(), CancellationToken.None);

		Task[] updates = Enumerable
			.Range(0, 25)
			.Select(index => Task.Run(
				() => store.UpdateAsync(
					document => document with
					{
						Projects = document.Projects
							.Concat([CreateProjectRecord($"project-{index}")])
							.ToArray()
					},
					CancellationToken.None)))
			.ToArray();

		await Task.WhenAll(updates);

		var loaded = await store.LoadAsync(CancellationToken.None);

		loaded.Projects.Count.ShouldBe(25);
		for (var index = 0; index < 25; index++)
		{
			loaded.Projects.ShouldContain(project => project.Id == $"project-{index}");
		}
	}

	[Test]
	public async Task SaveAndLoad_RoundTripsNotesTab()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		JsonProjectStore store = new(root);
		NotesTabRecord notesTab = new(
			"note-1",
			DateTimeOffset.Parse("2026-07-11T10:00:00Z"),
			DateTimeOffset.Parse("2026-07-11T11:00:00Z"));
		var project = CreateProjectRecord("p1") with { NotesTab = notesTab };

		await store.SaveAsync(new ProjectsDocument(1, [project]), CancellationToken.None);
		var loaded = await store.LoadAsync(CancellationToken.None);

		var loadedTab = loaded.Projects.Single().NotesTab;
		loadedTab.ShouldNotBeNull();
		loadedTab.Id.ShouldBe("note-1");
		loadedTab.CreatedAt.ShouldBe(notesTab.CreatedAt);
		loadedTab.LastActiveAt.ShouldBe(notesTab.LastActiveAt);
	}

	[Test]
	public async Task Load_ProjectsJsonWithoutNotesTab_LoadsNull()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.ProjectsPath,
								 /*lang=json,strict*/
								 """
            {
              "schemaVersion": 1,
              "projects": [{
                "id": "p1", "name": "Project", "rootPath": "D:\\proj",
                "createdAt": "2026-07-11T10:00:00Z", "lastActiveAt": "2026-07-11T10:00:00Z",
                "notes": null
              }]
            }
            """);
		JsonProjectStore store = new(root);

		var loaded = await store.LoadAsync(CancellationToken.None);

		loaded.Projects.Single().NotesTab.ShouldBeNull();
	}

	[Test]
	public async Task Legacy_repository_and_branch_hints_load_but_are_omitted_on_next_save()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.ProjectsPath,
								 /*lang=json,strict*/
								 """
            {
              "schemaVersion": 1,
              "projects": [{
                "id": "p1",
                "name": "Project",
                "rootPath": "C:\\project",
                "createdAt": "2026-07-28T00:00:00+00:00",
                "lastActiveAt": "2026-07-28T00:00:00+00:00",
                "notes": null,
                "repositoryHint": "old",
                "branchHint": "main",
                "sessions": [],
                "webPages": []
              }]
            }
            """);
		JsonProjectStore store = new(root);

		var document = await store.LoadAsync(CancellationToken.None);
		await store.SaveAsync(document, CancellationToken.None);
		var saved = await File.ReadAllTextAsync(paths.ProjectsPath);

		saved.ShouldNotContain("repositoryHint");
		saved.ShouldNotContain("branchHint");
	}

	private static ProjectRecord CreateProjectRecord(string id)
	{
		var now = DateTimeOffset.UtcNow;
		return new ProjectRecord(
			id,
			$"Project {id}",
			Path.GetTempPath(),
			now,
			now,
			Notes: null);
	}
}
