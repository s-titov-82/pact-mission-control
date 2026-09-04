using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Tests.Controllers;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Presentation;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MainWindowHeadlessTests
{
	[AvaloniaTest]
	public void ProjectTreeRendersLoadedWorkspaceCollection()
	{
		ProjectTreeView view = new();
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		viewModel.Workspaces.Add(new WorkspaceViewModel(
			new ProjectRecord("project-1", "Project", "C:\\repo", now, now, null)));

		view.DataContext = viewModel;

		var tree = view.FindControl<TreeView>("ProjectTree")!;
		tree.ItemsSource.ShouldBeSameAs(viewModel.Workspaces);
	}

	[AvaloniaTest]
	public void GitFlyoutOpensRightWithTheCommandGridAlignedAndSlidesInsideTheWorkArea()
	{
		Border content = new();

		var flyout = MainWindow.CreateGitFlyout(content, static () => false);

		flyout.Content.ShouldBeSameAs(content);
		flyout.Placement.ShouldBe(PlacementMode.RightEdgeAlignedTop);
		flyout.VerticalOffset.ShouldBe(-64d);
		flyout.ShowMode.ShouldBe(FlyoutShowMode.Transient);
		flyout.PlacementConstraintAdjustment.ShouldBe(
			PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY);
	}

	[AvaloniaTest]
	public void Git_flyout_stays_open_while_its_own_dialog_is_showing()
	{
		var dialogShowing = true;
		Button anchor = new();
		Window window = new()
		{
			Width = 400,
			Height = 300,
			Content = anchor,
			Template = new FuncControlTemplate<Window>((owner, scope) =>
			{
				ContentPresenter presenter = new()
				{
					[!ContentPresenter.ContentProperty] = owner[!ContentControl.ContentProperty]
				};
				VisualLayerManager layers = new()
				{
					Name = "PART_VisualLayerManager",
					Child = presenter
				};
				scope.Register(layers.Name, layers);
				return layers;
			})
		};
		var flyout = MainWindow.CreateGitFlyout(new Border(), () => dialogShowing);
		window.Show();
		window.UpdateLayout();
		try
		{
			flyout.ShowAt(anchor);
			flyout.IsOpen.ShouldBeTrue();

			flyout.Hide();
			flyout.IsOpen.ShouldBeTrue();

			dialogShowing = false;
			flyout.Hide();

			flyout.IsOpen.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task Selection_sources_share_the_center_popover_and_keep_right_panel_visible()
	{
		await using SelectionWindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow window = new(fixture.Controller);
		var popover = window.FindControl<SelectionActionsPopover>("SelectionActions").ShouldNotBeNull();
		fixture.Host.SelectedTextBlocker = CompletedSelection("terminal selected");

		fixture.Host.RaiseSelectionCompleted(
			new TerminalSelectionCompleted(
				"session-1",
				new TerminalSelectionAnchor(80, 90, 2)));
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();
		Dispatcher.UIThread.RunJobs();

		popover.IsOpen.ShouldBeTrue();
		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 80, 90, true));

		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			workspace.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectItemAsync(
			note,
			TestContext.CurrentContext.CancellationToken);
		var notesPane = window.FindControl<NotesPaneView>("NotesPane").ShouldNotBeNull();
		var editor = notesPane.FindControl<TextBox>("Editor").ShouldNotBeNull();
		editor.SelectionStart = 0;
		editor.SelectionEnd = "note selected".Length;

		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});
		Dispatcher.UIThread.RunJobs();

		popover.IsOpen.ShouldBeTrue();
		fixture.Controller.SelectionActionsAnchor.ShouldBe(
			new SelectionActionAnchor(SelectionActionSourceKind.Notes, 0, 0, false));
		window.FindControl<RightActionsPanel>("RightActions")!
			.FindControl<Control>("DefaultActionsPanel")!.IsVisible.ShouldBeTrue();
		window.GetLogicalDescendants().OfType<TextBlock>()
			.Single(text => string.Equals(text.Text, "Usage limits", StringComparison.Ordinal))
			.IsVisible.ShouldBeTrue();

		var target = fixture.ViewModel.SelectionActionCompactTargetProject!
			.Sessions.First(session => string.Equals(
				session.Record.Id,
				"session-1",
				StringComparison.Ordinal));
		var targetItem = BuildSelectionTarget(popover, target);
		targetItem.GetSelfAndVisualDescendants().OfType<Button>().Single()
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		await fixture.Controller.GetEventTasks().WaitForIdleAsync();

		fixture.Backends.SelectMany(static backend => backend.Inputs)
			.ShouldContain("\u001b[200~note selected\u001b[201~");

		editor.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyUpEvent,
			Key = Key.Right,
			KeyModifiers = KeyModifiers.Shift
		});
		Dispatcher.UIThread.RunJobs();

		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		popover.IsOpen.ShouldBeTrue();

		notesPane.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.Escape
		});
		Dispatcher.UIThread.RunJobs();

		fixture.Controller.IsSelectionActionsOpen.ShouldBeFalse();
		popover.IsOpen.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Notes_pointer_anchor_is_translated_from_the_editor_not_the_pane()
	{
		await using SelectionWindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow mainWindow = new(fixture.Controller);
		using CenterPaneHost host = new(mainWindow);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			workspace.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectItemAsync(
			note,
			TestContext.CurrentContext.CancellationToken);
		host.UpdateLayout();
		var notesPane = mainWindow.FindControl<NotesPaneView>("NotesPane").ShouldNotBeNull();
		var editor = notesPane.FindControl<TextBox>("Editor").ShouldNotBeNull();
		Point editorPoint = new(40, 30);
		editor.SelectionStart = 0;
		editor.SelectionEnd = "note selected".Length;

		RaisePointerReleased(editor, host.Window, editorPoint);
		Dispatcher.UIThread.RunJobs();
		host.UpdateLayout();

		var expectedAnchor = editor.TranslatePoint(editorPoint, host.Pane).ShouldNotBeNull();
		expectedAnchor.Y.ShouldBeGreaterThan(editorPoint.Y);
		GetDividerTop(mainWindow, host.Pane).ShouldBe(expectedAnchor.Y, tolerance: 0.01);
	}

	[AvaloniaTest]
	public async Task Repeated_notes_pointer_release_over_unchanged_selection_keeps_anchor_and_expanded_targets()
	{
		await using SelectionWindowFixture fixture = new();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		using MainWindow mainWindow = new(fixture.Controller);
		using CenterPaneHost host = new(mainWindow);
		var workspace = fixture.ViewModel.Workspaces.Single();
		var note = await fixture.ViewModel.ShowNotesTabAsync(
			workspace.Id,
			TestContext.CurrentContext.CancellationToken);
		await fixture.Controller.SelectItemAsync(
			note,
			TestContext.CurrentContext.CancellationToken);
		host.UpdateLayout();
		var notesPane = mainWindow.FindControl<NotesPaneView>("NotesPane").ShouldNotBeNull();
		var editor = notesPane.FindControl<TextBox>("Editor").ShouldNotBeNull();
		var popover =
			mainWindow.FindControl<SelectionActionsPopover>("SelectionActions").ShouldNotBeNull();
		var popup = popover.FindControl<Popup>("SelectionPopup").ShouldNotBeNull();
		var popupOpened = 0;
		var popupClosed = 0;
		popup.Opened += (_, _) => popupOpened++;
		popup.Closed += (_, _) => popupClosed++;
		var openStateChanges = 0;
		fixture.Controller.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(AvaloniaMainShellController.IsSelectionActionsOpen))
			{
				openStateChanges++;
			}
		};
		editor.SelectionStart = 0;
		editor.SelectionEnd = "note selected".Length;

		RaisePointerReleased(editor, host.Window, new Point(30, 20));
		Dispatcher.UIThread.RunJobs();
		host.UpdateLayout();
		popover.FindControl<Button>("MoreTargetsButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		Dispatcher.UIThread.RunJobs();
		host.UpdateLayout();
		var firstDividerTop = GetDividerTop(mainWindow, host.Pane);
		var firstPlacementTop = popup.VerticalOffset;
		popover.FindControl<TreeView>("CompactTargetsTree")!.IsVisible.ShouldBeFalse();
		popover.FindControl<TreeView>("AllTargetsTree")!.IsVisible.ShouldBeTrue();

		RaisePointerReleased(editor, host.Window, new Point(30, 70));
		Dispatcher.UIThread.RunJobs();
		host.UpdateLayout();
		var secondDividerTop = GetDividerTop(mainWindow, host.Pane);
		var secondPlacementTop = popup.VerticalOffset;

		secondPlacementTop.ShouldBe(firstPlacementTop, tolerance: 0.01);
		secondDividerTop.ShouldBe(firstDividerTop, tolerance: 0.01);
		popupOpened.ShouldBe(1);
		popupClosed.ShouldBe(0);
		popover.FindControl<TreeView>("CompactTargetsTree")!.IsVisible.ShouldBeFalse();
		popover.FindControl<TreeView>("AllTargetsTree")!.IsVisible.ShouldBeTrue();
		openStateChanges.ShouldBe(1);
		fixture.Controller.IsSelectionActionsOpen.ShouldBeTrue();
		popover.IsOpen.ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task Disposing_main_window_closes_selection_popup_before_detaching_events()
	{
		await using SelectionWindowFixture fixture = new();
		MainWindow mainWindow = new(fixture.Controller);
		var popover =
			mainWindow.FindControl<SelectionActionsPopover>("SelectionActions").ShouldNotBeNull();
		await fixture.Controller.InitializeAsync(
			new Uri("file:///terminal.html"),
			TestContext.CurrentContext.CancellationToken);
		popover.Open(
			mainWindow.FindControl<TerminalPaneView>("TerminalPane").ShouldNotBeNull(),
			mainWindow.FindControl<Grid>("CenterPane").ShouldNotBeNull(),
			new SelectionActionAnchor(SelectionActionSourceKind.Terminal, 0, 0, false));
		popover.IsOpen.ShouldBeTrue();
		var compactTree =
			popover.FindControl<TreeView>("CompactTargetsTree").ShouldNotBeNull();
		var itemsBeforeDispose = compactTree.ItemsSource;

		mainWindow.Dispose();
		fixture.ViewModel.SelectionActionTargetProjects.Clear();
		Dispatcher.UIThread.RunJobs();

		popover.IsOpen.ShouldBeFalse();
		compactTree.ItemsSource.ShouldBeSameAs(itemsBeforeDispose);
	}

	private static void RaisePointerReleased(
		TextBox editor,
		Window root,
		Point editorPoint)
	{
		var rootPoint = editor.TranslatePoint(editorPoint, root).ShouldNotBeNull();
		using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
		editor.RaiseEvent(new PointerReleasedEventArgs(
			editor,
			pointer,
			root,
			rootPoint,
			timestamp: 1,
			new PointerPointProperties(
				RawInputModifiers.None,
				PointerUpdateKind.LeftButtonReleased),
			KeyModifiers.None,
			MouseButton.Left));
	}

	private static double GetDividerTop(MainWindow mainWindow, Grid pane)
	{
		var popover =
			mainWindow.FindControl<SelectionActionsPopover>("SelectionActions").ShouldNotBeNull();
		var divider = popover.FindControl<Border>("PlacementDivider").ShouldNotBeNull();
		var dividerPosition = divider.TranslatePoint(new Point(), pane).ShouldNotBeNull();
		return dividerPosition.Y;
	}

	private static TaskCompletionSource<string> CompletedSelection(string text)
	{
		TaskCompletionSource<string> result = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		result.SetResult(text);
		return result;
	}

	private static Control BuildSelectionTarget(
		SelectionActionsPopover popover,
		SessionViewModel target)
	{
		var template = popover.DataTemplates.Single(candidate => candidate.Match(target));
		var item = template.Build(target).ShouldBeAssignableTo<Control>()!;
		item.DataContext = target;
		return item;
	}

	private sealed class SelectionWindowFixture : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private readonly ShellControllerTestBuilder _builder;

		public SelectionWindowFixture()
		{
			var now = DateTimeOffset.UtcNow;
			SessionRecord[] sessions =
			[
				new(
					"session-1",
					AgentKind.Pwsh,
					"Source",
					_temporaryDirectory.Path,
					"pwsh",
					null,
					SessionStatus.Stopped,
					now,
					now),
				new(
					"session-2",
					AgentKind.Pwsh,
					"Target",
					_temporaryDirectory.Path,
					"pwsh",
					null,
					SessionStatus.Stopped,
					now,
					now)
			];
			ProjectRecord project = new(
				"project-1",
				"Project",
				_temporaryDirectory.Path,
				now,
				now,
				null)
			{
				ActiveItemId = sessions[0].Id,
				Sessions = sessions
			};
			ViewModel = new MainWindowViewModel(
				new InMemoryProjectStore(new ProjectsDocument(1, [project])),
				new SelectionNotesStore());
			Host = new FakeTerminalWebViewHost();
			AppPaths paths = new(_temporaryDirectory.Path);
			_builder = new ShellControllerTestBuilder(
				ViewModel,
				new SettingsFileStore(paths),
				paths,
				Host,
				() =>
				{
					FakeTerminalBackend backend = new();
					Backends.Add(backend);
					return backend;
				});
			Controller = _builder.Build();
		}

		public MainWindowViewModel ViewModel { get; }
		public FakeTerminalWebViewHost Host { get; }
		public List<FakeTerminalBackend> Backends { get; } = [];
		public AvaloniaMainShellController Controller { get; }

		public async ValueTask DisposeAsync()
		{
			await Controller.DisposeAsync();
			await _builder.DisposeAsync();
			await _temporaryDirectory.DisposeAsync();
		}
	}

	private sealed class CenterPaneHost : IDisposable
	{
		public CenterPaneHost(MainWindow mainWindow)
		{
			var root = mainWindow.FindControl<Grid>("RootGrid").ShouldNotBeNull();
			Pane = mainWindow.FindControl<Grid>("CenterPane").ShouldNotBeNull();
			root.Children.Remove(Pane).ShouldBeTrue();
			Pane.Children.Remove(
				mainWindow.FindControl<TerminalPaneView>("TerminalPane").ShouldNotBeNull());
			Pane.Children.Remove(
				mainWindow.FindControl<BrowserPaneView>("BrowserPane").ShouldNotBeNull());
			Pane.DataContext = mainWindow.DataContext;
			Window = new Window
			{
				Width = 640,
				Height = 480,
				Content = Pane,
				Template = new FuncControlTemplate<Window>((owner, scope) =>
				{
					ContentPresenter presenter = new()
					{
						[!ContentPresenter.ContentProperty] =
							owner[!ContentControl.ContentProperty]
					};
					VisualLayerManager layers = new()
					{
						Name = "PART_VisualLayerManager",
						Child = presenter
					};
					scope.Register(layers.Name, layers);
					return layers;
				})
			};
			Window.Show();
			UpdateLayout();
		}

		public Grid Pane { get; }
		public Window Window { get; }

		public void UpdateLayout()
		{
			Window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			Window.UpdateLayout();
		}

		public void Dispose() => Window.Close();
	}

	private sealed class InMemoryProjectStore(ProjectsDocument document) : IProjectStore
	{
		private ProjectsDocument _document = document;

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(_document);

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

	private sealed class SelectionNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult("note selected");

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());

		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task<ProjectsDocument> UpdateAsync(
			Func<ProjectsDocument, ProjectsDocument> update,
			CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(
			string projectRootPath,
			string text,
			CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
