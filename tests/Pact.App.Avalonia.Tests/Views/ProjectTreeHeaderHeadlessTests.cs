using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Projects;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectTreeHeaderHeadlessTests
{
	[AvaloniaTest]
	public void Root_dashboard_button_supports_enter_and_space()
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		ProjectTreeView view = new();
		Window window = new() { Content = view, Width = 400, Height = 400 };
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			var button = view.FindControl<Button>("RootDashboardButton")!;
			var requests = 0;
			view.RootDashboardRequested += (_, _) => requests++;
			button.Focus().ShouldBeTrue();
			window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
			window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
			requests.ShouldBe(1);
			window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
			window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
			requests.ShouldBe(2);
		}
		finally { window.Close(); Application.Current.Styles.Remove(theme); }
	}
	[AvaloniaTest]
	public void Arrow_up_from_a_root_child_opens_dashboard()
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		ProjectTreeView view = new();
		Window window = new() { Content = view, Width = 400, Height = 400 };
		try
		{
			var root = view.FindControl<TreeViewItem>("RootSectionItem")!;
			TreeViewItem child = new() { Header = "Session" };
			root.ItemsSource = new[] { child };
			window.Show();
			Dispatcher.UIThread.RunJobs();
			var requests = 0;
			view.RootDashboardRequested += (_, _) => requests++;
			child.Focus().ShouldBeTrue();
			window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
			window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
			requests.ShouldBe(1);
		}
		finally { window.Close(); Application.Current.Styles.Remove(theme); }
	}

	[AvaloniaTest]
	public void User_selection_of_root_row_opens_dashboard_but_programmatic_selection_does_not()
	{
		ProjectTreeView view = new();
		var requests = 0;
		view.RootDashboardRequested += (_, _) => requests++;
		var tree = view.FindControl<TreeView>("RootTree")!;
		var root = view.FindControl<TreeViewItem>("RootSectionItem")!;
		tree.SelectedItem = root;
		requests.ShouldBe(0);
		view.NotifyUserSelectionInput();
		tree.SelectedItem = root;
		requests.ShouldBe(1);
	}
	[AvaloniaTest]
	public void Root_dashboard_activation_is_separate_from_add_actions_and_expansion()
	{
		ProjectTreeView view = new();
		var requested = 0;
		view.RootDashboardRequested += (_, _) => requested++;
		view.FindControl<Button>("RootDashboardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		requested.ShouldBe(1);
		view.FindControl<Button>("AddRootSessionButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		view.FindControl<TreeViewItem>("RootSectionItem")!.IsExpanded = false;
		requested.ShouldBe(1);
		view.SetDashboardCurrent(true);
		view.FindControl<Border>("RootDashboardHeader")!.Classes.ShouldContain("current");
		view.SetDashboardCurrent(false);
		view.FindControl<Border>("RootDashboardHeader")!.Classes.ShouldNotContain("current");
	}
	[AvaloniaTest]
	public void Root_and_projects_headers_are_uppercase_and_root_starts_expanded()
	{
		ProjectTreeView view = new();

		view.FindControl<TextBlock>("RootHeaderText")!.Text.ShouldBe("ROOT");
		view.FindControl<TextBlock>("ProjectsHeaderText")!.Text.ShouldBe("PROJECTS");
		view.FindControl<TreeViewItem>("RootSectionItem")!.IsExpanded.ShouldBeTrue();
		view.FindControl<TreeView>("RootTree").ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void Root_header_actions_raise_separate_add_requests()
	{
		ProjectTreeView view = new();
		RootActionFlyoutRequest? terminalRequest = null;
		RootActionFlyoutRequest? webRequest = null;
		view.AddRootSessionRequested += (_, request) => terminalRequest = request;
		view.AddRootWebPageRequested += (_, request) => webRequest = request;

		view.FindControl<Button>("AddRootSessionButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		view.FindControl<Button>("AddRootWebPageButton")!
			.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		terminalRequest.ShouldNotBeNull();
		webRequest.ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void ClickingAddProjectButtonRaisesAddProjectRequested()
	{
		ProjectTreeView view = new();
		var raised = false;
		view.AddProjectRequested += (_, _) => raised = true;

		var addProjectButton = view.FindControl<Button>("AddProjectButton")!;
		addProjectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		raised.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void ClickingPausedWorkspaceItemRaisesResumePausedProjectRequestedWithWorkspace()
	{
		ProjectTreeView view = new();
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel paused = new(new ProjectRecord("paused-1", "Paused Project", "C:\\repo", now, now, null));
		viewModel.PausedWorkspaces.Add(paused);
		view.DataContext = viewModel;

		Window window = new() { Content = view, Width = 400, Height = 400 };
		window.Styles.Add(new FluentTheme());
		window.Show();
		Dispatcher.UIThread.RunJobs();

		var pausedProjectsButton = view.FindControl<Button>("PausedProjectsButton")!;
		pausedProjectsButton.Flyout!.ShowAt(pausedProjectsButton);
		Dispatcher.UIThread.RunJobs();
		window.UpdateLayout();
		Dispatcher.UIThread.RunJobs();

		WorkspaceViewModel? received = null;
		view.ResumePausedProjectRequested += (_, workspace) => received = workspace;

		var descendantButtons = window.GetVisualDescendants().OfType<Button>().ToArray();
		(descendantButtons.Length > 0).ShouldBeTrue(
			$"No buttons realized. Descendants: {string.Join(", ", window.GetVisualDescendants().Select(v => v.GetType().Name))}");
		var itemButton = descendantButtons.Single(button => ReferenceEquals(button.DataContext, paused));
		itemButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		received.ShouldBeSameAs(paused);
	}

	private sealed class EmptyProjectStore : IProjectStore
	{
		public Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken) =>
			Task.FromResult(ProjectsDocument.CreateDefault());
		public Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task<ProjectsDocument> UpdateAsync(Func<ProjectsDocument, ProjectsDocument> update, CancellationToken cancellationToken) =>
			Task.FromResult(update(ProjectsDocument.CreateDefault()));
	}

	private sealed class EmptyNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) => Task.FromResult(string.Empty);
		public Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
		public Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken) => Task.CompletedTask;
	}

}