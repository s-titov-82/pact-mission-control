using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class SelectionActionsPopoverHeadlessTests
{
	[AvaloniaTest]
	public async Task OpenShowsCompactActionsWithoutMovingSourceFocus()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		fixture.Source.Focus();

		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();

		fixture.Popover.IsOpen.ShouldBeTrue();
		fixture.Source.IsFocused.ShouldBeTrue();
		var popup = fixture.Popover.FindControl<Popup>("SelectionPopup").ShouldNotBeNull();
		popup.IsLightDismissEnabled.ShouldBeTrue();
		popup.OverlayDismissEventPassThrough.ShouldBeTrue();
		popup.TakesFocusFromNativeControl.ShouldBeFalse();
		var actionList = fixture.Popover.FindControl<ListBox>("ActionsList").ShouldNotBeNull();
		actionList.IsVisible.ShouldBeTrue();
		actionList.ItemsSource!.Cast<SelectionActionChoiceViewModel>()
			.ShouldBe(fixture.ViewModel.SelectionActionChoices);
		var compactTree = fixture.Popover.FindControl<TreeView>("CompactTargetsTree").ShouldNotBeNull();
		compactTree.IsVisible.ShouldBeTrue();
		compactTree.ItemsSource!.Cast<SelectionActionTargetProjectViewModel>()
			.ShouldBe([fixture.ViewModel.SelectionActionCompactTargetProject!]);
		fixture.Popover.FindControl<TreeView>("AllTargetsTree")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task MoreTargetsSwitchesFromCompactProjectToFullTargetTree()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();

		fixture.Popover.FindControl<Button>("MoreTargetsButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		fixture.UpdateLayout();

		fixture.Popover.FindControl<TreeView>("CompactTargetsTree")!.IsVisible.ShouldBeFalse();
		var allTargets = fixture.Popover.FindControl<TreeView>("AllTargetsTree").ShouldNotBeNull();
		allTargets.IsVisible.ShouldBeTrue();
		allTargets.ItemsSource.ShouldBeSameAs(fixture.ViewModel.SelectionActionTargetProjects);
	}

	[AvaloniaTest]
	public async Task More_targets_hides_compact_empty_state_and_shows_full_tree()
	{
		using var fixture = await PopoverFixture.CreateAsync(
			includeSameProjectTarget: false,
			notesCompatibleAction: false);
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();

		fixture.ViewModel.HasSelectionActionTargets.ShouldBeTrue();
		fixture.ViewModel.SelectionActionCompactTargetProject.ShouldBeNull();
		fixture.Popover.FindControl<TextBlock>("CompactEmptyState")!
			.IsVisible.ShouldBeTrue();
		fixture.Popover.FindControl<Button>("MoreTargetsButton")!.IsVisible.ShouldBeTrue();

		fixture.Popover.FindControl<Button>("MoreTargetsButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		fixture.UpdateLayout();

		fixture.Popover.FindControl<TextBlock>("CompactEmptyState")!
			.IsVisible.ShouldBeFalse();
		fixture.Popover.FindControl<TreeView>("AllTargetsTree")!
			.IsVisible.ShouldBeTrue();
	}

	[AvaloniaTest]
	public async Task ReopeningResetsExpandedTargetsToCompactMode()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();
		fixture.Popover.FindControl<Button>("MoreTargetsButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		fixture.Popover.Close();

		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();

		fixture.Popover.FindControl<TreeView>("CompactTargetsTree")!.IsVisible.ShouldBeTrue();
		fixture.Popover.FindControl<TreeView>("AllTargetsTree")!.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task PlacementMovesToTheOtherSideAtTheRightEdge()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		fixture.Popover.Open(
			fixture.Source,
			fixture.Pane,
			fixture.Anchor with { X = 80 });
		fixture.UpdateLayout();
		var popup = fixture.Popover.FindControl<Popup>("SelectionPopup").ShouldNotBeNull();
		var leftAnchorX = popup.HorizontalOffset;
		fixture.Popover.Close();

		fixture.Popover.Open(
			fixture.Source,
			fixture.Pane,
			fixture.Anchor with { X = fixture.Pane.Bounds.Width - 20 });
		fixture.UpdateLayout();
		var rightAnchorX = popup.HorizontalOffset;

		leftAnchorX.ShouldBeGreaterThan(80);
		rightAnchorX.ShouldBeLessThan(fixture.Pane.Bounds.Width - 20);
	}

	[AvaloniaTest]
	public async Task VisibleDividerAndPopupChromeStayInsideCalculatedBoundsWhenTargetsExpand()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();
		var compactGeometry = AssertPopupGeometry(fixture);

		fixture.Popover.FindControl<Button>("MoreTargetsButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		fixture.UpdateLayout();
		var expandedGeometry = AssertPopupGeometry(fixture);

		compactGeometry.DividerTop.ShouldBe(fixture.Anchor.Y, tolerance: 0.01);
		expandedGeometry.DividerTop.ShouldBe(compactGeometry.DividerTop, tolerance: 0.01);
	}

	[AvaloniaTest]
	public async Task SessionAndNotesButtonsRaiseTypedEvents()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		SessionViewModel? receivedSession = null;
		ProjectNotesTargetViewModel? receivedNotes = null;
		fixture.Popover.SendSelectionRequested += (_, session) => receivedSession = session;
		fixture.Popover.SendSelectionToNotesRequested += (_, notes) => receivedNotes = notes;
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.UpdateLayout();
		var sourceGroup = fixture.ViewModel.SelectionActionCompactTargetProject.ShouldNotBeNull();
		var session = sourceGroup.Sessions.Single();
		var notes = sourceGroup.NotesTarget.ShouldNotBeNull();
		var sessionItem = BuildTemplateItem(fixture.Popover, session);
		var projectItem = BuildTemplateItem(fixture.Popover, sourceGroup);

		FindButton(sessionItem, session).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		FindButton(projectItem, notes).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		receivedSession.ShouldBeSameAs(session);
		receivedNotes.ShouldBeSameAs(notes);
	}

	[AvaloniaTest]
	public async Task EscapeAndLightDismissRequestControllerCloseButProgrammaticCloseDoesNot()
	{
		using var fixture = await PopoverFixture.CreateAsync();
		var closeRequests = 0;
		fixture.Popover.CloseRequested += (_, _) => closeRequests++;
		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.Source.RaiseEvent(new KeyEventArgs
		{
			RoutedEvent = InputElement.KeyDownEvent,
			Key = Key.Escape
		});

		closeRequests.ShouldBe(1);
		fixture.Popover.Close();
		closeRequests.ShouldBe(1);

		fixture.Popover.Open(fixture.Source, fixture.Pane, fixture.Anchor);
		fixture.Popover.FindControl<Popup>("SelectionPopup")!.IsOpen = false;
		Dispatcher.UIThread.RunJobs();

		closeRequests.ShouldBe(2);
	}

	private static Button FindButton(Control root, object dataContext) =>
		root.GetSelfAndVisualDescendants()
			.OfType<Button>()
			.Single(button => ReferenceEquals(button.DataContext, dataContext));

	private static PopupGeometry AssertPopupGeometry(PopoverFixture fixture)
	{
		var popup = fixture.Popover.FindControl<Popup>("SelectionPopup").ShouldNotBeNull();
		var popupChrome = popup.Child.ShouldBeAssignableTo<Control>()!;
		var actions = fixture.Popover.FindControl<ScrollViewer>("ActionsScroll").ShouldNotBeNull();
		var divider = fixture.Popover.FindControl<Border>("PlacementDivider").ShouldNotBeNull();
		var targets = fixture.Popover.FindControl<ScrollViewer>("TargetsScroll").ShouldNotBeNull();
		var placementTop = popup.VerticalOffset;
		var placementBottom =
			placementTop + actions.MaxHeight + divider.DesiredSize.Height + targets.MaxHeight;
		// The popup host applies the placement offsets to the hosted content, so translated
		// child geometry already carries them and this assertion exercises the visible chrome
		// rather than MaxHeight math.
		var chromeTop = TranslateTopToPane(popupChrome, fixture.Pane);
		var chromeBottom = chromeTop + popupChrome.Bounds.Height;
		var dividerTop = TranslateTopToPane(divider, fixture.Pane);

		divider.Bounds.Height.ShouldBeGreaterThan(0);
		dividerTop.ShouldBe(
			fixture.Anchor.Y,
			tolerance: 0.01,
			$"placement=({popup.HorizontalOffset},{popup.VerticalOffset}); chrome={popupChrome.Bounds}; " +
			$"divider={divider.Bounds}; chromeTop={chromeTop}; chromeBottom={chromeBottom}");
		chromeTop.ShouldBe(placementTop, tolerance: 0.01);
		chromeBottom.ShouldBe(placementBottom, tolerance: 0.01);
		chromeTop.ShouldBeGreaterThanOrEqualTo(8);
		chromeBottom.ShouldBeLessThanOrEqualTo(fixture.Pane.Bounds.Height - 8);

		return new PopupGeometry(dividerTop);
	}

	private static double TranslateTopToPane(Control control, Control pane)
	{
		var translated = control.TranslatePoint(new Point(), pane);
		translated.HasValue.ShouldBeTrue();
		return translated.Value.Y;
	}

	private static Control BuildTemplateItem(SelectionActionsPopover popover, object dataContext)
	{
		var template = popover.DataTemplates.Single(candidate => candidate.Match(dataContext));
		var item = template.Build(dataContext).ShouldBeAssignableTo<Control>()!;
		item.DataContext = dataContext;
		Dispatcher.UIThread.RunJobs();
		return item;
	}

	private sealed record PopupGeometry(double DividerTop);

	private sealed class PopoverFixture : IDisposable
	{
		private PopoverFixture(
			MainWindowViewModel viewModel,
			SelectionActionsPopover popover,
			Grid pane,
			Border source,
			Window window)
		{
			ViewModel = viewModel;
			Popover = popover;
			Pane = pane;
			Source = source;
			Window = window;
		}

		public MainWindowViewModel ViewModel { get; }
		public SelectionActionsPopover Popover { get; }
		public Grid Pane { get; }
		public Border Source { get; }
		public Window Window { get; }
		public SelectionActionAnchor Anchor { get; } =
			new(SelectionActionSourceKind.Terminal, 160, 180, true);

		public static async Task<PopoverFixture> CreateAsync(
			bool includeSameProjectTarget = true,
			bool notesCompatibleAction = true)
		{
			MainWindowViewModel viewModel = new(
				new InMemoryProjectStore(ProjectsDocument.CreateDefault()),
				new EmptyNotesStore());
			var sourceProject = await viewModel.EnsureWorkspaceForDirectoryAsync(
				@"D:\Work\Source",
				CancellationToken.None);
			var otherProject = await viewModel.EnsureWorkspaceForDirectoryAsync(
				@"D:\Work\Other",
				CancellationToken.None);
			var sourceSession = await viewModel.CreateSessionAsync(
				"source",
				"default",
				AgentKind.Codex,
				"source",
				sourceProject.RootPath,
				"codex",
				null,
				CancellationToken.None,
				sourceProject.Id);
			if (includeSameProjectTarget)
			{
				await viewModel.CreateSessionAsync(
					"same-project",
					"default",
					AgentKind.Codex,
					"same project",
					sourceProject.RootPath,
					"codex",
					null,
					CancellationToken.None,
					sourceProject.Id);
			}
			await viewModel.CreateSessionAsync(
				"other-project",
				"default",
				AgentKind.Codex,
				"other project",
				otherProject.RootPath,
				"codex",
				null,
				CancellationToken.None,
				otherProject.Id);
			viewModel.ReplacePromptTemplates([
				new PromptTemplateRecord(
					"explain",
					"Explain",
					notesCompatibleAction
						? "Explain {selectedText}"
						: "Explain {selectedText} for {task}",
					false,
					PromptActionType.Prompt)
			]);
			viewModel.SelectedSession = sourceSession;
			if (!notesCompatibleAction)
			{
				viewModel.SelectedSelectionAction =
					viewModel.SelectionActionChoices.Single(choice => choice.Name == "Explain");
			}

			SelectionActionsPopover popover = new() { DataContext = viewModel };
			Border source = new()
			{
				Focusable = true,
				HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Stretch,
				VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Stretch
			};
			Grid pane = new();
			pane.Children.Add(source);
			pane.Children.Add(popover);
			Window window = new()
			{
				Width = 520,
				Height = 420,
				Content = pane,
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
			window.Show();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			return new PopoverFixture(viewModel, popover, pane, source, window);
		}

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
		private ProjectsDocument Document { get; set; } = document;

		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(Document);

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
