using Pact.Core.Sessions;
using Pact.Core.AgentControl;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class AgentControlDispatcherTests
{
	private const string ProjectSessionId = "session-project";
	private const string RootSessionId = "session-root";

	[Test]
	public async Task GetNotesAsync_returns_the_callers_project_snapshot()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.GetNotesAsync(
			ProjectSessionId,
			CancellationToken.None);

		result.Payload.ShouldNotBeNull().ShouldContain("\"text\":\"project notes\"");
		result.Payload.ShouldContain("\"revision\":");
		host.ReadProjectIds.ShouldBe(["project-1"]);
	}

	[Test]
	public async Task GetNotesAsync_refuses_a_root_session()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.GetNotesAsync(
			RootSessionId,
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("owner-not-a-project");
		host.ReadProjectIds.ShouldBeEmpty();
	}

	[Test]
	public async Task ReplaceNotesAsync_reports_a_conflict_without_claiming_success()
	{
		FakeAgentControlHost host = new()
		{
			ReplaceResult = new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText("current"),
				ProjectNotesMutationStatus.Conflict)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.ReplaceNotesAsync(
			ProjectSessionId,
			new ReplaceNoteRequest("new", "stale"),
			CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe("notes-conflict");
	}

	[Test]
	public async Task ReplaceNotesAsync_reports_changed_but_unpersisted_content()
	{
		FakeAgentControlHost host = new()
		{
			ReplaceResult = new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText("new"),
				ProjectNotesMutationStatus.AppliedButNotPersisted)
		};
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.ReplaceNotesAsync(
			ProjectSessionId,
			new ReplaceNoteRequest("new", "revision"),
			CancellationToken.None);

		result.Succeeded.ShouldBeFalse();
		result.Failure.ShouldNotBeNull().Code.ShouldBe("notes-save-failed");
		result.Failure.Message.ShouldContain("buffer changed");
	}

	[Test]
	public async Task AppendNoteAsync_RoutesTextToOwningProject()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			ProjectSessionId,
			new AppendNoteRequest("captured decision"),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.AppendedNotes.ShouldHaveSingleItem();
		host.AppendedNotes[0].ProjectId.ShouldBe("project-1");
		host.AppendedNotes[0].Text.ShouldBe("captured decision");
	}

	[Test]
	public async Task AppendNoteAsync_RefusesUnknownSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			"ghost",
			new AppendNoteRequest("text"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("unknown-session");
		host.AppendedNotes.ShouldBeEmpty();
	}

	[Test]
	public async Task AppendNoteAsync_RefusesRootSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.AppendNoteAsync(
			RootSessionId,
			new AppendNoteRequest("text"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("owner-not-a-project");
		host.AppendedNotes.ShouldBeEmpty();
	}

	[Test]
	public async Task Open_document_from_root_is_refused()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenDocumentAsync(
			RootSessionId,
			new OpenDocumentRequest("docs/a.md"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("owner-not-a-project");
		host.OpenedDocuments.ShouldBeEmpty();
	}

	[Test]
	public async Task Open_document_resolves_against_the_project_root()
	{
		using var root = TemporaryDirectory.Create();
		Directory.CreateDirectory(Path.Combine(root.Path, "docs"));
		await File.WriteAllTextAsync(Path.Combine(root.Path, "docs", "a.md"), "# A");
		FakeAgentControlHost host = new() { ProjectRoot = root.Path };
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenDocumentAsync(
			ProjectSessionId,
			new OpenDocumentRequest(Path.Combine(root.Path, "docs", "a.md")),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.OpenedDocuments.ShouldBe([("project-1", "docs/a.md")]);
	}

	[Test]
	public async Task Open_document_refuses_a_path_outside_the_project()
	{
		using var root = TemporaryDirectory.Create();
		FakeAgentControlHost host = new() { ProjectRoot = root.Path };
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenDocumentAsync(
			ProjectSessionId,
			new OpenDocumentRequest("../secret.md"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("outside-project");
		host.OpenedDocuments.ShouldBeEmpty();
	}

	[Test]
	public async Task Open_document_reports_a_file_the_docs_pane_does_not_list()
	{
		using var root = TemporaryDirectory.Create();
		Directory.CreateDirectory(Path.Combine(root.Path, ".pact-reviews"));
		await File.WriteAllTextAsync(Path.Combine(root.Path, ".pact-reviews", "a.md"), "# A");
		FakeAgentControlHost host = new() { ProjectRoot = root.Path, DocumentShown = false };
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenDocumentAsync(
			ProjectSessionId,
			new OpenDocumentRequest(".pact-reviews/a.md"),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("not-listed");
	}

	[Test]
	public async Task OpenWebTabAsync_CreatesTabUnderProjectOwner()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			ProjectSessionId,
			new OpenWebTabRequest("https://example.com/mr/42", "MR 42"),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.WebTabs.ShouldHaveSingleItem();
		host.WebTabs[0].Owner.ProjectId.ShouldBe("project-1");
		host.WebTabs[0].Url.ShouldBe("https://example.com/mr/42");
		host.WebTabs[0].Title.ShouldBe("MR 42");
	}

	[Test]
	public async Task OpenWebTabAsync_CreatesTabForRootSession()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			RootSessionId,
			new OpenWebTabRequest("https://example.com", null),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.WebTabs[0].Owner.IsRoot.ShouldBeTrue();
	}

	[Test]
	public async Task OpenWebTabAsync_RefusesNonHttpUrl()
	{
		FakeAgentControlHost host = new();
		AgentControlDispatcher dispatcher = new(host);

		var result = await dispatcher.OpenWebTabAsync(
			ProjectSessionId,
			new OpenWebTabRequest("file:///c:/secret.txt", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("invalid-argument");
		host.WebTabs.ShouldBeEmpty();
	}

	[Test]
	public async Task AddFileLink_stores_an_absolute_path()
	{
		using var folder = TemporaryDirectory.Create();
		var file = Path.Combine(folder.Path, "a.txt");
		await File.WriteAllTextAsync(file, "x");
		FakeAgentControlHost host = new();

		var result = await new AgentControlDispatcher(host).AddFileLinkAsync(
			ProjectSessionId,
			new AddFileLinkRequest(file, "Plan"),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue(result.Failure?.Message);
		var (sessionId, link) = host.AddedLinks.ShouldHaveSingleItem();
		sessionId.ShouldBe(ProjectSessionId);
		link.Path.ShouldBe(file);
		link.Title.ShouldBe("Plan");
		result.Payload.ShouldNotBeNull().ShouldContain("a.txt");
	}

	[Test]
	public async Task A_relative_path_is_resolved_against_the_working_directory()
	{
		using var folder = TemporaryDirectory.Create();
		Directory.CreateDirectory(Path.Combine(folder.Path, "out"));
		var file = Path.Combine(folder.Path, "out", "a.txt");
		await File.WriteAllTextAsync(file, "x");
		FakeAgentControlHost host = new() { WorkingDirectory = folder.Path };
		AgentControlDispatcher dispatcher = new(host);

		await dispatcher.AddFileLinkAsync(ProjectSessionId, new AddFileLinkRequest(@".\out\a.txt", null), CancellationToken.None);
		await dispatcher.AddFileLinkAsync(ProjectSessionId, new AddFileLinkRequest("\"out/a.txt\"", null), CancellationToken.None);

		host.AddedLinks.Select(added => added.Link.Path).ShouldBe([file, file]);
	}

	[Test]
	public async Task A_missing_file_is_refused_and_says_so()
	{
		using var folder = TemporaryDirectory.Create();
		FakeAgentControlHost host = new() { WorkingDirectory = folder.Path };

		var result = await new AgentControlDispatcher(host).AddFileLinkAsync(
			ProjectSessionId,
			new AddFileLinkRequest("none.txt", null),
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("file-not-found");
		result.Failure.Message.ShouldContain(Path.Combine(folder.Path, "none.txt"));
		host.AddedLinks.ShouldBeEmpty();
	}

	[Test]
	public async Task A_folder_is_refused()
	{
		using var folder = TemporaryDirectory.Create();
		FakeAgentControlHost host = new();

		var result = await new AgentControlDispatcher(host).AddFileLinkAsync(
			ProjectSessionId,
			new AddFileLinkRequest(folder.Path, null),
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("not-a-file");
		host.AddedLinks.ShouldBeEmpty();
	}

	[Test]
	public async Task A_root_session_may_add_links()
	{
		using var folder = TemporaryDirectory.Create();
		var file = Path.Combine(folder.Path, "a.txt");
		await File.WriteAllTextAsync(file, "x");
		FakeAgentControlHost host = new();

		var result = await new AgentControlDispatcher(host).AddFileLinkAsync(
			RootSessionId,
			new AddFileLinkRequest(file, null),
			CancellationToken.None);

		result.Succeeded.ShouldBeTrue(result.Failure?.Message);
		host.AddedLinks.ShouldHaveSingleItem().SessionId.ShouldBe(RootSessionId);
	}

	[Test]
	public async Task An_unknown_session_is_refused()
	{
		FakeAgentControlHost host = new();

		var result = await new AgentControlDispatcher(host).AddFileLinkAsync(
			"gone",
			new AddFileLinkRequest(@"C:\a.txt", null),
			CancellationToken.None);

		result.Failure.ShouldNotBeNull().Code.ShouldBe("unknown-session");
		host.AddedLinks.ShouldBeEmpty();
	}

	internal sealed class FakeAgentControlHost : IAgentControlHost
	{
		public string WorkingDirectory { get; set; } = Path.GetTempPath();

		public List<(string SessionId, AgentFileLink Link)> AddedLinks { get; } = [];

		public Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken) =>
			Task.FromResult(TryGetOwner(sessionId, out _) ? WorkingDirectory : null);

		public Task AddSessionFileLinkAsync(string sessionId, AgentFileLink link, CancellationToken cancellationToken)
		{
			AddedLinks.Add((sessionId, link));
			return Task.CompletedTask;
		}

		public List<(string ProjectId, string Text)> AppendedNotes { get; } = [];

		public List<string> ReadProjectIds { get; } = [];

		public ProjectNotesSnapshot ReadSnapshot { get; set; } =
			ProjectNotesSnapshot.FromText("project notes");

		public ProjectNotesMutationResult ReplaceResult { get; set; } =
			new(
				ProjectNotesSnapshot.FromText("replacement"),
				ProjectNotesMutationStatus.Applied);

		public List<(AgentControlOwner Owner, string Url, string? Title)> WebTabs { get; } = [];

		public bool TryGetOwner(string sessionId, out AgentControlOwner owner)
		{
			switch (sessionId)
			{
				case ProjectSessionId:
					owner = new AgentControlOwner(IsRoot: false, ProjectId: "project-1");
					return true;
				case RootSessionId:
					owner = new AgentControlOwner(IsRoot: true, ProjectId: null);
					return true;
				default:
					owner = new AgentControlOwner(IsRoot: false, ProjectId: null);
					return false;
			}
		}

		public Task<ProjectNotesSnapshot> ReadProjectNotesAsync(
			string projectId,
			CancellationToken cancellationToken)
		{
			ReadProjectIds.Add(projectId);
			return Task.FromResult(ReadSnapshot);
		}

		public Task<ProjectNotesMutationResult> ReplaceProjectNotesAsync(
			string projectId,
			ReplaceNoteRequest request,
			CancellationToken cancellationToken) =>
			Task.FromResult(ReplaceResult);

		public Task<ProjectNotesMutationResult> AppendToProjectNotesAsync(
			string projectId,
			string text,
			CancellationToken cancellationToken)
		{
			AppendedNotes.Add((projectId, text));
			return Task.FromResult(new ProjectNotesMutationResult(
				ProjectNotesSnapshot.FromText(text),
				ProjectNotesMutationStatus.Applied));
		}

		public Task CreateWebTabAsync(
			AgentControlOwner owner,
			string url,
			string? title,
			CancellationToken cancellationToken)
		{
			WebTabs.Add((owner, url, title));
			return Task.CompletedTask;
		}

		public string ProjectRoot { get; set; } = string.Empty;

		public List<(string ProjectId, string RelativePath)> OpenedDocuments { get; } = [];

		public bool DocumentShown { get; set; } = true;

		public Task<string> GetProjectRootAsync(string projectId, CancellationToken cancellationToken) =>
			Task.FromResult(ProjectRoot);

		public Task<bool> OpenProjectDocumentAsync(
			string projectId,
			string relativePath,
			CancellationToken cancellationToken)
		{
			OpenedDocuments.Add((projectId, relativePath));
			return Task.FromResult(DocumentShown);
		}
	}
}
