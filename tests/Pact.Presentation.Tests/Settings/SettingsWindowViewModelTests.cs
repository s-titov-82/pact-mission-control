using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Infrastructure.Orchestrator;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class SettingsWindowViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _dir => _temporaryDirectory.Path;
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public void Sections_are_created_in_spec_order_with_spec_labels()
	{
		var vm = CreateViewModel();

		vm.Sections.Count.ShouldBe(11);
		vm.Sections.Select(section => section.Section).ShouldBe([
				SettingsSection.Projects,
				SettingsSection.PausedProjects,
				SettingsSection.LaunchProfiles,
				SettingsSection.ReviewProfiles,
				SettingsSection.WebLinkTemplates,
				SettingsSection.WebMonitoringRules,
				SettingsSection.PromptTemplates,
				SettingsSection.GitHelpers,
				SettingsSection.Scenarios,
				SettingsSection.RecentFolders,
				SettingsSection.Appearance
			]);
		vm.Sections.Select(section => section.Label).ShouldBe([
				"Current projects",
				"Paused projects",
				"Terminal templates",
				"Review profiles",
				"Web link templates",
				"Web monitoring rules",
				"Prompt/Shell templates",
				"Git popup",
				"Scenarios",
				"Recent directories",
				"Appearance"
			]);
	}

	[Test]
	public void Sections_include_the_orchestrator_when_its_runtime_dependencies_are_available()
	{
		var section = CreateOrchestratorSection();
		SettingsFileStore store = new(_dir);
		SettingsWindowViewModel vm = new(
			store,
			() => [],
			new FakeProjectSettingsEditor(),
			() => Task.FromResult<string?>(null),
			orchestratorSection: section);

		vm.Sections.ShouldContain(section);
		vm.Sections.Single(candidate => candidate.Section == SettingsSection.Orchestrator)
			.Label.ShouldBe("Orchestrator");
	}

	[Test]
	public async Task Paused_projects_section_is_second_and_loads_from_the_paused_workspaces_provider()
	{
		using var projectDirectory1 = TemporaryDirectory.Create();
		var projectDir = projectDirectory1.Path;
		try
		{
			SettingsFileStore store = new(_dir);
			await store.EnsureDefaultFilesAsync(CancellationToken.None);

			var now = DateTimeOffset.UtcNow;
			ProjectRecord pausedRecord = new("paused-1", "Paused One", projectDir, now, now, null);
			WorkspaceViewModel pausedWorkspace = new(pausedRecord, _ => false);

			SettingsWindowViewModel vm = new(
				store,
				() => [],
				new FakeProjectSettingsEditor(),
				() => Task.FromResult<string?>(null),
				() => [pausedWorkspace]);

			vm.Sections[1].Section.ShouldBe(SettingsSection.PausedProjects);

			await vm.InitializeAsync(SettingsSection.PausedProjects, null, null, CancellationToken.None);

			var pausedSection =
				vm.Sections.Single(s => s.Section == SettingsSection.PausedProjects).ShouldBeOfType<ProjectsSectionViewModel>();
			pausedSection.Items.ShouldHaveSingleItem();
			pausedSection.Items[0].Id.ShouldBe("paused-1");
			pausedSection.ShowAddButton.ShouldBeFalse();
		}
		finally
		{
			Directory.Delete(projectDir, recursive: true);
		}
	}

	[Test]
	public async Task InitializeAsync_forwards_deep_link_selection_into_the_paused_projects_section()
	{
		using var projectDirectory2 = TemporaryDirectory.Create();
		var projectDir = projectDirectory2.Path;
		try
		{
			SettingsFileStore store = new(_dir);
			await store.EnsureDefaultFilesAsync(CancellationToken.None);

			var now = DateTimeOffset.UtcNow;
			ProjectRecord pausedRecord = new("paused-1", "Paused One", projectDir, now, now, null);
			WorkspaceViewModel pausedWorkspace = new(pausedRecord, _ => false);
			SessionRecord sessionRecord = new(
				"sess-1", AgentKind.Codex, "Main", projectDir, "codex", null, SessionStatus.Running, now, now);
			pausedWorkspace.Sessions.Add(new SessionViewModel(sessionRecord));

			SettingsWindowViewModel vm = new(
				store,
				() => [],
				new FakeProjectSettingsEditor(),
				() => Task.FromResult<string?>(null),
				() => [pausedWorkspace]);

			await vm.InitializeAsync(SettingsSection.PausedProjects, "paused-1", "sess-1", CancellationToken.None);

			var paused = vm.ActiveSection.ShouldBeOfType<ProjectsSectionViewModel>();
			paused.Section.ShouldBe(SettingsSection.PausedProjects);
			var selectedProject = paused.SelectedItem;
			selectedProject.ShouldNotBeNull();
			selectedProject.Id.ShouldBe("paused-1");
			selectedProject.SelectedSession.ShouldNotBeNull();
			selectedProject.SelectedSession.Id.ShouldBe("sess-1");
		}
		finally
		{
			Directory.Delete(projectDir, recursive: true);
		}
	}

	[Test]
	public async Task Saving_a_paused_project_edit_routes_through_the_project_settings_editor()
	{
		using var projectDirectory3 = TemporaryDirectory.Create();
		var projectDir = projectDirectory3.Path;
		try
		{
			SettingsFileStore store = new(_dir);
			await store.EnsureDefaultFilesAsync(CancellationToken.None);

			var now = DateTimeOffset.UtcNow;
			ProjectRecord pausedRecord = new("paused-1", "Paused One", projectDir, now, now, null);
			WorkspaceViewModel pausedWorkspace = new(pausedRecord, _ => false);

			RecordingProjectSettingsEditor editor = new();
			SettingsWindowViewModel vm = new(
				store,
				() => [],
				editor,
				() => Task.FromResult<string?>(null),
				() => [pausedWorkspace]);

			await vm.InitializeAsync(SettingsSection.PausedProjects, null, null, CancellationToken.None);
			var paused = vm.ActiveSection.ShouldBeOfType<ProjectsSectionViewModel>();
			paused.Items[0].Name = "Renamed while paused";

			var result = await vm.SaveActiveSectionAsync(CancellationToken.None);

			result.ShouldBeTrue();
			editor.LastUpdatedProjectId.ShouldBe("paused-1");
		}
		finally
		{
			Directory.Delete(projectDir, recursive: true);
		}
	}

	[Test]
	public async Task InitializeAsync_activates_requested_section_and_forwards_selection()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		await WriteSingleScenarioAsync(store, "deep-link-scenario");

		var vm = CreateViewModel(store);

		await vm.InitializeAsync(SettingsSection.Scenarios, "deep-link-scenario", null, CancellationToken.None);

		vm.ActiveSection.ShouldNotBeNull();
		vm.ActiveSection.Section.ShouldBe(SettingsSection.Scenarios);
		var scenarios = vm.ActiveSection.ShouldBeOfType<ScenariosSectionViewModel>();
		scenarios.SelectedItem.ShouldNotBeNull();
		((ScenarioItemViewModel)scenarios.SelectedItem).Id.ShouldBe("deep-link-scenario");
	}

	[Test]
	public async Task InitializeAsync_forwards_deep_link_selection_into_the_projects_section()
	{
		using var projectDirectory4 = TemporaryDirectory.Create();
		var projectDir = projectDirectory4.Path;
		try
		{
			SettingsFileStore store = new(_dir);
			await store.EnsureDefaultFilesAsync(CancellationToken.None);

			var now = DateTimeOffset.UtcNow;
			ProjectRecord projectRecord = new("proj-1", "Proj One", projectDir, now, now, null);
			WorkspaceViewModel workspace = new(projectRecord, _ => false);
			SessionRecord sessionRecord = new(
				"sess-1", AgentKind.Codex, "Main", projectDir, "codex", null, SessionStatus.Running, now, now);
			workspace.Sessions.Add(new SessionViewModel(sessionRecord));

			SettingsWindowViewModel vm = new(
				store,
				() => [workspace],
				new FakeProjectSettingsEditor(),
				() => Task.FromResult<string?>(null));

			await vm.InitializeAsync(SettingsSection.Projects, "proj-1", "sess-1", CancellationToken.None);

			vm.ActiveSection.ShouldNotBeNull();
			var projects = vm.ActiveSection.ShouldBeOfType<ProjectsSectionViewModel>();
			var selectedProject = projects.SelectedItem;
			selectedProject.ShouldNotBeNull();
			selectedProject.Id.ShouldBe("proj-1");
			selectedProject.SelectedSession.ShouldNotBeNull();
			selectedProject.SelectedSession.Id.ShouldBe("sess-1");
		}
		finally
		{
			Directory.Delete(projectDir, recursive: true);
		}
	}

	[Test]
	public async Task InitializeAsync_loads_every_section()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var vm = CreateViewModel(store);

		await vm.InitializeAsync(SettingsSection.LaunchProfiles, null, null, CancellationToken.None);

		var launchProfiles =
			vm.Sections.Single(s => s.Section == SettingsSection.LaunchProfiles).ShouldBeOfType<LaunchProfilesSectionViewModel>();
		launchProfiles.Items.ShouldNotBeEmpty();
	}

	[Test]
	public async Task InitializeAsync_falls_back_to_first_section_for_unknown_section_or_item()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var vm = CreateViewModel(store);

		await vm.InitializeAsync((SettingsSection)999, "does-not-exist", null, CancellationToken.None);

		vm.ActiveSection.ShouldNotBeNull();
		vm.ActiveSection.ShouldBeSameAs(vm.Sections[0]);
		vm.ActiveSection.Section.ShouldBe(SettingsSection.Projects);
	}

	[Test]
	public async Task SaveActiveSectionAsync_sets_SavedAnyFile_on_success()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var vm = CreateViewModel(store);
		await vm.InitializeAsync(SettingsSection.LaunchProfiles, null, null, CancellationToken.None);

		vm.SavedAnyFile.ShouldBeFalse();
		var result = await vm.SaveActiveSectionAsync(CancellationToken.None);

		result.ShouldBeTrue();
		vm.SavedAnyFile.ShouldBeTrue();
	}

	[Test]
	public async Task SaveActiveSectionAsync_does_not_set_SavedAnyFile_on_validation_failure()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var vm = CreateViewModel(store);
		await vm.InitializeAsync(SettingsSection.LaunchProfiles, null, null, CancellationToken.None);
		var launchProfiles = vm.ActiveSection.ShouldBeOfType<LaunchProfilesSectionViewModel>();
		// Force a validation failure: duplicate ids.
		var first = (ShellProfileItemViewModel)launchProfiles.Items[0];
		var second = (ShellProfileItemViewModel)launchProfiles.Items[1];
		second.Id = first.Id;

		var result = await vm.SaveActiveSectionAsync(CancellationToken.None);

		result.ShouldBeFalse();
		vm.SavedAnyFile.ShouldBeFalse();
	}

	[Test]
	public async Task AnyDirty_aggregates_across_sections()
	{
		SettingsFileStore store = new(_dir);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var vm = CreateViewModel(store);
		await vm.InitializeAsync(SettingsSection.LaunchProfiles, null, null, CancellationToken.None);

		vm.AnyDirty.ShouldBeFalse();

		var launchProfiles = vm.ActiveSection.ShouldBeOfType<LaunchProfilesSectionViewModel>();
		((ShellProfileItemViewModel)launchProfiles.Items[0]).DisplayName = "Changed";

		vm.AnyDirty.ShouldBeTrue();
	}

	private SettingsWindowViewModel CreateViewModel(SettingsFileStore? store = null)
	{
		store ??= new SettingsFileStore(_dir);
		return new SettingsWindowViewModel(
			store,
			() => [],
			new FakeProjectSettingsEditor(),
			() => Task.FromResult<string?>(null));
	}

	private OrchestratorSectionViewModel CreateOrchestratorSection()
	{
		var hermesHome = Path.Combine(_dir, ".hermes");
		return new OrchestratorSectionViewModel(
			new OrchestratorStore(Path.Combine(_dir, "orchestrator.json")),
			new HermesProvisioner(new MissingHermesCli()),
			hermesHome,
			"http://127.0.0.1:8765/mcp/");
	}

	private static async Task WriteSingleScenarioAsync(SettingsFileStore store, string scenarioId)
	{
		var json = $$"""
            [
              {
                "id": "{{scenarioId}}",
                "kind": "reviewLoop",
                "name": "Deep link scenario",
                "maxIterations": 3,
                "stopMarker": "STOP",
                "defaultTarget": "main",
                "startPromptTemplate": "start",
                "firstFeedbackTemplate": "first",
                "authorReturnTemplate": "author",
                "feedbackTemplate": "feedback",
                "reviewerInstructions": [
                  { "id": "default", "name": "Default", "text": "review" }
                ],
                "defaultReviewerInstructionId": "default"
              }
            ]
            """;
		await store.SaveAsync("scenarios.json", json, CancellationToken.None);
	}

	private sealed class FakeProjectSettingsEditor : IProjectSettingsEditor
	{
		public Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct) => Task.CompletedTask;

		public Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct) => Task.CompletedTask;

		public Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct) => Task.FromResult<string?>(null);
	}

	private sealed class RecordingProjectSettingsEditor : IProjectSettingsEditor
	{
		public string? LastUpdatedProjectId { get; private set; }

		public Task UpdateProjectSettingsAsync(string projectId, ProjectSettingsEdit edit, CancellationToken ct)
		{
			LastUpdatedProjectId = projectId;
			return Task.CompletedTask;
		}

		public Task UpdateSessionSettingsAsync(string sessionId, SessionSettingsEdit edit, CancellationToken ct) => Task.CompletedTask;

		public Task<string?> CreateProjectForDirectoryAsync(string directory, CancellationToken ct) => Task.FromResult<string?>(null);
	}

	private sealed class MissingHermesCli : IHermesCli
	{
		public bool IsInstalled() => false;

		public Task<HermesCliResult> CreateProfileAsync(
			string profileName,
			CancellationToken cancellationToken) => throw new InvalidOperationException();
	}
}
