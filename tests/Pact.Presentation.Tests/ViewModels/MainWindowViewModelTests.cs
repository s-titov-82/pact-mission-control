using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
	[Test]
	public void Web_page_exposes_loading_state_and_glyph()
	{
		WebPageViewModel page = new(new WebPageRecord(
			"web-1", "Example", "https://example.test", "https://example.test",
			DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

		page.SetLoading(true);
		page.SetLoadingGlyph("⠙");

		page.IsLoading.ShouldBeTrue();
		page.LoadingGlyph.ShouldBe("⠙");
	}

	[Test]
	public async Task CreateSessionAsync_appends_session_to_project_through_store_update()
	{
		var project = CreateProjectRecord("project-1", "Project");
		UpdateOnlyProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		WorkspaceViewModel workspace = new(project);
		viewModel.Workspaces.Add(workspace);

		var session = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "Implement status updates",
			workingDirectory: "D:\\Work",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: project.Id);

		store.UpdateCallCount.ShouldBe(1);
		session.Record.Id.ShouldBe("session-1");
		session.Record.LaunchCommand.ShouldBe("pwsh");
		viewModel.SelectedSession.ShouldBeSameAs(session);
		viewModel.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
		workspace.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
		workspace.TreeItems.ShouldHaveSingleItem().ShouldBeSameAs(session);
		store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldContain(record => record.Id == "session-1");
	}

	[Test]
	public async Task CreateSessionAsync_KeepsCurrentSelectionWhenSelectIsFalse()
	{
		var project = CreateProjectRecord("project-1", "Project");
		UpdateOnlyProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		viewModel.Workspaces.Add(new WorkspaceViewModel(project));
		var first = await viewModel.CreateSessionAsync(
			"project-1",
			AgentKind.Claude,
			"author",
			@"C:\repo",
			"claude",
			null,
			CancellationToken.None);

		var second = await viewModel.CreateSessionAsync(
			"project-1",
			AgentKind.Codex,
			"reviewer",
			@"C:\repo",
			"codex",
			null,
			CancellationToken.None,
			workspaceId: null,
			select: false);

		viewModel.SelectedSession.ShouldBe(first);
		viewModel.Sessions.ShouldContain(second);
	}

	[Test]
	public async Task UpdateSessionStatusAsync_persists_status_and_updates_loaded_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		var beforeUpdate = DateTimeOffset.UtcNow;
		await viewModel.UpdateSessionStatusAsync(
			"session-1",
			SessionStatus.Running,
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.Status.ShouldBe(SessionStatus.Running);
		(updatedSession.LastActiveAt >= beforeUpdate).ShouldBeTrue();
		session.Status.ShouldBe("Running");
		session.Record.Status.ShouldBe(SessionStatus.Running);
		changedProperties.ShouldContain(nameof(SessionViewModel.Status));
	}

	[Test]
	public async Task UpdateSessionResumeCommandAsync_persists_resume_command_and_updates_loaded_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		await viewModel.UpdateSessionResumeCommandAsync(
			"session-1",
			"codex resume real-session-id",
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.ResumeCommand.ShouldBe("codex resume real-session-id");
		session.Record.ResumeCommand.ShouldBe("codex resume real-session-id");
		changedProperties.ShouldContain(nameof(SessionViewModel.Record));
	}

	[Test]
	public async Task ClearSessionResumeCommandAsync_clears_codex_resume_id_without_rewriting_profile_command()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt) with
		{
			Kind = AgentKind.Codex,
			ResumeCommand = "codex-personal resume abc12345"
		};
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		var beforeUpdate = DateTimeOffset.UtcNow;
		await viewModel.ClearSessionResumeCommandAsync(
			"session-1",
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.ResumeCommand.ShouldBe("codex-personal resume");
		(updatedSession.LastActiveAt >= beforeUpdate).ShouldBeTrue();
		session.Record.ResumeCommand.ShouldBe("codex-personal resume");
		changedProperties.ShouldContain(nameof(SessionViewModel.Record));
	}

	[Test]
	public async Task ClearSessionResumeCommandAsync_clears_claude_resume_id_without_rewriting_profile_command()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt) with
		{
			Kind = AgentKind.Claude,
			ResumeCommand = "claude-personal --resume abc12345"
		};
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();

		await viewModel.ClearSessionResumeCommandAsync(
			"session-1",
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.ResumeCommand.ShouldBe("claude-personal --resume");
		session.Record.ResumeCommand.ShouldBe("claude-personal --resume");
	}

	[Test]
	public async Task ClearSessionResumeCommandAsync_leaves_generic_resume_command_unchanged()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt) with
		{
			Kind = AgentKind.Claude,
			ResumeCommand = "claude-personal --resume"
		};
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();

		await viewModel.ClearSessionResumeCommandAsync(
			"session-1",
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.ResumeCommand.ShouldBe("claude-personal --resume");
		session.Record.ResumeCommand.ShouldBe("claude-personal --resume");
	}

	[Test]
	public async Task ClearSessionResumeCommandAsync_is_noop_for_unknown_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt) with
		{
			ResumeCommand = "codex resume abc12345"
		};
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var beforeUpdate = store.Document;
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.ClearSessionResumeCommandAsync(
			"missing",
			CancellationToken.None);

		store.Document.ShouldBeSameAs(beforeUpdate);
	}

	[Test]
	public async Task UpdateSessionTitleAsync_persists_title_and_updates_loaded_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var session = viewModel.Sessions.ShouldHaveSingleItem();
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		await viewModel.UpdateSessionTitleAsync(
			"session-1",
			"Reviewer",
			CancellationToken.None);

		var updatedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		updatedSession.Title.ShouldBe("Reviewer");
		session.Title.ShouldBe("Reviewer");
		changedProperties.ShouldContain(nameof(SessionViewModel.Record));
		changedProperties.ShouldContain(nameof(SessionViewModel.Title));
	}

	[Test]
	public async Task CreateWebPageAsync_adds_page_to_project_and_selects_it()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingSession = CreateSessionRecord("session-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [existingSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.ShouldHaveSingleItem();

		var webPage = await viewModel.CreateWebPageAsync(
			"web-1",
			workspace.Id,
			"GitLab",
			"https://gitlab/group/project",
			CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		var storedPage = storedProject.WebPages.ShouldHaveSingleItem();
		storedPage.Id.ShouldBe("web-1");
		storedPage.Title.ShouldBe("GitLab");
		storedPage.StartUrl.ShouldBe("https://gitlab/group/project");
		storedPage.ResumeUrl.ShouldBe("https://gitlab/group/project");
		storedProject.ActiveItemId.ShouldBe(webPage.Record.Id);
		workspace.WebPages.ShouldHaveSingleItem().ShouldBeSameAs(webPage);
		viewModel.WebPages.ShouldHaveSingleItem().Record.Id.ShouldBe(webPage.Record.Id);
		workspace.TreeItems.OfType<WebPageViewModel>().ShouldHaveSingleItem().ShouldBeSameAs(webPage);
		viewModel.SelectedWebPage.ShouldBeSameAs(webPage);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedScenarioRun.ShouldBeNull();
	}

	[Test]
	public async Task UpdateWebPageResumeUrlAsync_persists_url_title_and_last_active_time()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			WebPages = [existingPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var webPage = viewModel.WebPages.ShouldHaveSingleItem();
		List<string?> changedProperties = [];
		webPage.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		var beforeUpdate = DateTimeOffset.UtcNow;
		await viewModel.UpdateWebPageResumeUrlAsync(
			"web-1",
			"https://gitlab/group/project/-/merge_requests/1",
			"  Merge request !1  ",
			CancellationToken.None);

		var updatedPage = store.Document.Projects.ShouldHaveSingleItem().WebPages.ShouldHaveSingleItem();
		updatedPage.ResumeUrl.ShouldBe("https://gitlab/group/project/-/merge_requests/1");
		updatedPage.Title.ShouldBe("Merge request !1");
		(updatedPage.LastActiveAt >= beforeUpdate).ShouldBeTrue();
		webPage.ResumeUrl.ShouldBe("https://gitlab/group/project/-/merge_requests/1");
		webPage.Title.ShouldBe("Merge request !1");
		changedProperties.ShouldContain(nameof(WebPageViewModel.Record));
		changedProperties.ShouldContain(nameof(WebPageViewModel.ResumeUrl));
		changedProperties.ShouldContain(nameof(WebPageViewModel.Title));
	}

	[Test]
	public async Task UpdateWebPageResumeUrlAsync_keeps_existing_title_when_browser_title_is_blank()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var existingPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			WebPages = [existingPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateWebPageResumeUrlAsync(
			"web-1",
			"https://gitlab/group/project/-/merge_requests/1",
			"  ",
			CancellationToken.None);

		var updatedPage = store.Document.Projects.ShouldHaveSingleItem().WebPages.ShouldHaveSingleItem();
		updatedPage.ResumeUrl.ShouldBe("https://gitlab/group/project/-/merge_requests/1");
		updatedPage.Title.ShouldBe(existingPage.Title);
		viewModel.WebPages.ShouldHaveSingleItem().Title.ShouldBe(existingPage.Title);
	}

	[Test]
	public async Task UpdateWebPageTitleAsync_persists_title_and_defaults_blank_title()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstPage = CreateWebPageRecord("web-1", createdAt);
		var secondPage = CreateWebPageRecord("web-2", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			WebPages = [firstPage, secondPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.UpdateWebPageTitleAsync("web-1", "  Merge request  ", CancellationToken.None);
		await viewModel.UpdateWebPageTitleAsync("web-2", "  ", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.WebPages[0].Title.ShouldBe("Merge request");
		storedProject.WebPages[1].Title.ShouldBe("Web page");
		viewModel.WebPages[0].Title.ShouldBe("Merge request");
		viewModel.WebPages[1].Title.ShouldBe("Web page");
	}

	[Test]
	public async Task SelectedWebPage_clears_terminal_and_scenario_selection()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var sessionRecord = CreateSessionRecord("session-1", createdAt);
		var webPageRecord = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [sessionRecord],
			WebPages = [webPageRecord]
		};
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		}));
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.ShouldHaveSingleItem();
		using var run = CreateScenarioRunViewModel();
		viewModel.AddScenarioRun(workspace.Id, run);

		viewModel.SelectedSession = viewModel.Sessions.ShouldHaveSingleItem();
		viewModel.SelectedWebPage = viewModel.WebPages.ShouldHaveSingleItem();

		(viewModel.SelectedWebPage?.Record.Id).ShouldBe(webPageRecord.Id);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedScenarioRun.ShouldBeNull();
		viewModel.Sessions.ShouldHaveSingleItem().IsCurrentTerminal.ShouldBeFalse();
		viewModel.SendSelectedTargets.ShouldBeEmpty();
		viewModel.PromptTemplateTargets.ShouldBeEmpty();
	}

	[Test]
	public async Task RemoveWebPageAsync_removes_nested_page_and_selects_replacement()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var sessionRecord = CreateSessionRecord("session-1", createdAt);
		var firstPage = CreateWebPageRecord("web-1", createdAt);
		var secondPage = CreateWebPageRecord("web-2", createdAt);
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			activeItemId: "web-1") with
		{
			Sessions = [sessionRecord],
			WebPages = [firstPage, secondPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		viewModel.SelectedWebPage = viewModel.WebPages[0];

		await viewModel.RemoveWebPageAsync("web-1", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBe("web-2");
		storedProject.WebPages.ShouldNotContain(page => page.Id == "web-1");
		viewModel.WebPages.ShouldNotContain(page => page.Record.Id == "web-1");
		viewModel.Workspaces.ShouldHaveSingleItem().WebPages.ShouldNotContain(page => page.Record.Id == "web-1");
		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-2");

		await viewModel.RemoveWebPageAsync("web-2", CancellationToken.None);

		storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.WebPages.ShouldBeEmpty();
		storedProject.ActiveItemId.ShouldBe("session-1");
		viewModel.SelectedWebPage.ShouldBeNull();
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-1");
	}

	[Test]
	public async Task RemoveWebPageAsync_persists_replacement_active_item_in_replacement_workspace()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstProjectPage = CreateWebPageRecord("web-1", createdAt);
		var secondProjectPage = CreateWebPageRecord("web-2", createdAt);
		var firstProject = CreateProjectRecord(
			"project-1",
			"First",
			activeItemId: "web-1") with
		{
			WebPages = [firstProjectPage]
		};
		var secondProject = CreateProjectRecord("project-2", "Second") with
		{
			WebPages = [secondProjectPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [firstProject, secondProject]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		viewModel.SelectedWebPage = viewModel.WebPages.Where(page => page.Record.Id == "web-1").ShouldHaveSingleItem();

		await viewModel.RemoveWebPageAsync("web-1", CancellationToken.None);

		var storedFirstProject = store.Document.Projects.Where(project => project.Id == "project-1").ShouldHaveSingleItem();
		var storedSecondProject = store.Document.Projects.Where(project => project.Id == "project-2").ShouldHaveSingleItem();
		storedFirstProject.ActiveItemId.ShouldBeNull();
		storedSecondProject.ActiveItemId.ShouldBe("web-2");
		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-2");
		viewModel.SelectedSession.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Id).ShouldBe("project-2");
	}

	[Test]
	public async Task RemoveWebPageAsync_persists_session_replacement_active_item_in_replacement_workspace()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstProjectPage = CreateWebPageRecord("web-1", createdAt);
		var secondProjectSession = CreateSessionRecord("session-2", createdAt);
		var firstProject = CreateProjectRecord(
			"project-1",
			"First",
			activeItemId: "web-1") with
		{
			WebPages = [firstProjectPage]
		};
		var secondProject = CreateProjectRecord("project-2", "Second") with
		{
			Sessions = [secondProjectSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [firstProject, secondProject]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		viewModel.SelectedWebPage = viewModel.WebPages.ShouldHaveSingleItem();

		await viewModel.RemoveWebPageAsync("web-1", CancellationToken.None);

		var storedFirstProject = store.Document.Projects.Where(project => project.Id == "project-1").ShouldHaveSingleItem();
		var storedSecondProject = store.Document.Projects.Where(project => project.Id == "project-2").ShouldHaveSingleItem();
		storedFirstProject.ActiveItemId.ShouldBeNull();
		storedSecondProject.ActiveItemId.ShouldBe("session-2");
		viewModel.SelectedWebPage.ShouldBeNull();
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		(viewModel.SelectedWorkspace?.Id).ShouldBe("project-2");
	}

	[Test]
	public async Task RemoveSessionAsync_removes_nested_session_and_selects_next_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstSession = CreateSessionRecord("session-1", createdAt);
		var secondSession = CreateSessionRecord("session-2", createdAt);
		var thirdSession = CreateSessionRecord("session-3", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [firstSession, secondSession, thirdSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		viewModel.SelectedSession = viewModel.Sessions[1];

		await viewModel.RemoveSessionAsync("session-2", CancellationToken.None);

		viewModel.Sessions.ShouldNotContain(session => session.Record.Id == "session-2");
		store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldNotContain(session => session.Id == "session-2");
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-3");
	}

	[Test]
	public async Task RemoveSessionAsync_persists_replacement_session_active_item()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstSession = CreateSessionRecord("session-1", createdAt);
		var secondSession = CreateSessionRecord("session-2", createdAt);
		var project = CreateProjectRecord("project-1", "Project", activeItemId: "session-1") with
		{
			Sessions = [firstSession, secondSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBe("session-2");
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		(viewModel.SelectedWorkspace?.Record.ActiveItemId).ShouldBe("session-2");
	}

	[Test]
	public async Task RemoveSessionAsync_updates_selected_workspace_to_replacement_session_owner()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstProjectSession = CreateSessionRecord("session-1", createdAt);
		var secondProjectSession = CreateSessionRecord("session-2", createdAt);
		var firstProject = CreateProjectRecord(
			"project-1",
			"First",
			activeItemId: "session-1") with
		{
			Sessions = [firstProjectSession]
		};
		var secondProject = CreateProjectRecord("project-2", "Second") with
		{
			Sessions = [secondProjectSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [firstProject, secondProject]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);

		var storedFirstProject = store.Document.Projects.Where(project => project.Id == "project-1").ShouldHaveSingleItem();
		var storedSecondProject = store.Document.Projects.Where(project => project.Id == "project-2").ShouldHaveSingleItem();
		storedFirstProject.ActiveItemId.ShouldBeNull();
		storedSecondProject.ActiveItemId.ShouldBe("session-2");
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		viewModel.SelectedWebPage.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Id).ShouldBe("project-2");
	}

	[Test]
	public async Task RemoveSessionAsync_persists_replacement_web_page_active_item()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt);
		var webPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project", activeItemId: "session-1") with
		{
			Sessions = [session],
			WebPages = [webPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBe("web-1");
		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		viewModel.SelectedSession.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Record.ActiveItemId).ShouldBe("web-1");
	}

	[Test]
	public async Task RemoveSessionAsync_clears_active_item_when_no_replacement_exists()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project", activeItemId: "session-1") with
		{
			Sessions = [session]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBeNull();
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWebPage.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Record.ActiveItemId).ShouldBeNull();
	}

	[Test]
	public async Task RemoveSessionAsync_selects_web_page_when_removed_selected_session_has_no_session_replacement()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt);
		var webPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [session],
			WebPages = [webPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);

		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		viewModel.SelectedSession.ShouldBeNull();
	}

	[Test]
	public void ReplaceShellProfiles_replaces_dynamic_launch_profiles()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		AgentProfileRecord[] shellProfiles =
		[
			new("pwsh", AgentKind.Pwsh, "pwsh", "pwsh", ResumeCommandTemplate: null, "pwsh"),
			new("ssh-prod", AgentKind.Custom, "prod ssh", "ssh user@server", ResumeCommandTemplate: null, "pwsh")
		];

		viewModel.ReplaceShellProfiles(shellProfiles);

		viewModel.ShellProfiles.ShouldBe(shellProfiles);
	}

	[Test]
	public void ReplaceScenarioDefinitions_replaces_configured_scenarios()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		ScenarioDefinition[] scenarioDefinitions =
		[
			new(
				"custom-review",
				ScenarioKind.ReviewLoop,
				"Custom Review",
				MaxIterations: 3,
				StopMarker: "DONE",
				DefaultTarget: "start",
				StartPromptTemplate: "review {target}",
				FirstFeedbackTemplate: "feedback {reviewerOutput}",
				AuthorReturnTemplate: "author return {authorOutput}",
				FeedbackTemplate: "feedback {reviewerOutput}",
				ReviewerInstructions:
				[
					new("strict", "Strict", "Strict tail")
				],
				DefaultReviewerInstructionId: "strict")
		];

		viewModel.ReplaceScenarioDefinitions(scenarioDefinitions);

		viewModel.ScenarioDefinitions.ShouldBe(scenarioDefinitions);
	}

	[Test]
	public async Task LoadAsync_splits_active_and_paused_projects()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var activeSession = CreateSessionRecord("active-session", createdAt);
		var pausedSession = CreateSessionRecord("paused-session", createdAt);
		var activeProject = CreateProjectRecord(
			"active-project",
			"Active",
			WorkspaceStatus.Active) with
		{
			Sessions = [activeSession]
		};
		var pausedProject = CreateProjectRecord(
			"paused-project",
			"Paused",
			WorkspaceStatus.Paused,
			activeItemId: "paused-session") with
		{
			Sessions = [pausedSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [activeProject, pausedProject]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);

		await viewModel.LoadAsync(CancellationToken.None);

		var activeViewModel = viewModel.Workspaces.ShouldHaveSingleItem();
		var pausedViewModel = viewModel.PausedWorkspaces.ShouldHaveSingleItem();
		activeViewModel.Id.ShouldBe(activeProject.Id);
		pausedViewModel.Id.ShouldBe(pausedProject.Id);
		viewModel.Sessions.ShouldHaveSingleItem().Record.Id.ShouldBe(activeSession.Id);
		activeViewModel.Sessions.ShouldHaveSingleItem().Record.Id.ShouldBe(activeSession.Id);
		pausedViewModel.Sessions.ShouldHaveSingleItem().Record.Id.ShouldBe(pausedSession.Id);
	}

	[Test]
	public async Task LoadAsync_marks_stale_running_sessions_stopped_after_process_restart()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var staleRunningSession = CreateSessionRecord("session-1", createdAt) with
		{
			Status = SessionStatus.Running
		};
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			activeItemId: staleRunningSession.Id) with
		{
			Sessions = [staleRunningSession]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);

		await viewModel.LoadAsync(CancellationToken.None);

		var session = viewModel.Sessions.ShouldHaveSingleItem();
		var storedSession = store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem();
		session.Record.Status.ShouldBe(SessionStatus.Stopped);
		session.Status.ShouldBe("Stopped");
		storedSession.Status.ShouldBe(SessionStatus.Stopped);
	}

	[Test]
	public async Task LoadAsync_falls_back_to_first_global_session_before_first_workspace_web_page()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var firstProjectPage = CreateWebPageRecord("web-1", createdAt);
		var secondProjectSession = CreateSessionRecord("session-2", createdAt);
		var firstProject = CreateProjectRecord("project-1", "First") with
		{
			WebPages = [firstProjectPage]
		};
		var secondProject = CreateProjectRecord("project-2", "Second") with
		{
			Sessions = [secondProjectSession]
		};
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [firstProject, secondProject]
		}));

		await viewModel.LoadAsync(CancellationToken.None);

		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		viewModel.SelectedWebPage.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Id).ShouldBe("project-2");
	}

	[Test]
	public async Task LoadAsync_selects_saved_active_web_page()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var first = CreateWebPageRecord("web-1", createdAt);
		var second = CreateWebPageRecord("web-2", createdAt);
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			WorkspaceStatus.Active,
			activeItemId: "web-2") with
		{
			WebPages = [first, second]
		};
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		}));

		await viewModel.LoadAsync(CancellationToken.None);

		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-2");
		viewModel.SelectedSession.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Id).ShouldBe(project.Id);
	}

	[Test]
	public async Task LoadAsync_selects_active_project_saved_active_session()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var first = CreateSessionRecord("session-1", createdAt);
		var second = CreateSessionRecord("session-2", createdAt);
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			WorkspaceStatus.Active,
			activeItemId: "session-2") with
		{
			Sessions = [first, second]
		};
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		}));

		await viewModel.LoadAsync(CancellationToken.None);

		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		(viewModel.SelectedWorkspace?.Id).ShouldBe(project.Id);
	}

	[Test]
	public async Task LoadAsync_falls_back_to_first_session_when_active_item_is_missing()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var first = CreateSessionRecord("session-1", createdAt);
		var second = CreateSessionRecord("session-2", createdAt);
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			WorkspaceStatus.Active,
			activeItemId: "missing") with
		{
			Sessions = [first, second]
		};
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		}));

		await viewModel.LoadAsync(CancellationToken.None);

		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-1");
	}

	[Test]
	public async Task SetActiveItemAsync_persists_active_session_for_project()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var first = CreateSessionRecord("session-1", createdAt);
		var second = CreateSessionRecord("session-2", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			ActiveItemId = "session-1",
			Sessions = [first, second]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.SetActiveItemAsync("session-2", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBe("session-2");
		(viewModel.SelectedSession?.Record.Id).ShouldBe("session-2");
		(viewModel.SelectedWorkspace?.Record.ActiveItemId).ShouldBe("session-2");
	}

	[Test]
	public async Task SetActiveItemAsync_persists_active_web_page_for_project()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt);
		var webPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			ActiveItemId = "session-1",
			Sessions = [session],
			WebPages = [webPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);

		await viewModel.SetActiveItemAsync("web-1", CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.ActiveItemId.ShouldBe("web-1");
		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		viewModel.SelectedSession.ShouldBeNull();
		(viewModel.SelectedWorkspace?.Record.ActiveItemId).ShouldBe("web-1");
	}

	[Test]
	public async Task Active_sessions_register_with_terminal_status_and_selection_acknowledges_completion()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions =
			[
				CreateSessionRecord("session-1", createdAt) with { Status = SessionStatus.Running },
				CreateSessionRecord("session-2", createdAt) with { Status = SessionStatus.Running }
			]
		};
		TerminalTabStatusCoordinator statuses = new(action => action());
		var viewModel = MainWindowViewModelTestFactory.Create(
			new InMemoryProjectStore(ProjectsDocument.CreateDefault() with { Projects = [project] }),
			statuses);
		statuses.SetWindowFacts(true, true, createdAt);
		await viewModel.LoadAsync(CancellationToken.None);
		await viewModel.UpdateSessionStatusAsync("session-1", SessionStatus.Running, CancellationToken.None);
		await viewModel.UpdateSessionStatusAsync("session-2", SessionStatus.Running, CancellationToken.None);
		viewModel.SelectedSession = viewModel.Sessions[0];

		statuses.OnUserInput("session-1", "\r", createdAt.AddSeconds(1));
		statuses.OnScreenSnapshot("session-1", @"PS D:\Work> ", createdAt.AddSeconds(2));
		statuses.OnUserInput("session-2", "\r", createdAt.AddSeconds(3));
		statuses.OnScreenSnapshot("session-2", @"PS D:\Work> ", createdAt.AddSeconds(4));

		viewModel.Sessions[0].Indicator.ShouldBe(TerminalTabIndicator.None);
		viewModel.Sessions[1].Indicator.ShouldBe(TerminalTabIndicator.Unread);
	}

	[Test]
	public async Task Unread_session_driven_by_a_running_scenario_does_not_ask_for_attention()
	{
		using var fixture = await ScenarioAttentionFixture.CreateAsync();

		fixture.ViewModel.Sessions[1].Indicator.ShouldBe(TerminalTabIndicator.Unread);
		fixture.ViewModel.HasUnreadCompletions.ShouldBeFalse();
	}

	[Test]
	public async Task Unread_session_of_a_paused_scenario_asks_for_attention()
	{
		using var fixture = await ScenarioAttentionFixture.CreateAsync();
		List<string?> notifications = [];
		fixture.ViewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

		await fixture.PauseAsync();

		fixture.ViewModel.HasUnreadCompletions.ShouldBeTrue();
		notifications.ShouldContain(nameof(MainWindowViewModel.HasUnreadCompletions));
	}

	[Test]
	public async Task Unread_aggregate_notifies_only_when_projected_indicator_changes()
	{
		var now = DateTimeOffset.UtcNow;
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions =
			[
				CreateSessionRecord("session-1", now) with { Status = SessionStatus.Running },
				CreateSessionRecord("session-2", now) with { Status = SessionStatus.Running }
			]
		};
		TerminalTabStatusCoordinator statuses = new(action => action());
		var viewModel = MainWindowViewModelTestFactory.Create(
			new InMemoryProjectStore(ProjectsDocument.CreateDefault() with { Projects = [project] }),
			statuses);
		statuses.SetWindowFacts(true, true, now);
		await viewModel.LoadAsync(CancellationToken.None);
		await viewModel.UpdateSessionStatusAsync("session-1", SessionStatus.Running, CancellationToken.None);
		await viewModel.UpdateSessionStatusAsync("session-2", SessionStatus.Running, CancellationToken.None);
		viewModel.SelectedSession = viewModel.Sessions[0];
		List<string?> notifications = [];
		viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

		statuses.OnUserInput("session-2", "\r", now.AddSeconds(1));
		notifications.Clear();
		statuses.OnScreenSnapshot("session-2", @"PS D:\Work> ", now.AddSeconds(2));

		viewModel.HasUnreadCompletions.ShouldBeTrue();
		notifications.ShouldBe([nameof(MainWindowViewModel.HasUnreadCompletions)]);

		notifications.Clear();
		viewModel.Sessions[0].UpdateRecord(viewModel.Sessions[0].Record with { Title = "Renamed" });
		notifications.ShouldBeEmpty();

		viewModel.SelectedSession = viewModel.Sessions[1];
		viewModel.HasUnreadCompletions.ShouldBeFalse();
		notifications.ShouldContain(nameof(MainWindowViewModel.HasUnreadCompletions));
	}

	[Test]
	[TestCase(SessionStatus.Running, TerminalTabIndicator.None)]
	[TestCase(SessionStatus.Stopped, TerminalTabIndicator.Paused)]
	[TestCase(SessionStatus.Exited, TerminalTabIndicator.Paused)]
	[TestCase(SessionStatus.Failed, TerminalTabIndicator.Failed)]
	public async Task UpdateSessionStatusAsync_forwards_persisted_lifecycle_to_status_engine(
		SessionStatus status,
		TerminalTabIndicator expectedIndicator)
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt) with
		{
			Status = SessionStatus.Stopped
		};
		var project = CreateProjectRecord("project-1", "Project") with { Sessions = [session] };
		TerminalTabStatusCoordinator statuses = new(action => action());
		var viewModel = MainWindowViewModelTestFactory.Create(
			new InMemoryProjectStore(ProjectsDocument.CreateDefault() with { Projects = [project] }),
			statuses);
		await viewModel.LoadAsync(CancellationToken.None);
		viewModel.Sessions.ShouldHaveSingleItem().Indicator.ShouldBe(TerminalTabIndicator.Paused);

		await viewModel.UpdateSessionStatusAsync("session-1", status, CancellationToken.None);

		viewModel.Sessions.ShouldHaveSingleItem().Indicator.ShouldBe(expectedIndicator);
	}

	[Test]
	public async Task Removing_session_unregisters_its_status_engine()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [CreateSessionRecord("session-1", createdAt) with { Status = SessionStatus.Running }]
		};
		TerminalTabStatusCoordinator statuses = new(action => action());
		var viewModel = MainWindowViewModelTestFactory.Create(
			new InMemoryProjectStore(ProjectsDocument.CreateDefault() with { Projects = [project] }),
			statuses);
		await viewModel.LoadAsync(CancellationToken.None);
		await viewModel.UpdateSessionStatusAsync("session-1", SessionStatus.Running, CancellationToken.None);
		var removed = viewModel.Sessions.ShouldHaveSingleItem();
		statuses.OnUserInput("session-1", "\r", createdAt.AddSeconds(1));
		removed.Indicator.ShouldBe(TerminalTabIndicator.Busy);

		await viewModel.RemoveSessionAsync("session-1", CancellationToken.None);
		List<string?> notifications = [];
		viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
		statuses.OnScreenSnapshot("session-1", "Worked for 1s", createdAt.AddSeconds(2));

		removed.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		notifications.ShouldNotContain(nameof(MainWindowViewModel.HasUnreadCompletions));
	}

	[Test]
	public async Task EnsureWorkspaceForDirectoryAsync_creates_project_once_per_root()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);

		var first = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact\",
			CancellationToken.None);
		var second = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);

		second.ShouldBeSameAs(first);
		first.Name.ShouldBe("Pact");
		viewModel.SelectedWorkspace.ShouldBe(first);
		var project = store.Document.Projects.ShouldHaveSingleItem();
		project.Id.ShouldBe(first.Id);
		project.Sessions.ShouldBeEmpty();
	}

	[Test]
	public async Task CreateSessionAsync_adds_kind_and_commands_to_project_group()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);

		var session = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "Codex session",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume codex-session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		session.Record.Kind.ShouldBe(AgentKind.Codex);
		session.Record.LaunchCommand.ShouldBe("codex");
		session.Record.ResumeCommand.ShouldBe("codex resume codex-session-1");
		workspace.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
		workspace.TreeItems.ShouldHaveSingleItem().ShouldBeSameAs(session);
		store.Document.Projects.ShouldHaveSingleItem().Sessions.ShouldHaveSingleItem().Kind.ShouldBe(AgentKind.Codex);
	}

	[Test]
	public async Task SelectedSession_refreshes_send_selected_targets_from_same_project()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);
		var author = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var reviewer = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		viewModel.SelectedSession = author;

		viewModel.SendSelectedTargets.ShouldBe([reviewer]);
	}

	[Test]
	public async Task SelectedSession_refreshes_prompt_template_targets_from_same_project_including_active()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);
		var author = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var reviewer = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		viewModel.SelectedSession = author;

		viewModel.PromptTemplateTargets.ShouldBe([author, reviewer]);
	}

	[Test]
	public async Task ReplacePromptTemplates_splits_quick_actions_and_selection_templates()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "codex",
			workingDirectory: workspace.RootPath,
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		viewModel.ReplacePromptTemplates([
			new PromptTemplateRecord("status", "Status", "Status {project}", false, PromptActionType.Prompt),
			new PromptTemplateRecord("review", "Review", "Review {selectedText}", false, PromptActionType.SelectionTemplate),
			new PromptTemplateRecord("git-status", "git status", "git status", false, PromptActionType.TerminalCommand)
		]);

		viewModel.VisibleQuickActions.Select(action => action.Id).ToArray().ShouldBe(["status"]);
		viewModel.SelectionActionChoices.Where(action => action.Template is not null).Select(action => action.Template!.Id).ToArray().ShouldBe(["review"]);
	}

	[Test]
	public async Task VisibleQuickActions_switches_to_terminal_commands_for_pwsh()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		var pwsh = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "pwsh",
			workingDirectory: workspace.RootPath,
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		viewModel.ReplacePromptTemplates([
			new PromptTemplateRecord("status", "Status", "Status {project}", false, PromptActionType.Prompt),
			new PromptTemplateRecord("git-status", "git status", "git status", false, PromptActionType.TerminalCommand)
		]);
		viewModel.SelectedSession = pwsh;

		viewModel.VisibleQuickActions.Select(action => action.Id).ToArray().ShouldBe(["git-status"]);
	}

	[Test]
	public async Task SelectionActionTargetProjects_groups_unlocked_sessions_by_active_projects()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var first = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\First", CancellationToken.None);
		var second = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\Second", CancellationToken.None);
		var author = await viewModel.CreateSessionAsync("session-1", "default", AgentKind.Codex, "author", first.RootPath, "codex", null, CancellationToken.None, first.Id);
		var reviewer = await viewModel.CreateSessionAsync("session-2", "default", AgentKind.Codex, "reviewer", first.RootPath, "codex", null, CancellationToken.None, first.Id);
		var shell = await viewModel.CreateSessionAsync("session-3", "default", AgentKind.Pwsh, "shell", second.RootPath, "pwsh", null, CancellationToken.None, second.Id);
		var secondAuthor = await viewModel.CreateSessionAsync("session-4", "default", AgentKind.Codex, "second author", second.RootPath, "codex", null, CancellationToken.None, second.Id);
		viewModel.SetScenarioLocks("run-1", [shell.Record.Id], locked: true);
		viewModel.SelectedSession = secondAuthor;

		viewModel.SelectionActionTargetProjects.Select(project => project.Name).ToArray().ShouldBe(["Second", "First"]);
		var secondTarget = viewModel.SelectionActionTargetProjects[0];
		secondTarget.Sessions.ShouldBeEmpty();
		secondTarget.NotesTarget.ShouldNotBeNull();
		var firstTarget = viewModel.SelectionActionTargetProjects[1];
		firstTarget.Sessions.Select(session => session.Title).ToArray().ShouldBe(["author", "reviewer"]);
		firstTarget.NotesTarget.ShouldNotBeNull();
	}

	[Test]
	public async Task SelectionActionCompactTargetProject_for_project_session_excludes_source_and_keeps_compatible_notes()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\Project", CancellationToken.None);
		var source = await viewModel.CreateSessionAsync("source", "default", AgentKind.Codex, "source", workspace.RootPath, "codex", null, CancellationToken.None, workspace.Id);
		var target = await viewModel.CreateSessionAsync("target", "default", AgentKind.Codex, "target", workspace.RootPath, "codex", null, CancellationToken.None, workspace.Id);
		viewModel.SelectedSession = source;

		var compactTarget = viewModel.SelectionActionCompactTargetProject.ShouldNotBeNull();
		compactTarget.Id.ShouldBe(workspace.Id);
		compactTarget.Sessions.ShouldBe([target]);
		compactTarget.NotesTarget.ShouldNotBeNull();
		viewModel.HasAdditionalSelectionActionTargets.ShouldBeFalse();
	}

	[Test]
	public async Task SelectionActionCompactTargetProject_for_root_session_has_no_notes_target()
	{
		var now = DateTimeOffset.UtcNow;
		var source = new SessionRecord("root-source", AgentKind.Codex, "source", @"C:\", "codex", null, SessionStatus.Stopped, now, now);
		var target = source with { Id = "root-target", Title = "target" };
		var rootTabsStore = new InMemoryRootTabsStore(new RootTabsRecord(1, source.Id, [source, target], [], []));
		var viewModel = MainWindowViewModelTestFactory.Create(
			new InMemoryProjectStore(ProjectsDocument.CreateDefault()),
			rootTabsStore: rootTabsStore);
		await viewModel.LoadAsync(CancellationToken.None);

		var compactTarget = viewModel.SelectionActionCompactTargetProject.ShouldNotBeNull();
		compactTarget.Id.ShouldBe("root");
		compactTarget.Sessions.Select(session => session.Record.Id).ShouldBe([target.Id]);
		compactTarget.NotesTarget.ShouldBeNull();
		viewModel.HasAdditionalSelectionActionTargets.ShouldBeFalse();
	}

	[Test]
	public async Task SelectionActionCompactTargetProject_for_notes_excludes_source_notes_target()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\Project", CancellationToken.None);
		var session = await viewModel.CreateSessionAsync("session", "default", AgentKind.Codex, "target", workspace.RootPath, "codex", null, CancellationToken.None, workspace.Id);
		await viewModel.ShowNotesTabAsync(workspace.Id, CancellationToken.None);

		var compactTarget = viewModel.SelectionActionCompactTargetProject.ShouldNotBeNull();
		compactTarget.Id.ShouldBe(workspace.Id);
		compactTarget.Sessions.ShouldBe([session]);
		compactTarget.NotesTarget.ShouldBeNull();
		viewModel.HasAdditionalSelectionActionTargets.ShouldBeFalse();
	}

	[Test]
	public async Task SelectionActionCompactTargetProject_refreshes_when_selected_template_has_targets_only_in_another_project()
	{
		var viewModel = MainWindowViewModelTestFactory.Create(new InMemoryProjectStore(ProjectsDocument.CreateDefault()));
		var sourceWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\Source", CancellationToken.None);
		var targetWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(@"D:\Work\Target", CancellationToken.None);
		var source = await viewModel.CreateSessionAsync("source", "default", AgentKind.Codex, "source", sourceWorkspace.RootPath, "codex", null, CancellationToken.None, sourceWorkspace.Id);
		await viewModel.CreateSessionAsync("target", "default", AgentKind.Pwsh, "target", targetWorkspace.RootPath, "pwsh", null, CancellationToken.None, targetWorkspace.Id);
		viewModel.ReplacePromptTemplates([
			new PromptTemplateRecord("command", "Command", "{missing} {selectedText}", false, PromptActionType.TerminalCommand)
		]);
		viewModel.SelectedSession = source;

		viewModel.SelectedSelectionAction = viewModel.SelectionActionChoices.Single(choice => choice.Template?.Id == "command");

		viewModel.SelectionActionCompactTargetProject.ShouldBeNull();
		viewModel.HasAdditionalSelectionActionTargets.ShouldBeTrue();
		viewModel.SelectionActionTargetProjects.Select(project => project.Id).ShouldBe([targetWorkspace.Id]);
	}

	[Test]
	public async Task Web_pages_do_not_appear_in_terminal_action_targets()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);
		var session = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		var webPage = await viewModel.CreateWebPageAsync(
			"web-1",
			workspace.Id,
			"GitLab",
			"https://gitlab/group/project",
			CancellationToken.None);
		viewModel.SelectedSession = session;

		viewModel.SendSelectedTargets.Cast<object>().ShouldNotContain(webPage);
		viewModel.PromptTemplateTargets.Cast<object>().ShouldNotContain(webPage);
		viewModel.PromptTemplateTargets.ShouldBe([session]);
	}

	[Test]
	public async Task SelectedSession_excludes_scenario_locked_send_targets()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);
		var author = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var reviewer = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		reviewer.LockForScenario("run-1");

		viewModel.SelectedSession = author;

		viewModel.SendSelectedTargets.ShouldBeEmpty();
	}

	[Test]
	public async Task SelectedSession_excludes_scenario_locked_prompt_template_targets()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Personal\Pact",
			CancellationToken.None);
		var author = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var reviewer = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Personal\Pact",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		reviewer.LockForScenario("run-1");

		viewModel.SelectedSession = author;

		viewModel.PromptTemplateTargets.ShouldBe([author]);
	}

	[Test]
	public async Task RemoveWorkspaceAsync_selects_replacement_web_page_when_selected_page_was_removed()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "first",
			workingDirectory: @"D:\Work\First",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: firstWorkspace.Id);
		var secondSession = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "second",
			workingDirectory: @"D:\Work\Second",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: secondWorkspace.Id);
		var firstWebPage = await viewModel.CreateWebPageAsync(
			"web-1",
			firstWorkspace.Id,
			"First web",
			"https://first.example",
			CancellationToken.None);
		var secondWebPage = await viewModel.CreateWebPageAsync(
			"web-2",
			secondWorkspace.Id,
			"Second web",
			"https://second.example",
			CancellationToken.None);
		viewModel.SelectedWebPage = firstWebPage;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.RemoveWorkspaceAsync(firstWorkspace.Id, CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		viewModel.Sessions.ShouldNotContain(session => session.Record.Id == firstSession.Record.Id);
		viewModel.WebPages.ShouldNotContain(page => page.Record.Id == firstWebPage.Record.Id);
		viewModel.Workspaces.ShouldHaveSingleItem().ShouldBeSameAs(secondWorkspace);
		viewModel.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(secondSession);
		viewModel.WebPages.ShouldHaveSingleItem().ShouldBeSameAs(secondWebPage);
		viewModel.SelectedWebPage.ShouldBeSameAs(secondWebPage);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(secondWorkspace);
		store.Document.Projects.ShouldNotContain(project => project.Id == firstWorkspace.Id);
		store.Document.Projects.ShouldContain(project => project.Id == secondWorkspace.Id);
	}

	[Test]
	public async Task RemoveWorkspaceAsync_selects_remaining_web_page_when_selected_session_was_removed()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "first",
			workingDirectory: @"D:\Work\First",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: firstWorkspace.Id);
		var secondWebPage = await viewModel.CreateWebPageAsync(
			"web-2",
			secondWorkspace.Id,
			"Second web",
			"https://second.example",
			CancellationToken.None);
		viewModel.SelectedSession = firstSession;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.RemoveWorkspaceAsync(firstWorkspace.Id, CancellationToken.None);

		viewModel.SelectedWebPage.ShouldBeSameAs(secondWebPage);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(secondWorkspace);
	}

	[Test]
	public async Task RemoveWorkspaceAsync_syncs_selected_workspace_to_global_fallback_web_page_owner()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var thirdWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Third",
			CancellationToken.None);
		var firstWebPage = await viewModel.CreateWebPageAsync(
			"web-1",
			firstWorkspace.Id,
			"First web",
			"https://first.example",
			CancellationToken.None);
		var thirdWebPage = await viewModel.CreateWebPageAsync(
			"web-3",
			thirdWorkspace.Id,
			"Third web",
			"https://third.example",
			CancellationToken.None);
		viewModel.SelectedWebPage = firstWebPage;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.RemoveWorkspaceAsync(firstWorkspace.Id, CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		secondWorkspace.WebPages.ShouldBeEmpty();
		viewModel.SelectedWebPage.ShouldBeSameAs(thirdWebPage);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(thirdWorkspace);
	}

	[Test]
	public async Task RemoveWorkspaceAsync_syncs_selected_workspace_to_global_fallback_session_owner()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var thirdWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Third",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "first",
			workingDirectory: @"D:\Work\First",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: firstWorkspace.Id);
		var thirdSession = await viewModel.CreateSessionAsync(
			sessionId: "session-3",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "third",
			workingDirectory: @"D:\Work\Third",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: thirdWorkspace.Id);
		viewModel.SelectedSession = firstSession;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.RemoveWorkspaceAsync(firstWorkspace.Id, CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		secondWorkspace.Sessions.ShouldBeEmpty();
		viewModel.SelectedSession.ShouldBeSameAs(thirdSession);
		viewModel.SelectedWebPage.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(thirdWorkspace);
	}

	[Test]
	public async Task RemoveWorkspaceAsync_removes_project_scenario_runs()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		using var run = CreateScenarioRunViewModel();
		viewModel.AddScenarioRun(workspace.Id, run);

		await viewModel.RemoveWorkspaceAsync(workspace.Id, CancellationToken.None);

		viewModel.ScenarioRuns.ShouldBeEmpty();
		workspace.ScenarioRuns.ShouldBeEmpty();
		workspace.TreeItems.OfType<ScenarioRunViewModel>().ShouldBeEmpty();
		viewModel.SelectedScenarioRun.ShouldBeNull();
	}

	[Test]
	public async Task PauseWorkspaceAsync_marks_project_paused_and_keeps_nested_sessions()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);

		_ = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		_ = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "runner",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var webPage = await viewModel.CreateWebPageAsync(
			"web-1",
			workspace.Id,
			"GitLab",
			"https://gitlab/group/project",
			CancellationToken.None);
		viewModel.SelectedWebPage = webPage;

		await viewModel.PauseWorkspaceAsync(
			workspace.Id,
			activeItemId: webPage.Record.Id,
			CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.Status.ShouldBe(WorkspaceStatus.Paused);
		storedProject.ActiveItemId.ShouldBe("web-1");
		storedProject.Sessions.Count.ShouldBe(2);
		storedProject.WebPages.ShouldHaveSingleItem().Id.ShouldBe("web-1");
		viewModel.Workspaces.ShouldBeEmpty();
		viewModel.PausedWorkspaces.ShouldHaveSingleItem().ShouldBeSameAs(workspace);
		viewModel.Sessions.ShouldBeEmpty();
		viewModel.WebPages.ShouldBeEmpty();
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWebPage.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeNull();
		workspace.Sessions.Count.ShouldBe(2);
		workspace.WebPages.ShouldHaveSingleItem().ShouldBeSameAs(webPage);
		workspace.Record.Status.ShouldBe(WorkspaceStatus.Paused);
	}

	[Test]
	public async Task PauseWorkspaceAsync_ignores_active_item_from_another_workspace()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var secondWebPage = await viewModel.CreateWebPageAsync(
			"web-2",
			secondWorkspace.Id,
			"Second web",
			"https://second.example",
			CancellationToken.None);

		await viewModel.PauseWorkspaceAsync(
			firstWorkspace.Id,
			activeItemId: secondWebPage.Record.Id,
			CancellationToken.None);

		var storedFirstProject = store.Document.Projects
			.Where(project => string.Equals(
				project.Id,
				firstWorkspace.Id,
				StringComparison.Ordinal))
			.ShouldHaveSingleItem();
		storedFirstProject.ActiveItemId.ShouldBeNull();
	}

	[Test]
	public async Task PauseWorkspaceAsync_selects_remaining_web_page_when_selected_page_was_paused()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var firstWebPage = await viewModel.CreateWebPageAsync(
			"web-1",
			firstWorkspace.Id,
			"First web",
			"https://first.example",
			CancellationToken.None);
		var secondWebPage = await viewModel.CreateWebPageAsync(
			"web-2",
			secondWorkspace.Id,
			"Second web",
			"https://second.example",
			CancellationToken.None);
		viewModel.SelectedWebPage = firstWebPage;

		await viewModel.PauseWorkspaceAsync(
			firstWorkspace.Id,
			activeItemId: firstWebPage.Record.Id,
			CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		viewModel.Workspaces.ShouldHaveSingleItem().ShouldBeSameAs(secondWorkspace);
		viewModel.WebPages.ShouldHaveSingleItem().ShouldBeSameAs(secondWebPage);
		viewModel.SelectedWebPage.ShouldBeSameAs(secondWebPage);
		viewModel.SelectedSession.ShouldBeNull();
	}

	[Test]
	public async Task PauseWorkspaceAsync_selects_remaining_web_page_when_selected_session_was_paused()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "first",
			workingDirectory: @"D:\Work\First",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: firstWorkspace.Id);
		var secondWebPage = await viewModel.CreateWebPageAsync(
			"web-2",
			secondWorkspace.Id,
			"Second web",
			"https://second.example",
			CancellationToken.None);
		viewModel.SelectedSession = firstSession;

		await viewModel.PauseWorkspaceAsync(
			firstWorkspace.Id,
			activeItemId: firstSession.Record.Id,
			CancellationToken.None);

		viewModel.SelectedWebPage.ShouldBeSameAs(secondWebPage);
		viewModel.SelectedSession.ShouldBeNull();
	}

	[Test]
	public async Task PauseWorkspaceAsync_syncs_selected_workspace_to_global_fallback_web_page_owner()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var thirdWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Third",
			CancellationToken.None);
		var firstWebPage = await viewModel.CreateWebPageAsync(
			"web-1",
			firstWorkspace.Id,
			"First web",
			"https://first.example",
			CancellationToken.None);
		var thirdWebPage = await viewModel.CreateWebPageAsync(
			"web-3",
			thirdWorkspace.Id,
			"Third web",
			"https://third.example",
			CancellationToken.None);
		viewModel.SelectedWebPage = firstWebPage;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.PauseWorkspaceAsync(
			firstWorkspace.Id,
			activeItemId: firstWebPage.Record.Id,
			CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		secondWorkspace.WebPages.ShouldBeEmpty();
		viewModel.SelectedWebPage.ShouldBeSameAs(thirdWebPage);
		viewModel.SelectedSession.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(thirdWorkspace);
	}

	[Test]
	public async Task PauseWorkspaceAsync_syncs_selected_workspace_to_global_fallback_session_owner()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var firstWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\First",
			CancellationToken.None);
		var secondWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Second",
			CancellationToken.None);
		var thirdWorkspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Third",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "first",
			workingDirectory: @"D:\Work\First",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: firstWorkspace.Id);
		var thirdSession = await viewModel.CreateSessionAsync(
			sessionId: "session-3",
			projectId: "default",
			kind: AgentKind.Pwsh,
			title: "third",
			workingDirectory: @"D:\Work\Third",
			launchCommand: "pwsh",
			resumeCommand: null,
			cancellationToken: CancellationToken.None,
			workspaceId: thirdWorkspace.Id);
		viewModel.SelectedSession = firstSession;
		viewModel.SelectedWorkspace = firstWorkspace;

		await viewModel.PauseWorkspaceAsync(
			firstWorkspace.Id,
			activeItemId: firstSession.Record.Id,
			CancellationToken.None);

		viewModel.Workspaces.ShouldNotContain(workspace => workspace.Id == firstWorkspace.Id);
		secondWorkspace.Sessions.ShouldBeEmpty();
		viewModel.SelectedSession.ShouldBeSameAs(thirdSession);
		viewModel.SelectedWebPage.ShouldBeNull();
		viewModel.SelectedWorkspace.ShouldBeSameAs(thirdWorkspace);
	}

	[Test]
	public async Task PauseWorkspaceAsync_removes_project_scenario_runs()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		var session = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		using var run = CreateScenarioRunViewModel();
		viewModel.AddScenarioRun(workspace.Id, run);

		await viewModel.PauseWorkspaceAsync(
			workspace.Id,
			activeItemId: session.Record.Id,
			CancellationToken.None);

		viewModel.ScenarioRuns.ShouldBeEmpty();
		workspace.ScenarioRuns.ShouldBeEmpty();
		workspace.TreeItems.OfType<ScenarioRunViewModel>().ShouldBeEmpty();
		viewModel.SelectedScenarioRun.ShouldBeNull();
	}

	[Test]
	public async Task RestoreWorkspaceAsync_marks_project_active_and_preserves_restore_snapshot()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);

		_ = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume codex-session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		_ = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume codex-session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var webPage = await viewModel.CreateWebPageAsync(
			"web-1",
			workspace.Id,
			"GitLab",
			"https://gitlab/group/project",
			CancellationToken.None);

		await viewModel.PauseWorkspaceAsync(
			workspace.Id,
			activeItemId: webPage.Record.Id,
			CancellationToken.None);
		viewModel.Workspaces.ShouldBeEmpty();
		viewModel.WebPages.ShouldBeEmpty();
		viewModel.PausedWorkspaces.ShouldHaveSingleItem().ShouldBeSameAs(workspace);

		await viewModel.RestoreWorkspaceAsync(workspace.Id, CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.Status.ShouldBe(WorkspaceStatus.Active);
		storedProject.ActiveItemId.ShouldBe(webPage.Record.Id);
		workspace.Record.Status.ShouldBe(WorkspaceStatus.Active);
		viewModel.Workspaces.ShouldHaveSingleItem().ShouldBeSameAs(workspace);
		viewModel.PausedWorkspaces.ShouldBeEmpty();
		viewModel.SelectedWorkspace.ShouldBeSameAs(workspace);
		viewModel.Sessions.Count.ShouldBe(2);
		workspace.Sessions.Count.ShouldBe(2);
		viewModel.WebPages.ShouldHaveSingleItem().Record.Id.ShouldBe(webPage.Record.Id);
		workspace.WebPages.ShouldHaveSingleItem().Record.Id.ShouldBe("web-1");
		workspace.TreeItems.OfType<SessionViewModel>().ShouldBe(workspace.Sessions);
		workspace.TreeItems.OfType<WebPageViewModel>().ShouldBe(workspace.WebPages);
	}

	[Test]
	public async Task RestoreWorkspaceAsync_selects_restored_active_web_page()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var session = CreateSessionRecord("session-1", createdAt);
		var webPage = CreateWebPageRecord("web-1", createdAt);
		var project = CreateProjectRecord(
			"project-1",
			"Project",
			WorkspaceStatus.Paused,
			activeItemId: "web-1") with
		{
			Sessions = [session],
			WebPages = [webPage]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var pausedWorkspace = viewModel.PausedWorkspaces.ShouldHaveSingleItem();

		await viewModel.RestoreWorkspaceAsync(pausedWorkspace.Id, CancellationToken.None);

		var restoredWorkspace = viewModel.Workspaces.ShouldHaveSingleItem();
		viewModel.SelectedWorkspace.ShouldBeSameAs(restoredWorkspace);
		(viewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		viewModel.SelectedSession.ShouldBeNull();
	}

	[Test]
	public async Task SaveWorkspaceNotesAsync_persists_notes_and_updates_loaded_project()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);

		await viewModel.SaveWorkspaceNotesAsync(
			workspace.Id,
			"Pause summary: restore ordering verified.",
			CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.Notes.ShouldBe("Pause summary: restore ordering verified.");
		workspace.Record.Notes.ShouldBe("Pause summary: restore ordering verified.");
	}

	[Test]
	public async Task SaveWorkspaceGitLabRepoIdAsync_persists_repo_id_and_updates_loaded_project()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);

		await viewModel.SaveWorkspaceGitLabRepoIdAsync(
			workspace.Id,
			"group/repo",
			CancellationToken.None);

		var storedProject = store.Document.Projects.ShouldHaveSingleItem();
		storedProject.GitLabRepoId.ShouldBe("group/repo");
		workspace.Record.GitLabRepoId.ShouldBe("group/repo");
	}

	[Test]
	public async Task SetScenarioLocks_locks_only_listed_sessions()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var secondSession = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);

		viewModel.SetScenarioLocks("run-1", ["session-1", "missing-session"], locked: true);

		firstSession.LockedByScenarioRunId.ShouldBe("run-1");
		firstSession.IsLockedByScenario.ShouldBeTrue();
		secondSession.LockedByScenarioRunId.ShouldBeNull();
		secondSession.IsLockedByScenario.ShouldBeFalse();
	}

	[Test]
	public async Task SetScenarioLocks_unlocks_only_sessions_locked_by_the_same_run()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		var firstSession = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		var secondSession = await viewModel.CreateSessionAsync(
			sessionId: "session-2",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "reviewer",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-2",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		firstSession.LockForScenario("run-1");
		secondSession.LockForScenario("run-2");

		viewModel.SetScenarioLocks("run-1", ["session-1", "session-2"], locked: false);

		firstSession.LockedByScenarioRunId.ShouldBeNull();
		firstSession.IsLockedByScenario.ShouldBeFalse();
		secondSession.LockedByScenarioRunId.ShouldBe("run-2");
		secondSession.IsLockedByScenario.ShouldBeTrue();
	}

	[Test]
	public async Task AddScenarioRun_adds_run_to_project_scope_and_selects_it()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		var session = await viewModel.CreateSessionAsync(
			sessionId: "session-1",
			projectId: "default",
			kind: AgentKind.Codex,
			title: "author",
			workingDirectory: @"D:\Work\Project",
			launchCommand: "codex",
			resumeCommand: "codex resume session-1",
			cancellationToken: CancellationToken.None,
			workspaceId: workspace.Id);
		using var run = CreateScenarioRunViewModel();

		viewModel.AddScenarioRun(workspace.Id, run);

		workspace.ScenarioRuns.ShouldHaveSingleItem().ShouldBeSameAs(run);
		workspace.TreeItems.OfType<ScenarioRunViewModel>().ShouldHaveSingleItem().ShouldBeSameAs(run);
		workspace.TreeItems.ShouldBe([session, run]);
		viewModel.ScenarioRuns.ShouldHaveSingleItem().ShouldBeSameAs(run);
		viewModel.SelectedScenarioRun.ShouldBeSameAs(run);
		run.IsCurrentScenario.ShouldBeTrue();
		viewModel.SelectedSession.ShouldBeNull();
		session.IsCurrentTerminal.ShouldBeFalse();

		viewModel.SelectedSession = session;

		run.IsCurrentScenario.ShouldBeFalse();
	}

	[Test]
	public async Task RemoveScenarioRun_removes_from_window_and_workspace_scope()
	{
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault());
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		var workspace = await viewModel.EnsureWorkspaceForDirectoryAsync(
			@"D:\Work\Project",
			CancellationToken.None);
		using var run = CreateScenarioRunViewModel();
		viewModel.AddScenarioRun(workspace.Id, run);

		viewModel.RemoveScenarioRun(run);

		workspace.ScenarioRuns.ShouldBeEmpty();
		workspace.TreeItems.OfType<ScenarioRunViewModel>().ShouldBeEmpty();
		viewModel.ScenarioRuns.ShouldBeEmpty();
		viewModel.SelectedScenarioRun.ShouldBeNull();
	}

	[Test]
	public async Task MoveTreeItemAsync_persists_project_session_order_and_preserves_selection()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			ActiveItemId = "session-2",
			Sessions =
			[
				CreateSessionRecord("session-1", createdAt),
				CreateSessionRecord("session-2", createdAt),
				CreateSessionRecord("session-3", createdAt)
			]
		};
		InMemoryProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.Single();
		var selected = viewModel.SelectedSession.ShouldNotBeNull();

		var moved = await viewModel.MoveTreeItemAsync(
			workspace.Sessions[2],
			workspace.Sessions[0],
			insertAfter: false,
			CancellationToken.None);

		moved.ShouldBeTrue();
		workspace.Sessions.Select(item => item.Record.Id)
			.ShouldBe(["session-3", "session-1", "session-2"]);
		store.Document.Projects.Single().Sessions.Select(item => item.Id)
			.ShouldBe(["session-3", "session-1", "session-2"]);
		viewModel.SelectedSession.ShouldBeSameAs(selected);
	}

	[Test]
	public async Task MoveTreeItemAsync_rejects_different_item_types_without_persisting()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions = [CreateSessionRecord("session-1", createdAt)],
			WebPages = [CreateWebPageRecord("web-1", createdAt)]
		};
		UpdateOnlyProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.Single();

		var moved = await viewModel.MoveTreeItemAsync(
			workspace.Sessions.Single(),
			workspace.WebPages.Single(),
			insertAfter: false,
			CancellationToken.None);

		moved.ShouldBeFalse();
		store.UpdateCallCount.ShouldBe(0);
	}

	[Test]
	public async Task MoveTreeItemAsync_rejects_existing_relative_order_without_persisting()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions =
			[
				CreateSessionRecord("session-1", createdAt),
				CreateSessionRecord("session-2", createdAt)
			]
		};
		UpdateOnlyProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.Single();

		var moved = await viewModel.MoveTreeItemAsync(
			workspace.Sessions[0],
			workspace.Sessions[1],
			insertAfter: false,
			CancellationToken.None);

		moved.ShouldBeFalse();
		store.UpdateCallCount.ShouldBe(0);
	}

	[Test]
	public async Task MoveTreeItemAsync_store_failure_leaves_observable_order_unchanged()
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		var project = CreateProjectRecord("project-1", "Project") with
		{
			Sessions =
			[
				CreateSessionRecord("session-1", createdAt),
				CreateSessionRecord("session-2", createdAt)
			]
		};
		UpdateOnlyProjectStore store = new(ProjectsDocument.CreateDefault() with
		{
			Projects = [project]
		});
		var viewModel = MainWindowViewModelTestFactory.Create(store);
		await viewModel.LoadAsync(CancellationToken.None);
		var workspace = viewModel.Workspaces.Single();
		store.UpdateFailure = new IOException("write failed");

		await Should.ThrowAsync<IOException>(() => viewModel.MoveTreeItemAsync(
			workspace.Sessions[1],
			workspace.Sessions[0],
			insertAfter: false,
			CancellationToken.None));

		workspace.Sessions.Select(session => session.Record.Id)
			.ShouldBe(["session-1", "session-2"]);
	}

	private static ProjectRecord CreateProjectRecord(
		string id,
		string name,
		WorkspaceStatus status = WorkspaceStatus.Active,
		string? activeItemId = null)
	{
		var createdAt = DateTimeOffset.UtcNow.AddMinutes(-10);
		return new ProjectRecord(
			id,
			name,
			$@"D:\Work\{name}",
			createdAt,
			createdAt,
			Notes: null)
		{
			Status = status,
			ActiveItemId = activeItemId
		};
	}

	/// <summary>
	/// One project whose second session is unread while a scenario drives it, so attention rules
	/// can be observed against a real run rather than a stubbed state.
	/// </summary>
	private sealed class ScenarioAttentionFixture : IDisposable
	{
		private ScenarioAttentionFixture(
			MainWindowViewModel viewModel,
			ScenarioRunHandle handle,
			ScenarioRunViewModel run)
		{
			ViewModel = viewModel;
			Handle = handle;
			Run = run;
		}

		public MainWindowViewModel ViewModel { get; }

		public ScenarioRunHandle Handle { get; }

		public ScenarioRunViewModel Run { get; }

		public static async Task<ScenarioAttentionFixture> CreateAsync()
		{
			var now = DateTimeOffset.UtcNow;
			var project = CreateProjectRecord("project-1", "Project") with
			{
				Sessions =
				[
					CreateSessionRecord("session-1", now) with { Status = SessionStatus.Running },
					CreateSessionRecord("session-2", now) with { Status = SessionStatus.Running }
				]
			};
			TerminalTabStatusCoordinator statuses = new(action => action());
			var viewModel = MainWindowViewModelTestFactory.Create(
				new InMemoryProjectStore(ProjectsDocument.CreateDefault() with { Projects = [project] }),
				statuses);
			statuses.SetWindowFacts(true, true, now);
			await viewModel.LoadAsync(CancellationToken.None);
			await viewModel.UpdateSessionStatusAsync("session-1", SessionStatus.Running, CancellationToken.None);
			await viewModel.UpdateSessionStatusAsync("session-2", SessionStatus.Running, CancellationToken.None);
			viewModel.SelectedSession = viewModel.Sessions[0];

			TaskCompletionSource waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
			ScenarioRunService service = new(new AttentionGateway());
			var handle = service.Start(
				AttentionBlueprint,
				new WaitingProgram(waitStarted),
				"project-1",
				new Dictionary<string, string> { ["reviewer"] = "session-2" },
				"start",
				maxIterations: 1);
			ScenarioRunViewModel run = new(handle, dispatch: action => action());
			viewModel.AddScenarioRun("project-1", run, select: false);
			viewModel.Sessions[1].LockForScenario(handle.RunId);
			await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

			statuses.OnUserInput("session-2", "\r", now.AddSeconds(1));
			statuses.OnScreenSnapshot("session-2", @"PS D:\Work> ", now.AddSeconds(2));
			// Stated here so a projection change fails at its own cause rather than inside an
			// attention assertion that assumes the unread precondition holds.
			viewModel.Sessions[1].Indicator.ShouldBe(TerminalTabIndicator.Unread);
			return new ScenarioAttentionFixture(viewModel, handle, run);
		}

		public async Task PauseAsync()
		{
			Handle.RequestPause();
			await WaitForRunStateAsync(Handle, ScenarioRunState.Paused);
		}

		public void Dispose()
		{
			Handle.Abort();
			Run.Dispose();
			Handle.Dispose();
		}
	}

	private static readonly ScenarioBlueprint AttentionBlueprint = new(
		"attention-scenario",
		"Attention scenario",
		["reviewer"],
		[
			new ScenarioStepMetadata("send", "reviewer", null, "Send prompt", ScenarioStepKind.Send),
			new ScenarioStepMetadata("capture", "reviewer", null, "Capture response", ScenarioStepKind.Capture)
		],
		DefaultMaxIterations: 1,
		DefaultTarget: "start");

	private sealed class AttentionGateway : IScenarioTerminalGateway
	{
		public Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken) =>
			Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true));

		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken) => Task.CompletedTask;

		public bool IsSessionAlive(string sessionId) => true;

		public string GetSessionLabel(string sessionId) => sessionId;
	}

	private sealed class WaitingProgram(TaskCompletionSource waitStarted) : IScenarioProgram
	{
		public async Task<bool> RunIterationAsync(
			ScenarioIterationContext context,
			CancellationToken cancellationToken)
		{
			await context.SendAsync("send", "reviewer", "work", cancellationToken);
			await context.WaitForResponseAsync(
				"capture",
				"reviewer",
				async (_, waitCancellationToken) =>
				{
					waitStarted.TrySetResult();
					await Task.Delay(Timeout.InfiniteTimeSpan, waitCancellationToken);
					return "unreachable";
				},
				cancellationToken);
			return true;
		}
	}

	private static async Task WaitForRunStateAsync(ScenarioRunHandle handle, ScenarioRunState state)
	{
		TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStateChanged(object? _, EventArgs __)
		{
			if (handle.State == state)
			{
				reached.TrySetResult();
			}
		}

		handle.StateChanged += OnStateChanged;
		try
		{
			if (handle.State == state)
			{
				return;
			}

			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			handle.StateChanged -= OnStateChanged;
		}
	}

	private static SessionRecord CreateSessionRecord(string id, DateTimeOffset createdAt) => new SessionRecord(
			id,
			AgentKind.Pwsh,
			$"Task {id}",
			"D:\\Work",
			"pwsh",
			null,
			SessionStatus.Stopped,
			createdAt,
			createdAt);

	private static WebPageRecord CreateWebPageRecord(string id, DateTimeOffset createdAt) => new WebPageRecord(
			id,
			$"Page {id}",
			"https://example.test",
			"https://example.test/resume",
			createdAt,
			createdAt);

	private static ScenarioRunViewModel CreateScenarioRunViewModel()
	{
		ScenarioRunService service = new(new ImmediateScenarioGateway());
		ScenarioBlueprint blueprint = new(
			"test-scenario",
			"Test scenario",
			["reviewer"],
			[new ScenarioStepMetadata("run", "reviewer", null, "Run", ScenarioStepKind.Decision)],
			DefaultMaxIterations: 1,
			DefaultTarget: "start");
		var handle = service.Start(
			blueprint,
			new ImmediateScenarioProgram(),
			"project-1",
			new Dictionary<string, string> { ["reviewer"] = "session-1" },
			"start",
			maxIterations: 1);
		return new ScenarioRunViewModel(handle, action => action());
	}

	private sealed class ImmediateScenarioGateway : IScenarioTerminalGateway
	{
		public Task<PromptDeliveryResult> SendPromptAsync(
			string sessionId,
			string prompt,
			bool confirmDelivery,
			CancellationToken cancellationToken) =>
			Task.FromResult(new PromptDeliveryResult(
				PromptDeliveryOutcome.Confirmed,
				string.Empty,
				WriteAttempted: true,
				SubmitAttempted: true));

		public Task SendEscapeAsync(string sessionId, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public bool IsSessionAlive(string sessionId) => true;

		public string GetSessionLabel(string sessionId) => sessionId;
	}

	private sealed class ImmediateScenarioProgram : IScenarioProgram
	{
		public Task<bool> RunIterationAsync(ScenarioIterationContext context, CancellationToken cancellationToken) =>
			Task.FromResult(true);
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

	private sealed class UpdateOnlyProjectStore(ProjectsDocument document) : IProjectStore
	{
		public ProjectsDocument Document { get; private set; } = document;
		public int UpdateCallCount { get; private set; }
		public Exception? UpdateFailure { get; set; }

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Document);

		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) => throw new InvalidOperationException("CreateSessionAsync must use store-level updates.");

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken)
		{
			UpdateCallCount++;
			if (UpdateFailure is not null)
			{
				return Task.FromException<ProjectsDocument>(UpdateFailure);
			}

			Document = update(Document);
			return Task.FromResult(Document);
		}
	}
}
