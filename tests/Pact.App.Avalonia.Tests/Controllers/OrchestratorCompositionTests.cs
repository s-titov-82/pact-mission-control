using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.Agents;
using Pact.Core.Orchestrator;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class OrchestratorCompositionTests
{
	[Test]
	public async Task Orchestrator_tools_are_reachable_only_with_the_slot_credential()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		var asSession = await fixture.ListToolsAsync(fixture.SessionToken);
		var asOrchestrator = await fixture.ListToolsAsync(fixture.Credential);

		asSession.ShouldNotContain("pact_list_workspaces");
		asOrchestrator.ShouldContain("pact_list_workspaces");
		asOrchestrator.ShouldContain("pact_get_review_run");
		asOrchestrator.ShouldContain("pact_pause_review");
		asOrchestrator.ShouldContain("pact_resume_review");
	}

	[Test]
	public async Task Review_control_reports_an_unknown_run_through_the_live_endpoint()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		var result = await fixture.InvokeAsync(
			fixture.Credential,
			"pact_pause_review",
			new JsonObject { ["runId"] = "missing-run" });

		result.ShouldContain("unknown-review-run");
	}

	[Test]
	public async Task List_workspaces_reports_sessions_from_the_project()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		var result = await fixture.InvokeAsync(
			fixture.Credential,
			"pact_list_workspaces",
			new JsonObject());

		result.ShouldContain("project-a");
		result.ShouldContain("session-a");
		result.ShouldContain("project-b");
		result.ShouldContain("session-b");
		result.ShouldContain("ROOT");
	}

	[Test]
	public async Task Project_notes_can_be_read_replaced_and_read_through_the_live_endpoint()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		var before = JsonNode.Parse(await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_project_notes",
			new JsonObject { ["workspaceId"] = "project-a" }))!;

		await fixture.InvokeAsync(
			fixture.Credential,
			"pact_replace_project_notes",
			new JsonObject
			{
				["workspaceId"] = "project-a",
				["text"] = "replacement",
				["expectedRevision"] = before["revision"]!.GetValue<string>()
			});
		var after = JsonNode.Parse(await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_project_notes",
			new JsonObject { ["workspaceId"] = "project-a" }))!;

		after["text"]!.GetValue<string>().ShouldBe("replacement");
	}

	[Test]
	public async Task Project_notes_reject_a_paused_project()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		var result = await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_project_notes",
			new JsonObject { ["workspaceId"] = "paused-project" });

		result.ShouldContain("unknown-workspace");
	}

	[Test]
	public async Task Web_tools_exclude_paused_projects_and_resume_without_changing_selection()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		var selectedSession = fixture.Controller.ViewModel.SelectedSession;

		var listed = await fixture.InvokeAsync(
			fixture.Credential,
			"pact_list_web_tabs",
			new JsonObject());
		await fixture.InvokeAsync(
			fixture.Credential,
			"pact_resume_web_tab",
			new JsonObject { ["pageId"] = "project-web" });

		listed.ShouldContain("project-web");
		listed.ShouldNotContain("paused-project-web");
		fixture.Controller.ViewModel.SelectedSession.ShouldBeSameAs(selectedSession);
		var host = fixture.WebFactory.Hosts["project-web"];
		host.Calls.ShouldContain("hide");
		host.Calls.ShouldContain("navigate");
		host.Calls.ShouldNotContain("show");
		host.Calls.ShouldNotContain("focus");
	}

	[Test]
	public async Task Web_html_is_available_after_background_resume()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		await fixture.InvokeAsync(
			fixture.Credential,
			"pact_resume_web_tab",
			new JsonObject { ["pageId"] = "project-web" });

		var result = JsonNode.Parse(await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_web_tab_html",
			new JsonObject { ["pageId"] = "project-web" }))!;

		result["html"]!.GetValue<string>().ShouldBe("<main>live</main>");
		result["pageId"]!.GetValue<string>().ShouldBe("project-web");
	}

	[Test]
	public async Task Send_message_accepts_the_catalog_message_property()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		await fixture.InvokeAsync(
			fixture.Credential,
			"pact_send_message",
			new JsonObject
			{
				["sessionId"] = "session-a",
				["message"] = "status please"
			});

		string.Concat(fixture.Backends[1].Inputs).ShouldContain("status please");
	}

	[Test]
	public async Task Lock_policy_is_applied_to_the_live_slot_without_restart()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		await fixture.Controller.DeliverWorkstationLockPromptAsync(locked: true);
		var countAfterEnabled = fixture.OrchestratorBackend.Inputs.Count;
		await fixture.SaveAndReloadAsync(
			fixture.Record with { LockDetectionEnabled = false });
		await fixture.Controller.DeliverWorkstationLockPromptAsync(locked: true);

		countAfterEnabled.ShouldBeGreaterThan(0);
		fixture.OrchestratorBackend.Inputs.Count.ShouldBe(countAfterEnabled);
	}

	[Test]
	public async Task Disabling_the_slot_stops_it_and_clears_its_rights()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		await fixture.SaveAndReloadAsync(fixture.Record with { Enabled = false });

		fixture.Controller.IsOrchestratorRunning.ShouldBeFalse();
		(await fixture.ListToolsAsync(fixture.Credential)).ShouldBeEmpty();
	}

	[Test]
	public async Task Reissuing_the_credential_invalidates_the_old_one_immediately()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		await fixture.SaveAndReloadAsync(
			fixture.Record with { Credential = "reissued" });

		(await fixture.ListToolsAsync(fixture.Credential)).ShouldBeEmpty();
		(await fixture.ListToolsAsync("reissued")).ShouldContain("pact_list_workspaces");
	}

	[Test]
	public async Task An_intentional_tier_stop_does_not_start_a_replacement()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		var backendCount = fixture.Backends.Count;

		await fixture.Controller.StopOrchestratorAsync();
		await Task.Yield();

		fixture.Controller.IsOrchestratorRunning.ShouldBeFalse();
		fixture.Backends.Count.ShouldBe(backendCount);
	}

	[Test]
	public async Task Selecting_the_pinned_tier_presents_its_terminal()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		await fixture.Controller.SelectOrchestratorAsync();

		fixture.Controller.ViewModel.SelectedSession!.Record.Id
			.ShouldBe("pact-orchestrator");
		fixture.Host.ShownSessions.ShouldContain("pact-orchestrator");
	}

	[Test]
	public async Task Selection_in_the_pinned_tier_terminal_opens_selection_actions()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		await fixture.Controller.SelectOrchestratorAsync();
		fixture.Host.SelectedTextBlocker = CompletedSelection("orchestrator output");

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"pact-orchestrator",
				new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
	}

	[Test]
	public async Task Right_click_copy_in_the_pinned_tier_terminal_reaches_the_clipboard()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		await fixture.Controller.SelectOrchestratorAsync();

		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest("pact-orchestrator", "orchestrator output", Anchor: null));
		await WaitForEventTasksAsync(fixture);

		fixture.Clipboard.WrittenText.ShouldBe("orchestrator output");
	}

	[Test]
	public async Task Right_click_paste_reaches_the_pinned_tier_terminal()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		await fixture.Controller.SelectOrchestratorAsync();
		fixture.Clipboard.NextRead = Task.FromResult("pasted text");

		fixture.Host.RaisePasteRequested();
		await WaitForEventTasksAsync(fixture);

		string.Concat(fixture.OrchestratorBackend.Inputs)
			.ShouldContain("pasted text");
	}

	[Test]
	public async Task A_disabled_slot_cannot_be_started_from_the_tier()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		await fixture.SaveAndReloadAsync(fixture.Record with { Enabled = false });
		var backendCount = fixture.Backends.Count;

		await fixture.Controller.StartOrchestratorAsync();

		fixture.Controller.IsOrchestratorRunning.ShouldBeFalse();
		fixture.Controller.ViewModel.OrchestratorSlot.CanStart.ShouldBeFalse();
		fixture.Backends.Count.ShouldBe(backendCount);
	}

	[Test]
	public async Task An_unexpected_exit_restarts_the_enabled_slot()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();

		fixture.OrchestratorBackend.CompleteOutput();
		await fixture.WaitForBackendCountAsync(3);

		fixture.Controller.IsOrchestratorRunning.ShouldBeTrue();
	}

	[Test]
	public async Task Application_shutdown_does_not_restart_the_slot()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		var backendCount = fixture.Backends.Count;

		await fixture.Controller.ShutdownAsync();

		fixture.Controller.IsOrchestratorRunning.ShouldBeFalse();
		fixture.Backends.Count.ShouldBe(backendCount);
	}

	[Test]
	public async Task Closing_a_session_drops_its_retained_screen()
	{
		await using CompositionFixture fixture = new();
		await fixture.InitializeAsync();
		fixture.Host.RaiseScreenSnapshotReceived("session-a", "retained screen");
		(await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_session",
			new JsonObject
			{
				["sessionId"] = "session-a",
				["content"] = "screen"
			})).ShouldContain("retained screen");

		var session = fixture.Controller.ViewModel.Workspaces
			.Single(workspace => workspace.Id == "project-a")
			.Sessions.Single(candidate => candidate.Record.Id == "session-a");
		await fixture.Controller.CloseSessionAsync(session);

		(await fixture.InvokeAsync(
			fixture.Credential,
			"pact_get_session",
			new JsonObject
			{
				["sessionId"] = "session-a",
				["content"] = "screen"
			})).ShouldContain("session-a");
	}

	private static Task WaitForEventTasksAsync(CompositionFixture fixture) =>
		fixture.Controller.GetEventTasks()
			.WaitForIdleAsync()
			.WaitAsync(TimeSpan.FromSeconds(5));

	private static TaskCompletionSource<string> CompletedSelection(string text)
	{
		TaskCompletionSource<string> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.SetResult(text);
		return source;
	}

	private sealed class RecordingClipboardService : IClipboardService
	{
		public string? WrittenText { get; private set; }

		public Task<string> NextRead { get; set; } = Task.FromResult(string.Empty);

		public Task<string> GetTextAsync() => NextRead;

		public Task<bool> TrySetTextAsync(string text)
		{
			WrittenText = text;
			return Task.FromResult(true);
		}
	}

	private sealed class CompositionFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly HttpClient _client = new();
		private readonly ShellControllerTestBuilder _builder;

		public CompositionFixture()
		{
			var now = DateTimeOffset.UtcNow;
			var projectRoot = Path.Combine(_temporaryDirectory.Path, "project-a");
			Directory.CreateDirectory(projectRoot);
			SessionRecord session = new(
				"session-a",
				AgentKind.Codex,
				"Session A",
				projectRoot,
				"codex",
				ResumeCommand: null,
				SessionStatus.Stopped,
				now,
				now);
			ProjectRecord project = new(
				"project-a",
				"Project A",
				projectRoot,
				now,
				now,
				Notes: null)
			{
				Status = WorkspaceStatus.Active,
				ActiveItemId = session.Id,
				Sessions = [session],
				WebPages =
				[
					new WebPageRecord(
						"project-web",
						"Project web",
						"https://example.test/start",
						"https://example.test/resume",
						now,
						now)
				]
			};
			var secondRoot = Path.Combine(_temporaryDirectory.Path, "project-b");
			Directory.CreateDirectory(secondRoot);
			SessionRecord secondSession = new(
				"session-b",
				AgentKind.Claude,
				"Session B",
				secondRoot,
				"claude",
				ResumeCommand: null,
				SessionStatus.Stopped,
				now,
				now);
			ProjectRecord secondProject = new(
				"project-b",
				"Project B",
				secondRoot,
				now,
				now,
				Notes: null)
			{
				Status = WorkspaceStatus.Active,
				Sessions = [secondSession]
			};
			var pausedRoot = Path.Combine(_temporaryDirectory.Path, "paused-project");
			Directory.CreateDirectory(pausedRoot);
			ProjectRecord pausedProject = new(
				"paused-project",
				"Paused project",
				pausedRoot,
				now,
				now,
				Notes: null)
			{
				Status = WorkspaceStatus.Paused,
				WebPages =
				[
					new WebPageRecord(
						"paused-project-web",
						"Paused project web",
						"https://example.test/paused",
						"https://example.test/paused",
						now,
						now)
				]
			};
			MainWindowViewModel viewModel = new(
				new InMemoryProjectStore(
					new ProjectsDocument(1, [project, secondProject, pausedProject])),
				new EmptyNotesStore());
			var paths = new AppPaths(_temporaryDirectory.Path);
			SettingsFileStore settings = new(paths);
			Host = new FakeTerminalWebViewHost();
			_builder = new ShellControllerTestBuilder(
				viewModel,
				settings,
				paths,
				Host,
				() =>
				{
					FakeTerminalBackend backend = new()
					{
						ExitResponse =
							"To continue, run codex resume 019f6050-35a4-7951-9748-47239487c08d\r\n"
					};
					lock (Backends)
					{
						Backends.Add(backend);
					}

					return backend;
				});
			_builder
				.WithExecutableLocator(new InstalledAgentLocator())
				.WithWebPageHostFactory(WebFactory)
				.WithClipboard(Clipboard);
			Controller = _builder.Build();
			Record = OrchestratorRecord.CreateDefault() with
			{
				Enabled = true,
				LockDetectionEnabled = true,
				LaunchCommand = "hermes -p pact",
				WorkingDirectory = _temporaryDirectory.Path,
				Credential = Credential
			};
			Store = new OrchestratorStore(paths.OrchestratorPath);
		}

		public string Credential { get; } = "orchestrator-token";

		public AvaloniaMainShellController Controller { get; }

		public OrchestratorStore Store { get; }

		public OrchestratorRecord Record { get; }

		public FakeTerminalWebViewHost Host { get; }

		public FakeWebPageHostFactory WebFactory { get; } = new()
		{
			ConfigureHost = host => host.DocumentHtml = "<main>live</main>"
		};

		public RecordingClipboardService Clipboard { get; } = new();

		public List<FakeTerminalBackend> Backends { get; } = [];

		public FakeTerminalBackend OrchestratorBackend => Backends[0];

		public string SessionToken => Backends[1].LastStartOptions!
			.EnvironmentVariables!["PACT_AGENT_CONTROL_TOKEN"];

		public async Task InitializeAsync()
		{
			await Store.SaveAsync(Record, CancellationToken.None);
			await Controller.InitializeAsync(
				new Uri("file:///terminal.html"),
				TestContext.CurrentContext.CancellationToken);
			Backends.Count.ShouldBe(2);
		}

		public async Task SaveAndReloadAsync(OrchestratorRecord record)
		{
			await Store.SaveAsync(record, CancellationToken.None);
			(await Controller.ReloadExternalSettingsAsync(CancellationToken.None))
				.ShouldBeTrue();
		}

		public async Task WaitForBackendCountAsync(int expected)
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			while (true)
			{
				lock (Backends)
				{
					if (Backends.Count >= expected)
					{
						return;
					}
				}

				timeout.Token.ThrowIfCancellationRequested();
				await Task.Yield();
			}
		}

		public async Task<string[]> ListToolsAsync(string credential)
		{
			using var response = await PostAsync(
				credential,
				new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = 1,
					["method"] = "tools/list"
				});
			if (response.StatusCode == HttpStatusCode.Unauthorized)
			{
				return [];
			}

			var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
			return json["result"]!["tools"]!.AsArray()
				.Select(tool => tool!["name"]!.GetValue<string>())
				.ToArray();
		}

		public async Task<string> InvokeAsync(
			string credential,
			string toolName,
			JsonObject arguments)
		{
			using var response = await PostAsync(
				credential,
				new JsonObject
				{
					["jsonrpc"] = "2.0",
					["id"] = 2,
					["method"] = "tools/call",
					["params"] = new JsonObject
					{
						["name"] = toolName,
						["arguments"] = arguments
					}
				});
			response.StatusCode.ShouldBe(HttpStatusCode.OK);
			var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
			return json["result"]!["content"]![0]!["text"]!.GetValue<string>();
		}

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			_client.Dispose();
			await _temporaryDirectory.DisposeAsync();
		}

		private async Task<HttpResponseMessage> PostAsync(
			string credential,
			JsonObject body)
		{
			using HttpRequestMessage request = new(HttpMethod.Post, Controller.AgentControlAddress)
			{
				Content = new StringContent(
					body.ToJsonString(),
					Encoding.UTF8,
					"application/json")
			};
			request.Headers.Authorization =
				new AuthenticationHeaderValue("Bearer", credential);
			return await _client.SendAsync(request);
		}
	}

	private sealed class InstalledAgentLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName) =>
			executableName is "codex" or "hermes"
				? $@"C:\bin\{executableName}.exe"
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

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) => Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) => Task.CompletedTask;

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
