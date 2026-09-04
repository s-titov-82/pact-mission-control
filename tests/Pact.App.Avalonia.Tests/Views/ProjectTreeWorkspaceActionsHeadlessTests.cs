using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Projects;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

/// <summary>
/// Exercises the seven-button workspace card added in Task 9. Building the workspace
/// <see cref="TreeDataTemplate"/> content directly (rather than realizing it through a live
/// <see cref="TreeView"/>) sidesteps TreeView container virtualization/theming concerns that
/// are irrelevant to what this suite verifies: that each button raises its event carrying the
/// row's <see cref="WorkspaceViewModel"/>.
/// </summary>
public sealed class ProjectTreeWorkspaceActionsHeadlessTests
{
	[AvaloniaTest]
	public void ClickingProjectPencilRaisesEditProjectRequested()
	{
		WorkspaceViewModel? received = null;
		RunWorkspaceCardTest(
			view => view.EditProjectRequested += (_, workspace) => received = workspace,
			"Edit project settings",
			(workspace, button) =>
			{
				button.IsEnabled.ShouldBeTrue();
				received.ShouldBeSameAs(workspace);
			});
	}

	[AvaloniaTest]
	public void ClickingAddSessionButtonRaisesAddSessionRequestedWithWorkspace()
	{
		WorkspaceActionFlyoutRequest? received = null;
		RunWorkspaceCardTest(
			view => view.AddSessionRequested += (_, request) => received = request,
			"Add terminal",
			(workspace, button) =>
			{
				(received?.Workspace).ShouldBeSameAs(workspace);
				(received?.Anchor).ShouldBeSameAs(button);
			});
	}

	[AvaloniaTest]
	public void ClickingAddWebPageButtonRaisesAddWebPageRequestedWithWorkspace()
	{
		WorkspaceActionFlyoutRequest? received = null;
		RunWorkspaceCardTest(
			view => view.AddWebPageRequested += (_, request) => received = request,
			"Add web page",
			(workspace, button) =>
			{
				(received?.Workspace).ShouldBeSameAs(workspace);
				(received?.Anchor).ShouldBeSameAs(button);
			});
	}

	[AvaloniaTest]
	public void ClickingNotesToggleButtonRaisesNotesToggleRequestedWithWorkspace()
	{
		WorkspaceViewModel? received = null;
		RunWorkspaceCardTest(
			view => view.NotesToggleRequested += (_, workspace) => received = workspace,
			"Show/hide project docs and notes",
			(workspace, _) => received.ShouldBeSameAs(workspace));
	}

	[AvaloniaTest]
	public void ClickingGitButtonRaisesGitRequestedWithWorkspaceAndAnchor()
	{
		ProjectTreeView view = new();
		GitFlyoutRequest? received = null;
		view.GitRequested += (_, request) => received = request;
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(
			new ProjectRecord("project-1", "Project", "C:\\repo", now, now, null),
			isGitRepository: static _ => true);
		var tree = view.FindControl<TreeView>("ProjectTree")!;
		var template = tree.DataTemplates.Single(candidate => candidate.Match(workspace));
		var cardRoot = template.Build(workspace)!;
		cardRoot.DataContext = workspace;
		var gitButton = cardRoot.GetSelfAndVisualDescendants().OfType<Button>()
			.Single(button => Equals(ToolTip.GetTip(button), "Git"));

		gitButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		received.ShouldNotBeNull();
		received.Workspace.ShouldBeSameAs(workspace);
		received.Anchor.ShouldBeSameAs(gitButton);
	}

	private static void RunWorkspaceCardTest(
		Action<ProjectTreeView> wireEvent,
		string buttonToolTip,
		Action<WorkspaceViewModel, Button> assertReceived)
	{
		ProjectTreeView view = new();
		wireEvent(view);

		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(
			new ProjectRecord("project-1", "Project", "C:\\repo", now, now, null),
			isGitRepository: static _ => false);

		var tree = view.FindControl<TreeView>("ProjectTree")!;
		var template = tree.DataTemplates.Single(candidate => candidate.Match(workspace));
		var cardRoot = template.Build(workspace)!;
		cardRoot.DataContext = workspace;

		var actionButton = cardRoot.GetSelfAndVisualDescendants()
			.OfType<Button>()
			.Single(button => Equals(ToolTip.GetTip(button), buttonToolTip));

		actionButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

		assertReceived(workspace, actionButton);
	}
}