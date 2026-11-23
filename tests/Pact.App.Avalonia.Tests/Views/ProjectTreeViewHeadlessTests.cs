using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectTreeViewHeadlessTests
{
	[Test]
	public void Drag_threshold_and_drop_half_decisions_are_stable()
	{
		ProjectTreeView.HasExceededDragThreshold(
			new Point(10, 10),
			new Point(13, 13),
			new Size(8, 8)).ShouldBeFalse();
		ProjectTreeView.HasExceededDragThreshold(
			new Point(10, 10),
			new Point(15, 10),
			new Size(8, 8)).ShouldBeTrue();
		ProjectTreeView.IsInsertAfter(9, 20).ShouldBeFalse();
		ProjectTreeView.IsInsertAfter(10, 20).ShouldBeTrue();
	}

	[AvaloniaTest]
	public void Drop_targets_require_same_type_and_owner()
	{
		ProjectTreeView view = new();
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		SessionViewModel first = new(CreateSession("session-1", now));
		SessionViewModel second = new(CreateSession("session-2", now));
		SessionViewModel otherProject = new(CreateSession("session-3", now));
		SessionViewModel root = new(CreateSession("root-session", now), isRootItem: true);
		WorkspaceViewModel firstWorkspace = new(
			new ProjectRecord("project-1", "One", "C:\\one", now, now, null));
		WorkspaceViewModel secondWorkspace = new(
			new ProjectRecord("project-2", "Two", "C:\\two", now, now, null));
		firstWorkspace.Sessions.Add(first);
		firstWorkspace.Sessions.Add(second);
		secondWorkspace.Sessions.Add(otherProject);
		viewModel.Workspaces.Add(firstWorkspace);
		viewModel.Workspaces.Add(secondWorkspace);
		view.DataContext = viewModel;

		view.CanDropTreeItem(first, second).ShouldBeTrue();
		view.CanDropTreeItem(first, otherProject).ShouldBeFalse();
		view.CanDropTreeItem(first, root).ShouldBeFalse();
		view.CanDropTreeItem(first, first).ShouldBeFalse();
		view.CanDropTreeItem(first, new object()).ShouldBeFalse();
		view.CanDropTreeItem(firstWorkspace, secondWorkspace).ShouldBeTrue();
		view.CanDropTreeItem(firstWorkspace, firstWorkspace).ShouldBeFalse();
		view.CanDropTreeItem(firstWorkspace, first).ShouldBeFalse();
		viewModel.Workspaces.Remove(secondWorkspace);
		viewModel.PausedWorkspaces.Add(secondWorkspace);
		view.CanDropTreeItem(firstWorkspace, secondWorkspace).ShouldBeFalse();
	}

	[AvaloniaTest]
	public void Dragging_a_project_title_requests_a_move_and_a_click_opens_the_project()
	{
		MainWindowViewModel model = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel first = new(new ProjectRecord("one", "One", "C:\\one", now, now, null));
		WorkspaceViewModel second = new(new ProjectRecord("two", "Two", "C:\\two", now, now, null));
		model.Workspaces.Add(first);
		model.Workspaces.Add(second);
		model.SelectProject(first);
		ProjectTreeView view = new() { DataContext = model };
		TreeItemDropRequest? request = null;
		List<object?> activations = [];
		view.TreeItemDropRequested += (_, args) => request = args;
		view.SelectedItemChanged += (_, item) => activations.Add(item);
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = view, Width = 600, Height = 800 };
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			var tree = view.FindControl<TreeView>("ProjectTree").ShouldNotBeNull();
			Point HeaderPoint(WorkspaceViewModel project)
			{
				var item = tree.ContainerFromItem(project).ShouldBeOfType<TreeViewItem>();
				var title = item.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == project.Name);
				return title.TranslatePoint(new Point(3, 5), window)!.Value;
			}

			var start = HeaderPoint(second);
			var drop = HeaderPoint(first);
			view.BeginDragAsync = (_, transfer) =>
			{
				var hit = window.InputHitTest(drop).ShouldBeAssignableTo<Control>().ShouldNotBeNull();
				var args = new DragEventArgs(DragDrop.DropEvent, transfer, hit, window.TranslatePoint(drop, hit)!.Value, KeyModifiers.None);
				if (DragDrop.GetAllowDrop(hit))
				{
					hit.RaiseEvent(args);
				}
				return Task.FromResult(args.DragEffects);
			};
			var moved = new Point(start.X + 16, start.Y);
			window.MouseMove(start);
			window.MouseDown(start, MouseButton.Left);
			window.MouseMove(moved, RawInputModifiers.LeftMouseButton);
			window.MouseUp(moved, MouseButton.Left);

			request.ShouldBe(new TreeItemDropRequest(second, first, InsertAfter: false));
			activations.ShouldBeEmpty();

			window.MouseMove(start);
			window.MouseDown(start, MouseButton.Left);
			activations.ShouldBeEmpty();
			window.MouseUp(start, MouseButton.Left);

			activations.ShouldBe([second]);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public void Orchestrator_tier_uses_the_terminal_current_style_only_while_selected()
	{
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		SessionViewModel orchestrator = new(CreateSession("orchestrator", now));
		viewModel.OrchestratorSlot.AttachSession(orchestrator);
		ProjectTreeView view = new() { DataContext = viewModel };
		SolidColorBrush surface = new(Color.Parse("#010203"));
		SolidColorBrush border = new(Color.Parse("#040506"));
		SolidColorBrush accentSoft = new(Color.Parse("#070809"));
		SolidColorBrush accent = new(Color.Parse("#0A0B0C"));
		Window window = new()
		{
			RequestedThemeVariant = ThemeVariant.Light,
			Content = view
		};
		window.Resources["AppSurfaceBrush"] = surface;
		window.Resources["AppBorderBrush"] = border;
		window.Resources["AppAccentSoftBrush"] = accentSoft;
		window.Resources["AppAccentBrush"] = accent;
		window.Show();
		var tier = view.FindControl<Border>("OrchestratorTier").ShouldNotBeNull();

		AssertTierStyle(
			tier,
			surface,
			border,
			new Thickness(1));

		viewModel.SelectedSession = orchestrator;

		viewModel.OrchestratorSlot.IsCurrent.ShouldBeTrue();
		tier.Classes.ShouldContain("current");
		AssertTierStyle(
			tier,
			accentSoft,
			accent,
			new Thickness(3, 0, 0, 0));

		viewModel.SelectedSession = null;

		viewModel.OrchestratorSlot.IsCurrent.ShouldBeFalse();
		tier.Classes.ShouldNotContain("current");
		AssertTierStyle(
			tier,
			surface,
			border,
			new Thickness(1));
		window.Close();
	}

	[AvaloniaTest]
	public void Project_rows_open_as_saved_and_report_the_user_opening_or_closing_them()
	{
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel collapsed = new(
			new ProjectRecord("project-1", "One", "C:\\one", now, now, null) { IsCollapsed = true },
			static _ => false);
		WorkspaceViewModel expanded = new(new ProjectRecord("project-2", "Two", "C:\\two", now, now, null), static _ => false);
		viewModel.Workspaces.Add(collapsed);
		viewModel.Workspaces.Add(expanded);
		ProjectTreeView view = new() { DataContext = viewModel };
		List<(WorkspaceViewModel Workspace, bool IsExpanded)> changes = [];
		view.ProjectExpansionChanged += (_, change) => changes.Add(change);
		// The headless app has no theme; the tree needs one to create its rows.
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = view, Width = 600, Height = 800 };
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			var tree = view.FindControl<TreeView>("ProjectTree").ShouldNotBeNull();
			var collapsedRow = tree.ContainerFromItem(collapsed).ShouldBeOfType<TreeViewItem>();
			var expandedRow = tree.ContainerFromItem(expanded).ShouldBeOfType<TreeViewItem>();

			collapsedRow.IsExpanded.ShouldBeFalse();
			expandedRow.IsExpanded.ShouldBeTrue();
			changes.ShouldBeEmpty();

			expandedRow.IsExpanded = false;
			collapsedRow.IsExpanded = true;

			changes.ShouldBe([(expanded, false), (collapsed, true)]);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public void Project_header_shades_the_chevron_and_title_and_highlights_the_current_project()
	{
		MainWindowViewModel viewModel = new(new EmptyProjectStore(), new EmptyNotesStore());
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("project-1", "One", "C:\\one", now, now, null), static _ => false);
		workspace.Sessions.Add(new SessionViewModel(CreateSession("session-1", now)));
		viewModel.Workspaces.Add(workspace);
		ProjectTreeView view = new() { DataContext = viewModel };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = view, Width = 600, Height = 800 };
		SolidColorBrush background = new(Color.Parse("#334455"));
		SolidColorBrush accentSoft = new(Color.Parse("#445566"));
		SolidColorBrush accent = new(Color.Parse("#556677"));
		window.Resources["AppProjectHeaderBrush"] = background;
		window.Resources["AppAccentSoftBrush"] = accentSoft;
		window.Resources["AppAccentBrush"] = accent;
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			var tree = view.FindControl<TreeView>("ProjectTree").ShouldNotBeNull();
			var container = tree.ContainerFromItem(workspace).ShouldBeOfType<TreeViewItem>();
			var chevron = container.GetVisualDescendants().OfType<ToggleButton>().First();
			var title = container.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == workspace.Name);
			var header = chevron.GetVisualAncestors().OfType<Border>()
				.First(border => title.GetVisualAncestors().Contains(border));
			header.Background.ShouldBeSameAs(background);

			viewModel.SelectProject(workspace);

			AssertTierStyle(header, accentSoft, accent, new Thickness(3, 0, 0, 0));
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	private static void AssertTierStyle(
		Border tier,
		SolidColorBrush expectedBackground,
		SolidColorBrush expectedBorder,
		Thickness borderThickness)
	{
		tier.Background.ShouldBeOfType<SolidColorBrush>().Color
			.ShouldBe(expectedBackground.Color);
		tier.BorderBrush.ShouldBeOfType<SolidColorBrush>().Color
			.ShouldBe(expectedBorder.Color);
		tier.BorderThickness.ShouldBe(borderThickness);
	}

	private static SessionRecord CreateSession(string id, DateTimeOffset now) => new(
		id,
		AgentKind.Codex,
		id,
		"C:\\",
		"codex",
		null,
		SessionStatus.Stopped,
		now,
		now);

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
