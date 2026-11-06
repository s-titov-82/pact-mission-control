using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Views;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Presentation.Services.Remarks;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class RightActionsPanelHeadlessTests
{
	[AvaloniaTest]
	public async Task Three_scope_controls_filter_commits_and_return_to_live_aggregate_in_a_narrow_window()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs"), 1, 0, root.Path);
		var screen = Screen(root.Path, changes);
		await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view, Width = 460, Height = 720 };
		window.Show();
		try
		{
			view.FindControl<Button>("UnmergedScopeButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await EventuallyAsync(() => changes.SelectedReviewScope == GitReviewScope.Unmerged && changes.ScopeCommits.Count == 1);
			var filter = view.FindControl<ComboBox>("ReviewFilter")!;
			filter.Items.Count.ShouldBe(3);
			filter.SelectedIndex = 2;
			await EventuallyAsync(() => changes.SelectedCommit is not null);
			changes.SelectedReviewScope.ShouldBe(GitReviewScope.Unmerged);
			filter.SelectedIndex = 1;
			await EventuallyAsync(() => changes.IsUncommittedFilter);
			changes.WorkingTreeFileCount.ShouldBe(1);
			filter.SelectedIndex = 0;
			await EventuallyAsync(() => changes.SelectedCommit is null && !changes.IsUncommittedFilter && changes.Files.Count == 3);
			window.UpdateLayout();
			view.FindControl<Control>("ReviewTargetButton")!.IsEffectivelyVisible.ShouldBeTrue();
			view.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Text == "Loading…").IsVisible.ShouldBeFalse();
			foreach (var name in new[] { "UncommittedScopeButton", "UnmergedScopeButton", "UnpushedScopeButton" })
			{
				var button = view.FindControl<Button>(name)!;
				var position = button.TranslatePoint(default, window)!.Value;
				position.X.ShouldBeGreaterThanOrEqualTo(0);
				(position.X + button.Bounds.Width).ShouldBeLessThanOrEqualTo(window.Bounds.Width);
			}
		}
		finally { window.Close(); }
	}

	[AvaloniaTest]
	public async Task Review_pull_has_an_explicit_strategy_and_respects_hidden_pull_setting()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs"), 1, 0, root.Path);
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p", "Pact", root.Path, now, now, null), _ => true);
		var commands = GitButtonCommandSet.Create([new GitButtonCommandRecord(GitButtonCommandSet.PullId, "Pull", Enabled: false)]);
		GitPanelViewModel git = new(root.Path, new NoGitWrites(), [], (_, _, _) => { }, _ => false, commands);
		ProjectScreenViewModel screen = new(workspace, changes, new ProjectMarkdownFileStore(), repository: git);
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view };
		window.Show();
		try
		{
			await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
			var button = view.FindControl<Button>("PullRebaseButton")!;
			button.IsVisible.ShouldBeFalse();
			button.Content.ShouldBe("Pull (rebase)");
			ToolTip.GetTip(button).ShouldNotBeNull().ToString()!.ShouldContain("never stashes");
		}
		finally { window.Close(); }
	}

	private sealed class NoGitWrites : IGitCliRunner
	{
		public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, IProgress<string>? outputLine, CancellationToken cancellationToken) =>
			Task.FromResult(new GitCommandResult(1, "", "not used"));
	}

	[AvaloniaTest]
	public async Task Working_tree_and_current_branch_have_independent_counts_and_navigation_requests()
	{
		using var changes = Changes(Files("a.cs", "b.cs"), 84, 12);
		RightActionsPanel panel = new() { Changes = changes };
		Window window = new() { Content = panel, Width = 360, Height = 700 };
		window.Show();
		try
		{
			var working = 0;
			var branch = 0;
			panel.ReviewScopeRequested += (_, scope) => { if (scope == GitReviewScope.Uncommitted) { working++; } };
			panel.ReviewScopeRequested += (_, scope) => { if (scope == GitReviewScope.Unmerged) { branch++; } };
			var workingEntry = panel.FindControl<Button>("ChangesEntry").ShouldNotBeNull();
			var branchEntry = panel.FindControl<Button>("UnmergedEntry").ShouldNotBeNull();
			branchEntry.IsEnabled.ShouldBeTrue();
			panel.FindControl<TextBlock>("UnmergedEntryTitle")!.Text.ShouldBe("Unmerged · 3");
			panel.FindControl<TextBlock>("UnmergedEntrySubtitle")!.Text.ShouldBe("+5 −2");
			branchEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			workingEntry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			branch.ShouldBe(1);
			working.ShouldBe(1);
			var unpushed = 0;
			panel.ReviewScopeRequested += (_, scope) => { if (scope == GitReviewScope.Unpushed) { unpushed++; } };
			panel.FindControl<Button>("UnpushedEntry")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			unpushed.ShouldBe(1);
			panel.FindControl<TextBlock>("UnpushedEntryTitle")!.Text.ShouldBe("Unpushed · 3");
			await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
			panel.FindControl<TextBlock>("ChangesEntryTitle")!.Text.ShouldBe("Uncommitted · 2");
			panel.FindControl<TextBlock>("ChangesEntrySubtitle")!.Text.ShouldBe("+84 −12");
			changes.Files.Count.ShouldBe(3);
		}
		finally { window.Close(); }
	}
	[AvaloniaTest]
	public void The_actions_title_is_gone_and_the_settings_button_stays()
	{
		RightActionsPanel panel = new();

		panel.GetLogicalDescendants().OfType<TextBlock>().ShouldNotContain(text => text.Text == "Actions");
		panel.FindControl<Button>("SettingsButton").ShouldNotBeNull();
	}

	[AvaloniaTest]
	public void The_changes_entry_sits_on_top_above_quick_actions_and_the_tab_details()
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

			Top(quickActions, panel).ShouldBeGreaterThan(Top(entry, panel));
			Top(details, panel).ShouldBeGreaterThan(Top(quickActions, panel));
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
			panel.ReviewScopeRequested += (_, _) => requested++;
			var entry = panel.FindControl<Button>("ChangesEntry")!;

			entry.IsVisible.ShouldBeTrue();
			var texts = entry.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
			texts.ShouldContain("Uncommitted · 2");
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
	public void Agent_files_show_under_the_changes_entry()
	{
		using var changes = Changes(Files("a.cs"), added: 1, removed: 0);
		using var files = AgentFiles(@"D:\proj\docs\plan.md", @"C:\tools\bin\run.ps1");
		RightActionsPanel panel = new() { Changes = changes, AgentFiles = files };
		InThemedWindow(panel, () =>
		{
			var section = panel.FindControl<Control>("AgentFilesSection")!;

			section.IsVisible.ShouldBeTrue();
			var texts = section.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
			texts.ShouldContain("Agent files");
			texts.ShouldContain("plan.md");
			texts.ShouldContain("project");
			texts.ShouldContain("run.ps1");
			texts.ShouldContain("bin");
			Top(section, panel).ShouldBeGreaterThan(Top(panel.FindControl<Control>("ChangesEntry")!, panel));

			panel.AgentFiles = null;
			section.IsVisible.ShouldBeFalse();
			using var empty = AgentFiles();
			panel.AgentFiles = empty;
			section.IsVisible.ShouldBeFalse();
		});
	}

	[AvaloniaTest]
	public void Clicking_an_agent_file_raises_the_request()
	{
		using var files = AgentFiles(@"C:\tools\a.txt");
		RightActionsPanel panel = new() { AgentFiles = files };
		InThemedWindow(panel, () =>
		{
			List<AgentFileItem> requested = [];
			panel.AgentFileRequested += (_, item) => requested.Add(item);

			AgentFileButtons(panel).First(button => button.Classes.Contains("agent-file"))
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			requested.ShouldBe([files.Items.Single()]);
		});
	}

	[AvaloniaTest]
	public void The_cross_raises_removal_and_not_the_open_request()
	{
		using var files = AgentFiles(@"C:\tools\a.txt");
		RightActionsPanel panel = new() { AgentFiles = files };
		InThemedWindow(panel, () =>
		{
			List<AgentFileItem> opened = [];
			List<AgentFileItem> removed = [];
			panel.AgentFileRequested += (_, item) => opened.Add(item);
			panel.AgentFileRemoveRequested += (_, item) => removed.Add(item);

			AgentFileButtons(panel).First(button => ToolTip.GetTip(button) as string == "Remove link")
				.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			removed.ShouldBe([files.Items.Single()]);
			opened.ShouldBeEmpty();
		});
	}

	private static IEnumerable<Button> AgentFileButtons(RightActionsPanel panel) =>
		panel.FindControl<Control>("AgentFilesSection")!.GetVisualDescendants().OfType<Button>();

	// The list's rows come from the theme's templates.
	private static void InThemedWindow(RightActionsPanel panel, Action test)
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			test();
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	private static AgentFilesViewModel AgentFiles(params string[] paths)
	{
		var at = DateTimeOffset.UtcNow;
		SessionViewModel session = new(new SessionRecord(
			"s1", Pact.Core.Agents.AgentKind.Claude, "Claude", @"D:\proj", "claude", null, SessionStatus.Stopped, at, at)
		{
			FileLinks = [.. paths.Select(path => new AgentFileLink(path, null, at))]
		});
		return new AgentFilesViewModel(session, @"D:\proj", [], (_, _, _) => Task.CompletedTask);
	}

	[AvaloniaTest]
	public void A_clean_tree_keeps_a_clickable_working_tree_entry_with_zero_counts()
	{
		using var changes = Changes([], added: 0, removed: 0);
		RightActionsPanel panel = new() { Changes = changes };
		Window window = new() { Content = panel };
		window.Show();
		try
		{
			window.UpdateLayout();

			panel.FindControl<Button>("ChangesEntry")!.IsVisible.ShouldBeTrue();
			panel.FindControl<TextBlock>("ChangesEntryTitle")!.Text.ShouldBe("Uncommitted · 0");
			panel.FindControl<TextBlock>("ChangesEntrySubtitle")!.Text.ShouldBe("+0 −0");
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
	public async Task The_changes_tab_lists_the_files_under_the_entry_without_quick_actions()
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
			var entry = panel.FindControl<Control>("ChangesEntry")!;
			entry.IsVisible.ShouldBeTrue();
			Top(list, panel).ShouldBeGreaterThan(Top(entry, panel));
			panel.FindControl<Control>("QuickActionsSection")!.IsVisible.ShouldBeFalse();

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
	public async Task Delete_all_shows_only_while_there_are_logs_and_asks_to_delete_them()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-a.md"), "# A");
		var screen = Screen(root.Path, changes: null);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		RightActionsPanel panel = new() { ProjectScreen = screen };
		var requests = 0;
		panel.DeleteAllReviewLogsRequested += (_, _) => requests++;
		var deleteAll = panel.FindControl<Button>("DeleteAllReviewLogsButton")!;

		deleteAll.IsVisible.ShouldBeTrue();
		deleteAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
		requests.ShouldBe(1);

		await screen.DeleteAllReviewLogsAsync(CancellationToken.None);

		deleteAll.IsVisible.ShouldBeFalse();
	}

	[AvaloniaTest]
	public async Task Delete_all_shows_its_label_next_to_the_title()
	{
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-a.md"), "# A");
		var screen = Screen(root.Path, changes: null);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		RightActionsPanel panel = new() { ProjectScreen = screen };
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = panel, Width = 320, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var deleteAll = panel.FindControl<Button>("DeleteAllReviewLogsButton")!;
			var label = deleteAll.GetLogicalDescendants().OfType<TextBlock>().Single();
			var title = panel.FindControl<TextBlock>("ProjectListTitle")!;

			label.Text.ShouldBe("Delete all");
			// compact-icon fixes the width at 24 and clips the label; the test app does not load
			// App.axaml styles, so the class itself is checked.
			deleteAll.Classes.ShouldNotContain("compact-icon");
			deleteAll.Bounds.Width.ShouldBeGreaterThanOrEqualTo(label.DesiredSize.Width + 12);
			var titleRight = title.TranslatePoint(new Point(title.Bounds.Width, 0), window)!.Value.X;
			var buttonLeft = deleteAll.TranslatePoint(default, window)!.Value.X;
			(buttonLeft - titleRight).ShouldBeInRange(4, 16);
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

	[AvaloniaTest]
	public async Task The_changes_entry_shows_open_remarks()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs"), added: 1, removed: 0, root.Path);
		using RemarkBook book = new(root.Path, new RemarkFileStore(root.Path), TimeProvider.System);
		using ChangesRemarksViewModel remarks = Remarks(changes, book);
		RightActionsPanel panel = new() { Changes = changes, Remarks = remarks };
		Window window = new() { Content = panel };
		window.Show();
		try
		{
			var line = panel.FindControl<TextBlock>("ChangesEntryRemarks")!;
			line.IsVisible.ShouldBeFalse();

			await book.AddAsync(OldAnchor("a.cs"), null, "why?", CancellationToken.None);

			await EventuallyAsync(() => line.IsVisible && line.Text == "remarks: 1");
		}
		finally
		{
			window.Close();
		}
	}

	[AvaloniaTest]
	public async Task A_file_row_shows_its_open_remarks()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs", "b.cs"), added: 1, removed: 0, root.Path);
		using RemarkBook book = new(root.Path, new RemarkFileStore(root.Path), TimeProvider.System);
		using ChangesRemarksViewModel remarks = Remarks(changes, book);
		var screen = Screen(root.Path, changes);
		await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
		RightActionsPanel panel = new() { ProjectScreen = screen };
		// List rows build their content from the theme's item template.
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			await book.AddAsync(OldAnchor("b.cs"), null, "one", CancellationToken.None);
			await book.AddAsync(OldAnchor("b.cs"), null, "two", CancellationToken.None);
			window.UpdateLayout();

			var list = panel.FindControl<ListBox>("ChangedFilesList")!;
			await EventuallyAsync(() => RowTexts(list, 1).Contains("2 remarks"));
			RowTexts(list, 0).ShouldNotContain(text => text != null && text.Contains("remark", StringComparison.Ordinal));
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	[AvaloniaTest]
	public async Task The_remark_buttons_stay_under_the_files_on_the_changes_tab_only()
	{
		using var root = TemporaryDirectory.Create();
		using var changes = Changes(Files("a.cs"), added: 1, removed: 0, root.Path);
		using RemarkBook book = new(root.Path, new RemarkFileStore(root.Path), TimeProvider.System);
		using ChangesRemarksViewModel remarks = Remarks(changes, book);
		var screen = Screen(root.Path, changes, remarks);
		await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
		RightActionsPanel panel = new() { Changes = changes, ProjectScreen = screen };
		Window window = new() { Content = panel, Height = 800 };
		window.Show();
		try
		{
			window.UpdateLayout();
			var buttons = panel.FindControl<Control>("RemarksPanel")!;
			var list = panel.FindControl<ListBox>("ChangedFilesList")!;

			buttons.IsEffectivelyVisible.ShouldBeTrue();
			Top(buttons, panel).ShouldBeGreaterThan(Top(list, panel));
			buttons.GetLogicalDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)
				.Select(button => button.Content as string)
				.ShouldBe(["Resolved (0)", "Open (0)", "Send", "Очистить замечания…"]);
			buttons.FindControl<Button>("CleanupRemarksButton")!.IsEnabled.ShouldBeFalse();
			buttons.FindControl<Control>("CleanupQuestion")!.IsVisible.ShouldBeFalse();

			await screen.SelectTabAsync(ProjectScreenTab.Overview, CancellationToken.None);
			window.UpdateLayout();

			buttons.IsVisible.ShouldBeFalse();
		}
		finally
		{
			window.Close();
		}
	}

	private static List<string?> RowTexts(ListBox list, int index) =>
		list.ContainerFromIndex(index)?.GetLogicalDescendants().OfType<TextBlock>()
			.Where(text => text.IsEffectivelyVisible)
			.Select(text => text.Text)
			.ToList() ?? [];

	private static ChangesRemarksViewModel Remarks(ProjectChangesViewModel changes, RemarkBook book) =>
		new(
			changes,
			book,
			() => [],
			(_, _, _) => Task.FromResult(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true)),
			action => Dispatcher.UIThread.Post(action));

	private static RemarkAnchor OldAnchor(string path) => new(path, RemarkSide.Old, 1, "x", [], [], "head", null);

	private static ProjectScreenViewModel Screen(string root, ProjectChangesViewModel? changes, ChangesRemarksViewModel? remarks = null)
	{
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root, now, now, null), _ => changes is not null);
		return new ProjectScreenViewModel(workspace, changes, new ProjectMarkdownFileStore(), remarks: remarks);
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

	private sealed class FixedReader(GitFileEntry[] files, int added, int removed) : IGitWorkingTreeReader, IGitCommitReader, IGitComparisonReader, IGitReviewReader
	{
		public Task<GitReviewChanges> ReadReviewChangesAsync(string root, GitReviewScope scope, string? targetBranch, CancellationToken cancellationToken) => Task.FromResult(new GitReviewChanges(scope, "main", "origin/main", new GitLocalChanges(scope == GitReviewScope.Uncommitted ? "head" : "base", "head", scope == GitReviewScope.Uncommitted ? files : _branchFiles, scope == GitReviewScope.Uncommitted ? added : 5, scope == GitReviewScope.Uncommitted ? removed : 2), 1, 0, false, null));
		public Task<GitLocalChanges> ReadLocalChangesAsync(string root, string? baseCommit, CancellationToken cancellationToken) => Task.FromResult(new GitLocalChanges(baseCommit, "head", files, added, removed));
		public Task<FileDiff> ReadLocalDiffAsync(string root, GitFileEntry entry, string? baseCommit, CancellationToken cancellationToken) => ReadDiffAsync(root, entry, baseCommit, cancellationToken);
		public Task<IReadOnlyList<string>> ReadBranchesAsync(string root, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(["main"]);
		public Task<IReadOnlyList<GitCommitInfo>> ReadScopeCommitsAsync(string root, string? baseCommit, string headCommit, int skip, int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitCommitInfo>>([_head]);
		private readonly GitFileEntry[] _branchFiles = Files("one", "two", "three");
		private readonly GitCommitInfo _head = new(new string('b', 40), [new string('a', 40)], "author", DateTimeOffset.MinValue, "head");
		public Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken) => Task.FromResult(new GitRevisionChanges(baseCommit, targetCommit, _branchFiles, 5, 2));
		public Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken) => Task.FromResult(FileDiff.AllAdded("branch\n"));
		public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitCommitInfo>>([_head]);
		public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken) => Task.FromResult<GitCommitInfo?>(_head);
		public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken) => Task.FromResult(new CommitChanges(commit, _branchFiles, 5, 2));
		public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken) => Task.FromResult(FileDiff.AllAdded("branch\n"));
		public Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken) => Task.FromResult<string?>("branch\n");
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

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
