using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaScenarioRuntimeTests
{
	[Test]
	public async Task Setup_offers_only_prompt_compatible_sessions_and_reports_missing_blueprint()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();

		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace).ShouldBeOfType<ScenarioSetupViewModel>();
		setup.RoleBindings[0].Candidates.Select(session => session.Record.Id).ShouldBe(["author-session", "reviewer-session"]);

		var missing = fixture.Definition with { Kind = (ScenarioKind)999 };
		fixture.Controller.CreateScenarioSetup(missing, workspace).ShouldBeNull();
		fixture.Controller.StatusText.ShouldBe("Scenario has no blueprint");
	}

	[Test]
	public async Task Real_runtime_writes_one_task_trigger_then_agent_specific_enter_and_releases_locks_on_abort()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		var reviewerBackend = fixture.Backends[1];
		reviewerBackend.EmitOutput("\u001b[?9001h");
		await WaitUntilAsync(() => fixture.Controller.Runtimes[reviewer.Record.Id].Win32InputMode.IsActive);
		reviewerBackend.InputWritten = input =>
		{
			if (input == Win32InputEncoder.EnterKey)
			{
				fixture.Host.RaiseScreenSnapshotReceived(
					reviewer.Record.Id,
					"Working (1s)");
			}
		};

		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace).ShouldBeOfType<ScenarioSetupViewModel>();
		setup.Target = new string('t', 8 * 1024);
		var run = (await fixture.Controller.StartScenarioAsync(
				fixture.Definition, workspace, setup, TestContext.CurrentContext.CancellationToken)).ShouldBeOfType<ScenarioRunViewModel>();

		await WaitUntilAsync(() => reviewerBackend.Inputs.Count >= 2);
		reviewerBackend.Inputs.Count.ShouldBe(2);
		var trigger = reviewerBackend.Inputs[0];
		trigger.ShouldStartWith("\u001b[200~Read and follow the complete instructions in \"");
		trigger.ShouldEndWith("\u001b[201~");
		trigger.Contains("[Pasted Content", StringComparison.Ordinal).ShouldBeFalse();
		reviewerBackend.Inputs[1].ShouldBe(Win32InputEncoder.EnterKey);
		var taskPath = ExtractTaskPath(trigger);
		(await File.ReadAllTextAsync(taskPath, TestContext.CurrentContext.CancellationToken)).Contains(setup.Target, StringComparison.Ordinal).ShouldBeTrue();
		fixture.Host.SnapshotBaselineResetSessions.ShouldContain(reviewer.Record.Id);
		reviewerBackend.Inputs.Any(input =>
				input.Contains("\u001b[200~", StringComparison.Ordinal))
			.ShouldBeTrue();
		setup.RoleBindings
			.Select(binding => binding.SelectedSession!)
			.ShouldAllBe(session => session.LockedByScenarioRunId == run.RunId);
		workspace.ScenarioRuns.ShouldContain(run);
		fixture.Controller.SelectedScenarioRun.ShouldBeSameAs(run);

		run.Abort();
		await run.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);
		setup.RoleBindings
			.Select(binding => binding.SelectedSession!)
			.ShouldAllBe(session => !session.IsLockedByScenario);
	}

	[Test]
	public async Task Abort_after_trigger_write_cancels_submit_before_Enter_without_failing_run()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(
			session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		var reviewerBackend = fixture.Backends[1];
		TaskCompletionSource triggerWritten = new(TaskCreationOptions.RunContinuationsAsynchronously);
		reviewerBackend.InputWritten = input =>
		{
			if (input.StartsWith(
				"\u001b[200~Read and follow the complete instructions in \"",
				StringComparison.Ordinal))
			{
				triggerWritten.TrySetResult();
			}
		};

		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(
			fixture.Definition,
			workspace)!;
		var run = (await fixture.Controller.StartScenarioAsync(
				fixture.Definition,
				workspace,
				setup,
				TestContext.CurrentContext.CancellationToken)).ShouldBeOfType<ScenarioRunViewModel>();
		await triggerWritten.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		run.Abort();
		await run.Completion.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		run.State.ShouldBe(ScenarioRunState.Aborted);
		reviewerBackend.Inputs.ShouldNotContain("\r");
	}

	[Test]
	public async Task Manual_pause_unlocks_all_bound_sessions_and_resume_relocks_them()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		var reviewerBackend = fixture.Backends[1];
		reviewerBackend.InputWritten = input =>
		{
			if (input == "\r")
			{
				fixture.Host.RaiseScreenSnapshotReceived(
					reviewer.Record.Id,
					"Working (1s)");
			}
		};
		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace)!;
		var run = (await fixture.Controller.StartScenarioAsync(
			fixture.Definition,
			workspace,
			setup,
			TestContext.CurrentContext.CancellationToken)).ShouldBeOfType<ScenarioRunViewModel>();
		await WaitUntilAsync(() => reviewerBackend.Inputs.Count >= 2);

		fixture.Controller.PauseScenario(run);
		await WaitUntilAsync(() => run.State == ScenarioRunState.Paused);

		setup.RoleBindings
			.Select(binding => binding.SelectedSession!)
			.ShouldAllBe(session => !session.IsLockedByScenario);

		fixture.Controller.ResumeScenario(run);
		await WaitUntilAsync(() => run.State == ScenarioRunState.Running);

		setup.RoleBindings
			.Select(binding => binding.SelectedSession!)
			.ShouldAllBe(session => session.LockedByScenarioRunId == run.RunId);

		run.Abort();
		await run.Completion.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
	}

	[Test]
	public async Task Session_exit_notifies_real_scenario_service_and_fails_run()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace)!;
		var run = (await fixture.Controller.StartScenarioAsync(
			fixture.Definition, workspace, setup, TestContext.CurrentContext.CancellationToken)).ShouldBeOfType<ScenarioRunViewModel>();
		await WaitUntilAsync(() => fixture.Backends[1].Inputs.Count >= 2);

		fixture.Backends[1].CompleteOutput();

		await run.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);
		run.State.ShouldBe(ScenarioRunState.Failed);
		setup.RoleBindings
			.Select(binding => binding.SelectedSession!)
			.ShouldAllBe(session => !session.IsLockedByScenario);
	}

	[Test]
	public async Task Save_default_target_changes_only_selected_definition_and_refreshes_catalog()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace)!;
		setup.Target = "new persisted target";
		setup.SaveTargetAsDefault = true;

		var run = (await fixture.Controller.StartScenarioAsync(
			fixture.Definition,
			workspace,
			setup,
			TestContext.CurrentContext.CancellationToken))
			.ShouldBeOfType<ScenarioRunViewModel>();
		ScenarioDefinitionStore store = new(fixture.Paths.ScenariosPath);
		var saved = await store.LoadAsync(TestContext.CurrentContext.CancellationToken);

		saved.Single(item => item.Id == fixture.Definition.Id).DefaultTarget.ShouldBe("new persisted target");
		saved.Single(item => item.Id == "other").DefaultTarget.ShouldBe("other target");
		fixture.ViewModel.ScenarioDefinitions.Single(item => item.Id == fixture.Definition.Id).DefaultTarget.ShouldBe("new persisted target");
		run.Abort();
		await run.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.CurrentContext.CancellationToken);
	}

	[Test]
	public async Task Workspace_close_aborts_scenario_before_disposing_terminals()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace)!;
		var run = (await fixture.Controller.StartScenarioAsync(
			fixture.Definition, workspace, setup, TestContext.CurrentContext.CancellationToken))!;
		await WaitUntilAsync(() => fixture.Backends[1].Inputs.Count >= 2);

		var close = fixture.Controller.CloseWorkspaceAsync(workspace, TestContext.CurrentContext.CancellationToken);
		await fixture.Backends[0].StopStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		run.IsTerminal.ShouldBeTrue();
		await close;
	}

	[Test]
	public async Task App_shutdown_aborts_scenario_before_disposing_terminals()
	{
		await using ScenarioFixture fixture = new();
		await fixture.InitializeAsync(TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var reviewer = workspace.Sessions.Single(session => session.Record.Id == "reviewer-session");
		await fixture.Controller.SelectSessionAsync(
			reviewer,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		ReportIdleScreen(fixture, reviewer.Record.Id);
		var setup = fixture.Controller.CreateScenarioSetup(fixture.Definition, workspace)!;
		var run = (await fixture.Controller.StartScenarioAsync(
			fixture.Definition, workspace, setup, TestContext.CurrentContext.CancellationToken))!;
		await WaitUntilAsync(() => fixture.Backends[1].Inputs.Count >= 2);

		var shutdown = fixture.Controller.ShutdownAsync();
		await fixture.Backends[0].StopStarted.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		run.IsTerminal.ShouldBeTrue();
		await shutdown;
	}

	/// <summary>
	/// A launched session stays busy until a stable screen says otherwise, and a scenario only
	/// writes into an idle session, so the fake terminal has to report one before delivery.
	/// </summary>
	private static void ReportIdleScreen(ScenarioFixture fixture, string sessionId) =>
		fixture.Host.RaiseScreenSnapshotReceived(
			sessionId,
			"• ready\nWorked for 1s\n❯ ",
			stable: true);

	private static async Task WaitUntilAsync(Func<bool> predicate)
	{
		using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
		while (!predicate())
		{
			await Task.Delay(10, timeout.Token);
		}
	}

	private static string ExtractTaskPath(string trigger)
	{
		const string prefix = "\u001b[200~Read and follow the complete instructions in \"";
		const string suffix = "\".\u001b[201~";
		trigger.StartsWith(prefix, StringComparison.Ordinal).ShouldBeTrue();
		trigger.EndsWith(suffix, StringComparison.Ordinal).ShouldBeTrue();
		return trigger[prefix.Length..^suffix.Length];
	}

	private sealed class ScenarioFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;
		private string _root => _temporaryDirectory.Path;

		public ScenarioFixture()
		{
			var now = DateTimeOffset.UtcNow;
			var author = Session("author-session", AgentKind.Codex, now);
			var reviewer = Session("reviewer-session", AgentKind.Codex, now);
			var shell = Session("pwsh-session", AgentKind.Pwsh, now);
			ProjectRecord project = new("project-1", "Project", _root, now, now, null)
			{
				ActiveItemId = author.Id,
				Sessions = [author, reviewer, shell]
			};
			ProjectStore = new InMemoryProjectStore(new ProjectsDocument(1, [project]));
			ViewModel = new MainWindowViewModel(ProjectStore, new EmptyNotesStore());
			Host = new FakeTerminalWebViewHost();
			Paths = new AppPaths(_root);
			SettingsFileStore = new SettingsFileStore(Paths);
			Definition = DefinitionRecord("review-loop", "initial target");
			var other = DefinitionRecord("other", "other target");
			ScenarioDefinitionStore scenarioStore = new(Paths.ScenariosPath);
			scenarioStore.SaveAsync([Definition, other], CancellationToken.None).GetAwaiter().GetResult();
			WebMonitorSnapshotStore snapshotStore = new(Paths);
			MonitorCoordinator = new WebMonitorCoordinator(
				snapshotStore,
				TimeProvider.System,
				action => action());
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				SettingsFileStore,
				Paths,
				Host,
				() =>
				{
					FakeTerminalBackend backend = new();
					Backends.Add(backend);
					return backend;
				});
			_builder
				.WithSnapshotReader(new WebMonitorSnapshotReader(snapshotStore))
				.WithWebMonitorCoordinator(MonitorCoordinator)
				.WithUiTaskDispatcher(new ImmediateUiTaskDispatcher())
				.WithScenarioDefinitionStore(scenarioStore);
			Controller = _builder.Build();
		}

		public AppPaths Paths { get; }
		public SettingsFileStore SettingsFileStore { get; }
		public InMemoryProjectStore ProjectStore { get; }
		public MainWindowViewModel ViewModel { get; }
		public FakeTerminalWebViewHost Host { get; }
		public WebMonitorCoordinator MonitorCoordinator { get; }
		public List<FakeTerminalBackend> Backends { get; } = [];
		public AvaloniaMainShellController Controller { get; }
		public ScenarioDefinition Definition { get; }

		public async Task InitializeAsync(CancellationToken cancellationToken) =>
			await Controller.InitializeAsync(new Uri("file:///terminal.html"), cancellationToken);

		public async ValueTask DisposeAsync()
		{
			foreach (var backend in Backends)
			{
				backend.CompleteOutput();
			}

			using CancellationTokenSource exitTimeout = new(TimeSpan.FromSeconds(5));
			while (Controller.Runtimes.Values.Any(runtime =>
				runtime.TryGetController(out var controller) && controller.IsActive))
			{
				await Task.Delay(10, exitTimeout.Token);
			}

			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await MonitorCoordinator.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}

		private static SessionRecord Session(string id, AgentKind kind, DateTimeOffset now) => new(
			id, kind, id, Path.GetTempPath(), kind == AgentKind.Pwsh ? "pwsh" : "codex",
			kind == AgentKind.Pwsh
				? null
				: "codex resume 019f6050-35a4-7951-9748-47239487c08d",
			SessionStatus.Stopped, now, now);

		private static ScenarioDefinition DefinitionRecord(string id, string target) => new(
			id, ScenarioKind.ReviewLoop, id, 2, "DONE", target,
			"Review {target}", "Fix {reviewerOutput}", "Recheck {authorOutput}", "Fix {reviewerOutput}",
			[new("strict", "Strict", "Review strictly")], "strict");
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		private ProjectsDocument _document = document;
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_document);
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken)
		{
			_document = document;
			return Task.CompletedTask;
		}
		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken)
		{
			_document = update(_document);
			return Task.FromResult(_document);
		}
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
