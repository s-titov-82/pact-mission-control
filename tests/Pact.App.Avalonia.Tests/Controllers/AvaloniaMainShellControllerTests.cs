using System.Collections.Concurrent;
using System.Text.Json;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Prompting;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaMainShellControllerTests
{
	[Test]
	public async Task Terminal_link_creates_and_selects_web_page_in_session_project()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var session = fixture.ViewModel.Workspaces.Single().Sessions[0];

		await fixture.Controller.OpenTerminalLinkAsync(
			session.Record.Id,
			new Uri("https://example.test/review/42"),
			TestContext.CurrentContext.CancellationToken);

		var page = fixture.ViewModel.SelectedWebPage.ShouldNotBeNull();
		page.ResumeUrl.ShouldBe("https://example.test/review/42");
		fixture.ViewModel.SelectedWorkspace.ShouldBe(fixture.ViewModel.Workspaces.Single());
		fixture.ViewModel.Workspaces.Single().WebPages.ShouldContain(page);
	}

	[Test]
	public async Task Terminal_link_creates_and_selects_root_web_page_for_root_session()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord rootSession = new(
			"root-session",
			AgentKind.Codex,
			"Root Codex",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"codex",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using ControllerFixture fixture = new(rootTabs: new RootTabsRecord(
			1,
			rootSession.Id,
			[rootSession],
			[],
			[]));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		await fixture.Controller.OpenTerminalLinkAsync(
			rootSession.Id,
			new Uri("https://example.test/root"),
			TestContext.CurrentContext.CancellationToken);

		var page = fixture.ViewModel.SelectedWebPage.ShouldNotBeNull();
		page.ResumeUrl.ShouldBe("https://example.test/root");
		page.IsRootItem.ShouldBeTrue();
		fixture.ViewModel.RootTabs.WebPages.ShouldContain(page);
		fixture.ViewModel.SelectedWorkspace.ShouldBeNull();
	}

	[Test]
	public async Task Initialize_selects_paused_root_session_without_starting_it()
	{
		var now = DateTimeOffset.UtcNow;
		var rootSession = new SessionRecord(
			"root-session",
			AgentKind.Codex,
			"Root Hermes",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using ControllerFixture fixture = new(rootTabs: new RootTabsRecord(
			1,
			rootSession.Id,
			[rootSession],
			[],
			[rootSession.Id]));

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		fixture.ViewModel.SelectedSession?.Record.Id.ShouldBe(rootSession.Id);
		fixture.Controller.IsPausedItemVisible.ShouldBeTrue();
		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
		fixture.Backends.ShouldBeEmpty();
	}

	[Test]
	public async Task Root_session_resumes_only_through_explicit_resume_action()
	{
		var now = DateTimeOffset.UtcNow;
		var rootSession = new SessionRecord(
			"root-session",
			AgentKind.Codex,
			"Root Hermes",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"hermes",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using ControllerFixture fixture = new(rootTabs: new RootTabsRecord(
			1,
			rootSession.Id,
			[rootSession],
			[],
			[rootSession.Id]));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var session = fixture.ViewModel.SelectedSession.ShouldNotBeNull();

		await fixture.Controller.SelectItemAsync(
			session,
			TestContext.CurrentContext.CancellationToken);
		fixture.Backends.ShouldBeEmpty();

		await fixture.Controller.ResumeRootSessionAsync(
			session,
			TestContext.CurrentContext.CancellationToken);

		session.IsManuallyPaused.ShouldBeFalse();
		fixture.Controller.IsPausedItemVisible.ShouldBeFalse();
		fixture.Controller.IsTerminalVisible.ShouldBeTrue();
		fixture.Backends.Count.ShouldBe(1);
	}

	[Test]
	public async Task Initialize_removes_abandoned_Pact_reviews_but_preserves_generic_reviews()
	{
		await using ControllerFixture fixture = new();
		Directory.CreateDirectory(Path.Combine(fixture.Root, ".pact-reviews", "abandoned"));
		Directory.CreateDirectory(Path.Combine(fixture.Root, ".reviews", "someone-else"));

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		Directory.Exists(Path.Combine(fixture.Root, ".pact-reviews")).ShouldBeFalse();
		Directory.Exists(Path.Combine(fixture.Root, ".reviews")).ShouldBeTrue();
	}

	[Test]
	public async Task Terminal_loader_covers_a_starting_runtime_until_its_first_stable_screen()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Backends[0].EmitOutput("first-ready");
		await fixture.Controller.Runtimes["session-1"].InitialOutputTask!;
		fixture.Host.RaiseScreenSnapshotReceived("session-1", "first-ready", stable: true);
		List<bool> loadingChanges = [];
		fixture.Controller.TerminalLoadingChanged += (_, isLoading) => loadingChanges.Add(isLoading);
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];

		await fixture.Controller.SelectSessionAsync(second, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		loadingChanges.ShouldBe([true]);

		fixture.Backends[^1].EmitOutput("starting");
		await fixture.Controller.Runtimes["session-2"].InitialOutputTask!;
		loadingChanges.ShouldBe([true]);

		fixture.Host.RaiseScreenSnapshotReceived(second.Record.Id, "ready", stable: false);
		loadingChanges.ShouldBe([true]);

		fixture.Host.RaiseScreenSnapshotReceived(second.Record.Id, string.Empty, stable: true);
		loadingChanges.ShouldBe([true]);

		fixture.Host.RaiseScreenSnapshotReceived(second.Record.Id, "ready", stable: true);
		loadingChanges.ShouldBe([true, false]);

		loadingChanges.Clear();
		await fixture.Controller.SelectSessionAsync(first, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		loadingChanges.ShouldBeEmpty();
	}

	[Test]
	public async Task Rapid_webview_resizes_are_observed_and_finish_at_the_latest_dimensions()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		TaskCompletionSource firstResizeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource releaseFirstResize = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var resizeCount = 0;
		fixture.Backends[0].ResizeHandler = async (_, _) =>
		{
			if (Interlocked.Increment(ref resizeCount) == 1)
			{
				firstResizeEntered.SetResult();
				await releaseFirstResize.Task;
			}
		};

		fixture.Host.RaiseResize("session-1", 100, 30);
		await firstResizeEntered.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);
		fixture.Host.RaiseResize("session-1", 101, 37);
		var drain = fixture.Controller.GetEventTasks().CompleteAndDrainAsync();

		drain.IsCompleted.ShouldBeFalse();
		releaseFirstResize.SetResult();
		await drain;

		fixture.Backends[0].ResizeRequests.ShouldBe([(100, 30), (101, 37)]);
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "Viewport").Value.ShouldBe("101 × 37");
		fixture.Controller.StatusText.ShouldBeNull();
	}

	[Test]
	public async Task External_process_metrics_poll_only_for_the_selected_live_terminal_when_enabled()
	{
		var sampledAt = new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);
		ManualTimeProvider time = new(sampledAt);
		FakeProcessTreeSnapshotReader reader = new(
			new ProcessTreeSnapshot(
				42,
				2,
				1024 * 1024,
				new Dictionary<int, TimeSpan> { [42] = TimeSpan.FromSeconds(1) },
				sampledAt),
			new ProcessTreeSnapshot(
				42,
				2,
				2 * 1024 * 1024,
				new Dictionary<int, TimeSpan> { [42] = TimeSpan.FromSeconds(1.2) },
				sampledAt.AddSeconds(2)));
		await using ControllerFixture fixture = new(
			timeProvider: time,
			processTreeSnapshotReader: reader,
			terminalProcessId: 42);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.SetExternalProcessMetricsEnabled(true);
		time.Advance(TimeSpan.Zero);

		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "Working set").Value.ShouldBe("1.0 MiB");
		reader.RootProcessIds.ShouldBe([42]);

		time.Advance(TimeSpan.FromSeconds(2));
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "CPU").Value.ShouldNotBe("Sampling…");

		await fixture.Controller.SelectWebPageAsync(
			fixture.ViewModel.WebPages.Single(),
			TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromSeconds(2));

		reader.RootProcessIds.ShouldBe([42, 42]);
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.ShouldNotContain(row => row.Label == "Working set");
	}

	[Test]
	public async Task External_process_metrics_show_selected_web_page_and_shared_runtime_groups()
	{
		var sampledAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
		FakeWebProcessMetricsReader reader = new(new WebProcessMetricsSnapshot(
			new ProcessSetSnapshot(
				2,
				2 * 1024 * 1024,
				new Dictionary<int, TimeSpan> { [20] = TimeSpan.FromSeconds(1) },
				sampledAt),
			new ProcessSetSnapshot(
				4,
				8 * 1024 * 1024,
				new Dictionary<int, TimeSpan> { [100] = TimeSpan.FromSeconds(2) },
				sampledAt)));
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			webProcessMetricsSnapshotReader: reader);
		fixture.Controller.WebPageHostFactory = new FakeWebPageHostFactory();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.SetExternalProcessMetricsEnabled(true);
		await reader.WaitForReadAsync();
		await WaitForDetailRowAsync(fixture.Controller, "Page renderers");

		reader.PageIds.ShouldBe(["web-1"]);
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "Page working set").Value.ShouldBe("2.0 MiB");
		fixture.Controller.SelectedTabDetails.Rows
			.Single(row => row.Label == "Shared working set").Value.ShouldBe("8.0 MiB");
	}

	[Test]
	public async Task External_metrics_refresh_preserves_selected_details_and_unchanged_rows()
	{
		var sampledAt = new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
		ManualTimeProvider time = new(sampledAt);
		using QueuedUiTaskDispatcher dispatcher = new();
		FakeWebProcessMetricsReader reader = new(
			WebMetricsSnapshot(sampledAt, pageCpuSeconds: 1),
			WebMetricsSnapshot(sampledAt.AddSeconds(2), pageCpuSeconds: 1.2));
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			timeProvider: time,
			uiTaskDispatcher: dispatcher,
			webProcessMetricsSnapshotReader: reader);
		fixture.Controller.WebPageHostFactory = new FakeWebPageHostFactory();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.SetExternalProcessMetricsEnabled(true);
		await reader.WaitForReadCountAsync(1);
		await dispatcher.RunUntilAsync(() =>
			fixture.Controller.SelectedTabDetails?.Rows.Any(
				row => row.Label == "Page renderers") == true);
		var details = fixture.Controller.SelectedTabDetails.ShouldNotBeNull();
		var address = details.Rows.Single(row => row.Label == "Address");

		await time.WaitForTimerCountAsync(TimeSpan.FromSeconds(2), 1)
			.WaitAsync(TimeSpan.FromSeconds(1));
		time.Advance(TimeSpan.FromSeconds(2));
		await reader.WaitForReadCountAsync(2);
		await dispatcher.RunUntilAsync(() =>
			fixture.Controller.SelectedTabDetails?.Rows
				.FirstOrDefault(row => row.Label == "Page CPU")?.Value is { } value
			&& value != "Sampling…");

		fixture.Controller.SelectedTabDetails.ShouldBeSameAs(details);
		fixture.Controller.SelectedTabDetails.Rows
			.Single(row => row.Label == "Address").ShouldBeSameAs(address);
	}

	[Test]
	public async Task External_process_metrics_do_not_load_a_paused_web_page()
	{
		FakeWebProcessMetricsReader reader = new();
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			webProcessMetricsSnapshotReader: reader);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.SetExternalProcessMetricsEnabled(true);

		reader.PageIds.ShouldBeEmpty();
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "External metrics").Value.ShouldBe("Not loaded");
	}

	[Test]
	public async Task CopyWebPageAddressWritesFullResumeUrlToClipboard()
	{
		await using ControllerFixture fixture = new();
		var now = DateTimeOffset.UtcNow;
		WebPageViewModel page = new(new WebPageRecord(
			"web-copy",
			"GitLab",
			"https://gitlab.example/project",
			"https://gitlab.example/project/-/tags",
			now,
			now));

		await fixture.Controller.CopyWebPageAddressAsync(page);

		fixture.Clipboard.WrittenText.ShouldBe(page.ResumeUrl);
	}

	[Test]
	public async Task CopyWebPageAddressReportsClipboardFailure()
	{
		await using ControllerFixture fixture = new();
		fixture.Clipboard.NextWriteResult = false;
		var now = DateTimeOffset.UtcNow;
		WebPageViewModel page = new(new WebPageRecord(
			"web-copy",
			"GitLab",
			"https://gitlab.example/project",
			"https://gitlab.example/project/-/tags",
			now,
			now));

		await fixture.Controller.CopyWebPageAddressAsync(page);

		fixture.Controller.StatusText.ShouldBe("Could not copy the web page address to the clipboard.");
	}

	[Test]
	public async Task CopyTerminalSelectionReportsClipboardFailure()
	{
		await using ControllerFixture fixture = new();
		fixture.Host.SelectedTextBlocker = CompletedSelection("selected text");
		fixture.Clipboard.NextWriteResult = false;

		await fixture.Controller.CopyAsync();

		fixture.Controller.StatusText.ShouldBe("Could not copy the terminal selection to the clipboard.");
	}

	[Test]
	public async Task Concurrent_right_click_paste_requests_write_normalized_bracketed_text_once()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		TaskCompletionSource<string> clipboardRead = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Clipboard.NextRead = clipboardRead.Task;

		fixture.Host.RaisePasteRequested();
		fixture.Host.RaisePasteRequested();
		clipboardRead.SetResult("first\r\nsecond\rthird");

		await WaitForEventTasksAsync(fixture);
		fixture.Backends[0].Inputs.ShouldBe(["\u001b[200~first\nsecond\nthird\u001b[201~"]);

		fixture.Clipboard.NextRead = Task.FromResult("again");
		fixture.Host.RaisePasteRequested();
		await WaitForEventTasksAsync(fixture);
		fixture.Backends[0].Inputs.ShouldBe(
			[
				"\u001b[200~first\nsecond\nthird\u001b[201~",
				"\u001b[200~again\u001b[201~"
			]);
	}

	[Test]
	public async Task Clipboard_paste_is_no_op_without_selected_session_and_never_starts_a_stopped_session()
	{
		await using ControllerFixture fixture = new();
		await fixture.ViewModel.LoadAsync(TestContext.CurrentContext.CancellationToken);
		fixture.Clipboard.NextRead = Task.FromResult("text");

		await Should.ThrowAsync<InvalidOperationException>(fixture.Controller.PasteAsync);

		fixture.Backends.ShouldBeEmpty();
		fixture.ViewModel.SelectedSession = null;
		await fixture.Controller.PasteAsync();
		fixture.Backends.ShouldBeEmpty();
	}

	[Test]
	public async Task Clipboard_paste_rejects_scenario_locked_selected_session()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var session = fixture.ViewModel.SelectedSession.ShouldNotBeNull();
		session.LockForScenario("run-1");
		fixture.Clipboard.NextRead = Task.FromResult("blocked");

		await Should.ThrowAsync<InvalidOperationException>(fixture.Controller.PasteAsync);

		fixture.Backends[0].Inputs.ShouldBeEmpty();
	}

	[Test]
	public async Task GetActiveSessions_uses_live_controllers_instead_of_saved_status()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();

		fixture.Controller.GetActiveSessions().Select(session => session.Record.Id)
			.ShouldBe(["session-1"]);
		fixture.Controller.GetActiveSessions(workspace.Sessions).Select(session => session.Record.Id)
			.ShouldBe(["session-1"]);
		fixture.Controller.HasActiveTerminalProcess(workspace.Sessions[1]).ShouldBeFalse();

		await fixture.Controller.SelectSessionAsync(
			workspace.Sessions[1],
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);

		fixture.Controller.GetActiveSessions().Select(session => session.Record.Id)
			.ShouldBe(["session-1", "session-2"]);
	}

	[Test]
	public async Task ReloadExternalSettings_updates_catalogs_and_invalidates_cached_git_panel()
	{
		await using ControllerFixture fixture = new();
		Directory.CreateDirectory(Path.Combine(fixture.Root, ".git"));
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var workspace = fixture.ViewModel.Workspaces.Single();
		await fixture.Controller.SelectWorkspaceAsync(workspace);
		var firstPanel = fixture.Controller.CurrentGitPanel.ShouldBeOfType<GitPanelViewModel>();

		await fixture.SettingsFileStore.SaveAsync(
			"shell-profiles.json",
								 /*lang=json,strict*/
								 """
            [
              {
                "id": "custom",
                "kind": "custom",
                "displayName": "Reloaded profile",
                "commandTemplate": "custom",
                "defaultShell": "pwsh"
              }
            ]
            """,
			TestContext.CurrentContext.CancellationToken);

		(await fixture.Controller.ReloadExternalSettingsAsync(TestContext.CurrentContext.CancellationToken)).ShouldBeTrue();
		fixture.ViewModel.ShellProfiles.ShouldHaveSingleItem().DisplayName.ShouldBe("Reloaded profile");
		fixture.Controller.CurrentGitPanel.ShouldBeNull();

		await fixture.Controller.SelectWorkspaceAsync(workspace);
		fixture.Controller.CurrentGitPanel.ShouldNotBeSameAs(firstPanel);
	}

	[Test]
	public async Task ReloadExternalSettings_reports_the_first_failure()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await File.WriteAllTextAsync(
			fixture.Paths.ShellProfilesPath,
			"{",
			TestContext.CurrentContext.CancellationToken);

		(await fixture.Controller.ReloadExternalSettingsAsync(TestContext.CurrentContext.CancellationToken)).ShouldBeFalse();
		fixture.Controller.StatusText.ShouldStartWith("Settings load failed: shell-profiles.json:");
		fixture.ViewModel.ShellProfiles.ShouldNotBeEmpty();
	}

	[Test]
	public async Task ReloadExternalSettingsAppliesMonitorRulesToAnAlreadyLoadedPage()
	{
		await using ControllerFixture fixture = new(withSessions: false);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		var host = factory.Hosts[page.Record.Id];
		host.EvaluationQueries.ShouldBeEmpty();
		await SaveMonitorRulesAsync(fixture, MonitorRule());

		(await fixture.Controller.ReloadExternalSettingsAsync(
			TestContext.CurrentContext.CancellationToken)).ShouldBeTrue();
		await host.WaitForNonNullQueryAsync().WaitAsync(TimeSpan.FromSeconds(5));

		host.EvaluationQueries.ShouldContain(query => query != null);
	}

	[Test]
	public async Task TestWebMonitorRuleOnCurrentTabReturnsErrorForSelectedUnloadedPage()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		fixture.ViewModel.SelectedWebPage.ShouldBeSameAs(page);
		page.IsBrowserLoaded.ShouldBeFalse();

		var result =
			await fixture.Controller.TestWebMonitorRuleOnCurrentTabAsync(
				MonitorRule(),
				TestContext.CurrentContext.CancellationToken);

		result.UrlMatched.ShouldBeFalse();
		result.Activity.ShouldBeNull();
		result.Revision.ShouldBeNull();
		result.Error.ShouldBe("No selected loaded web tab is available for testing.");
	}

	[Test]
	public async Task TestWebMonitorRuleOnCurrentTabUsesLoadedSelectedPageWithoutMutatingLiveState()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host => host.EvaluationResults.Enqueue(Task.FromResult(
				Evaluation("https://example.test", activity: true, revision: "1842")))
		};
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		var host = factory.Hosts["web-1"];
		page.SetMonitorUnread(true);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		var beforeStatus = page.MonitorStatus;
		var beforeSnapshot =
			(await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None))
			.ShouldNotBeNull();
		var beforeRules = await File.ReadAllTextAsync(
			fixture.Paths.WebMonitorRulesPath,
			TestContext.CurrentContext.CancellationToken);

		var result =
			await fixture.Controller.TestWebMonitorRuleOnCurrentTabAsync(
				MonitorRule(),
				TestContext.CurrentContext.CancellationToken);

		result.UrlMatched.ShouldBeTrue();
		result.Activity.ShouldBe(true);
		result.Revision.ShouldBe("1842");
		result.Error.ShouldBeNull();
		host.EvaluationQueries.ShouldHaveSingleItem()
			.ShouldNotBeNull();
		page.MonitorStatus.ShouldBe(beforeStatus);
		(await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None))
			.ShouldBe(beforeSnapshot);
		(await File.ReadAllTextAsync(
			fixture.Paths.WebMonitorRulesPath,
			TestContext.CurrentContext.CancellationToken))
			.ShouldBe(beforeRules);
	}

	[Test]
	public async Task SelectedPageAndWindowFactsAcknowledgeRetainedUnread()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		await SaveMonitorRulesAsync(fixture, MonitorRule());
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		fixture.Controller.SelectedTabDetails.ShouldNotBeNull()
			.Rows.Single(row => row.Label == "Address").Value.ShouldBe(page.ResumeUrl);
		fixture.Controller.SelectedTabDetails.Rows.Single(row => row.Label == "Monitor")
			.Value.ShouldBe("Unread");

		fixture.Controller.SetWebMonitorWindowFacts(visible: true, active: true);
		await WaitForMonitorStatusAsync(page, WebMonitorStatus.None);
		page.MonitorStatus.ShouldBe(WebMonitorStatus.None);
		fixture.Controller.SelectedTabDetails.Rows.Single(row => row.Label == "Monitor")
			.Value.ShouldBe("None");
		await fixture.MonitorCoordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			TestContext.CurrentContext.CancellationToken);

		((await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None))?.Unread)
			.ShouldBe(false);
	}

	[Test]
	public async Task CoordinatorStatusAndDiagnosticCallbacksTargetOnlyTheirOwningPage()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(
			withSecondWebPage: true,
			timeProvider: time);
		await SaveMonitorRulesAsync(fixture, MonitorRule());
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		TaskCompletionSource<WebMonitorEvaluation> evaluation =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(evaluation.Task);
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		TaskCompletionSource<WebMonitorDiagnosticEventArgs> diagnostic =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.MonitorCoordinator.DiagnosticChanged += (_, change) =>
		{
			if (change.WebPageId == "web-1")
			{
				diagnostic.TrySetResult(change);
			}
		};
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var first = fixture.ViewModel.WebPages.Single(page => page.Record.Id == "web-1");
		var second = fixture.ViewModel.WebPages.Single(page => page.Record.Id == "web-2");

		var settleTimer =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await fixture.Controller.SelectWebPageAsync(first, CancellationToken.None);
		await settleTimer.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromMilliseconds(500));
		var host = factory.Hosts["web-1"];
		await host.WaitForEvaluationAsync(1);
		evaluation.SetException(new InvalidOperationException("Evaluation failed."));
		await diagnostic.Task.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		first.MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		first.MonitorDiagnostic.ShouldNotBeNull();
		second.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
		second.MonitorDiagnostic.ShouldBeNull();
	}

	[Test]
	public async Task ShutdownUnregistersWebPagesBeforeHostsAndLeavesCoordinatorUsable()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		var host = factory.Hosts["web-1"];
		var pageUnregisteredBeforeHost = false;
		host.Disposing = () =>
		{
			var result = fixture.MonitorCoordinator
				.TestAsync("web-1", MonitorRule(), CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			result.Error.ShouldBe("No loaded web tab is registered for testing.");
			fixture.MonitorCoordinator
				.SetRulesAsync([], CancellationToken.None)
				.GetAwaiter()
				.GetResult();
			pageUnregisteredBeforeHost = true;
		};

		await fixture.Controller.ShutdownAsync();

		pageUnregisteredBeforeHost.ShouldBeTrue();
		(await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
	}

	[Test]
	public async Task ControllerDisposalWaitsForInFlightMonitoringBeforeDisposingWebViews()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			timeProvider: time);
		await SaveMonitorRulesAsync(fixture, MonitorRule());
		TaskCompletionSource<WebMonitorEvaluation> evaluation =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(evaluation.Task);
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		var settleTimer =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var host = factory.Hosts["web-1"];
		await settleTimer.WaitAsync(TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationAsync(1);

		var dispose = fixture.Controller.DisposeAsync().AsTask();
		dispose.IsCompleted.ShouldBeFalse();
		host.WaitForDisposalAsync().IsCompleted.ShouldBeFalse();
		host.Calls.ShouldNotContain("dispose");

		evaluation.SetResult(
			Evaluation("https://example.test", activity: false, revision: "1"));
		await dispose;
		await host.WaitForDisposalAsync();

		host.Calls.ShouldContain("dispose");
	}

	[Test]
	public void WebPageMonitorStatusNotifiesAndUsesActivityUnreadPausedNonePriority()
	{
		var now = DateTimeOffset.UtcNow;
		WebPageViewModel page = new(new WebPageRecord(
			"web-status",
			"Status",
			"https://example.test",
			"https://example.test",
			now,
			now));
		List<string?> changed = [];
		page.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

		page.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
		page.SetMonitorUnread(true);
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		page.SetMonitorStatus(WebMonitorStatus.Activity);
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Activity);
		page.SetLoading(true);
		page.ShowMonitorActivity.ShouldBeFalse();
		page.SetLoading(false);
		page.SetMonitorStatus(WebMonitorStatus.None);
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
		page.SetBrowserLoaded(true);
		page.MonitorStatus.ShouldBe(WebMonitorStatus.None);
		page.SetMonitorDiagnostic("web-1 / rule-1 / timeout");

		page.MonitorDiagnostic.ShouldBe("web-1 / rule-1 / timeout");
		page.MonitorToolTip.ShouldContain("web-1 / rule-1 / timeout");
		changed.ShouldContain(nameof(WebPageViewModel.MonitorStatus));
		changed.ShouldContain(nameof(WebPageViewModel.IsMonitorActive));
		changed.ShouldContain(nameof(WebPageViewModel.HasMonitorUnread));
		changed.ShouldContain(nameof(WebPageViewModel.MonitorDiagnostic));
		changed.ShouldContain(nameof(WebPageViewModel.MonitorToolTip));
		changed.ShouldContain(nameof(WebPageViewModel.ShowMonitorActivity));
	}

	[Test]
	public async Task AddProjectFromDirectoryCreatesSelectsAndRecordsRecentDirectory()
	{
		await using ControllerFixture fixture = new();
		var directory = Path.Combine(fixture.Root, "added-project");
		Directory.CreateDirectory(directory);

		await fixture.Controller.AddProjectFromDirectoryAsync(
			directory,
			TestContext.CurrentContext.CancellationToken);

		var selected = fixture.ViewModel.SelectedWorkspace.ShouldBeOfType<WorkspaceViewModel>();
		selected.RootPath.ShouldBe(Path.GetFullPath(directory));
		fixture.ViewModel.Workspaces.ShouldContain(workspace =>
			string.Equals(
				workspace.RootPath,
				Path.GetFullPath(directory),
				StringComparison.OrdinalIgnoreCase));
		(await fixture.RecentDirectoryStore.LoadAsync(TestContext.CurrentContext.CancellationToken)).ShouldHaveSingleItem().ShouldBe(Path.GetFullPath(directory));
	}

	[Test]
	public async Task TerminalInputEventRecordsProductionControllerBoundary()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];

		fixture.Host.RaiseInputReceived(session.Record.Id, "\u001b[c");
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.DiagnosticSnapshot.Any(entry =>
			entry.Phase == "terminal-input-received"
			&& entry.Detail?.Contains($"session={session.Record.Id}", StringComparison.Ordinal) == true)
			.ShouldBeTrue();
		fixture.Controller.DiagnosticSnapshot.Any(entry =>
			entry.Phase == "terminal-input-processed"
			&& entry.Detail?.Contains("runtimeActive=True", StringComparison.Ordinal) == true)
			.ShouldBeTrue();
	}

	[Test]
	public async Task TerminalInputEventIsIgnoredAfterShutdownStarts()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		await fixture.Controller.ShutdownAsync();
		var inputCount = fixture.Backends[0].Inputs.Count;

		fixture.Host.RaiseInputReceived(session.Record.Id, "late-input");

		fixture.Backends[0].Inputs.Count.ShouldBe(inputCount);
		fixture.Controller.DiagnosticSnapshot.Any(entry =>
			entry.Phase == "terminal-input-received"
			&& entry.Detail?.Contains("length=10", StringComparison.Ordinal) == true)
			.ShouldBeFalse();
	}

	[Test]
	public async Task FirstBrowserActivationShowsHostBeforeNavigation()
	{
		await using ControllerFixture fixture = new();
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var page = fixture.ViewModel.WebPages.Single();
		page.IsBrowserLoaded.ShouldBeFalse();

		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);

		var host = factory.Hosts[page.Record.Id];
		host.Calls.Count(call => call == "navigate").ShouldBe(1);
		host.Calls.Count(call => call == "show").ShouldBe(1);
		host.Calls.Count(call => call == "focus").ShouldBe(1);
		page.IsBrowserLoaded.ShouldBeTrue();
		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
	}

	[Test]
	public async Task InitializeSweepsActiveAndPausedSnapshotsAndRestoresUnreadWithoutCreatingHosts()
	{
		await using ControllerFixture fixture = new(includePausedWorkspace: true);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-paused", unread: true),
			TestContext.CurrentContext.CancellationToken);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("orphan", unread: true),
			TestContext.CurrentContext.CancellationToken);

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		(await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		(await fixture.SnapshotStore.LoadAsync("web-paused", CancellationToken.None)).ShouldNotBeNull();
		(await fixture.SnapshotStore.LoadAsync("orphan", CancellationToken.None)).ShouldBeNull();
		fixture.ViewModel.WebPages.Single().MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		fixture.ViewModel.PausedWorkspaces.Single().WebPages.Single().MonitorStatus
			.ShouldBe(WebMonitorStatus.Unread);
		factory.Hosts.ShouldBeEmpty();
	}

	[Test]
	public async Task SnapshotSweepFailureDoesNotPreventRulesLoadsOrPersistedPageActivation()
	{
		FakeWebMonitorSnapshotReader snapshotReader = new()
		{
			SweepFailure = new IOException(@"snapshot sweep failed at C:\private\retained.json")
		};
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			snapshotReader: snapshotReader);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		snapshotReader.LoadedWebPageIds.ShouldContain("web-1");
		fixture.Controller.DiagnosticSnapshot
			.ShouldContain(entry => entry.Phase == "web-monitor-rules-applied");
		var failure = fixture.Controller.DiagnosticSnapshot
			.Single(entry => entry.Phase == "web-monitor-snapshot-sweep-failed");
		var detail = failure.Detail.ShouldNotBeNull();
		detail.ShouldBe("category=io");
		detail.ShouldNotContain("private", Case.Insensitive);
		(fixture.ViewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		factory.Hosts.ShouldContainKey("web-1");
	}

	[Test]
	public async Task LockedRealSnapshotDoesNotBlockCoordinatorRegistrationOrPersistedPageActivation()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		var snapshotPath = Path.Combine(
			fixture.Paths.WebMonitorSnapshotsDirectory,
			"web-1.json");
		await using var snapshotLock = File.Open(
			snapshotPath,
			FileMode.Open,
			FileAccess.Read,
			FileShare.None);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		var page = fixture.ViewModel.WebPages.Single();
		page.MonitorStatus.ShouldBe(WebMonitorStatus.None);
		(fixture.ViewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		factory.Hosts.ShouldContainKey("web-1");
		fixture.Controller.GetWebPageHostForDiagnostics("web-1").ShouldNotBeNull();
		fixture.Controller.DiagnosticSnapshot
			.ShouldContain(entry =>
				entry.Phase == "web-monitor-snapshot-sweep-failed"
				&& entry.Detail == "category=io");
		fixture.Controller.DiagnosticSnapshot
			.ShouldContain(entry =>
				entry.Phase == "web-monitor-register"
				&& entry.Detail == "page=web-1");
	}

	[Test]
	public async Task SnapshotLoadFailureIsolatedToPageWhileAnotherUnreadRestoresAndSelectionActivates()
	{
		FakeWebMonitorSnapshotReader snapshotReader = new();
		snapshotReader.LoadFailures.Add(
			"web-1",
			new UnauthorizedAccessException(
				@"snapshot denied at C:\private\web-1.json"));
		snapshotReader.Snapshots.Add(
			"web-2",
			Snapshot("web-2", unread: true));
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			withSecondWebPage: true,
			snapshotReader: snapshotReader);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		var failed =
			fixture.ViewModel.WebPages.Single(page => page.Record.Id == "web-1");
		var restored =
			fixture.ViewModel.WebPages.Single(page => page.Record.Id == "web-2");
		failed.MonitorStatus.ShouldBe(WebMonitorStatus.None);
		restored.MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		snapshotReader.LoadedWebPageIds.ShouldBe(["web-1", "web-2"]);
		var failure = fixture.Controller.DiagnosticSnapshot
			.Single(entry => entry.Phase == "web-monitor-snapshot-load-failed");
		var detail = failure.Detail.ShouldNotBeNull();
		detail.ShouldBe("page=web-1;category=access");
		detail.ShouldNotContain("private", Case.Insensitive);
		(fixture.ViewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
		factory.Hosts.ShouldContainKey("web-1");
	}

	[Test]
	public async Task SnapshotRestoreDoesNotSwallowCancellation()
	{
		FakeWebMonitorSnapshotReader snapshotReader = new()
		{
			SweepFailure = new OperationCanceledException("snapshot restore canceled")
		};
		await using ControllerFixture fixture = new(snapshotReader: snapshotReader);

		await Should.ThrowAsync<OperationCanceledException>(
			() => fixture.Controller.InitializeAsync(
				new Uri("file:///terminal.html"),
				TestContext.CurrentContext.CancellationToken));
	}

	[Test]
	public async Task PausedPageSnapshotLoadFailureKeepsPausedProjection()
	{
		FakeWebMonitorSnapshotReader snapshotReader = new();
		snapshotReader.LoadFailures.Add(
			"web-paused",
			new JsonException("invalid retained snapshot payload"));
		await using ControllerFixture fixture = new(
			includePausedWorkspace: true,
			snapshotReader: snapshotReader);

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		var paused =
			fixture.ViewModel.PausedWorkspaces.Single().WebPages.Single();
		paused.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
		fixture.Controller.DiagnosticSnapshot
			.ShouldContain(entry =>
				entry.Phase == "web-monitor-snapshot-load-failed"
				&& entry.Detail == "page=web-paused;category=data");
	}

	[Test]
	public async Task InitializeLoadsRulesBeforeRegisteringThePersistedActiveWebPage()
	{
		await using ControllerFixture fixture = new(activeItemId: "web-1");
		await SaveMonitorRulesAsync(fixture, MonitorRule());
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
			}
		};
		fixture.Controller.WebPageHostFactory = factory;

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var host = factory.Hosts["web-1"];
		await host.WaitForNonNullQueryAsync().WaitAsync(TimeSpan.FromSeconds(5));

		host.EvaluationQueries.ShouldContain(query => query != null);
		host.Calls.IndexOf("subscribe-navigation-started")
			.ShouldBeLessThan(host.Calls.IndexOf("read-source"));
		host.Calls.IndexOf("read-source").ShouldBeLessThan(host.Calls.IndexOf("navigate"));
	}

	[Test]
	public async Task StartupDefersSelectedPageAndWindowFactsUntilRulesAreAppliedBeforeRegistration()
	{
		await using ControllerFixture fixture = new(activeItemId: "web-1");
		await SaveMonitorRulesAsync(fixture, MonitorRule());
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;

		fixture.Controller.SetWebMonitorWindowFacts(visible: true, active: true);
		fixture.Controller.DiagnosticSnapshot
			.ShouldNotContain(entry => entry.Phase == "web-monitor-presentation-facts");

		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);

		var phases = fixture.Controller.DiagnosticSnapshot
			.Select(entry => entry.Phase)
			.Where(phase => phase.StartsWith("web-monitor-", StringComparison.Ordinal))
			.ToArray();
		var rulesApplied = Array.IndexOf(phases, "web-monitor-rules-applied");
		var presentationFacts = Array.IndexOf(phases, "web-monitor-presentation-facts");
		var registration = Array.IndexOf(phases, "web-monitor-register");
		rulesApplied.ShouldBeGreaterThanOrEqualTo(0);
		presentationFacts.ShouldBeGreaterThan(rulesApplied);
		registration.ShouldBeGreaterThan(presentationFacts);
		(fixture.ViewModel.SelectedWebPage?.Record.Id).ShouldBe("web-1");
	}

	[Test]
	public async Task NavigationStartedSuspendsEvaluationUntilCompletionAndDomSettle()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(timeProvider: time);
		var rule = MonitorRule() with { PollIntervalSeconds = 6 };
		await SaveMonitorRulesAsync(fixture, rule);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot(
				"web-1",
				unread: false,
				ruleFingerprint: WebMonitorRuleCompiler.Compile(rule).Fingerprint),
			TestContext.CurrentContext.CancellationToken);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "2")));
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		var host = factory.Hosts[page.Record.Id];

		var initialSettleTimer =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		host.RaiseNavigationCompleted();
		await initialSettleTimer.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var pollingTimer =
			time.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(rule.PollIntervalSeconds));
		time.Advance(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationAsync(1).WaitAsync(TimeSpan.FromSeconds(5));
		await pollingTimer.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var baselineEvaluationCount = host.EvaluationQueries.Count;
		baselineEvaluationCount.ShouldBe(1);

		host.RaiseNavigationStarted();
		time.Advance(TimeSpan.FromSeconds(rule.PollIntervalSeconds + 1));
		await Task.Yield();
		host.EvaluationQueries.Count.ShouldBe(baselineEvaluationCount);

		var settleTimerCreated =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		host.RaiseNavigationCompleted();
		await settleTimerCreated.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromMilliseconds(499));
		await Task.Yield();
		host.EvaluationQueries.Count.ShouldBe(baselineEvaluationCount);

		time.Advance(TimeSpan.FromMilliseconds(1));
		await host.WaitForEvaluationAsync(baselineEvaluationCount + 1)
			.WaitAsync(TimeSpan.FromSeconds(5));
		host.EvaluationQueries.Count.ShouldBe(baselineEvaluationCount + 1);

		var navigationPhases = fixture.Controller.DiagnosticSnapshot
			.Where(entry => entry.Phase.StartsWith(
				"web-monitor-navigation-",
				StringComparison.Ordinal))
			.Select(entry => entry.Phase)
			.ToArray();
		navigationPhases.ShouldContain("web-monitor-navigation-started");
		navigationPhases.ShouldContain("web-monitor-navigation-completed");
	}

	[Test]
	public async Task SelectingAnUnloadedPageCreatesAndRegistersExactlyOneHost()
	{
		await using ControllerFixture fixture = new();
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();

		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);

		factory.Hosts.Count.ShouldBe(1);
		var host = factory.Hosts[page.Record.Id];
		host.Calls.Count(call => call == "navigate").ShouldBe(1);
		host.Calls.IndexOf("subscribe-navigation-started")
			.ShouldBeLessThan(host.Calls.IndexOf("read-source"));
		host.Calls.IndexOf("read-source").ShouldBeLessThan(host.Calls.IndexOf("navigate"));
	}

	[Test]
	public async Task ConfirmedSpaUrlPersistsRawAddressAndMainFrameConfirmationDoesNotWriteTwice()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(timeProvider: time);
		var rule = MonitorRule() with { PollIntervalSeconds = 6 };
		await SaveMonitorRulesAsync(fixture, rule);
		Uri spa = new("https://example.test/spa#details");
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: false, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation(spa.AbsoluteUri, activity: false, revision: "2")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation(spa.AbsoluteUri, activity: false, revision: "2")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation(spa.AbsoluteUri, activity: false, revision: "2")));
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		ConcurrentQueue<WebMonitorStableUrlChangedEventArgs> stableUrls = new();
		fixture.MonitorCoordinator.StableUrlChanged +=
			(_, change) => stableUrls.Enqueue(change);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		var initialSettle =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		var host = factory.Hosts[page.Record.Id];
		await initialSettle.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var initialConfirmation =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		time.Advance(TimeSpan.FromMilliseconds(500));
		await initialConfirmation.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var initialPolling =
			time.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(rule.PollIntervalSeconds));
		time.Advance(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationAsync(3).WaitAsync(TimeSpan.FromSeconds(5));
		await initialPolling.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		var spaConfirmation =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		time.Advance(TimeSpan.FromSeconds(rule.PollIntervalSeconds));
		await spaConfirmation.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var spaPolling =
			time.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(rule.PollIntervalSeconds));
		time.Advance(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationAsync(6).WaitAsync(TimeSpan.FromSeconds(5));
		page.ResumeUrl.ShouldBe(spa.AbsoluteUri);
		await spaPolling.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		stableUrls.Count.ShouldBe(1);
		stableUrls.Single().DocumentUrl.ShouldBe(spa);

		var writesBeforeMainFrame = fixture.Store.ResumeUrlUpdateCount;
		var evaluationsBeforeMainFrame = host.EvaluationQueries.Count;
		var stableUrlsBeforeMainFrame = stableUrls.Count;
		Uri mainFrame = new("https://example.test/main#fragment");
		host.EvaluationResults.Enqueue(Task.FromResult(
			Evaluation(mainFrame.AbsoluteUri, activity: false, revision: "3")));
		host.EvaluationResults.Enqueue(Task.FromResult(
			Evaluation(mainFrame.AbsoluteUri, activity: false, revision: "3")));
		host.EvaluationResults.Enqueue(Task.FromResult(
			Evaluation(mainFrame.AbsoluteUri, activity: false, revision: "3")));
		host.RaiseNavigationStarted();
		host.RaiseSourceChanged(mainFrame);
		await fixture.Store.WaitForResumeUrlUpdateCountAsync(writesBeforeMainFrame + 1)
			.WaitAsync(TimeSpan.FromSeconds(5));
		var mainFrameSettle =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		host.RaiseNavigationCompleted();
		await mainFrameSettle.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		var mainFrameConfirmation =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		time.Advance(TimeSpan.FromMilliseconds(500));
		await mainFrameConfirmation.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationAsync(evaluationsBeforeMainFrame + 3)
			.WaitAsync(TimeSpan.FromSeconds(5));

		host.EvaluationQueries[evaluationsBeforeMainFrame].ShouldNotBeNull();
		host.EvaluationQueries[evaluationsBeforeMainFrame + 1].ShouldBeNull();
		host.EvaluationQueries[evaluationsBeforeMainFrame + 2].ShouldNotBeNull();
		stableUrls.Count.ShouldBe(stableUrlsBeforeMainFrame);
		fixture.Store.ResumeUrlUpdateCount.ShouldBe(writesBeforeMainFrame + 1);
		page.ResumeUrl.ShouldBe(mainFrame.AbsoluteUri);
	}

	[Test]
	public async Task CloseWebPageDeletesSnapshotBeforeDisposingItsHost()
	{
		await using ControllerFixture fixture = new(withSessions: false);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var page = fixture.ViewModel.WebPages.Single();
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		var host = factory.Hosts[page.Record.Id];
		var snapshotWasDeletedBeforeDispose = false;
		host.Disposing = () => snapshotWasDeletedBeforeDispose =
				!File.Exists(Path.Combine(fixture.Paths.WebMonitorSnapshotsDirectory, "web-1.json"));

		await fixture.Controller.CloseWebPageAsync(
			page,
			TestContext.CurrentContext.CancellationToken);

		snapshotWasDeletedBeforeDispose.ShouldBeTrue();
		host.Calls.Last().ShouldBe("dispose");
	}

	[Test]
	public async Task PauseUnregistersAndUnloadsWebHostsPreservesSnapshotsAndResumeStaysLazy()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: false),
			TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var page = workspace.WebPages.Single();
		var host = factory.Hosts[page.Record.Id];

		await fixture.Controller.PauseWorkspaceAsync(
			workspace,
			TestContext.CurrentContext.CancellationToken);

		page.IsBrowserLoaded.ShouldBeFalse();
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
		host.Calls.ShouldContain("dispose");
		(await fixture.SnapshotStore.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		fixture.ViewModel.PausedWorkspaces.ShouldHaveSingleItem();
		var hostsBeforeResume = factory.Hosts.Count;

		await fixture.Controller.ResumeWorkspaceAsync(
			fixture.ViewModel.PausedWorkspaces.Single(),
			TestContext.CurrentContext.CancellationToken);

		factory.Hosts.Count.ShouldBe(hostsBeforeResume);
		page.IsBrowserLoaded.ShouldBeFalse();
		page.MonitorStatus.ShouldBe(WebMonitorStatus.Paused);
	}

	[Test]
	public async Task PauseReceivesFinalCoordinatorStatusBeforeUnloadingAnActivePage()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false,
			timeProvider: time);
		var rule = MonitorRule();
		await SaveMonitorRulesAsync(fixture, rule);
		var compiled = WebMonitorRuleCompiler.Compile(rule);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot(
				"web-1",
				unread: true,
				activity: true,
				ruleFingerprint: compiled.Fingerprint),
			TestContext.CurrentContext.CancellationToken);
		FakeWebPageHostFactory factory = new()
		{
			ConfigureHost = host =>
			{
				host.RaiseNavigationEventsOnNavigate = true;
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: true, revision: "1")));
				host.EvaluationResults.Enqueue(Task.FromResult(
					Evaluation("https://example.test", activity: true, revision: "1")));
			}
		};
		fixture.Controller.WebPageHostFactory = factory;
		TaskCompletionSource<WebMonitorStatusChangedEventArgs> activity =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.MonitorCoordinator.StatusChanged += (_, change) =>
		{
			if (change.WebPageId == "web-1"
				&& change.Status == WebMonitorStatus.Activity)
			{
				activity.TrySetResult(change);
			}
		};
		var settleTimer =
			time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var page = workspace.WebPages.Single();
		await settleTimer.WaitAsync(TestContext.CurrentContext.CancellationToken);
		time.Advance(TimeSpan.FromMilliseconds(500));
		await activity.Task.WaitAsync(TestContext.CurrentContext.CancellationToken);

		await fixture.Controller.PauseWorkspaceAsync(
			workspace,
			TestContext.CurrentContext.CancellationToken);

		page.MonitorStatus.ShouldBe(WebMonitorStatus.Unread);
		page.IsBrowserLoaded.ShouldBeFalse();
	}

	[Test]
	public async Task CloseWorkspaceDeletesSnapshotsBeforeDisposingAffectedHosts()
	{
		await using ControllerFixture fixture = new(
			activeItemId: "web-1",
			withSessions: false);
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		await fixture.SnapshotStore.SaveAsync(
			Snapshot("web-1", unread: true),
			TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var host = factory.Hosts["web-1"];
		var snapshotDeletedAtDispose = false;
		host.Disposing = () => snapshotDeletedAtDispose =
				!File.Exists(Path.Combine(fixture.Paths.WebMonitorSnapshotsDirectory, "web-1.json"));

		await fixture.Controller.CloseWorkspaceAsync(
			workspace,
			TestContext.CurrentContext.CancellationToken);

		snapshotDeletedAtDispose.ShouldBeTrue();
	}

	[Test]
	public async Task InitializeActivatesPersistedWebPageWithoutAnotherSelection()
	{
		await using ControllerFixture fixture = new(activeItemId: "web-1");
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;

		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		var page = fixture.ViewModel.WebPages.Single();
		fixture.ViewModel.SelectedWebPage.ShouldBeSameAs(page);
		page.IsBrowserLoaded.ShouldBeTrue();
		var host = factory.Hosts[page.Record.Id];
		host.Calls.Count(call => call == "show").ShouldBe(1);
		host.Calls.Count(call => call == "navigate").ShouldBe(1);
		host.Calls.Count(call => call == "focus").ShouldBe(1);
		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
		fixture.Backends.ShouldBeEmpty();
	}

	[Test]
	public async Task BrowserNavigationFailureKeepsSelectedBrowserVisible()
	{
		await using ControllerFixture fixture = new();
		FakeWebPageHostFactory factory = new();
		fixture.Controller.WebPageHostFactory = factory;
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var page = fixture.ViewModel.WebPages.Single();
		await fixture.Controller.SelectWebPageAsync(page, CancellationToken.None);
		var host = factory.Hosts[page.Record.Id];
		host.Calls.Clear();

		host.RaiseNavigationFailed("unreachable");
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
		fixture.ViewModel.SelectedWebPage.ShouldBeSameAs(page);
		host.Calls.ShouldNotContain("hide");
	}

	[Test]
	public async Task InitializeLoadsTreeBeforeWebViewAndStartsOnlyAfterReady()
	{
		await using ControllerFixture fixture = new();
		fixture.Host.InitializeBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);

		var initialize = fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await fixture.Host.InitializeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

		fixture.Backends.Sum(backend => backend.StartCount).ShouldBe(0);
		fixture.Host.InitializeBlocker.SetResult(true);
		await initialize;

		fixture.Backends.Sum(backend => backend.StartCount).ShouldBe(1);
		fixture.Host.ShownSessions.Single().ShouldBe("session-1");
	}

	[Test]
	public async Task SelectingAnotherSessionPreservesPriorController()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];
		fixture.Controller.Runtimes[first.Record.Id]
			.TryGetController(out var firstController).ShouldBeTrue();

		await fixture.Controller.SelectSessionAsync(second, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);

		fixture.Controller.Runtimes[first.Record.Id]
			.TryGetController(out var currentFirstController).ShouldBeTrue();
		currentFirstController.ShouldBeSameAs(firstController);
		firstController.IsActive.ShouldBeTrue();
		fixture.Controller.Runtimes[second.Record.Id]
			.TryGetController(out var secondController).ShouldBeTrue();
		secondController.IsActive.ShouldBeTrue();
	}

	[Test]
	public async Task RapidDoubleSelectionShowsOnlyLatestSession()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.ShownSessions.Clear();
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];
		TaskCompletionSource<bool> secondShow = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource secondShowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Host.ShowBlockers[second.Record.Id] = secondShow;
		fixture.Host.ShowStarted[second.Record.Id] = secondShowStarted;

		var firstSelection = fixture.Controller.SelectSessionAsync(second, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		await secondShowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		var secondSelection = fixture.Controller.SelectSessionAsync(first, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		secondShow.SetResult(true);
		await Task.WhenAll(firstSelection, secondSelection);

		(fixture.ViewModel.SelectedSession?.Record.Id).ShouldBe(first.Record.Id);
		fixture.Host.ShownSessions.Last().ShouldBe(first.Record.Id);
		fixture.Controller.Runtimes.ContainsKey(second.Record.Id).ShouldBeFalse();
	}

	[Test]
	public async Task SelectingWebPageHidesTerminalWithoutStoppingController()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Controller.Runtimes["session-1"]
			.TryGetController(out var controller).ShouldBeTrue();

		await fixture.Controller.SelectWebPageAsync(fixture.ViewModel.WebPages.Single(), TestContext.CurrentContext.CancellationToken);

		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
		controller.IsActive.ShouldBeTrue();
	}

	[Test]
	public async Task SelectingWebPageAfterNoteClearsNoteDocument()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(workspace.Id, TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectNoteAsync(note, TestContext.CurrentContext.CancellationToken);
		fixture.Controller.CurrentNoteDocument.ShouldNotBeNull();
		fixture.Controller.CurrentDocsAndNotes.ShouldNotBeNull();

		await fixture.Controller.SelectWebPageAsync(fixture.ViewModel.WebPages.Single(), TestContext.CurrentContext.CancellationToken);

		fixture.Controller.CurrentNoteDocument.ShouldBeNull();
		fixture.Controller.CurrentDocsAndNotes.ShouldBeNull();
		fixture.Controller.SelectedScenarioRun.ShouldBeNull();
		fixture.Controller.IsTerminalVisible.ShouldBeFalse();
	}

	[Test]
	public async Task RawSelectionIsDefaultAndQuickActionSubmitFollowsSendByDefault()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		(fixture.ViewModel.SelectedSelectionAction?.IsRaw).ShouldBe(true);
		await fixture.Controller.RunQuickActionAsync(new PromptTemplateRecord("manual", "Manual", "hello", false), TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.RunQuickActionAsync(new PromptTemplateRecord("submit", "Submit", "go", true), TestContext.CurrentContext.CancellationToken);

		fixture.Backends[0].Inputs[^2].EndsWith("\u001b[201~", StringComparison.Ordinal).ShouldBeTrue();
		fixture.Backends[0].Inputs[^1].EndsWith('\r').ShouldBeTrue();
	}

	[Test]
	public async Task BusyOverlayMirrorsToTerminalAndForwardsForceCloseAction()
	{
		await using ControllerFixture fixture = new();
		var actionRequested = false;
		fixture.Controller.BusyOverlayActionRequested += (_, _) => actionRequested = true;

		await fixture.Controller.SetBusyOverlayAsync("Saving session state...", true, true, "Force close");
		fixture.Host.RaiseBusyOverlayActionRequested();

		fixture.Host.BusyOverlayCalls.Single().ShouldBe(("Saving session state...", true, true, "Force close"));
		actionRequested.ShouldBeTrue();
	}

	[Test]
	public async Task ReopeningGitWorkspaceReusesPanelAndRetainsLastLog()
	{
		await using ControllerFixture fixture = new();
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(
			new ProjectRecord("git-project", "Git project", Path.GetTempPath(), now, now, null),
			_ => true);

		await fixture.Controller.SelectWorkspaceAsync(workspace);
		var first = fixture.Controller.CurrentGitPanel.ShouldBeOfType<GitPanelViewModel>();
		first.ReportError("last operation log");
		await fixture.Controller.SelectWorkspaceAsync(workspace);

		fixture.Controller.CurrentGitPanel.ShouldBeSameAs(first);
		fixture.Controller.CurrentGitPanel.LogText.Contains("last operation log", StringComparison.Ordinal).ShouldBeTrue();
	}

	[Test]
	public async Task TerminalSelectionCompletionOpensActionsAtTerminalAnchor()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.SelectedTextBlocker = CompletedSelection("selected");

		fixture.Host.RaiseSelectionChanged("session-1", true);
		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 120, 80, true));
	}

	[Test]
	public async Task LateTerminalSelectionCompletionCannotReopenPanelAfterSelectionWasCleared()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.SelectedTextBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);

		fixture.Host.RaiseSelectionChanged("session-1", true);
		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(120, 80, 3)));
		fixture.Host.RaiseSelectionChanged("session-1", false);
		fixture.Host.SelectedTextBlocker.SetResult("stale selection");
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Selection_loss_closes_actions_and_restores_terminal_focus()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.SelectedTextBlocker = CompletedSelection("selected");
		var initialFocusCalls = fixture.Host.FocusCallCount;

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);
		fixture.Host.RaiseSelectionChanged("session-1", false);

		await WaitForEventTasksAsync(fixture);
		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Host.FocusCallCount.ShouldBeGreaterThan(initialFocusCalls);
	}

	[Test]
	public async Task Terminal_interaction_closes_actions_opened_by_an_agent_owned_copy()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest("session-1", "copied by the agent", new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);
		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();

		fixture.Host.RaiseSelectionDismissed("session-1");

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Interaction_in_another_session_leaves_the_open_selection_actions_alone()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest("session-1", "copied by the agent", new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);

		fixture.Host.RaiseSelectionDismissed("session-2");

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
	}

	[Test]
	public async Task Switching_sessions_invalidates_pending_selection_capture()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.SelectedTextBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(120, 80, 3)));
		await fixture.Controller.SelectSessionAsync(
			fixture.ViewModel.Sessions[1],
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		fixture.Host.SelectedTextBlocker.SetResult("stale selection");
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
	}

	[Test]
	public async Task Replacing_selected_session_invalidates_the_captured_source()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Controller.UpdateSelectionText("selected");

		fixture.ViewModel.SelectedSession = fixture.ViewModel.Sessions[1];

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Closing_the_last_selected_session_invalidates_its_snapshot()
	{
		await using ControllerFixture fixture = new(withSecondSession: false);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var source = fixture.ViewModel.SelectedSession.ShouldNotBeNull();
		fixture.Controller.UpdateSelectionText("selected");

		await fixture.Controller.CloseSessionAsync(
			source,
			TestContext.CurrentContext.CancellationToken);

		fixture.ViewModel.SelectedSession.ShouldBeNull();
		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Pausing_the_source_project_invalidates_its_snapshot()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var sourceProject = fixture.ViewModel.Workspaces.Single();
		fixture.Controller.UpdateSelectionText("selected");

		await fixture.Controller.PauseWorkspaceAsync(
			sourceProject,
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Root_selection_survives_project_root_lifecycle(bool closeProject)
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord rootSession = new(
			"root-session",
			AgentKind.Codex,
			"Root Codex",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"codex",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using ControllerFixture fixture = new(
			projectId: "root",
			rootTabs: new RootTabsRecord(
				1,
				rootSession.Id,
				[rootSession],
				[],
				[]));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		fixture.ViewModel.SelectedSession.ShouldBeSameAs(fixture.ViewModel.RootTabs.Sessions.Single());
		fixture.Controller.UpdateSelectionText("root selection");
		var project = fixture.ViewModel.Workspaces.Single();

		if (closeProject)
		{
			await fixture.Controller.CloseWorkspaceAsync(
				project,
				TestContext.CurrentContext.CancellationToken);
		}
		else
		{
			await fixture.Controller.PauseWorkspaceAsync(
				project,
				TestContext.CurrentContext.CancellationToken);
		}

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		await fixture.Controller.SendSelectionToSessionAsync(
			target: fixture.ViewModel.RootTabs.Sessions.Single(),
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		fixture.Backends.Single().Inputs.ShouldContain(
			input => input.Contains("root selection", StringComparison.Ordinal));
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Project_root_selection_routes_then_invalidates_with_its_project(bool closeProject)
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord rootSession = new(
			"root-session",
			AgentKind.Codex,
			"Root Codex",
			Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
			"codex",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using ControllerFixture fixture = new(
			projectId: "root",
			rootTabs: new RootTabsRecord(
				1,
				rootSession.Id,
				[rootSession],
				[],
				[]));
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var project = fixture.ViewModel.Workspaces.Single();
		var source = project.Sessions[0];
		await fixture.Controller.SelectSessionAsync(
			source,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		fixture.Controller.UpdateSelectionText("project selection");

		await fixture.Controller.SendSelectionToSessionAsync(
			source,
			TestContext.CurrentContext.CancellationToken);

		fixture.Backends[^1].Inputs.ShouldContain(
			input => input.Contains("project selection", StringComparison.Ordinal));
		fixture.Controller.UpdateSelectionText("project selection");
		if (closeProject)
		{
			await fixture.Controller.CloseWorkspaceAsync(
				project,
				TestContext.CurrentContext.CancellationToken);
		}
		else
		{
			await fixture.Controller.PauseWorkspaceAsync(
				project,
				TestContext.CurrentContext.CancellationToken);
		}

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Closing_the_last_notes_project_invalidates_its_snapshot()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var sourceProject = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			sourceProject.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectNoteAsync(note, TestContext.CurrentContext.CancellationToken);
		fixture.Controller.CompleteNotesSelection(
			"selected",
			new SelectionActionAnchor(SelectionActionSourceKind.Notes, 40, 60, true));

		await fixture.Controller.CloseWorkspaceAsync(
			sourceProject,
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Switching_the_active_notes_document_invalidates_its_snapshot()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var sourceProject = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			sourceProject.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectNoteAsync(note, TestContext.CurrentContext.CancellationToken);
		fixture.Controller.CompleteNotesSelection(
			"selected",
			new SelectionActionAnchor(SelectionActionSourceKind.Notes, 40, 60, true));

		await fixture.Controller.CurrentDocsAndNotes.ShouldNotBeNull().SelectSectionAsync(
			DocsAndNotesSection.Common,
			TestContext.CurrentContext.CancellationToken);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Successful_session_route_does_not_close_a_replacement_snapshot()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var source = fixture.ViewModel.Sessions[0];
		var target = fixture.ViewModel.Sessions[1];
		await fixture.Controller.SelectSessionAsync(
			target,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectSessionAsync(
			source,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		TaskCompletionSource releaseWrite =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		fixture.Backends[1].InputWriteBlocker = releaseWrite;
		fixture.Controller.UpdateSelectionText("first snapshot");

		var send = fixture.Controller.SendSelectionToSessionAsync(
			target,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Backends[1].InputWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		fixture.Controller.UpdateSelectionText("replacement snapshot");
		releaseWrite.SetResult();
		await send;

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
	}

	[Test]
	public async Task Successful_notes_route_does_not_close_a_replacement_snapshot()
	{
		TaskCompletionSource releaseAppend =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		BlockingNotesStore notesStore = new() { AppendBlocker = releaseAppend };
		await using ControllerFixture fixture = new(notesStore: notesStore);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var project = fixture.ViewModel.Workspaces.Single();
		fixture.Controller.UpdateSelectionText("first snapshot");

		var send = fixture.Controller.SendSelectionToNotesAsync(
			new ProjectNotesTargetViewModel(project.Id, project.Name),
			TestContext.CurrentContext.CancellationToken);
		await notesStore.AppendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		fixture.Controller.UpdateSelectionText("replacement snapshot");
		releaseAppend.SetResult();
		await send;

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
	}

	[Test]
	public async Task Failed_session_route_keeps_the_current_snapshot_open()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var source = fixture.ViewModel.Sessions[0];
		var target = fixture.ViewModel.Sessions[1];
		await fixture.Controller.SelectSessionAsync(
			target,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectSessionAsync(
			source,
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);
		fixture.Backends[1].InputWriteFailure = new IOException("write failed");
		fixture.Controller.UpdateSelectionText("selected");

		await Should.ThrowAsync<InvalidOperationException>(() =>
			fixture.Controller.SendSelectionToSessionAsync(
				target,
				TestContext.CurrentContext.CancellationToken));

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
	}

	[Test]
	public async Task Osc52CopyOpensSelectionActionsAtTerminalAnchor()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest(
				"session-1",
				"copied by claude",
				new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);

		(fixture.ViewModel.SelectedSelectionAction?.IsRaw).ShouldBe(true);
		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 120, 80, true));
	}

	[Test]
	public async Task Osc52CopyFromInactiveSessionIsIgnored()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await fixture.Controller.SelectSessionAsync(
			fixture.ViewModel.Sessions[1],
			startIfNeeded: true,
			cancellationToken: TestContext.CurrentContext.CancellationToken);

		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest(
				"session-1",
				"copied by hidden terminal",
				new TerminalSelectionAnchor(120, 80, 3)));
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task Osc52CopyInvalidatesPendingTerminalSelectionCapture()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		fixture.Host.SelectedTextBlocker = new(TaskCreationOptions.RunContinuationsAsynchronously);

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(120, 80, 3)));
		fixture.Host.RaiseCopyRequested(
			new TerminalCopyRequest(
				"session-1",
				"new OSC 52 copy",
				new TerminalSelectionAnchor(200, 160, 4)));
		fixture.Host.SelectedTextBlocker.SetResult("stale terminal selection");
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 200, 160, true));
	}

	[Test]
	public async Task Osc52CopyWithoutAnchorOpensSelectionActionsWithUnavailableTerminalAnchor()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);

		fixture.Host.RaiseCopyRequested(new TerminalCopyRequest("session-1", "copied by claude", null));
		await WaitForEventTasksAsync(fixture);

		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 0, 0, false));
	}

	[Test]
	public async Task NotesSelectionCompletionOpensActionsAtNotesAnchorAndEmptyTextClearsIt()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			workspace.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectNoteAsync(
			note,
			TestContext.CurrentContext.CancellationToken);
		SelectionActionAnchor anchor = new(SelectionActionSourceKind.Notes, 40, 60, true);

		fixture.Controller.CompleteNotesSelection("selected note", anchor);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		fixture.Controller.SelectionActionsAnchor.ShouldBe(anchor);

		fixture.Controller.CompleteNotesSelection(string.Empty, anchor);

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		fixture.Controller.SelectionActionsAnchor.ShouldBeNull();
	}

	[Test]
	public async Task LockedSessionRejectsManualInputWhileControllerStaysActive()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.SelectedSession!;
		session.LockForScenario("run-1");

		await fixture.Controller.WriteInputAsync(session.Record.Id, "blocked");

		fixture.Backends[0].Inputs.ShouldBeEmpty();
		fixture.Controller.Runtimes[session.Record.Id]
			.TryGetController(out var activeController).ShouldBeTrue();
		activeController.IsActive.ShouldBeTrue();
	}

	[Test]
	public async Task OneSessionExitDoesNotStopAnotherController()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];
		await fixture.Controller.SelectSessionAsync(second, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		var exited = WaitForSessionStatusAsync(second, SessionStatus.Exited);

		fixture.Backends[1].CompleteOutput();
		await exited;

		fixture.Controller.Runtimes.ShouldNotContainKey(second.Record.Id);
		fixture.Controller.Runtimes[first.Record.Id]
			.TryGetController(out var firstController).ShouldBeTrue();
		firstController.IsActive.ShouldBeTrue();
		second.Status.ShouldBe(SessionStatus.Exited.ToString());
	}

	[Test]
	public async Task TerminalExitFromWorkerProjectsStatusThroughInjectedUiDispatcher()
	{
		RecordingUiTaskDispatcher dispatcher = new();
		await using ControllerFixture fixture = new(uiTaskDispatcher: dispatcher);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		var exited = WaitForSessionStatusAsync(session, SessionStatus.Exited);

		await Task.Run(fixture.Backends[0].CompleteOutput);

		await exited;
		dispatcher.InvokeCount.ShouldBeGreaterThan(0);
	}

	[Test]
	public async Task Successful_input_and_screen_snapshot_drive_the_status_engine()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		fixture.Controller.SetTerminalWindowFacts(visible: true, active: true, DateTimeOffset.UtcNow);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		session.Indicator.ShouldBe(TerminalTabIndicator.None);

		fixture.Host.RaiseInputReceived(session.Record.Id, "\r");
		await WaitForEventTasksAsync(fixture);
		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		fixture.Backends[0].EmitOutput("Worked for 1s");
		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);

		fixture.Host.RaiseScreenSnapshotReceived(session.Record.Id, "Worked for 1s");

		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task Successful_enter_resets_the_browser_snapshot_baseline()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		var resetBeforeBackendWrite = false;
		fixture.Backends[0].InputWritten = input =>
		{
			if (input.Contains('\r', StringComparison.Ordinal))
			{
				resetBeforeBackendWrite = fixture.Host.SnapshotBaselineResetSessions.Contains(
					session.Record.Id,
					StringComparer.Ordinal);
			}
		};

		fixture.Host.RaiseInputReceived(session.Record.Id, "\r");
		await WaitForEventTasksAsync(fixture);

		resetBeforeBackendWrite.ShouldBeTrue();
		fixture.Host.SnapshotBaselineResetSessions.ShouldContain(session.Record.Id);
	}

	[Test]
	public async Task Atomic_window_fact_swap_does_not_acknowledge_unread_completion()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		fixture.Controller.SetTerminalWindowFacts(visible: false, active: true, DateTimeOffset.UtcNow);
		fixture.Host.RaiseInputReceived(session.Record.Id, "\r");
		await WaitForEventTasksAsync(fixture);
		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		fixture.Host.RaiseScreenSnapshotReceived(session.Record.Id, "Worked for 1s");
		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);

		fixture.Controller.SetTerminalWindowFacts(visible: true, active: false, DateTimeOffset.UtcNow);

		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);
		fixture.Controller.SetTerminalWindowFacts(visible: true, active: true, DateTimeOffset.UtcNow);
		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task Screen_snapshot_refreshes_window_facts_before_status_engine()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		fixture.Host.RaiseInputReceived(session.Record.Id, "\r");
		await WaitForEventTasksAsync(fixture);
		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		var refreshed = false;
		fixture.Controller.RefreshWindowFacts = () =>
		{
			session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
			refreshed = true;
			fixture.Controller.SetTerminalWindowFacts(visible: true, active: true, DateTimeOffset.UtcNow);
		};

		fixture.Host.RaiseScreenSnapshotReceived(session.Record.Id, "Worked for 1s");

		refreshed.ShouldBeTrue();
		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task Dispose_unsubscribes_screen_snapshots()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		var refreshCount = 0;
		fixture.Controller.RefreshWindowFacts = () => refreshCount++;

		await fixture.Controller.DisposeAsync();
		fixture.Host.RaiseScreenSnapshotReceived("session-1", "Worked for 1s");

		refreshCount.ShouldBe(0);
	}

	[Test]
	public async Task Display_filtered_control_only_output_does_not_start_activity()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];

		fixture.Backends[0].EmitOutput("\u001bc");
		await fixture.Backends[0].FirstOutputProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));

		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task Resume_start_is_busy_while_normal_start_is_idle()
	{
		await using ControllerFixture resumeFixture = new(firstResumeCommand: "codex resume abc12345");
		await resumeFixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		resumeFixture.ViewModel.Sessions[0].Indicator.ShouldBe(TerminalTabIndicator.Busy);

		await using ControllerFixture normalFixture = new(firstResumeCommand: string.Empty);
		await normalFixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		normalFixture.ViewModel.Sessions[0].Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task Output_from_replaced_controller_is_ignored_by_status_engine()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		var replacedBackend = fixture.Backends[0];

		await fixture.Controller.RestartSessionAsync(
			session,
			preferResumeCommand: false,
			TestContext.CurrentContext.CancellationToken);
		session.Indicator.ShouldBe(TerminalTabIndicator.None);

		replacedBackend.EmitOutput("late output from detached controller");

		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public async Task ResumingWorkspaceEagerlyStartsEverySessionBackendNotJustTheActiveOne()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];

		await fixture.Controller.PauseWorkspaceAsync(workspace, TestContext.CurrentContext.CancellationToken);
		fixture.ViewModel.Workspaces.ShouldBeEmpty();
		var backendsBeforeResume = fixture.Backends.Count;

		await fixture.Controller.ResumeWorkspaceAsync(workspace, TestContext.CurrentContext.CancellationToken);

		var resumedWorkspace = fixture.ViewModel.Workspaces.Single();
		fixture.Controller.Runtimes[first.Record.Id]
			.TryGetController(out var firstController).ShouldBeTrue();
		firstController.IsActive.ShouldBeTrue();
		fixture.Controller.Runtimes[second.Record.Id]
			.TryGetController(out var secondController).ShouldBeTrue();
		secondController.IsActive.ShouldBeTrue();
		(fixture.Backends.Count > backendsBeforeResume + 1).ShouldBeTrue(
			"Both sessions should start new backends on resume.");

		// Only the previously-active session becomes visible/selected.
		(fixture.ViewModel.SelectedSession?.Record.Id).ShouldBe(first.Record.Id);
		fixture.Host.ShownSessions.ShouldNotContain(second.Record.Id);
		resumedWorkspace.ShouldNotBeNull();
	}

	[Test]
	public async Task PausingWorkspaceCapturesConcreteResumeIdBeforeStoppingCodex()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: "codex resume");
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var codex = fixture.ViewModel.Sessions[0];
		fixture.Backends[0].InputWritten = input =>
		{
			if (input == "/exit")
			{
				fixture.Backends[0].EmitOutput("To continue, run codex resume 019f6050-35a4-7951-9748-47239487c08d\r\n");
			}
		};

		await fixture.Controller.PauseWorkspaceAsync(workspace, TestContext.CurrentContext.CancellationToken);

		codex.Record.ResumeCommand.ShouldBe("codex resume 019f6050-35a4-7951-9748-47239487c08d");
		fixture.Backends[0].Inputs.ShouldContain("/exit");
	}

	[Test]
	public async Task ShutdownCapturesExtractedResumeCommandWhenSavedCommandIsEmpty()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: string.Empty);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			CancellationToken.None);

		await fixture.Controller.ShutdownAsync();

		fixture.ViewModel.Sessions[0].Record.ResumeCommand.ShouldBe(
			"codex resume 019f6050-35a4-7951-9748-47239487c08d");
		fixture.Backends[0].Inputs.ShouldContain("/exit");
	}

	[Test]
	public async Task ShutdownUsesOneDeadlineForAllResumeCaptureAttemptsAndStillStopsSessions()
	{
		ManualTimeProvider time = new(
			new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero));
		await using ControllerFixture fixture = new(
			resumeCaptureSessionCount: 3,
			timeProvider: time);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			CancellationToken.None);
		foreach (var session in fixture.ViewModel.Sessions.Skip(1))
		{
			await fixture.Controller.SelectSessionAsync(
				session,
				startIfNeeded: true,
				cancellationToken: TestContext.CurrentContext.CancellationToken);
		}

		List<string> statuses = [];
		fixture.Controller.StatusMessage += (_, message) => statuses.Add(message);
		var deadlineCreated =
			time.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(20));

		var shutdown = fixture.Controller.ShutdownAsync();
		await deadlineCreated.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);
		fixture.Backends.Count.ShouldBe(3);
		fixture.Backends.ShouldAllBe(backend => backend.Inputs.Contains("/exit"));

		time.Advance(TimeSpan.FromSeconds(20));
		await shutdown.WaitAsync(
			TimeSpan.FromSeconds(5),
			TestContext.CurrentContext.CancellationToken);

		fixture.Backends.ShouldAllBe(backend => backend.StopStarted.Task.IsCompleted);
		foreach (var session in fixture.ViewModel.Sessions)
		{
			statuses.ShouldContain($"Resume capture timed out: {session.Record.Id}");
		}
	}

	[Test]
	public async Task ShutdownShowsTheAgentSessionWhoseResumeIdIsBeingCaptured()
	{
		await using ControllerFixture fixture = new(firstResumeCommand: "codex resume");
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var codex = fixture.ViewModel.Sessions[0];
		var pwsh = fixture.ViewModel.Sessions[1];
		await fixture.Controller.SelectSessionAsync(pwsh, startIfNeeded: true, cancellationToken: TestContext.CurrentContext.CancellationToken);
		SessionViewModel? selectedWhenCaptureWasReported = null;
		fixture.Controller.StatusMessage += (_, message) =>
		{
			if (message.StartsWith("Capturing resume session id:", StringComparison.Ordinal))
			{
				selectedWhenCaptureWasReported = fixture.ViewModel.SelectedSession;
			}
		};
		fixture.Backends[0].InputWritten = input =>
		{
			if (input == "/exit")
			{
				fixture.Backends[0].EmitOutput("To continue, run codex resume 019f6050-35a4-7951-9748-47239487c08d\r\n");
			}
		};

		await fixture.Controller.ShutdownAsync();

		fixture.Host.ShownSessions.Last().ShouldBe(codex.Record.Id);
		selectedWhenCaptureWasReported.ShouldBeSameAs(codex);
	}

	[Test]
	public async Task RestartSessionFreshClearsResumeIdAndStartsLaunchCommand()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];

		await fixture.Controller.RestartSessionAsync(session, preferResumeCommand: false, TestContext.CurrentContext.CancellationToken);

		// The agent kind contributes its agent-control arguments after the launch command, so
		// this assertion covers the command itself.
		(fixture.Backends[^1].LastStartOptions?.CommandLine).ShouldStartWith("codex");
		session.Record.ResumeCommand.ShouldBe("codex resume");
		fixture.Host.DisposedSessions.ShouldContain(session.Record.Id);
		fixture.Backends.Count.ShouldBe(2);
	}

	[Test]
	public async Task Any_saved_session_of_a_supported_kind_receives_the_launch_arguments()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];
		session.Record.Kind.ShouldBe(AgentKind.Codex);

		await fixture.Controller.RestartSessionAsync(
			session,
			preferResumeCommand: false,
			TestContext.CurrentContext.CancellationToken);

		var started = fixture.Backends[^1].LastStartOptions.ShouldNotBeNull();
		started.CommandLine.ShouldContain("mcp_servers.pact.url=");
		started.EnvironmentVariables.ShouldNotBeNull()
			.ShouldContainKey("PACT_AGENT_CONTROL_TOKEN");
	}

	[Test]
	public async Task RestartSessionCurrentPreservesAndStartsSavedResumeCommand()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var session = fixture.ViewModel.Sessions[0];

		await fixture.Controller.RestartSessionAsync(session, preferResumeCommand: true, TestContext.CurrentContext.CancellationToken);

		(fixture.Backends[^1].LastStartOptions?.CommandLine).ShouldStartWith("codex resume abc12345");
		session.Record.ResumeCommand.ShouldBe("codex resume abc12345");
		fixture.Host.DisposedSessions.ShouldContain(session.Record.Id);
		fixture.Backends.Count.ShouldBe(2);
	}

	[Test]
	public async Task ClosingSelectedSessionActivatesTheReplacementSessionHost()
	{
		await using ControllerFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var first = fixture.ViewModel.Sessions[0];
		var second = fixture.ViewModel.Sessions[1];
		fixture.Host.ShownSessions.Clear();

		await fixture.Controller.CloseSessionAsync(first, TestContext.CurrentContext.CancellationToken);

		fixture.ViewModel.SelectedSession.ShouldBeSameAs(second);
		fixture.Host.ShownSessions.Last().ShouldBe(second.Record.Id);
	}

	private static async Task SaveMonitorRulesAsync(
		ControllerFixture fixture,
		params WebMonitorRule[] rules)
	{
		var json = JsonSerializer.Serialize(rules, SettingsFileStore.JsonOptions);
		await fixture.SettingsFileStore.SaveAsync(
			"web-monitor-rules.json",
			json,
			TestContext.CurrentContext.CancellationToken);
	}

	private static WebMonitorRule MonitorRule() =>
		new(
			"rule-1",
			"Example",
			Enabled: true,
			"^https://example\\.test(?:/|$)",
			PollIntervalSeconds: WebMonitorRuleCompiler.MinimumPollIntervalSeconds,
			Activity: new WebMonitorExtractor(
				".busy",
				WebMonitorValueSource.Exists,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null),
			Revision: new WebMonitorExtractor(
				".revision",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null));

	private static WebMonitorEvaluation Evaluation(
		string url,
		bool? activity,
		string? revision) =>
		new(
			new Uri(url),
			new WebMonitorObservation(activity, revision));

	private static WebMonitorSnapshot Snapshot(
		string webPageId,
		bool unread,
		bool? activity = false,
		string ruleFingerprint = "retained-fingerprint") =>
		new(
			webPageId,
			webPageId == "web-paused"
				? "https://paused.example.test/"
				: "https://example.test/",
			"rule-1",
			ruleFingerprint,
			activity,
			Revision: "1",
			unread,
			DateTimeOffset.UtcNow);

	private sealed class ControllerFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;

		public ControllerFixture(
			string firstResumeCommand = "codex resume abc12345",
			string? activeItemId = null,
			bool includePausedWorkspace = false,
			bool withSessions = true,
			bool withSecondWebPage = false,
			TimeProvider? timeProvider = null,
			IWebMonitorSnapshotReader? snapshotReader = null,
			IUiTaskDispatcher? uiTaskDispatcher = null,
			int resumeCaptureSessionCount = 0,
			RootTabsRecord? rootTabs = null,
			bool withSecondSession = true,
			IProjectNotesStore? notesStore = null,
			string projectId = "project-1",
			IProcessTreeSnapshotReader? processTreeSnapshotReader = null,
			int? terminalProcessId = null,
			IWebProcessMetricsSnapshotReader? webProcessMetricsSnapshotReader = null)
		{
			var now = DateTimeOffset.UtcNow;
			SessionRecord first = new(
				"session-1",
				AgentKind.Codex,
				"First",
				Path.GetTempPath(),
				"codex",
				firstResumeCommand,
				SessionStatus.Stopped,
				now,
				now);
			var second = Session("session-2", "Second", now);
			var sessions = resumeCaptureSessionCount > 0
				? Enumerable.Range(1, resumeCaptureSessionCount)
					.Select(index =>
					{
						var claude = index == 2;
						return new SessionRecord(
							$"session-{index}",
							claude ? AgentKind.Claude : AgentKind.Codex,
							$"Agent {index}",
							Path.GetTempPath(),
							claude ? "claude" : "codex",
							claude ? "claude --resume" : "codex resume",
							SessionStatus.Stopped,
							now,
							now);
					})
					.ToArray()
				: withSecondSession ? [first, second] : [first];
			WebPageRecord[] webPages = withSecondWebPage
				?
				[
					new WebPageRecord(
						"web-1",
						"Docs",
						"https://example.test",
						"https://example.test",
						now,
						now),
					new WebPageRecord(
						"web-2",
						"Builds",
						"https://example.test/builds",
						"https://example.test/builds",
						now,
						now)
				]
				:
				[
					new WebPageRecord(
						"web-1",
						"Docs",
						"https://example.test",
						"https://example.test",
						now,
						now)
				];
			ProjectRecord project = new(projectId, "Project", Root, now, now, null)
			{
				ActiveItemId = activeItemId ?? (withSessions ? first.Id : webPages[0].Id),
				Sessions = withSessions ? sessions : [],
				WebPages = webPages
			};
			ProjectRecord[] projects = includePausedWorkspace
				?
				[
					project,
					new ProjectRecord(
						"project-paused",
						"Paused",
						Root + "-paused",
						now,
						now,
						null)
					{
						Status = WorkspaceStatus.Paused,
						ActiveItemId = "web-paused",
						WebPages =
						[
							new WebPageRecord(
								"web-paused",
								"Paused docs",
								"https://paused.example.test",
								"https://paused.example.test",
								now,
								now)
						]
					}
				]
				: [project];
			Store = new InMemoryProjectStore(new ProjectsDocument(1, projects));
			Statuses = new TerminalTabStatusCoordinator(action => action());
			RootStore = new InMemoryRootTabsStore(rootTabs ?? RootTabsRecord.CreateDefault());
			ViewModel = new MainWindowViewModel(
				Store,
				notesStore ?? new EmptyNotesStore(),
				Statuses,
				RootStore);
			Host = new FakeTerminalWebViewHost();
			AppPaths paths = new(Root);
			DataRootHousekeeping.Prepare(paths);
			Paths = paths;
			SettingsFileStore = new SettingsFileStore(paths);
			RecentDirectoryStore = new RecentDirectoryStore(paths.RecentDirectoriesPath);
			SnapshotStore = new WebMonitorSnapshotStore(paths);
			MonitorCoordinator = new WebMonitorCoordinator(
				SnapshotStore,
				timeProvider ?? TimeProvider.System,
				action => action());
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				SettingsFileStore,
				paths,
				Host,
				() =>
				{
					FakeTerminalBackend backend = new()
					{
						ProcessId = terminalProcessId,
						ExitResponse = string.IsNullOrEmpty(firstResumeCommand)
							? "To continue, run codex resume 019f6050-35a4-7951-9748-47239487c08d\r\n"
							: null
					};
					Backends.Add(backend);
					return backend;
				});
			_builder
				.WithSnapshotReader(snapshotReader ?? new WebMonitorSnapshotReader(SnapshotStore))
				.WithRecentDirectoryStore(RecentDirectoryStore)
				.WithWebMonitorCoordinator(MonitorCoordinator)
				.WithUiTaskDispatcher(uiTaskDispatcher ?? new ImmediateUiTaskDispatcher())
				.WithClipboard(Clipboard)
				.WithTimeProvider(timeProvider ?? TimeProvider.System);
			if (processTreeSnapshotReader is not null)
			{
				_builder.WithProcessTreeSnapshotReader(processTreeSnapshotReader);
			}
			if (webProcessMetricsSnapshotReader is not null)
			{
				_builder.WithWebProcessMetricsSnapshotReader(webProcessMetricsSnapshotReader);
			}
			Controller = _builder.Build();
		}

		public InMemoryProjectStore Store { get; }
		public InMemoryRootTabsStore RootStore { get; }
		public string Root => _temporaryDirectory.Path;
		public RecentDirectoryStore RecentDirectoryStore { get; }
		public AppPaths Paths { get; }
		public SettingsFileStore SettingsFileStore { get; }
		public WebMonitorSnapshotStore SnapshotStore { get; }
		public WebMonitorCoordinator MonitorCoordinator { get; }
		public MainWindowViewModel ViewModel { get; }
		public TerminalTabStatusCoordinator Statuses { get; }
		public FakeTerminalWebViewHost Host { get; }
		public FakeClipboardService Clipboard { get; } = new();
		public List<FakeTerminalBackend> Backends { get; } = [];
		public AvaloniaMainShellController Controller { get; }

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await MonitorCoordinator.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}

		private static SessionRecord Session(string id, string title, DateTimeOffset now) =>
			new(id, AgentKind.Pwsh, title, Path.GetTempPath(), "pwsh", null, SessionStatus.Stopped, now, now);
	}

	private static Task WaitForEventTasksAsync(ControllerFixture fixture) =>
		fixture.Controller.GetEventTasks()
			.WaitForIdleAsync()
			.WaitAsync(TimeSpan.FromSeconds(5));

	private static async Task WaitForDetailRowAsync(
		AvaloniaMainShellController controller,
		string label)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (DateTimeOffset.UtcNow < deadline)
		{
			if (controller.SelectedTabDetails?.Rows.Any(row => row.Label == label) == true)
			{
				return;
			}

			await Task.Yield();
		}

		throw new TimeoutException($"Detail row '{label}' was not published.");
	}

	private static WebProcessMetricsSnapshot WebMetricsSnapshot(
		DateTimeOffset sampledAt,
		double pageCpuSeconds) =>
		new(
			new ProcessSetSnapshot(
				2,
				2 * 1024 * 1024,
				new Dictionary<int, TimeSpan> { [20] = TimeSpan.FromSeconds(pageCpuSeconds) },
				sampledAt),
			new ProcessSetSnapshot(
				4,
				8 * 1024 * 1024,
				new Dictionary<int, TimeSpan> { [100] = TimeSpan.FromSeconds(2) },
				sampledAt));

	private sealed class FakeWebProcessMetricsReader(
		params WebProcessMetricsSnapshot[] snapshots) : IWebProcessMetricsSnapshotReader
	{
		private readonly Queue<WebProcessMetricsSnapshot> _snapshots = new(snapshots);
		private readonly TaskCompletionSource _read =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _readCount;

		internal List<string> PageIds { get; } = [];

		public Task<WebProcessMetricsSnapshot> ReadAsync(
			string pageId,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			PageIds.Add(pageId);
			Interlocked.Increment(ref _readCount);
			_read.TrySetResult();
			return Task.FromResult(_snapshots.Dequeue());
		}

		internal Task WaitForReadAsync() => _read.Task;

		internal async Task WaitForReadCountAsync(int expected)
		{
			var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
			while (DateTimeOffset.UtcNow < deadline)
			{
				if (Volatile.Read(ref _readCount) >= expected)
				{
					return;
				}

				await Task.Yield();
			}

			throw new TimeoutException($"Expected {expected} web metrics reads.");
		}
	}

	private static async Task WaitForSessionStatusAsync(
		SessionViewModel session,
		SessionStatus expected)
	{
		var expectedText = expected.ToString();
		if (session.Status == expectedText)
		{
			return;
		}

		TaskCompletionSource reached =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
		{
			if (args.PropertyName == nameof(SessionViewModel.Status)
				&& session.Status == expectedText)
			{
				reached.TrySetResult();
			}
		}

		session.PropertyChanged += OnPropertyChanged;
		try
		{
			if (session.Status == expectedText)
			{
				return;
			}

			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			session.PropertyChanged -= OnPropertyChanged;
		}
	}

	private static async Task WaitForMonitorStatusAsync(
		WebPageViewModel page,
		WebMonitorStatus expected)
	{
		if (page.MonitorStatus == expected)
		{
			return;
		}

		TaskCompletionSource reached =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
		{
			if (args.PropertyName == nameof(WebPageViewModel.MonitorStatus)
				&& page.MonitorStatus == expected)
			{
				reached.TrySetResult();
			}
		}

		page.PropertyChanged += OnPropertyChanged;
		try
		{
			if (page.MonitorStatus == expected)
			{
				return;
			}

			await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			page.PropertyChanged -= OnPropertyChanged;
		}
	}

	private sealed class RecordingUiTaskDispatcher : IUiTaskDispatcher
	{
		public int InvokeCount { get; private set; }

		public void Post(Action action) => action();

		public async Task InvokeAsync(Func<Task> operation)
		{
			InvokeCount++;
			await operation();
		}
	}

	private sealed class QueuedUiTaskDispatcher : IUiTaskDispatcher, IDisposable
	{
		private readonly ConcurrentQueue<Action> _actions = new();
		private readonly SemaphoreSlim _available = new(0);

		public void Post(Action action)
		{
			_actions.Enqueue(action);
			_available.Release();
		}

		public Task InvokeAsync(Func<Task> operation) => operation();

		public void Dispose() => _available.Dispose();

		internal async Task RunUntilAsync(Func<bool> condition)
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
			while (!condition())
			{
				await _available.WaitAsync(timeout.Token);
				if (_actions.TryDequeue(out var action))
				{
					action();
				}
			}
		}
	}

	private sealed class FakeWebMonitorSnapshotReader : IWebMonitorSnapshotReader
	{
		public Exception? SweepFailure { get; init; }
		public Dictionary<string, Exception> LoadFailures { get; } =
			new(StringComparer.Ordinal);
		public Dictionary<string, WebMonitorSnapshot?> Snapshots { get; } =
			new(StringComparer.Ordinal);
		public List<string> LoadedWebPageIds { get; } = [];

		public Task SweepAsync(
			IReadOnlySet<string> existingWebPageIds,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return SweepFailure is null
				? Task.CompletedTask
				: Task.FromException(SweepFailure);
		}

		public Task<WebMonitorSnapshot?> LoadAsync(
			string webPageId,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			LoadedWebPageIds.Add(webPageId);
			return LoadFailures.TryGetValue(webPageId, out var failure)
				? Task.FromException<WebMonitorSnapshot?>(failure)
				: Task.FromResult(Snapshots.GetValueOrDefault(webPageId));
		}
	}

	private sealed class FakeClipboardService : IClipboardService
	{
		public Task<string> NextRead { get; set; } = Task.FromResult(string.Empty);
		public bool NextWriteResult { get; set; } = true;
		public string? WrittenText { get; private set; }
		public Task<string> GetTextAsync() => NextRead;
		public Task<bool> TrySetTextAsync(string text)
		{
			WrittenText = text;
			return Task.FromResult(NextWriteResult);
		}
	}

	private static TaskCompletionSource<string> CompletedSelection(string text)
	{
		TaskCompletionSource<string> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
		source.SetResult(text);
		return source;
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		private readonly Lock _resumeUrlSignalLock = new();
		private readonly List<(int MinimumCount, TaskCompletionSource Completion)> _resumeUrlWaiters = [];
		private ProjectsDocument _document = document;
		public int ResumeUrlUpdateCount { get; private set; }
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(_document);
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken)
		{
			_document = document;
			return Task.CompletedTask;
		}
		public Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken)
		{
			var before = _document;
			_document = update(_document);
			var beforePages = before.Projects
				.SelectMany(project => project.WebPages)
				.ToDictionary(page => page.Id, StringComparer.Ordinal);
			ResumeUrlUpdateCount += _document.Projects
				.SelectMany(project => project.WebPages)
				.Count(page =>
					beforePages.TryGetValue(page.Id, out var previous)
					&& (page.LastActiveAt != previous.LastActiveAt
						|| !string.Equals(
							page.ResumeUrl,
							previous.ResumeUrl,
							StringComparison.Ordinal)));
			CompleteResumeUrlWaiters();
			return Task.FromResult(_document);
		}

		public Task WaitForResumeUrlUpdateCountAsync(int minimumCount)
		{
			lock (_resumeUrlSignalLock)
			{
				if (ResumeUrlUpdateCount >= minimumCount)
				{
					return Task.CompletedTask;
				}

				TaskCompletionSource completion =
					new(TaskCreationOptions.RunContinuationsAsynchronously);
				_resumeUrlWaiters.Add((minimumCount, completion));
				return completion.Task;
			}
		}

		private void CompleteResumeUrlWaiters()
		{
			List<TaskCompletionSource> completed = [];
			lock (_resumeUrlSignalLock)
			{
				for (var index = _resumeUrlWaiters.Count - 1; index >= 0; index--)
				{
					var (MinimumCount, Completion) = _resumeUrlWaiters[index];
					if (ResumeUrlUpdateCount >= MinimumCount)
					{
						completed.Add(Completion);
						_resumeUrlWaiters.RemoveAt(index);
					}
				}
			}

			foreach (var completion in completed)
			{
				completion.TrySetResult();
			}
		}
	}

	private sealed class InMemoryRootTabsStore(RootTabsRecord record) : IRootTabsStore
	{
		private RootTabsRecord _record = record;

		public Task<RootTabsRecord> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_record);

		public Task SaveAsync(RootTabsRecord document, CancellationToken cancellationToken)
		{
			_record = document.Normalize();
			return Task.CompletedTask;
		}

		public Task<RootTabsRecord> UpdateAsync(
			Func<RootTabsRecord, RootTabsRecord> update,
			CancellationToken cancellationToken)
		{
			_record = update(_record).Normalize();
			return Task.FromResult(_record);
		}
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	private sealed class BlockingNotesStore : IProjectNotesStore
	{
		// A notes append reaches the store as the appended document's flush, so the in-flight
		// point this fixture holds open is the save, not the store's own append.
		public TaskCompletionSource AppendStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource? AppendBlocker { get; init; }

		public Task<string> LoadAsync(
			string projectRootPath,
			CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public async Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken)
		{
			AppendStarted.TrySetResult();
			if (AppendBlocker is not null)
			{
				await AppendBlocker.Task.WaitAsync(cancellationToken);
			}
		}

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class FakeProcessTreeSnapshotReader(
		params ProcessTreeSnapshot[] snapshots) : IProcessTreeSnapshotReader
	{
		private readonly Queue<ProcessTreeSnapshot> _snapshots = new(snapshots);

		internal List<int> RootProcessIds { get; } = [];

		public ProcessTreeSnapshot Read(int rootProcessId)
		{
			RootProcessIds.Add(rootProcessId);
			return _snapshots.Dequeue();
		}
	}
}
