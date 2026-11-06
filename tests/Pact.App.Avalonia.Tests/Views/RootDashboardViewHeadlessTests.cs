using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Agents;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class RootDashboardViewHeadlessTests
{
	[AvaloniaTest]
	public async Task Paused_and_recent_linear_lists_are_below_cards_and_route_their_play_buttons()
	{
		WorkspaceViewModel paused = new(new ProjectRecord("paused", "Paused project", "C:/paused",
			DateTimeOffset.MinValue, DateTimeOffset.MinValue, null));
		using RootDashboardViewModel dashboard = new([], new UnusedReader(), _ => false, a => a(), TimeProvider.System,
			[paused], _ => Task.FromResult<IReadOnlyList<string>>(["C:/paused", "C:/recent folder"]));
		await dashboard.ActivateAsync(CancellationToken.None);
		RootDashboardView view = new() { DataContext = dashboard };
		Window window = new() { Content = view, Width = 900, Height = 600 };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			WorkspaceViewModel? resumed = null;
			string? recent = null;
			view.ResumeProjectRequested += (_, project) => resumed = project;
			view.RecentDirectoryRequested += (_, root) => recent = root;
			var buttons = view.GetVisualDescendants().OfType<Button>().ToArray();
			buttons.Single(button => button.Classes.Contains("dashboard-resume-project"))
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			resumed.ShouldBeSameAs(paused);
			recent.ShouldBeNull();
			buttons.Single(button => button.Classes.Contains("dashboard-open-recent"))
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			recent.ShouldBe("C:/recent folder");
			var lists = view.FindControl<Grid>("ProjectLists")!;
			lists.Bounds.Top.ShouldBeGreaterThanOrEqualTo(view.FindControl<ItemsControl>("ProjectCards")!.Bounds.Bottom);
			view.FindControl<Border>("PausedProjectsPanel")!.Bounds.Left
				.ShouldBeLessThan(view.FindControl<Border>("RecentDirectoriesPanel")!.Bounds.Left);
			window.Width = 300;
			window.UpdateLayout();
			Dispatcher.UIThread.RunJobs();
			lists.Bounds.Width.ShouldBeLessThanOrEqualTo(view.Bounds.Width - 32);
			view.FindControl<Border>("RecentDirectoriesPanel")!.Bounds.Right.ShouldBeLessThanOrEqualTo(lists.Bounds.Width);
		}
		finally { window.Close(); Application.Current!.Styles.Remove(theme); }
	}
	[AvaloniaTest]
	public void Card_actions_keep_their_identity_and_fit_a_narrow_center_pane()
	{
		WorkspaceViewModel project = new(new ProjectRecord("one", "Project one", "C:/one",
			DateTimeOffset.MinValue, DateTimeOffset.MinValue, null), _ => true);
		SessionViewModel session = new(new SessionRecord("agent", AgentKind.Claude, "Claude", "C:/one",
			"claude", null, SessionStatus.Running, DateTimeOffset.MinValue, DateTimeOffset.MinValue));
		project.Sessions.Add(session);
		using RootDashboardViewModel dashboard = new([project], new UnusedReader(), _ => true, a => a(), TimeProvider.System);
		dashboard.Cards.ShouldHaveSingleItem().Agents.ShouldBe([session]);
		RootDashboardView view = new() { DataContext = dashboard };
		Window window = new() { Content = view, Width = 300, Height = 500 };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		try
		{
			window.Show();
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			var items = view.FindControl<ItemsControl>("ProjectCards")!;
			items.ItemsSource.ShouldNotBeNull().Cast<object>().Count().ShouldBe(1);
			items.ApplyTemplate();
			window.UpdateLayout();
			items.IsEffectivelyVisible.ShouldBeTrue();
			items.Bounds.Width.ShouldBeGreaterThan(0);
			items.Template.ShouldNotBeNull();
			WorkspaceViewModel? opened = null;
			WorkspaceViewModel? changes = null;
			SessionViewModel? agent = null;
			view.ProjectRequested += (_, p) => opened = p;
			view.ChangesRequested += (_, p) => changes = p;
			view.AgentRequested += (_, s) => agent = s;
			var buttons = view.GetVisualDescendants().OfType<Button>().ToArray();
			var agentButton = buttons.Single(b => b.Classes.Contains("dashboard-agent"));
			agentButton.Focus();
			dashboard.RefreshAgents();
			project.UpdateRecord(project.Record with { Name = "Renamed" });
			Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			view.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("dashboard-agent"))
				.ShouldBeSameAs(agentButton);
			agentButton.IsFocused.ShouldBeTrue();
			buttons.ShouldContain(b => b.Classes.Contains("dashboard-agent"),
				string.Join("; ", view.GetVisualDescendants().Select(v => v.GetType().Name + ":" + (v as Control)?.DataContext?.GetType().Name)));
			buttons.Single(b => b.Classes.Contains("dashboard-agent")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			agent.ShouldBeSameAs(session);
			opened.ShouldBeNull();
			changes.ShouldBeNull();
			buttons.Single(b => b.Classes.Contains("dashboard-changes")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			changes.ShouldBeSameAs(project);
			opened.ShouldBeNull();
			buttons.Single(b => b.Classes.Contains("dashboard-title")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			opened.ShouldBeSameAs(project);
			var panel = view.GetVisualDescendants().OfType<WrapPanel>().Single();
			panel.ItemWidth.ShouldBeLessThanOrEqualTo(view.Bounds.Width - 32);
		}
		finally
		{
			window.Close();
			Application.Current!.Styles.Remove(theme);
		}
	}

	private sealed class UnusedReader : IGitWorkingTreeReader
	{
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
}