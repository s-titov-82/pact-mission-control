using Avalonia.Controls;
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
