using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Editing;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Settings;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MainWindowPaneVisibilityHeadlessTests
{
	[AvaloniaTest]
	public async Task Overview_notes_are_read_only_until_initial_load_and_keep_existing_text()
	{
		using ApplicationStyleScope styles = new(includeEditor: true);
		GatedInitialNotesStore store = new();
		await using WindowFixture fixture = new(notesStore: store);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		using MainWindow window = new(fixture.Controller);
		var center = window.FindControl<Grid>("CenterPane")!;
		center.Children.Remove(window.FindControl<TerminalPaneView>("TerminalPane")!);
		center.Children.Remove(window.FindControl<BrowserPaneView>("BrowserPane")!);
		window.Show();
		Task opening = Task.CompletedTask;
		try
		{
			opening = fixture.Controller.OpenProjectScreenAsync(fixture.ViewModel.Workspaces[0], ProjectScreenTab.Overview);
			await store.Entered.Task;
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			var editor = window.FindControl<ProjectScreenView>("ProjectScreen")!.DocumentPane.FindControl<SourceEditor>("Editor")!;
			editor.IsReadOnly.ShouldBeTrue();
			editor.Template.ShouldNotBeNull();
			editor.TextArea.Focus().ShouldBeTrue();
			window.KeyTextInput("too early");
			Dispatcher.UIThread.RunJobs();
			editor.Text.ShouldBeEmpty();
			await fixture.Controller.CurrentDocsAndNotes!.FlushAsync(CancellationToken.None);
			store.Saved.ShouldBeNull();
			store.Release.SetResult("existing notebook");
			await opening;
			Dispatcher.UIThread.RunJobs();
			editor.IsReadOnly.ShouldBeFalse();
			editor.Text.ShouldBe("existing notebook");
			window.UpdateLayout();
			editor.CaretIndex = editor.Text.Length;
			editor.TextArea.Focus().ShouldBeTrue();
			window.KeyTextInput(" edited");
			Dispatcher.UIThread.RunJobs();
			editor.Text.ShouldBe("existing notebook edited");
			await fixture.Controller.CurrentDocsAndNotes.FlushAsync(CancellationToken.None);
			store.Saved.ShouldBe("existing notebook edited");
		}
		finally
		{
			store.Release.TrySetResult("existing notebook");
			await opening;
			window.Close();
		}
	}

	private sealed class GatedInitialNotesStore : IProjectNotesStore
	{
		public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource<string> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public string? Saved { get; private set; }
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken)
		{
			Entered.TrySetResult();
			return Release.Task;
		}
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken)
		{
			Saved = text;
			return Task.CompletedTask;
		}
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}

	[AvaloniaTest]
	public async Task Overview_buttons_launch_current_project_and_open_or_remove_its_links()
	{
		using ApplicationStyleScope styles = new();
		await using WindowFixture fixture = new();
		var secondRoot = Path.Combine(fixture.Root, "second");
		Directory.CreateDirectory(secondRoot);
		await fixture.Store.UpdateAsync(document => document with
		{
			Projects = [.. document.Projects, document.Projects[0] with
			{
				Id = "second", Name = "Second", RootPath = secondRoot, Sessions = [], WebPages = [], ActiveItemId = null
			}]
		}, CancellationToken.None);
		RecordingIdeService ideService = new();
		fixture.Controller.InstalledIdeService = ideService;
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		using MainWindow window = new(fixture.Controller);
		var center = window.FindControl<Grid>("CenterPane")!;
		center.Children.Remove(window.FindControl<TerminalPaneView>("TerminalPane")!);
		center.Children.Remove(window.FindControl<BrowserPaneView>("BrowserPane")!);
		window.Show();
		var first = fixture.ViewModel.Workspaces[0];
		var second = fixture.ViewModel.Workspaces[1];
		await fixture.ViewModel.AddProjectLinkAsync(first.Id, "First", "https://example.test/first", "issue", CancellationToken.None);
		await fixture.Controller.OpenProjectScreenAsync(first, ProjectScreenTab.Overview);
		await fixture.Controller.OpenProjectScreenAsync(second, ProjectScreenTab.Overview);
		await fixture.ViewModel.AddProjectLinkAsync(second.Id, "Second build", "https://example.test/build", "ci", CancellationToken.None);
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		var view = window.FindControl<ProjectScreenView>("ProjectScreen")!;
		view.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), "Test IDE"))
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		ideService.OpenedRoot.ShouldBe(secondRoot);
		view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Second build"))
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		fixture.ViewModel.SelectedWebPage.ShouldNotBeNull().ResumeUrl.ShouldBe("https://example.test/build");
		fixture.ViewModel.SelectedWorkspace.ShouldBeSameAs(second);
		await fixture.Controller.OpenProjectScreenAsync(second, ProjectScreenTab.Overview);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		view.GetVisualDescendants().OfType<Button>().Single(button => Equals(ToolTip.GetTip(button), "Remove project link"))
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		var saved = await fixture.Store.LoadAsync(CancellationToken.None);
		saved.Projects.Single(project => project.Id == second.Id).Links.ShouldBeEmpty();
		saved.Projects.Single(project => project.Id == first.Id).Links.ShouldHaveSingleItem().Title.ShouldBe("First");
	}

	private sealed class RecordingIdeService : IInstalledIdeService
	{
		public string? OpenedRoot { get; private set; }
		public Task<IReadOnlyList<InstalledIde>> DiscoverAsync(bool refresh, CancellationToken cancellationToken) =>
			Task.FromResult<IReadOnlyList<InstalledIde>>([new("test", "Test IDE", @"C:\Test.exe", null)]);
		public Task OpenAsync(InstalledIde ide, string projectRoot, CancellationToken cancellationToken)
		{
			OpenedRoot = projectRoot;
			return Task.CompletedTask;
		}
	}
	[AvaloniaTest]
	public async Task Attention_card_opens_its_tab_once_and_reveals_a_collapsed_project()
	{
		TestContext.Progress.WriteLine("attention navigation: setup");
		using ApplicationStyleScope styles = new();
		await using WindowFixture fixture = new();
		await fixture.Store.UpdateAsync(document => document with
		{
			Projects = [.. Enumerable.Range(0, 30).Select(index => document.Projects.Single() with
			{
				Id = $"filler-{index}",
				Name = $"Filler {index}",
				Sessions = [],
				WebPages = [],
				ActiveItemId = null
			}), .. document.Projects]
		}, TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), TestContext.CurrentContext.CancellationToken);
		TestContext.Progress.WriteLine("attention navigation: initialized");
		var project = fixture.ViewModel.Workspaces.Single(workspace => workspace.Id == "project-1");
		using MainWindow window = new(fixture.Controller);
		List<string> errors = [];
		window.ShowMessageDialogAsyncOverride = request =>
		{
			errors.Add(request.Message);
			return Task.FromResult(MessageDialogResult.No);
		};
		var centerPane = window.FindControl<Grid>("CenterPane")!;
		centerPane.Children.Remove(window.FindControl<TerminalPaneView>("TerminalPane")!);
		centerPane.Children.Remove(window.FindControl<BrowserPaneView>("BrowserPane")!);
		window.Show();
		TestContext.Progress.WriteLine("attention navigation: shown");
		var target = project.WebPages.Last();
		project.WebPages.First().SetMonitorUnread(true);
		target.SetMonitorUnread(true);
		await fixture.Controller.OpenProjectScreenAsync(project, ProjectScreenTab.Overview);
		TestContext.Progress.WriteLine("attention navigation: overview");
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		var feed = window.GetVisualDescendants().OfType<AttentionFeedView>().ShouldHaveSingleItem();
		feed.IsEffectivelyVisible.ShouldBeTrue();
		var tree = window.FindControl<ProjectTreeView>("ProjectTree")!.FindControl<TreeView>("ProjectTree")!;
		tree.ScrollIntoView(project);
		window.UpdateLayout();
		var container = tree.ContainerFromItem(project).ShouldBeOfType<TreeViewItem>();
		container.IsExpanded = false;
		Dispatcher.UIThread.RunJobs();
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		TestContext.Progress.WriteLine("attention navigation: collapsed");
		tree.ScrollIntoView(fixture.ViewModel.Workspaces[0]);
		window.UpdateLayout();
		var button = feed.GetVisualDescendants().OfType<Button>()
			.Single(candidate => candidate.DataContext is AttentionItem item && ReferenceEquals(item.Target, target));
		var switches = 0;
		fixture.ViewModel.PropertyChanged += (_, args) =>
		{
			if (args.PropertyName == nameof(MainWindowViewModel.SelectedWebPage))
			{
				switches++;
			}
		};
		button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		TestContext.Progress.WriteLine("attention navigation: clicked");
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		TestContext.Progress.WriteLine("attention navigation: activated");
		errors.ShouldBeEmpty();
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		fixture.ViewModel.SelectedWebPage.ShouldBeSameAs(target);
		switches.ShouldBe(1);
		var revealed = tree.ContainerFromItem(project).ShouldBeOfType<TreeViewItem>();
		revealed.IsExpanded.ShouldBeTrue();
		var row = revealed.ContainerFromItem(target).ShouldBeOfType<TreeViewItem>();
		row.IsEffectivelyVisible.ShouldBeTrue();
		var at = row.TranslatePoint(default, tree).ShouldNotBeNull();
		at.Y.ShouldBeGreaterThanOrEqualTo(0);
		at.Y.ShouldBeLessThan(tree.Bounds.Height);
		feed.IsEffectivelyVisible.ShouldBeTrue(); // It also remains available on the Web surface.
	}

	[AvaloniaTest]
	public async Task Live_process_confirmation_lists_impacted_sessions_and_defaults_to_no()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		MessageDialogRequest? captured = null;
		window.ShowMessageDialogAsyncOverride = request =>
		{
			captured = request;
			return Task.FromResult(request.DefaultResult);
		};
		var liveSession = fixture.ViewModel.Workspaces.Single().Sessions.Single();

		var confirmed = await window.ConfirmStoppingSessionsAsync(
			"Close session",
			$"Close '{liveSession.Title}'?",
			[liveSession]);

		confirmed.ShouldBeFalse();
		captured.ShouldNotBeNull().DefaultResult.ShouldBe(MessageDialogResult.No);
		captured.Message.ShouldContain(liveSession.Title);
		captured.Message.ShouldContain("1 active terminal process");

		captured = null;
		SessionViewModel stopped = new(liveSession.Record with { Id = "stopped" });
		(await window.ConfirmStoppingSessionsAsync(
			"Close session",
			"Close stopped?",
			[stopped])).ShouldBeTrue();
		captured.ShouldBeNull();
	}

	[AvaloniaTest]
	public async Task Application_close_confirmation_serializes_repeated_close_and_honors_no_then_yes()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		TaskCompletionSource<MessageDialogResult> firstResult = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource firstPromptStarted = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		var promptCount = 0;
		var shutdownCount = 0;
		window.ShowMessageDialogAsyncOverride = _ =>
		{
			promptCount++;
			firstPromptStarted.TrySetResult();
			return firstResult.Task;
		};
		window.StartGracefulShutdownOverride = () => shutdownCount++;
		window.Closing += window.OnClosing;

		window.Close();
		await firstPromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		window.Close();
		promptCount.ShouldBe(1);
		shutdownCount.ShouldBe(0);

		firstResult.SetResult(MessageDialogResult.No);
		await fixture.Controller.GetEventTasks().WaitForIdleAsync()
			.WaitAsync(TimeSpan.FromSeconds(5));
		shutdownCount.ShouldBe(0);

		window.ShowMessageDialogAsyncOverride = _ =>
		{
			promptCount++;
			return Task.FromResult(MessageDialogResult.Yes);
		};
		window.Close();
		await fixture.Controller.GetEventTasks().WaitForIdleAsync()
			.WaitAsync(TimeSpan.FromSeconds(5));

		promptCount.ShouldBe(2);
		shutdownCount.ShouldBe(1);
		window.Close();
		shutdownCount.ShouldBe(1);
		window.Closing -= window.OnClosing;
	}

	[AvaloniaTest]
	public async Task Diagnostic_soft_restart_defaults_to_no_and_confirmed_path_skips_close_prompt()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var restartRequests = 0;
		var shutdowns = 0;
		var prompts = 0;
		MessageDialogRequest? confirmation = null;
		window.RequestSoftRestartAsyncOverride = _ =>
		{
			restartRequests++;
			return Task.FromResult(new SoftRestartRequestResult(true, [], null));
		};
		window.StartGracefulShutdownOverride = () => shutdowns++;
		window.ShowMessageDialogAsyncOverride = request =>
		{
			prompts++;
			confirmation = request;
			return Task.FromResult(request.DefaultResult);
		};

		await window.RequestDiagnosticSoftRestartAsync(CancellationToken.None);

		confirmation.ShouldNotBeNull().DefaultResult.ShouldBe(MessageDialogResult.No);
		restartRequests.ShouldBe(0);
		shutdowns.ShouldBe(0);

		window.ShowMessageDialogAsyncOverride = _ =>
		{
			prompts++;
			return Task.FromResult(MessageDialogResult.Yes);
		};
		await window.RequestDiagnosticSoftRestartAsync(CancellationToken.None);

		restartRequests.ShouldBe(1);
		shutdowns.ShouldBe(1);
		prompts.ShouldBe(2);
	}

	[AvaloniaTest]
	public async Task Blocked_diagnostic_soft_restart_lists_blockers_without_shutdown()
	{
		await using WindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var shutdowns = 0;
		List<MessageDialogRequest> dialogs = [];
		window.RequestSoftRestartAsyncOverride = _ => Task.FromResult(
			new SoftRestartRequestResult(
				false,
				[new SoftRestartBlocker("busy", "Codex is still busy.")],
				null));
		window.StartGracefulShutdownOverride = () => shutdowns++;
		window.ShowMessageDialogAsyncOverride = request =>
		{
			dialogs.Add(request);
			return Task.FromResult(MessageDialogResult.Yes);
		};

		await window.RequestDiagnosticSoftRestartAsync(CancellationToken.None);

		dialogs.Count.ShouldBe(2);
		dialogs[1].Message.ShouldContain("Codex is still busy.");
		shutdowns.ShouldBe(0);
	}

	[AvaloniaTest]
	public async Task Update_restart_defaults_to_no_and_only_successful_final_handoff_shuts_down()
	{
		await using WindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var requests = 0;
		var shutdowns = 0;
		MessageDialogRequest? confirmation = null;
		window.RequestUpdateRestartAsyncOverride = _ =>
		{
			requests++;
			return Task.FromResult(new SoftRestartRequestResult(true, [], null));
		};
		window.StartGracefulShutdownOverride = () => shutdowns++;
		window.ShowMessageDialogAsyncOverride = request =>
		{
			confirmation = request;
			return Task.FromResult(request.DefaultResult);
		};

		await window.RequestRestartAndUpdateAsync(CancellationToken.None);

		confirmation.ShouldNotBeNull().DefaultResult.ShouldBe(MessageDialogResult.No);
		requests.ShouldBe(0);
		shutdowns.ShouldBe(0);

		window.ShowMessageDialogAsyncOverride = _ => Task.FromResult(MessageDialogResult.Yes);
		window.RequestUpdateRestartAsyncOverride = _ =>
		{
			requests++;
			return Task.FromResult(new SoftRestartRequestResult(
				false,
				[new SoftRestartBlocker("terminal:busy", "Terminal is busy.")],
				null));
		};
		await window.RequestRestartAndUpdateAsync(CancellationToken.None);
		shutdowns.ShouldBe(0);

		window.RequestUpdateRestartAsyncOverride = _ =>
		{
			requests++;
			return Task.FromResult(new SoftRestartRequestResult(true, [], null));
		};
		await window.RequestRestartAndUpdateAsync(CancellationToken.None);

		requests.ShouldBe(2);
		shutdowns.ShouldBe(1);
	}

	[AvaloniaTest]
	public async Task Close_session_handler_honors_confirmation_before_stopping_runtime()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var session = workspace.Sessions.Single();
		window.ShowMessageDialogAsyncOverride = _ =>
			Task.FromResult(MessageDialogResult.No);

		await window.CloseSessionWithConfirmationAsync(session);

		workspace.Sessions.ShouldContain(session);
		fixture.Controller.HasActiveTerminalProcess(session).ShouldBeTrue();

		window.ShowMessageDialogAsyncOverride = _ =>
			Task.FromResult(MessageDialogResult.Yes);
		await window.CloseSessionWithConfirmationAsync(session);

		workspace.Sessions.ShouldNotContain(session);
		fixture.Controller.HasActiveTerminalProcess(session).ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Pause_project_handler_honors_confirmation_before_stopping_runtime()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var workspace = fixture.ViewModel.Workspaces.Single();
		window.ShowMessageDialogAsyncOverride = _ =>
			Task.FromResult(MessageDialogResult.No);

		await window.PauseWorkspaceWithConfirmationAsync(workspace);

		fixture.ViewModel.Workspaces.ShouldContain(workspace);
		fixture.Controller.GetActiveSessions(workspace.Sessions).ShouldNotBeEmpty();

		window.ShowMessageDialogAsyncOverride = _ =>
			Task.FromResult(MessageDialogResult.Yes);
		await window.PauseWorkspaceWithConfirmationAsync(workspace);

		fixture.ViewModel.Workspaces.ShouldNotContain(workspace);
		fixture.ViewModel.PausedWorkspaces.ShouldContain(workspace);
		fixture.Controller.GetActiveSessions(workspace.Sessions).ShouldBeEmpty();
	}

	[AvaloniaTest]
	public async Task Settings_entry_points_forward_default_project_and_session_deep_links()
	{
		await using WindowFixture fixture = new(includeSession: true);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		using MainWindow window = new(
			fixture.Controller,
			fixture.SettingsFileStore,
			new ProjectSettingsEditor(fixture.ViewModel),
			new FakeExternalLauncher(),
			new FakeFolderPicker());
		var shown = NewDialogSource();
		window.ShowSettingsWindowAsyncOverride = dialog =>
		{
			shown.TrySetResult(dialog);
			return Task.CompletedTask;
		};

		var actions = window.FindControl<RightActionsPanel>("RightActions").ShouldBeOfType<RightActionsPanel>();
		actions.FindControl<Button>("SettingsButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		var defaultDialog = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
		defaultDialog.InitialSection.ShouldBe(SettingsSection.Projects);
		defaultDialog.InitialItemId.ShouldBeNull();

		shown = NewDialogSource();
		var projectTree = window.FindControl<ProjectTreeView>("ProjectTree").ShouldBeOfType<ProjectTreeView>();
		var workspace = fixture.ViewModel.Workspaces.Single();
		var projectCard = BuildTreeItem(projectTree, workspace);
		FindButtonByToolTip(projectCard, "Edit project settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		var projectDialog = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
		projectDialog.InitialItemId.ShouldBe(workspace.Id);
		projectDialog.InitialSubItemId.ShouldBeNull();

		shown = NewDialogSource();
		var session = workspace.Sessions.Single();
		var sessionRow = BuildTreeItem(projectTree, session);
		FindButtonByToolTip(sessionRow, "Edit session settings").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		var sessionDialog = await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
		sessionDialog.InitialItemId.ShouldBe(workspace.Id);
		sessionDialog.InitialSubItemId.ShouldBe(session.Record.Id);
	}

	[AvaloniaTest]
	[System.Diagnostics.CodeAnalysis.SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The backend ownership is transferred to WindowFixture, which disposes the controller and runtime.")]
	public async Task ShutdownAfterWorkerRuntimeStopUpdatesPaneOnUiThread()
	{
		FakeTerminalBackend backend = new()
		{
			StopBlocker = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
		};
		await using WindowFixture fixture = new(includeSession: true, backend: backend);
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		// The started terminal stays behind its loading surface until its first stable screen.
		fixture.Host.RaiseScreenSnapshotReceived("session-1", "ready", stable: true);
		window.IsTerminalPaneVisible.ShouldBeTrue();

		var shutdown = fixture.Controller.ShutdownAsync();
		await backend.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Run(() => backend.StopBlocker.SetResult(true));
		await shutdown;

		window.IsTerminalPaneVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task First_stable_screen_swaps_the_loading_surface_for_the_terminal()
	{
		await using WindowFixture fixture = new(includeSession: true, restoreSelection: false);
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await fixture.Controller.SelectSessionAsync(fixture.ViewModel.Sessions.Single(), startIfNeeded: true);
		Dispatcher.UIThread.RunJobs();
		window.IsTerminalLoadingSurfaceVisible.ShouldBeTrue();
		window.IsTerminalPaneVisible.ShouldBeFalse();

		fixture.Host.RaiseScreenSnapshotReceived("session-1", "ready", stable: true);
		Dispatcher.UIThread.RunJobs();

		window.IsTerminalPaneVisible.ShouldBeTrue();
		window.IsTerminalLoadingSurfaceVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Closing_still_shows_the_terminal_the_resume_capture_switches_to()
	{
		await using WindowFixture fixture = new(includeSession: true);
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await fixture.Controller.SelectWebPageAsync(fixture.ViewModel.WebPages[0]);
		Dispatcher.UIThread.RunJobs();
		window.IsTerminalPaneVisible.ShouldBeFalse();

		typeof(MainWindow).GetMethod("BeginShellShutdown", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(window, null);
		var showTerminal = (Task)typeof(AvaloniaMainShellController)
			.GetMethod("ShowSessionTerminalUnderBusyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
			.Invoke(fixture.Controller, [fixture.ViewModel.Sessions[0]])!;
		await showTerminal;
		Dispatcher.UIThread.RunJobs();

		window.IsTerminalPaneVisible.ShouldBeTrue();
		window.IsBrowserPaneVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Paused_root_item_shows_the_logo_and_its_caption()
	{
		var now = DateTimeOffset.UtcNow;
		SessionRecord rootSession = new(
			"root-session",
			AgentKind.Codex,
			"Root Codex",
			@"C:\work\root",
			"codex",
			null,
			SessionStatus.Stopped,
			now,
			now);
		await using WindowFixture fixture = new(
			rootTabs: new RootTabsRecord(1, rootSession.Id, [rootSession], [], [rootSession.Id]));
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		Dispatcher.UIThread.RunJobs();

		window.IsPausedPaneVisible.ShouldBeTrue();
		window.PausedCaptionText.ShouldBe(@"Paused — Codex, C:\work\root");
		window.IsPausedLogoVisible.ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task SelectingWebPageFromFreshStateShowsBrowserPane()
	{
		// Fresh state: no sessions in the project, so nothing forces IsTerminalVisible to true.
		// The workspace loader auto-selects the first web page on load (web-1) purely as
		// ViewModel state, without IsTerminalVisible or any other MainWindow-watched controller
		// property ever changing. Selecting the second, still-unselected web page (web-2)
		// reproduces the reported repro exactly: SelectedWebPage changes while IsTerminalVisible
		// stays false the whole time, matching "fresh app start -> click a web page".
		await using WindowFixture fixture = new();
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		window.IsTerminalPaneVisible.ShouldBeFalse();

		var target = fixture.ViewModel.WebPages.Single(webPage => webPage.Record.Id == "web-2");
		await fixture.Controller.SelectItemAsync(target, TestContext.CurrentContext.CancellationToken);

		window.IsBrowserPaneVisible.ShouldBeTrue();
		window.IsNotesPaneVisible.ShouldBeFalse();
		window.IsTerminalPaneVisible.ShouldBeFalse();
		window.IsEmptyPaneVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task SelectingWebPageAfterNoteShowsBrowserPane()
	{
		await using WindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		using MainWindow window = new(fixture.Controller);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(workspace.Id, TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectItemAsync(note, TestContext.CurrentContext.CancellationToken);
		window.IsNotesPaneVisible.ShouldBeTrue();

		var target = fixture.ViewModel.WebPages.Single(webPage => webPage.Record.Id == "web-1");
		await fixture.Controller.SelectItemAsync(target, TestContext.CurrentContext.CancellationToken);

		window.IsBrowserPaneVisible.ShouldBeTrue();
		window.IsNotesPaneVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task ClickingATreeDocumentOpensItInTheDocumentationPane()
	{
		using ApplicationStyleScope theme = new();
		await using WindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		await File.WriteAllTextAsync(
			Path.Combine(fixture.Root, "README.md"),
			"# Readme",
			TestContext.CurrentContext.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(fixture.Root, "AGENTS.md"),
			"# Agents",
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var centerPane = window.FindControl<Grid>("CenterPane")!;
		centerPane.Children.Remove(window.FindControl<TerminalPaneView>("TerminalPane")!);
		centerPane.Children.Remove(
			window.FindControl<BrowserPaneView>("BrowserPane")!);
		window.Show();
		var project = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			project.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectItemAsync(note, TestContext.CurrentContext.CancellationToken);
		var workspace = fixture.Controller.CurrentDocsAndNotes.ShouldNotBeNull();
		await fixture.Controller.CurrentProjectScreen!.SelectTabAsync(
			ProjectScreenTab.Docs,
			TestContext.CurrentContext.CancellationToken);
		window.UpdateLayout();
		Dispatcher.UIThread.RunJobs();

		var actions = window.FindControl<RightActionsPanel>("RightActions")
			.ShouldBeOfType<RightActionsPanel>();
		actions.Workspace.ShouldBeSameAs(workspace);
		var tree = actions.FindControl<TreeView>("DocumentTree")!;
		var agents = workspace.DocsTree.Single(node => node.Title == "AGENTS.md");
		var agentsRow = tree.GetVisualDescendants()
			.OfType<TreeViewItem>()
			.Single(item => ReferenceEquals(item.DataContext, agents));
		agentsRow.DataContext.ShouldBeSameAs(agents);
		tree.SelectedItem = agents;
		Dispatcher.UIThread.RunJobs();
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();

		workspace.SelectedNode.ShouldBeSameAs(agents);
		workspace.ActiveDocument.ShouldBeSameAs(agents.Document);
		workspace.ActiveDocument!.Text.ShouldBe("# Agents");

		var target = fixture.ViewModel.WebPages.Single(webPage => webPage.Record.Id == "web-1");
		await fixture.Controller.SelectItemAsync(target, TestContext.CurrentContext.CancellationToken);

		actions.Workspace.ShouldBeNull();
	}

	[AvaloniaTest]
	public async Task SelectedWebPageLoadingStateControlsBrowserLoadingSurface()
	{
		await using WindowFixture fixture = new();
		using MainWindow window = new(fixture.Controller);
		await fixture.Controller.InitializeAsync(new Uri("file:///terminal.html"), CancellationToken.None);
		var page = fixture.ViewModel.WebPages.Single(item => item.Record.Id == "web-2");
		await fixture.Controller.SelectItemAsync(page, TestContext.CurrentContext.CancellationToken);

		page.SetLoading(true);
		window.IsBrowserLoadingSurfaceVisible.ShouldBeTrue();

		page.SetLoading(false);
		window.IsBrowserLoadingSurfaceVisible.ShouldBeFalse();
	}

	private sealed class WindowFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;
		private string _root => _temporaryDirectory.Path;

		public WindowFixture(
			bool includeSession = false,
			FakeTerminalBackend? backend = null,
			RootTabsRecord? rootTabs = null,
			IProjectNotesStore? notesStore = null,
			bool restoreSelection = true)
		{
			var now = DateTimeOffset.UtcNow;
			SessionRecord[] sessions = includeSession
				? [new SessionRecord(
					"session-1",
					AgentKind.Pwsh,
					"PowerShell",
					_root,
					"pwsh",
					null,
					SessionStatus.Stopped,
					now,
					now)]
				: [];
			ProjectRecord project = new("project-1", "Project", _root, now, now, null)
			{
				ActiveItemId = sessions.FirstOrDefault()?.Id,
				Sessions = sessions,
				WebPages =
				[
					new WebPageRecord("web-1", "Docs", "https://example.test", "https://example.test", now, now),
					new WebPageRecord("web-2", "Wiki", "https://example.test/wiki", "https://example.test/wiki", now, now)
				]
			};
			Store = new InMemoryProjectStore(new ProjectsDocument(1, [project]));
			ViewModel = rootTabs is null
				? new MainWindowViewModel(Store, notesStore ?? new EmptyNotesStore())
				: new MainWindowViewModel(
					Store,
					notesStore ?? new EmptyNotesStore(),
					new TerminalTabStatusCoordinator(action => action()),
					new InMemoryRootTabsStore(rootTabs));
			Host = new FakeTerminalWebViewHost();
			AppPaths paths = new(_root);
			Paths = paths;
			// Pane/runtime tests restore an explicit prior selection; first-run defaults to Dashboard.
			var selectedId = rootTabs?.ActiveItemId ?? project.ActiveItemId;
			if (restoreSelection && selectedId is not null)
			{
				var isRoot = rootTabs?.ActiveItemId is not null;
				var paused = isRoot && rootTabs!.PausedItemIds.Contains(selectedId);
				Task.Run(() => new SessionRestorationStore(paths.SessionRestorationPath).SaveAsync(new(
					paused ? [] : [selectedId], [], [], new(isRoot ? null : project.Id, selectedId), false),
					CancellationToken.None)).GetAwaiter().GetResult();
			}
			SettingsFileStore = new SettingsFileStore(paths);
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				SettingsFileStore,
				paths,
				Host,
				() => backend ?? new FakeTerminalBackend());
			Controller = _builder.Build();
		}

		public InMemoryProjectStore Store { get; }
		public MainWindowViewModel ViewModel { get; }
		public FakeTerminalWebViewHost Host { get; }
		public AvaloniaMainShellController Controller { get; }
		public AppPaths Paths { get; }
		public SettingsFileStore SettingsFileStore { get; }
		public string Root => _root;

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}
	}

	private sealed class ApplicationStyleScope : IDisposable
	{
		private readonly FluentTheme _theme = new();
		private readonly global::Avalonia.Markup.Xaml.Styling.StyleInclude? _editorStyle;

		public ApplicationStyleScope(bool includeEditor = false)
		{
			Application.Current!.Styles.Add(_theme);
			if (includeEditor)
			{
				_editorStyle = new(new Uri("avares://Pact.App.Avalonia.Tests/"))
				{
					Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
				};
				Application.Current.Styles.Add(_editorStyle);
			}
		}

		public void Dispose()
		{
			if (_editorStyle is not null)
			{
				Application.Current!.Styles.Remove(_editorStyle);
			}
			Application.Current!.Styles.Remove(_theme);
		}
	}

	private static TaskCompletionSource<SettingsWindow> NewDialogSource() =>
		new(TaskCreationOptions.RunContinuationsAsynchronously);

	private static Control BuildTreeItem(ProjectTreeView view, object item)
	{
		var tree = view.FindControl<TreeView>("ProjectTree")!;
		var template = tree.DataTemplates.Single(candidate => candidate.Match(item));
		var control = template.Build(item).ShouldBeAssignableTo<Control>()!;
		control.DataContext = item;
		return control;
	}

	private static Button FindButtonByToolTip(Control root, string toolTip) =>
		root.GetSelfAndVisualDescendants().OfType<Button>()
			.Single(button => Equals(ToolTip.GetTip(button), toolTip));

	private sealed class FakeExternalLauncher : IExternalLauncher
	{
		public Task OpenFileAsync(string path) => Task.CompletedTask;
		public Task OpenHttpUriAsync(Uri uri) => Task.CompletedTask;
	}

	private sealed class FakeFolderPicker : IFolderPicker
	{
		public Task<string?> PickFolderAsync(string? initialDirectory, string title) =>
			Task.FromResult<string?>(null);
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
		public Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken)
		{
			_document = update(_document);
			return Task.FromResult(_document);
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
}
