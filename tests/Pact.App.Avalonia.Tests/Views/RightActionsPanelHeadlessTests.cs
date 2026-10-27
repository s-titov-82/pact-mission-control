using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Pact.App.Avalonia.Views;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class RightActionsPanelHeadlessTests
{
	[AvaloniaTest]
	public void The_actions_title_is_gone_and_the_settings_button_stays()
	{
		RightActionsPanel panel = new();

		panel.GetLogicalDescendants().OfType<TextBlock>().ShouldNotContain(text => text.Text == "Actions");
		panel.FindControl<Button>("SettingsButton").ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void The_changes_entry_sits_between_quick_actions_and_the_tab_details()
	{
		using var changes = Changes(Files("a.cs"), added: 3, removed: 1);
		RightActionsPanel panel = new() { Changes = changes };
		panel.SetSelectedTabDetails(new SelectedTabDetailsViewModel("Terminal", "claude", []), visible: true);
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var quickActions = panel.FindControl<Control>("QuickActionsList")!;
			var entry = panel.FindControl<Control>("ChangesEntry")!;
			var details = panel.FindControl<Control>("SelectedTabDetailsSection")!;

			Top(entry, panel).ShouldBeGreaterThan(Top(quickActions, panel));
			Top(details, panel).ShouldBeGreaterThan(Top(entry, panel));
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void The_changes_entry_shows_counts_and_raises_its_request_on_click()
	{
		using var changes = Changes(Files("a.cs", "b.cs"), added: 84, removed: 12);
		RightActionsPanel panel = new() { Changes = changes };
		Window window = new() { Content = panel };
		window.Show();
		try
		{
			window.UpdateLayout();
			var requested = 0;
			panel.ChangesEntryRequested += (_, _) => requested++;
			var entry = panel.FindControl<Button>("ChangesEntry")!;

			entry.IsVisible.ShouldBeTrue();
			var texts = entry.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
			texts.ShouldContain("Changes · 2");
			texts.ShouldContain("+84 \u221212");
			entry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			requested.ShouldBe(1);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void A_clean_tree_shows_the_no_changes_line_without_a_button()
	{
		using var changes = Changes([], added: 0, removed: 0);
		RightActionsPanel panel = new() { Changes = changes };
		Window window = new() { Content = panel };
		window.Show();
		try
		{
			window.UpdateLayout();

			panel.FindControl<Button>("ChangesEntry")!.IsVisible.ShouldBeFalse();
			var clean = panel.FindControl<TextBlock>("NoChangesText")!;
			clean.IsVisible.ShouldBeTrue();
			clean.Text.ShouldBe("No uncommitted changes");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public void Moving_to_a_project_without_git_hides_the_changes_entry()
	{
		using var changes = Changes(Files("a.cs"), added: 1, removed: 0);
		RightActionsPanel panel = new() { Changes = changes };
		Window window = new() { Content = panel };
		window.Show();
		try
		{
			window.UpdateLayout();
			panel.FindControl<Control>("ChangesSection")!.IsVisible.ShouldBeTrue();

			panel.Changes = null;
			window.UpdateLayout();

			panel.FindControl<Control>("ChangesSection")!.IsVisible.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task The_changes_tab_lists_the_changed_files_above_quick_actions()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs", "b.cs"), added: 1, removed: 1, root.Path);
		var screen = Screen(root.Path, changes);
		await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
		RightActionsPanel panel = new() { Changes = changes, ProjectScreen = screen };
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var list = panel.FindControl<ListBox>("ChangedFilesList")!;

			list.IsEffectivelyVisible.ShouldBeTrue();
			list.ItemCount.ShouldBe(2);
			panel.FindControl<ListBox>("ReviewLogList")!.IsVisible.ShouldBeFalse();
			Top(panel.FindControl<Control>("QuickActionsList")!, panel).ShouldBeGreaterThan(Top(list, panel));
			panel.FindControl<Control>("ChangesEntry")!.IsVisible.ShouldBeTrue();

			list.SelectedItem = changes.Files[1];

			changes.SelectedFile.ShouldBeSameAs(changes.Files[1]);
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task The_review_logs_tab_lists_the_logs_and_opens_the_picked_one()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-a.md"), "# A");
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1300-b.md"), "# B");
		var screen = Screen(root.Path, changes: null);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		RightActionsPanel panel = new() { ProjectScreen = screen };
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var list = panel.FindControl<ListBox>("ReviewLogList")!;

			list.IsEffectivelyVisible.ShouldBeTrue();
			list.ItemCount.ShouldBe(2);
			panel.FindControl<ListBox>("ChangedFilesList")!.IsVisible.ShouldBeFalse();

			list.SelectedItem = ".pact/reviews/2026-10-26-1200-a.md";

			await EventuallyAsync(() => screen.ReviewLogText == "# A");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task A_log_row_shows_its_name_and_asks_to_delete_it()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-a.md"), "# A");
		var screen = Screen(root.Path, changes: null);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		RightActionsPanel panel = new() { ProjectScreen = screen };
		List<string> requested = [];
		panel.DeleteReviewLogRequested += (_, log) => requested.Add(log);
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var row = panel.FindControl<ListBox>("ReviewLogList")!.ContainerFromIndex(0).ShouldNotBeNull();
			var texts = row.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();

			texts.ShouldContain("2026-10-26-1200-a");
			texts.ShouldNotContain(text => text != null && text.Contains(".pact", StringComparison.Ordinal));
			row.GetLogicalDescendants().OfType<Button>().Single()
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			requested.ShouldBe([".pact/reviews/2026-10-26-1200-a.md"]);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public void The_overview_shows_no_project_list()
	{
		using var root = TemporaryDirectory.Create();
		RightActionsPanel panel = new() { ProjectScreen = Screen(root.Path, changes: null) };
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();

			panel.FindControl<Control>("ProjectListSection")!.IsVisible.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	private static ProjectScreenViewModel Screen(string root, ProjectChangesViewModel? changes)
	{
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root, now, now, null), _ => changes is not null);
		return new ProjectScreenViewModel(workspace, changes, new ProjectMarkdownFileStore());
	}

	private static async Task EventuallyAsync(Func<bool> condition)
	{
		for (var attempt = 0; attempt < 100 && !condition(); attempt++)
		{
			await Task.Delay(20);
			Dispatcher.UIThread.RunJobs();
		}

		condition().ShouldBeTrue();
	}

	private static double Top(Control control, Control root) =>
		control.TranslatePoint(new Point(0, 0), root)!.Value.Y;

	private static GitFileEntry[] Files(params string[] paths) =>
		paths.Select(path => new GitFileEntry(path, null, GitChangeKind.Modified)).ToArray();

	private static ProjectChangesViewModel Changes(GitFileEntry[] files, int added, int removed, string root = "C:\\repo")
	{
		ProjectChangesViewModel changes = new(root, new FixedReader(files, added, removed), TimeProvider.System, action => action());
		changes.Observe();
		return changes;
	}

	private sealed class FixedReader(GitFileEntry[] files, int added, int removed) : IGitWorkingTreeReader
	{
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges("head", "main", files, added, removed));

		public Task<FileDiff> ReadDiffAsync(
			string root,
			GitFileEntry entry,
			string? headCommit,
			CancellationToken cancellationToken) =>
			Task.FromResult(new FileDiff(null, []));
	}
}
