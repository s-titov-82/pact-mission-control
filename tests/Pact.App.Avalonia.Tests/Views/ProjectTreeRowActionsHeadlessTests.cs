using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectTreeRowActionsHeadlessTests
{
	[AvaloniaTest]
	public void SessionPencilRaisesEditSessionRequested()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		SessionViewModel? received = null;
		view.EditSessionRequested += (_, value) => received = value;
		var edit = FindButtonByToolTip(BuildRow(view, session), "Edit session settings");

		edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		edit.IsEnabled.ShouldBeTrue();
		received.ShouldBeSameAs(session);
	}

	[AvaloniaTest]
	public void SessionRestartAndCloseActionsCarryTheRowSession()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		SessionViewModel? restarted = null;
		bool? preferResume = null;
		SessionViewModel? closed = null;
		view.RestartSessionRequested += (_, request) =>
		{
			restarted = request.Session;
			preferResume = request.PreferResumeCommand;
		};
		view.CloseSessionRequested += (_, value) => closed = value;

		var row = BuildRow(view, session);
		var restart = FindButtonByToolTip(row, "Restart session");
		var flyout = restart.Flyout.ShouldBeOfType<MenuFlyout>();

		restart.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		flyout.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "Restart current"))
			.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
		FindButtonByToolTip(row, "Close session").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		restarted.ShouldBeSameAs(session);
		preferResume.ShouldBe(true);
		closed.ShouldBeSameAs(session);
	}

	[AvaloniaTest]
	public void WebPageReloadAndCloseActionsCarryTheRowPage()
	{
		ProjectTreeView view = new();
		var page = CreateWebPage();
		WebPageViewModel? reloaded = null;
		WebPageViewModel? closed = null;
		view.ReloadWebPageRequested += (_, value) => reloaded = value;
		view.CloseWebPageRequested += (_, value) => closed = value;

		var row = BuildRow(view, page);
		FindButtonByToolTip(row, "Reload web page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		FindButtonByToolTip(row, "Close web page").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		reloaded.ShouldBeSameAs(page);
		closed.ShouldBeSameAs(page);
	}

	[AvaloniaTest]
	public void RightClickingWebPageRowRaisesFullAddressCopyRequest()
	{
		ProjectTreeView view = new();
		var page = CreateWebPage();
		WebPageViewModel? requested = null;
		view.CopyWebPageAddressRequested += (_, value) => requested = value;
		var row = BuildRow(view, page);
		Window window = new() { Width = 500, Height = 200, Content = row };

		try
		{
			window.Show();
			window.UpdateLayout();
			var point = row.Bounds.Center;
			window.MouseDown(point, MouseButton.Right, RawInputModifiers.None);
			window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);

			requested.ShouldBeSameAs(page);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void LoadedWebPageDoesNotReserveSpaceForHiddenStatusIndicator()
	{
		ProjectTreeView view = new();
		var page = CreateWebPage();
		page.SetBrowserLoaded(true);
		var row = BuildRow(view, page);
		Dispatcher.UIThread.RunJobs();

		row.Measure(new Size(400, double.PositiveInfinity));

		var indicator = row.GetSelfAndVisualDescendants()
			.OfType<Grid>()
			.Single(grid => grid.Name == "WebPageStatusIndicator");
		indicator.DesiredSize.Width.ShouldBe(0);
	}

	[AvaloniaTest]
	public void RootLoadedWebPageDoesNotReserveSpaceForHiddenStatusIndicator()
	{
		ProjectTreeView view = new();
		var page = new WebPageViewModel(CreateWebPage().Record, isRootItem: true);
		page.SetBrowserLoaded(true);
		var row = BuildRootRow(view, page);
		Dispatcher.UIThread.RunJobs();

		row.Measure(new Size(400, double.PositiveInfinity));

		var indicator = row.GetSelfAndVisualDescendants()
			.OfType<Grid>()
			.Single(grid => grid.Name == "WebPageStatusIndicator");
		indicator.DesiredSize.Width.ShouldBe(0);
	}

	[AvaloniaTest]
	public void WebPageStatusUsesOneCellAndLoadingSuppressesEveryMonitorGlyph()
	{
		ProjectTreeView view = new();
		var page = CreateWebPage();
		page.SetBrowserLoaded(true);
		page.SetMonitorStatus(WebMonitorStatus.Activity);
		page.SetMonitorDiagnostic("web-1 / rule-1 / Timeout");
		var row = BuildRow(view, page);
		Dispatcher.UIThread.RunJobs();

		var status = row.GetSelfAndVisualDescendants()
			.OfType<Grid>()
			.Single(grid => grid.Name == "WebPageStatusIndicator");
		var loading = status.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Single(text => text.Name == "WebPageLoadingIndicator");
		var activity = status.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Single(text => text.Name == "WebPageActivityIndicator");
		var unread = status.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Single(text => text.Name == "WebPageUnreadIndicator");
		var paused = status.GetSelfAndVisualDescendants()
			.OfType<PathIcon>()
			.Single(icon => icon.Name == "WebPagePausedIndicator");

		activity.IsVisible.ShouldBeTrue();
		activity.Text.ShouldNotBeNullOrWhiteSpace();
		activity.Classes.ShouldContain("busy");
		ToolTip.GetTip(activity).ShouldBe(page.MonitorToolTip);
		unread.IsVisible.ShouldBeFalse();
		paused.IsVisible.ShouldBeFalse();
		status.Children.ShouldContain(activity);
		status.Children.ShouldContain(unread);
		status.Children.ShouldContain(paused);
		Grid.GetColumn(status).ShouldBe(0);
		status.GetVisualAncestors().OfType<Grid>().First().ColumnDefinitions.Count.ShouldBe(3);

		page.SetMonitorStatus(WebMonitorStatus.Unread);
		Dispatcher.UIThread.RunJobs();

		activity.IsVisible.ShouldBeFalse();
		unread.IsVisible.ShouldBeTrue();
		unread.Text.ShouldBe("●");
		unread.Classes.ShouldContain("unread");
		ToolTip.GetTip(unread).ShouldBe(page.MonitorToolTip);
		unread.Foreground.ShouldNotBeNull();
		activity.Foreground.ShouldBe(unread.Foreground);

		page.SetMonitorStatus(WebMonitorStatus.None);
		page.SetBrowserLoaded(false);
		Dispatcher.UIThread.RunJobs();

		activity.IsVisible.ShouldBeFalse();
		unread.IsVisible.ShouldBeFalse();
		paused.IsVisible.ShouldBeTrue();
		ToolTip.GetTip(paused).ShouldBe(page.MonitorToolTip);

		status.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Select(text => text.Text ?? string.Empty)
			.ShouldNotContain(text =>
				text.Contains("building", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(text, "unread", StringComparison.OrdinalIgnoreCase));

		page.SetMonitorStatus(WebMonitorStatus.Activity);
		page.SetLoading(true);
		Dispatcher.UIThread.RunJobs();

		loading.IsVisible.ShouldBeTrue();
		activity.IsVisible.ShouldBeFalse();
		unread.IsVisible.ShouldBeFalse();
		paused.IsVisible.ShouldBeFalse();

		page.SetMonitorStatus(WebMonitorStatus.Unread);
		Dispatcher.UIThread.RunJobs();
		activity.IsVisible.ShouldBeFalse();
		unread.IsVisible.ShouldBeFalse();
		paused.IsVisible.ShouldBeFalse();

		page.SetMonitorStatus(WebMonitorStatus.None);
		Dispatcher.UIThread.RunJobs();
		activity.IsVisible.ShouldBeFalse();
		unread.IsVisible.ShouldBeFalse();
		paused.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public void SessionWithoutStatusDoesNotReserveSpaceForStatusIndicator()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		var row = BuildRow(view, session);
		Dispatcher.UIThread.RunJobs();

		row.Measure(new Size(400, double.PositiveInfinity));

		var indicator = row.GetSelfAndVisualDescendants()
			.OfType<Grid>()
			.Single(grid => grid.Name == "SessionStatusIndicator");
		indicator.DesiredSize.Width.ShouldBe(0);
	}

	[AvaloniaTest]
	public void RootSessionWithoutStatusDoesNotReserveSpaceForStatusIndicator()
	{
		ProjectTreeView view = new();
		var session = new SessionViewModel(CreateSession().Record, isRootItem: true);
		var row = BuildRootRow(view, session);
		Dispatcher.UIThread.RunJobs();

		row.Measure(new Size(400, double.PositiveInfinity));

		var indicator = row.GetSelfAndVisualDescendants()
			.OfType<Grid>()
			.Single(grid => grid.Name == "SessionStatusIndicator");
		indicator.DesiredSize.Width.ShouldBe(0);
	}

	[AvaloniaTest]
	public void RootSessionWithPausedIndicatorShowsTheSamePauseGlyphAsAProjectSession()
	{
		ProjectTreeView view = new();
		var session = new SessionViewModel(CreateSession().Record, isRootItem: true);
		TerminalTabStatusCoordinator statuses = new(action => action());
		statuses.RegisterSession(session);
		statuses.OnLifecycleChanged(
			session.Record.Id,
			SessionStatus.Stopped,
			DateTimeOffset.UtcNow);
		var row = BuildRootRow(view, session);
		Dispatcher.UIThread.RunJobs();

		var paused = row.GetSelfAndVisualDescendants()
			.OfType<PathIcon>()
			.Single(icon => icon.Name == "SessionPausedIndicator");

		paused.IsVisible.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void SessionStatusDescriptionIsRenderedAsMutedSubtitle()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		TerminalTabStatusCoordinator statuses = new(action => action());
		statuses.RegisterSession(session);
		statuses.OnScreenSnapshot(
			session.Record.Id,
			"Working (1m 12s · esc to interrupt)",
			DateTimeOffset.UtcNow);
		var row = BuildRow(view, session);
		Dispatcher.UIThread.RunJobs();

		var description = row.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Single(text => text.Name == "SessionStatusDescription");

		description.Text.ShouldBe("Working");
		description.FontSize.ShouldBe(12);
		description.Foreground.ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void SessionRowUsesOnePrimaryIndicatorForFailedState()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		TerminalTabStatusCoordinator statuses = new(action => action());
		statuses.RegisterSession(session);
		statuses.OnLifecycleChanged(session.Record.Id, SessionStatus.Failed, DateTimeOffset.UtcNow);
		var row = BuildRow(view, session);
		Dispatcher.UIThread.RunJobs();

		var indicator = row.GetSelfAndVisualDescendants()
			.OfType<TextBlock>()
			.Single(text => text.Name == "SessionPrimaryIndicator");

		indicator.IsVisible.ShouldBeTrue();
		indicator.Text.ShouldBe("●");
		indicator.Classes.ShouldContain("failed");
	}

	[AvaloniaTest]
	public void BusySpinnerAdvancesFramesWithoutXamlAnimation()
	{
		ProjectTreeView view = new();
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord(
			"project-1", "Project", @"C:\repo", now, now, null));
		var session = CreateSession();
		workspace.Sessions.Add(session);
		TerminalTabStatusCoordinator statuses = new(action => action());
		statuses.RegisterSession(session);
		statuses.OnUserInput(session.Record.Id, "\r", DateTimeOffset.UtcNow);
		view.DataContext = new { Workspaces = new[] { workspace } };
		Window window = new() { Width = 500, Height = 300, Content = view };

		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			var firstFrame = view.AdvanceBusySpinnerFrame();
			var secondFrame = view.AdvanceBusySpinnerFrame();

			secondFrame.ShouldNotBe(firstFrame);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void DisabledSelectionActivationIgnoresTransientTreeSelection()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		object? received = null;
		view.SelectedItemChanged += (_, item) => received = item;
		var tree = view.FindControl<TreeView>("ProjectTree")!;

		view.SetSelectionActivationEnabled(false);
		tree.SelectedItem = session;

		received.ShouldBeNull();
		tree.SelectedItem.ShouldBeNull();

		view.SetSelectionActivationEnabled(true);
		tree.SelectedItem = session;

		received.ShouldBeNull();
		tree.SelectedItem.ShouldBeNull();

		view.NotifyUserSelectionInput();
		tree.SelectedItem = session;

		received.ShouldBeSameAs(session);
		tree.SelectedItem.ShouldBeNull();
	}

	[AvaloniaTest]
	public void NoteCloseActionCarriesTheRowNote()
	{
		ProjectTreeView view = new();
		ProjectNoteViewModel note = new(
			new NotesTabRecord("note-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
			@"C:\repo");
		ProjectNoteViewModel? closed = null;
		view.CloseNoteRequested += (_, value) => closed = value;

		var row = BuildRow(view, note);
		FindButtonByToolTip(row, "Hide notes").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		closed.ShouldBeSameAs(note);
	}

	[AvaloniaTest]
	public void LockedSessionRowUsesTheLockedStyleClass()
	{
		ProjectTreeView view = new();
		var session = CreateSession();
		session.LockForScenario("run-1");

		var row = BuildRow(view, session).ShouldBeOfType<Border>();

		row.Classes.ShouldContain("locked");
	}

	private static Control BuildRow(ProjectTreeView view, object item)
	{
		var tree = view.FindControl<TreeView>("ProjectTree")!;
		var template = tree.DataTemplates.Single(candidate => candidate.Match(item));
		var row = template.Build(item)!;
		row.DataContext = item;
		return row;
	}

	private static Control BuildRootRow(ProjectTreeView view, object item)
	{
		var tree = view.FindControl<TreeView>("RootTree")!;
		var template = tree.DataTemplates.Single(candidate => candidate.Match(item));
		var row = template.Build(item)!;
		row.DataContext = item;
		return row;
	}

	private static Button FindButtonByToolTip(Control row, string toolTip) =>
		row.GetSelfAndVisualDescendants()
			.OfType<Button>()
			.Single(button => Equals(ToolTip.GetTip(button), toolTip));

	private static SessionViewModel CreateSession()
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionViewModel(new SessionRecord(
			"session-1",
			AgentKind.Codex,
			"Codex",
			@"C:\repo",
			"codex",
			"codex resume abc",
			SessionStatus.Running,
			now,
			now));
	}

	private static WebPageViewModel CreateWebPage()
	{
		var now = DateTimeOffset.UtcNow;
		return new WebPageViewModel(new WebPageRecord(
			"web-1",
			"GitLab",
			"https://gitlab.example",
			"https://gitlab.example/project",
			now,
			now));
	}

}
