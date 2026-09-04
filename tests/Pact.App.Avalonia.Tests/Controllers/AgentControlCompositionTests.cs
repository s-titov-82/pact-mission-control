using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AgentControlCompositionTests
{
	private static readonly JsonObject PlanReviewArguments = new()
	{
		["scenarioId"] = "plan-review",
		["reviewProfileId"] = "codex-high",
		["target"] = "HEAD",
		["maxIterations"] = 1
	};

	[Test]
	public async Task Notes_tools_read_replace_and_reject_stale_revisions_without_selecting_notes()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		var selectedSession = fixture.ViewModel.SelectedSession;
		var workspace = fixture.ViewModel.Workspaces.Single();
		workspace.Notes.ShouldBeEmpty();

		var firstRead = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_get_notes",
			new JsonObject());
		var firstSnapshot = JsonNode.Parse(firstRead.Text)!;
		var firstRevision = firstSnapshot["revision"]!.GetValue<string>();

		var replace = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_replace_notes",
			new JsonObject
			{
				["text"] = "replacement",
				["expectedRevision"] = firstRevision
			});
		replace.IsError.ShouldBeFalse(replace.Text);

		var secondRead = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_get_notes",
			new JsonObject());
		var secondSnapshot = JsonNode.Parse(secondRead.Text)!;
		secondSnapshot["text"]!.GetValue<string>().ShouldBe("replacement");
		secondSnapshot["revision"]!.GetValue<string>().ShouldNotBe(firstRevision);

		var staleReplace = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_replace_notes",
			new JsonObject
			{
				["text"] = "stale overwrite",
				["expectedRevision"] = firstRevision
			});
		staleReplace.IsError.ShouldBeTrue();
		staleReplace.Text.ShouldContain("notes-conflict");
		fixture.NotesOf(CompositionFixture.ProjectA).ShouldBe("replacement");
		workspace.Notes.ShouldBeEmpty();
		fixture.ViewModel.SelectedSession.ShouldBeSameAs(selectedSession);
		fixture.ViewModel.SelectedProjectNote.ShouldBeNull();
	}

	[Test]
	public async Task ToolCallsFromTwoSessionsReachTheirOwnProjects()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		await fixture.StartSessionAsync(CompositionFixture.SessionB);

		await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_append_note",
			new JsonObject { ["text"] = "note-a" });
		await fixture.InvokeToolAsync(
			CompositionFixture.SessionB,
			"pact_append_note",
			new JsonObject { ["text"] = "note-b" });

		fixture.NotesOf(CompositionFixture.ProjectA).ShouldContain("note-a");
		fixture.NotesOf(CompositionFixture.ProjectA).ShouldNotContain("note-b");
		fixture.NotesOf(CompositionFixture.ProjectB).ShouldContain("note-b");
	}

	[Test]
	public async Task OpenWebTabCreatesThePageUnderTheCallingSessionsProject()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();

		var result = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_open_web_tab",
			new JsonObject
			{
				["url"] = "https://example.test/review",
				["title"] = "Review"
			});

		result.IsError.ShouldBeFalse();
		var project = fixture.ViewModel.Workspaces.Single(
			workspace => workspace.Id == CompositionFixture.ProjectA);
		project.WebPages.ShouldHaveSingleItem().Title.ShouldBe("Review");
		fixture.ViewModel.Workspaces.Single(
			workspace => workspace.Id == CompositionFixture.ProjectB)
			.WebPages.ShouldBeEmpty();
	}

	[Test]
	public async Task ConcurrentReviewRequestsForOneProjectStartExactlyOneRun()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		await fixture.StartSessionAsync(CompositionFixture.SessionB);

		var backendCount = fixture.Backends.Count;
		var resultsTask = Task.WhenAll(
			fixture.InvokeToolAsync(
				CompositionFixture.SessionA,
				"pact_request_review",
				PlanReviewArguments),
			fixture.InvokeToolAsync(
				CompositionFixture.SessionB,
				"pact_request_review",
				PlanReviewArguments));
		await fixture.MakeLatestReviewerReadyAsync(backendCount);
		var results = await resultsTask;

		results.Count(result => !result.IsError).ShouldBe(
			1,
			string.Join(Environment.NewLine, results.Select(result => result.Text)));
		results.Count(result => result.IsError).ShouldBe(1);
		fixture.ActiveRuns(CompositionFixture.ProjectA).Length.ShouldBe(1);
	}

	[Test]
	public async Task ASecondRequestHeldAtTheReservationStageIsToldAStartIsInProgress()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		await fixture.StartSessionAsync(CompositionFixture.SessionB);
		FakeTerminalBackend blockedReviewer = new()
		{
			StartBlocker = new TaskCompletionSource(
				TaskCreationOptions.RunContinuationsAsynchronously)
		};
		fixture.NextBackend = blockedReviewer;

		var first = fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_request_review",
			PlanReviewArguments);
		await blockedReviewer.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var second = await fixture.InvokeToolAsync(
			CompositionFixture.SessionB,
			"pact_request_review",
			PlanReviewArguments);

		second.IsError.ShouldBeTrue();
		second.Text.ShouldContain("already starting");
		second.Text.ShouldNotContain("run ''");
		blockedReviewer.StartBlocker.SetResult();
		await fixture.MakeReviewerReadyAsync(blockedReviewer);
		var firstResult = await first;
		firstResult.IsError.ShouldBeFalse(firstResult.Text);
	}

	[Test]
	public async Task ASecondRequestAgainstARunningReviewIsToldItsRunId()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		await fixture.StartSessionAsync(CompositionFixture.SessionB);
		var backendCount = fixture.Backends.Count;
		var firstTask = fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_request_review",
			PlanReviewArguments);
		await fixture.MakeLatestReviewerReadyAsync(backendCount);
		var first = await firstTask;

		var second = await fixture.InvokeToolAsync(
			CompositionFixture.SessionB,
			"pact_request_review",
			PlanReviewArguments);

		first.IsError.ShouldBeFalse(first.Text);
		second.IsError.ShouldBeTrue();
		second.Text.ShouldContain(first.Text);
	}

	[Test]
	public async Task AFailedReviewerStartLeavesNoSessionControllerOrToken()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		FakeTerminalBackend failedReviewer = new()
		{
			StartFailure = new InvalidOperationException("start failed")
		};
		fixture.NextBackend = failedReviewer;
		var initialCount = fixture.Sessions(CompositionFixture.ProjectA).Count;

		var result = await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_request_review",
			PlanReviewArguments);

		result.IsError.ShouldBeTrue();
		fixture.Sessions(CompositionFixture.ProjectA).Count.ShouldBe(initialCount);
		fixture.Controller.Runtimes.Count.ShouldBe(1);
		var token = failedReviewer.LastStartOptions!.EnvironmentVariables![
			"PACT_AGENT_CONTROL_TOKEN"];
		(await fixture.PostInitializeAsync(token)).ShouldBe(HttpStatusCode.Unauthorized);
		fixture.Host.DisposedSessions.ShouldContain(sessionId =>
			sessionId != CompositionFixture.SessionA);
	}

	[Test]
	public async Task ReviewerProfileCommandUsesTheSharedTerminalLaunchResolver()
	{
		List<(string Command, IReadOnlyList<string> Arguments)> resolvedCommands = [];
		await using CompositionFixture fixture = new(
			twoProjects: false,
			(command, arguments) =>
			{
				resolvedCommands.Add((command, arguments));
				return Task.FromResult<string?>($"resolved::{command}::{arguments.Count}");
			});
		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile(
				"claude-personal-opus",
				"Claude Personal Opus",
				AgentKind.Claude,
				"claude-personal --model claude-opus-5"));
		await fixture.InitializeAsync();

		var backendCount = fixture.Backends.Count;
		var resultTask = fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_request_review",
			new JsonObject
			{
				["scenarioId"] = "plan-review",
				["reviewProfileId"] = "claude-personal-opus",
				["target"] = "HEAD",
				["maxIterations"] = 1
			});
		await fixture.MakeLatestReviewerReadyAsync(backendCount);
		var result = await resultTask;

		result.IsError.ShouldBeFalse(result.Text);
		var reviewerCommand = resolvedCommands.Single(call =>
			call.Command.StartsWith(
				"claude-personal --model claude-opus-5",
				StringComparison.Ordinal));
		reviewerCommand.Command.ShouldBe("claude-personal --model claude-opus-5");
		reviewerCommand.Arguments.ShouldContain("--mcp-config");
		reviewerCommand.Arguments.ShouldContain("--append-system-prompt");
		reviewerCommand.Arguments.ShouldContain(argument =>
			argument.Contains("PactMcpSkill.md", StringComparison.Ordinal)
			&& argument.Contains("PactCommonSkill.md", StringComparison.Ordinal));
		fixture.Backends[^1].LastStartOptions!.CommandLine.ShouldBe(
			$"resolved::{reviewerCommand.Command}::{reviewerCommand.Arguments.Count}");
	}

	[Test]
	public async Task StoppingASessionRevokesItsToken()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		var token = fixture.TokenOf(CompositionFixture.SessionA);
		var session = fixture.ViewModel.FindSession(CompositionFixture.SessionA)!;

		await fixture.Controller.CloseSessionAsync(session);

		(await fixture.PostInitializeAsync(token)).ShouldBe(HttpStatusCode.Unauthorized);
	}

	[Test]
	public async Task ExecutedRequestsRaiseAStatusNotification()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		List<string> messages = [];
		fixture.Controller.StatusMessage += (_, message) => messages.Add(message);
		await fixture.InitializeAsync();

		await fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_append_note",
			new JsonObject { ["text"] = "status" });

		messages.ShouldContain(message => message == "Agent action: notes updated");
	}

	[Test]
	public async Task Connected_client_re_lists_changed_review_profiles_without_terminal_restart()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		FakeTerminalBackend originalBackend =
			fixture.BackendOf(CompositionFixture.SessionA);
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		(await fixture.ReviewProfileIdsAsync(CompositionFixture.SessionA))
			.ShouldNotContain("personal-opus");
		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile(
				"personal-opus",
				"Personal Opus",
				AgentKind.Claude,
				"claude-personal --model opus"));

		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();

		(await stream.ReadNextDataAsync())
			.ShouldContain("notifications/tools/list_changed");
		(await fixture.ReviewProfileIdsAsync(CompositionFixture.SessionA))
			.ShouldContain("personal-opus");
		fixture.BackendOf(CompositionFixture.SessionA).ShouldBeSameAs(originalBackend);
		fixture.Backends.Count.ShouldBe(1);
	}

	[Test]
	public async Task Review_profile_metadata_change_does_not_publish_tool_list_change()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile("stable", "Before", AgentKind.Claude, "claude --model sonnet"));
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile("stable", "After", AgentKind.Codex, "codex --model gpt-5"));
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();
		await fixture.Controller.DisposeAsync();

		(await stream.ReadAllDataUntilClosedAsync()).ShouldBeEmpty();
	}

	[Test]
	public async Task Malformed_review_profiles_publish_empty_catalog_change()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile("stable", "Stable", AgentKind.Claude, "claude"));
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		await fixture.WriteReviewProfilesJsonAsync("{");
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();

		(await stream.ReadNextDataAsync())
			.ShouldContain("notifications/tools/list_changed");
		(await fixture.ReviewProfileIdsAsync(CompositionFixture.SessionA))
			.ShouldBeEmpty();
	}

	[Test]
	public async Task Failed_review_profile_refresh_retains_catalog_without_notification()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		await fixture.WriteReviewProfilesAsync(
			new ReviewProfile("stable", "Stable", AgentKind.Claude, "claude"));
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		using FileStream profileLock = fixture.OpenReviewProfilesExclusiveLock();
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeFalse();
		(await fixture.ReviewProfileIdsAsync(CompositionFixture.SessionA))
			.ShouldBe(["stable"]);
		await fixture.Controller.DisposeAsync();

		(await stream.ReadAllDataUntilClosedAsync()).ShouldBeEmpty();
	}

	[Test]
	public async Task Connected_client_re_lists_changed_scenarios()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		ScenarioDefinition custom = ScenarioDefinitionStore.LoadDefaultDefinitions()[0] with
		{
			Id = "custom-review",
			Name = "Custom review"
		};
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		await fixture.WriteScenariosAsync(custom);
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();

		(await stream.ReadNextDataAsync())
			.ShouldContain("notifications/tools/list_changed");
		(await fixture.ScenarioIdsAsync(CompositionFixture.SessionA))
			.ShouldBe(["custom-review"]);
	}

	[Test]
	public async Task Malformed_scenarios_publish_fallback_catalog_change()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		ScenarioDefinition custom = ScenarioDefinitionStore.LoadDefaultDefinitions()[0] with
		{
			Id = "custom-review",
			Name = "Custom review"
		};
		await fixture.WriteScenariosAsync(custom);
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeTrue();
		await using CompositionFixture.AgentNotificationStream stream =
			await fixture.OpenNotificationStreamAsync(CompositionFixture.SessionA);

		await fixture.WriteScenariosJsonAsync("{");
		(await fixture.Controller.ReloadExternalSettingsAsync()).ShouldBeFalse();

		(await stream.ReadNextDataAsync())
			.ShouldContain("notifications/tools/list_changed");
		(await fixture.ScenarioIdsAsync(CompositionFixture.SessionA))
			.ShouldNotContain("custom-review");
	}

	[Test]
	public async Task AQualifiedProfileReachesTheBackendCommandAndEnvironment()
	{
		await using CompositionFixture fixture = new(twoProjects: true);

		await fixture.InitializeAsync();

		var started = fixture.BackendOf(CompositionFixture.SessionA).LastStartOptions!;
		started.CommandLine.ShouldContain("mcp_servers.pact.url=http://127.0.0.1:");
		started.CommandLine.ShouldNotContain(fixture.TokenOf(CompositionFixture.SessionA));
		started.EnvironmentVariables!["PACT_SESSION_ID"]
			.ShouldBe(CompositionFixture.SessionA);
	}

	[Test]
	public async Task Ordinary_and_resume_commands_keep_the_base_command_and_receive_arguments_once()
	{
		List<(string Command, IReadOnlyList<string> Arguments)> calls = [];
		await using CompositionFixture fixture = new(
			twoProjects: true,
			(command, arguments) =>
			{
				calls.Add((command, arguments.ToArray()));
				return Task.FromResult<string?>(command);
			});
		await fixture.InitializeAsync();

		var resumed = calls.Single(call =>
			call.Command.StartsWith("codex resume ", StringComparison.Ordinal)
			&& call.Arguments.Count > 0);
		resumed.Arguments.Count(argument =>
			argument.StartsWith("developer_instructions=", StringComparison.Ordinal)).ShouldBe(1);

		var session = fixture.ViewModel.FindSession(CompositionFixture.SessionA)!;
		await fixture.Controller.RestartSessionAsync(session, preferResumeCommand: false);
		var fresh = calls.Last(call => call.Command == "codex --model gpt-5.4");
		fresh.Arguments.Count(argument =>
			argument.StartsWith("developer_instructions=", StringComparison.Ordinal)).ShouldBe(1);
	}

	[Test]
	public async Task Claude_root_session_receives_inline_instruction_without_changing_profile_command()
	{
		List<(string Command, IReadOnlyList<string> Arguments)> calls = [];
		await using CompositionFixture fixture = new(
			twoProjects: true,
			(command, arguments) =>
			{
				calls.Add((command, arguments.ToArray()));
				return Task.FromResult<string?>(command);
			});
		await fixture.InitializeAsync();
		AgentProfileRecord profile = new(
			"claude-root",
			AgentKind.Claude,
			"Claude Root",
			"claude-personal --model opus",
			ResumeCommandTemplate: null,
			DefaultShell: "pwsh");

		await fixture.Controller.AddRootSessionAsync(profile);

		var launch = calls.Single(call =>
			call.Command == profile.CommandTemplate && call.Arguments.Count > 0);
		launch.Arguments.ShouldContain("--append-system-prompt");
		launch.Arguments.ShouldContain(argument =>
			argument.Contains("PactMcpSkill.md", StringComparison.Ordinal));
	}

	[Test]
	public async Task Disabled_agent_control_adds_common_guidance_without_mcp_or_token()
	{
		List<(string Command, IReadOnlyList<string> Arguments)> calls = [];
		await using CompositionFixture fixture = new(
			twoProjects: true,
			(command, arguments) =>
			{
				calls.Add((command, arguments.ToArray()));
				return Task.FromResult<string?>(command);
			},
			agentControlEnabled: false);

		await fixture.InitializeAsync();

		var launch = calls.Single(call =>
			call.Command.StartsWith("codex resume ", StringComparison.Ordinal)
			&& call.Arguments.Count > 0);
		launch.Arguments.ShouldNotContain(argument =>
			argument.Contains("mcp_servers.pact", StringComparison.Ordinal));
		launch.Arguments.ShouldContain(argument =>
			argument.Contains("PactCommonSkill.md", StringComparison.Ordinal)
			&& !argument.Contains("PactMcpSkill.md", StringComparison.Ordinal));
		fixture.BackendOf(CompositionFixture.SessionA).LastStartOptions!
			.EnvironmentVariables.ShouldBeEmpty();
	}

	[Test]
	public async Task Publisher_failure_keeps_mcp_injection_and_records_one_diagnostic()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		Directory.CreateDirectory(fixture.Paths.RetainedTempDirectory);
		File.WriteAllText(fixture.Paths.PactSkillsDirectory, "not a directory");

		await fixture.InitializeAsync();

		TerminalStartOptions started =
			fixture.BackendOf(CompositionFixture.SessionA).LastStartOptions!;
		started.CommandLine.ShouldContain("mcp_servers.pact.url=");
		started.CommandLine.ShouldNotContain("developer_instructions=");
		fixture.Controller.DiagnosticSnapshot.Count(entry =>
			entry.Phase == "pact-skill-publication-failed").ShouldBe(1);
	}

	[Test]
	public async Task RestartingASessionRevokesTheOldTokenAndInjectsAFreshOne()
	{
		await using CompositionFixture fixture = new(twoProjects: true);
		await fixture.InitializeAsync();
		var firstToken = fixture.TokenOf(CompositionFixture.SessionA);
		var session = fixture.ViewModel.FindSession(CompositionFixture.SessionA)!;

		await fixture.Controller.RestartSessionAsync(
			session,
			preferResumeCommand: false);
		fixture.RecordLatestBackend(CompositionFixture.SessionA);

		var secondToken = fixture.TokenOf(CompositionFixture.SessionA);
		secondToken.ShouldNotBe(firstToken);
		(await fixture.PostInitializeAsync(firstToken)).ShouldBe(HttpStatusCode.Unauthorized);
		(await fixture.PostInitializeAsync(secondToken)).ShouldBe(HttpStatusCode.OK);
	}

	[Test]
	public async Task AnAgentRequestedReviewLeavesTheSelectionWhereItWas()
	{
		await using CompositionFixture fixture = new(twoProjects: false);
		await fixture.InitializeAsync();
		await fixture.StartSessionAsync(CompositionFixture.SessionB);
		fixture.ViewModel.SelectedSession!.Record.Id.ShouldBe(CompositionFixture.SessionB);

		var backendCount = fixture.Backends.Count;
		var resultTask = fixture.InvokeToolAsync(
			CompositionFixture.SessionA,
			"pact_request_review",
			PlanReviewArguments);
		await fixture.MakeLatestReviewerReadyAsync(backendCount);
		var result = await resultTask;

		result.IsError.ShouldBeFalse(result.Text);
		fixture.ViewModel.SelectedSession!.Record.Id.ShouldBe(CompositionFixture.SessionB);
		fixture.ViewModel.SelectedScenarioRun.ShouldBeNull();
	}

	private sealed record ToolResult(string Text, bool IsError);

	private sealed class CompositionFixture : IAsyncDisposable
	{
		public const string ProjectA = "project-a";
		public const string ProjectB = "project-b";
		public const string SessionA = "session-a";
		public const string SessionB = "session-b";

		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;
		private readonly RecordingNotesStore _notes = new();
		private readonly Dictionary<string, FakeTerminalBackend> _backendBySession =
			new(StringComparer.Ordinal);
		private readonly HttpClient _client = new();
		private readonly WebMonitorCoordinator _monitor;
		private int _requestId;

		public CompositionFixture(
			bool twoProjects,
			Func<string, IReadOnlyList<string>, Task<string?>>? resolveCommandAsync = null,
			bool agentControlEnabled = true)
		{
			var now = DateTimeOffset.UtcNow;
			var rootA = Path.Combine(_temporaryDirectory.Path, ProjectA);
			var rootB = Path.Combine(_temporaryDirectory.Path, ProjectB);
			Directory.CreateDirectory(rootA);
			Directory.CreateDirectory(rootB);
			var first = Session(SessionA, rootA, now);
			var second = Session(SessionB, twoProjects ? rootB : rootA, now);
			ProjectRecord projectA = new(
				ProjectA,
				"Project A",
				rootA,
				now,
				now,
				Notes: null)
			{
				Status = WorkspaceStatus.Active,
				ActiveItemId = SessionA,
				Sessions = twoProjects ? [first] : [first, second]
			};
			ProjectRecord projectB = new(
				ProjectB,
				"Project B",
				rootB,
				now,
				now,
				Notes: null)
			{
				Status = WorkspaceStatus.Active,
				ActiveItemId = twoProjects ? SessionB : null,
				Sessions = twoProjects ? [second] : []
			};
			InMemoryProjectStore projectStore = new(new ProjectsDocument(
				1,
				twoProjects ? [projectA, projectB] : [projectA]));
			ViewModel = new MainWindowViewModel(projectStore, _notes);
			Host = new FakeTerminalWebViewHost();
			Paths = new AppPaths(_temporaryDirectory.Path);
			if (!agentControlEnabled)
			{
				Directory.CreateDirectory(Paths.SettingsDirectory);
				File.WriteAllText(
					Paths.AgentControlSettingsPath,
					"""{"port":8765,"enabled":false}""");
			}

			SettingsFileStore settings = new(Paths);
			WebMonitorSnapshotStore snapshots = new(Paths);
			_monitor = new WebMonitorCoordinator(
				snapshots,
				TimeProvider.System,
				action => action());
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				settings,
				Paths,
				Host,
				() =>
				{
					var backend = NextBackend ?? new FakeTerminalBackend();
					NextBackend = null;
					backend.ExitResponse =
						"codex resume 019f6050-35a4-7951-9748-47239487c08d";
					Backends.Add(backend);
					return backend;
				});
			_builder
				.WithExecutableLocator(new FakeExecutableLocator())
				.WithWebMonitorCoordinator(_monitor)
				.WithUiTaskDispatcher(new ImmediateUiTaskDispatcher());
			if (resolveCommandAsync is not null)
			{
				_builder.WithCommandResolver(resolveCommandAsync);
			}

			Controller = _builder.Build();
		}

		public AppPaths Paths { get; }
		public MainWindowViewModel ViewModel { get; }
		public FakeTerminalWebViewHost Host { get; }
		public AvaloniaMainShellController Controller { get; }
		public List<FakeTerminalBackend> Backends { get; } = [];
		public FakeTerminalBackend? NextBackend { get; set; }

		public async Task InitializeAsync()
		{
			await Controller.InitializeAsync(
				new Uri("file:///terminal.html"),
				TestContext.CurrentContext.CancellationToken);
			_backendBySession[ViewModel.SelectedSession!.Record.Id] = Backends[^1];
		}

		public async Task WriteReviewProfilesAsync(params ReviewProfile[] profiles)
		{
			await WriteReviewProfilesJsonAsync(
				JsonSerializer.Serialize(profiles, SettingsFileStore.JsonOptions));
		}

		public async Task WriteReviewProfilesJsonAsync(string json)
		{
			Directory.CreateDirectory(Paths.SettingsDirectory);
			await File.WriteAllTextAsync(
				Paths.ReviewProfilesPath,
				json,
				TestContext.CurrentContext.CancellationToken);
		}

		public FileStream OpenReviewProfilesExclusiveLock() =>
			new(
				Paths.ReviewProfilesPath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.None);

		public async Task WriteScenariosAsync(params ScenarioDefinition[] definitions)
		{
			ScenarioDefinitionStore store =
				new(Paths.ScenariosPath, Paths.AtomicTempDirectory);
			await store.SaveAsync(
				definitions,
				TestContext.CurrentContext.CancellationToken);
		}

		public async Task WriteScenariosJsonAsync(string json)
		{
			Directory.CreateDirectory(Paths.SettingsDirectory);
			await File.WriteAllTextAsync(
				Paths.ScenariosPath,
				json,
				TestContext.CurrentContext.CancellationToken);
		}

		public async Task StartSessionAsync(string sessionId)
		{
			var session = ViewModel.FindSession(sessionId)!;
			var before = Backends.Count;
			await Controller.SelectSessionAsync(
				session,
				startIfNeeded: true,
				cancellationToken: TestContext.CurrentContext.CancellationToken);
			if (Backends.Count > before)
			{
				_backendBySession[sessionId] = Backends[^1];
			}
		}

		public FakeTerminalBackend BackendOf(string sessionId) =>
			_backendBySession[sessionId];

		public async Task MakeLatestReviewerReadyAsync(int backendCountBefore)
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			while (Backends.Count <= backendCountBefore)
			{
				timeout.Token.ThrowIfCancellationRequested();
				await Task.Yield();
			}

			await MakeReviewerReadyAsync(Backends[^1]);
		}

		public async Task MakeReviewerReadyAsync(FakeTerminalBackend backend)
		{
			await backend.StartStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
			backend.EmitOutput("reviewer ready");
			await backend.FirstOutputProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
			var reviewer = Sessions(ProjectA).Single(session =>
				session.Title.StartsWith("Review ·", StringComparison.Ordinal));
			Host.RaiseScreenSnapshotReceived(
				reviewer.Record.Id,
				reviewer.Record.Kind == AgentKind.Codex
					? "\n──────────────────────────────\n❯"
					: "❯\n──────────────────────────────",
				stable: true);
		}

		public void RecordLatestBackend(string sessionId) =>
			_backendBySession[sessionId] = Backends[^1];

		public string TokenOf(string sessionId) =>
			BackendOf(sessionId).LastStartOptions!.EnvironmentVariables![
				"PACT_AGENT_CONTROL_TOKEN"];

		public ObservableCollection<SessionViewModel> Sessions(string projectId) =>
			ViewModel.Workspaces.Single(workspace => workspace.Id == projectId).Sessions;

		public ScenarioRunViewModel[] ActiveRuns(string projectId) =>
			ViewModel.Workspaces.Single(workspace => workspace.Id == projectId)
				.ScenarioRuns.Where(run => !run.IsTerminal).ToArray();

		public string NotesOf(string projectId)
		{
			var root = ViewModel.Workspaces.Single(workspace => workspace.Id == projectId).RootPath;
			return _notes.TextOf(root);
		}

		public async Task<ToolResult> InvokeToolAsync(
			string sessionId,
			string toolName,
			JsonObject arguments)
		{
			var id = Interlocked.Increment(ref _requestId);
			JsonObject requestBody = new()
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["method"] = "tools/call",
				["params"] = new JsonObject
				{
					["name"] = toolName,
					["arguments"] = arguments.DeepClone()
				}
			};
			using HttpRequestMessage request = new(HttpMethod.Post, Controller.AgentControlAddress)
			{
				Content = new StringContent(
					requestBody.ToJsonString(),
					Encoding.UTF8,
					"application/json")
			};
			request.Headers.Authorization =
				new AuthenticationHeaderValue("Bearer", TokenOf(sessionId));
			using var response = await _client.SendAsync(request);
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
			var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
			var result = json["result"]!;
			return new ToolResult(
				result["content"]![0]!["text"]!.GetValue<string>(),
				result["isError"]!.GetValue<bool>());
		}

		public async Task<HttpStatusCode> PostInitializeAsync(string token)
		{
			using HttpRequestMessage request = new(HttpMethod.Post, Controller.AgentControlAddress)
			{
				Content = new StringContent(
					"""{"jsonrpc":"2.0","id":1,"method":"initialize"}""",
					Encoding.UTF8,
					"application/json")
			};
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
			using var response = await _client.SendAsync(request);
			return response.StatusCode;
		}

		[SuppressMessage(
			"Reliability",
			"CA2000:Dispose objects before losing scope",
			Justification = "The returned AgentNotificationStream owns and disposes both the response and its reader.")]
		public async Task<AgentNotificationStream> OpenNotificationStreamAsync(string sessionId)
		{
			using HttpRequestMessage request = new(HttpMethod.Get, Controller.AgentControlAddress);
			request.Headers.Authorization =
				new AuthenticationHeaderValue("Bearer", TokenOf(sessionId));
			request.Headers.Accept.Add(
				new MediaTypeWithQualityHeaderValue("text/event-stream"));
			HttpResponseMessage response = await _client.SendAsync(
				request,
				HttpCompletionOption.ResponseHeadersRead);
			response.EnsureSuccessStatusCode();
			StreamReader reader = new(await response.Content.ReadAsStreamAsync());
			(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)))
				.ShouldBe(": connected");
			(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeEmpty();
			return new AgentNotificationStream(response, reader);
		}

		public async Task<string?[]> ReviewProfileIdsAsync(string sessionId)
		{
			JsonNode result = await PostRpcAsync(
				sessionId,
				"""{"jsonrpc":"2.0","id":41,"method":"tools/list"}""");
			JsonNode review = result["tools"]!.AsArray()
				.Single(tool => (string?)tool!["name"] == "pact_request_review")!;
			return review["inputSchema"]!["properties"]!["reviewProfileId"]!["enum"]!
				.AsArray().Select(value => (string?)value).ToArray();
		}

		public async Task<string?[]> ScenarioIdsAsync(string sessionId)
		{
			JsonNode result = await PostRpcAsync(
				sessionId,
				"""{"jsonrpc":"2.0","id":42,"method":"tools/list"}""");
			JsonNode review = result["tools"]!.AsArray()
				.Single(tool => (string?)tool!["name"] == "pact_request_review")!;
			return review["inputSchema"]!["properties"]!["scenarioId"]!["enum"]!
				.AsArray().Select(value => (string?)value).ToArray();
		}

		private async Task<JsonNode> PostRpcAsync(string sessionId, string body)
		{
			using HttpRequestMessage request = new(HttpMethod.Post, Controller.AgentControlAddress)
			{
				Content = new StringContent(body, Encoding.UTF8, "application/json")
			};
			request.Headers.Authorization =
				new AuthenticationHeaderValue("Bearer", TokenOf(sessionId));
			using HttpResponseMessage response = await _client.SendAsync(request);
			response.EnsureSuccessStatusCode();
			return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["result"]!;
		}

		public sealed class AgentNotificationStream(
			HttpResponseMessage response,
			StreamReader reader) : IAsyncDisposable
		{
			public async Task<string> ReadNextDataAsync()
			{
				while (true)
				{
					string? line = await reader.ReadLineAsync()
						.WaitAsync(TimeSpan.FromSeconds(5));
					if (line?.StartsWith("data: ", StringComparison.Ordinal) == true)
					{
						return line[6..];
					}
				}
			}

			public async Task<IReadOnlyList<string>> ReadAllDataUntilClosedAsync()
			{
				List<string> data = [];
				try
				{
					while (await reader.ReadLineAsync() is { } line)
					{
						if (line.StartsWith("data: ", StringComparison.Ordinal))
						{
							data.Add(line[6..]);
						}
					}
				}
				catch (IOException)
				{
					// HttpListener closes an active SSE response by resetting the loopback stream.
				}

				return data;
			}

			public ValueTask DisposeAsync()
			{
				reader.Dispose();
				response.Dispose();
				return ValueTask.CompletedTask;
			}
		}

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await _monitor.DisposeAsync();
			_client.Dispose();
			await _temporaryDirectory.DisposeAsync();
		}

		private static SessionRecord Session(
			string id,
			string root,
			DateTimeOffset now) =>
			new(
				id,
				AgentKind.Codex,
				id,
				root,
				"codex --model gpt-5.4",
				"codex resume 019f6050-35a4-7951-9748-47239487c08d",
				SessionStatus.Stopped,
				now,
				now);
	}

	private sealed class FakeExecutableLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName) =>
			executableName.Equals("codex", StringComparison.OrdinalIgnoreCase)
				? @"C:\bin\codex.exe"
				: null;
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		private ProjectsDocument _document = document;

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_document);

		public Task SaveAsync(
			ProjectsDocument document,
			CancellationToken cancellationToken)
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

	private sealed class RecordingNotesStore : IProjectNotesStore
	{
		private readonly Dictionary<string, string> _textByRoot =
			new(StringComparer.OrdinalIgnoreCase);

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(TextOf(projectRootPath));

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			_textByRoot[projectRootPath] = text;
			return Task.CompletedTask;
		}

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			_textByRoot[projectRootPath] = TextOf(projectRootPath) + text;
			return Task.CompletedTask;
		}

		public string TextOf(string root) => _textByRoot.GetValueOrDefault(root, string.Empty);
	}
}
