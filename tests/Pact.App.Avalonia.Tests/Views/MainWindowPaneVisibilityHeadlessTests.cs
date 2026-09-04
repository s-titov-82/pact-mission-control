using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.App.Avalonia.Views.Settings;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MainWindowPaneVisibilityHeadlessTests
{
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
		window.IsTerminalPaneVisible.ShouldBeTrue();

		var shutdown = fixture.Controller.ShutdownAsync();
		await backend.StopStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
		await Task.Run(() => backend.StopBlocker.SetResult(true));
		await shutdown;

		window.IsTerminalPaneVisible.ShouldBeFalse();
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
		await workspace.SelectSectionAsync(
			DocsAndNotesSection.Common,
			TestContext.CurrentContext.CancellationToken);
		window.UpdateLayout();
		Dispatcher.UIThread.RunJobs();

		var actions = window.FindControl<RightActionsPanel>("RightActions")
			.ShouldBeOfType<RightActionsPanel>();
		actions.Workspace.ShouldBeSameAs(workspace);
		var tree = actions.FindControl<TreeView>("DocumentTree")!;
		var agents = workspace.CommonTree.Single(node => node.Title == "AGENTS.md");
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

		public WindowFixture(bool includeSession = false, FakeTerminalBackend? backend = null)
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
			ViewModel = new MainWindowViewModel(Store, new EmptyNotesStore());
			Host = new FakeTerminalWebViewHost();
			AppPaths paths = new(_root);
			Paths = paths;
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

		public ApplicationStyleScope()
		{
			Application.Current!.Styles.Add(_theme);
		}

		public void Dispose() => Application.Current!.Styles.Remove(_theme);
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

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}
}
