using Avalonia;
using Avalonia.Controls;
using AvaloniaEdit;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Diff;
using Pact.App.Avalonia.Views.Editing;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectScreenViewHeadlessTests
{
	[AvaloniaTest]
	public async Task Selecting_a_changed_file_shows_its_numbered_lines()
	{
		await WithScreenAsync(
			new FileDiff(null,
			[
				new DiffHunk(4, 2, 4, 2, "class X",
				[
					new DiffLine(DiffLineKind.Removed, 4, null, "old line"),
					new DiffLine(DiffLineKind.Added, null, 4, "new line")
				])
			]),
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				window.UpdateLayout();

				view.FindControl<Control>("ChangesPanel")!.IsVisible.ShouldBeTrue();
				var editor = view.FindControl<TextEditor>("DiffEditor")!;
				editor.Document.Text.ShouldBe("@@ -4,2 +4,2 @@ class X\nold line\nnew line");
				editor.IsReadOnly.ShouldBeTrue();
				editor.FontSize.ShouldBeGreaterThanOrEqualTo(13);
				var margin = editor.TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single();
				margin.NumbersOf(1).ShouldBe(("", ""));
				margin.NumbersOf(2).ShouldBe(("4", ""));
				margin.NumbersOf(3).ShouldBe(("", "4"));
				var background = editor.TextArea.TextView.BackgroundRenderers.OfType<DiffLineBackgroundRenderer>().Single();
				background.KindOf(1).ShouldBe(DiffRowKind.HunkHeader);
				background.KindOf(2).ShouldBe(DiffRowKind.Removed);
				background.KindOf(3).ShouldBe(DiffRowKind.Added);
				view.FindControl<Control>("DiffPlaceholderText")!.IsVisible.ShouldBeFalse();
			});
	}

	[AvaloniaTest]
	public async Task The_diff_text_can_be_selected_across_lines()
	{
		await WithScreenAsync(
			new FileDiff(null,
			[
				new DiffHunk(1, 1, 1, 2, string.Empty,
				[
					new DiffLine(DiffLineKind.Context, 1, 1, "first"),
					new DiffLine(DiffLineKind.Added, null, 2, "second")
				])
			]),
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				window.UpdateLayout();
				var editor = view.FindControl<TextEditor>("DiffEditor")!;

				editor.SelectAll();

				editor.SelectedText.ShouldContain("first\nsecond");
			});
	}

	[AvaloniaTest]
	public async Task The_colouring_follows_the_picked_file_even_when_the_diffs_are_equal()
	{
		await WithScreenAsync(
			new FileDiff(null,
			[
				new DiffHunk(1, 1, 1, 1, string.Empty, [new DiffLine(DiffLineKind.Added, null, 1, "same")])
			]),
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				var editor = view.FindControl<SourceEditor>("DiffEditor")!;
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				editor.SyntaxScope.ShouldBe("source.cs");

				await screen.Changes.SelectFileAsync(screen.Changes.Files[1], CancellationToken.None);

				editor.SyntaxScope.ShouldBe("text.html.markdown");
			});
	}

	[TestCase("src/x.cs", "source.cs")]
	[TestCase("docs/plan.md", "text.html.markdown")]
	[TestCase("build/Build.PS1", "source.powershell")]
	[TestCase("bin/blob", null)]
	public void The_grammar_follows_the_file_extension(string path, string? scope) =>
		EditorSyntax.ScopeFor(path).ShouldBe(scope);

	[AvaloniaTest]
	public async Task A_binary_file_shows_the_placeholder()
	{
		await WithScreenAsync(
			FileDiff.Binary,
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				window.UpdateLayout();

				var placeholder = view.FindControl<TextBlock>("DiffPlaceholderText")!;
				placeholder.IsVisible.ShouldBeTrue();
				placeholder.Text.ShouldBe("Binary file, not shown");
			});
	}

	[AvaloniaTest]
	public async Task Rendered_is_offered_only_for_markdown_and_no_switch_for_a_binary_file()
	{
		await WithScreenAsync(
			new FileDiff(null, [new DiffHunk(1, 1, 1, 1, string.Empty, [new DiffLine(DiffLineKind.Added, null, 1, "x")])]),
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				window.UpdateLayout();
				view.FindControl<Control>("WholeFileModeButton")!.IsVisible.ShouldBeTrue();
				view.FindControl<Control>("RenderedModeButton")!.IsVisible.ShouldBeFalse();

				await screen.Changes.SelectFileAsync(screen.Changes.Files[1], CancellationToken.None);
				window.UpdateLayout();
				view.FindControl<Control>("RenderedModeButton")!.IsVisible.ShouldBeTrue();
			});

		await WithScreenAsync(
			FileDiff.Binary,
			async (screen, view, window) =>
			{
				await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
				await screen.Changes!.SelectFileAsync(screen.Changes.Files[0], CancellationToken.None);
				window.UpdateLayout();
				view.FindControl<Control>("DiffModeButton")!.IsVisible.ShouldBeFalse();
				view.FindControl<Control>("WholeFileModeButton")!.IsVisible.ShouldBeFalse();
				view.FindControl<Control>("RenderedModeButton")!.IsVisible.ShouldBeFalse();
			});
	}

	[AvaloniaTest]
	public async Task The_review_logs_tab_renders_the_selected_log()
	{
		await WithScreenAsync(
			new FileDiff(null, []),
			async (screen, view, window) =>
			{
				var folder = Path.Combine(screen.RootPath, ".pact", "reviews");
				Directory.CreateDirectory(folder);
				await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-code-review-a.md"), "# Review");

				(await screen.OpenReviewLogAsync(".pact/reviews/2026-10-26-1200-code-review-a.md", CancellationToken.None))
					.ShouldBeTrue();
				window.UpdateLayout();

				view.FindControl<Control>("ReviewLogsPanel")!.IsVisible.ShouldBeTrue();
				view.FindControl<Control>("ChangesPanel")!.IsVisible.ShouldBeFalse();
				view.FindControl<Markdown.Avalonia.Full.MarkdownScrollViewer>("ReviewLogPreview")!
					.Markdown.ShouldBe("# Review");
			});
	}

	// Markdown.Avalonia's click-to-select indexes the first block of the document, so a click on
	// an empty log once threw out of the message loop and froze the window.
	[AvaloniaTest]
	public async Task Clicking_the_log_area_after_the_last_log_is_deleted_is_harmless()
	{
		await WithScreenAsync(
			new FileDiff(null, []),
			async (screen, view, window) =>
			{
				var folder = Path.Combine(screen.RootPath, ".pact", "reviews");
				Directory.CreateDirectory(folder);
				const string log = ".pact/reviews/2026-10-26-1200-code-review-a.md";
				await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-26-1200-code-review-a.md"), "# Review");
				await screen.OpenReviewLogAsync(log, CancellationToken.None);
				window.UpdateLayout();

				await screen.DeleteReviewLogAsync(log, CancellationToken.None);
				window.UpdateLayout();
				var panel = view.FindControl<Control>("ReviewLogsPanel")!;
				var point = panel.TranslatePoint(new Point(40, 40), window)!.Value;

				Should.NotThrow(() =>
				{
					window.MouseDown(point, global::Avalonia.Input.MouseButton.Left);
					window.MouseUp(point, global::Avalonia.Input.MouseButton.Left);
				});
				view.FindControl<TextBlock>("NoReviewLogsText")!.IsVisible.ShouldBeTrue();
			});
	}

	[AvaloniaTest]
	public async Task The_overview_shows_the_project_and_its_branch()
	{
		await WithScreenAsync(
			new FileDiff(null, []),
			(screen, view, window) =>
			{
				window.UpdateLayout();

				view.FindControl<Control>("OverviewPanel")!.IsVisible.ShouldBeTrue();
				var texts = TextsUnder(view.FindControl<Control>("OverviewPanel")!);
				texts.ShouldContain("Pact");
				texts.ShouldContain("main");
				view.FindControl<Control>("ChangesTab")!.IsVisible.ShouldBeTrue();
				return Task.CompletedTask;
			});
	}

	private static List<string> TextsUnder(Control root) =>
		root.GetLogicalDescendants().OfType<TextBlock>()
			.Select(text => text.Text ?? string.Empty)
			.ToList();

	private static async Task WithScreenAsync(
		FileDiff diff,
		Func<ProjectScreenViewModel, ProjectScreenView, Window, Task> act)
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		using var root = TemporaryDirectory.Create();
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root.Path, now, now, null), _ => true);
		using ProjectChangesViewModel changes = new(root.Path, new OneFileReader(diff), TimeProvider.System, action => action());
		using var observation = changes.Observe();
		ProjectScreenViewModel screen = new(workspace, changes, new ProjectMarkdownFileStore());
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view, Width = 900, Height = 600 };
		window.Show();
		try
		{
			await act(screen, view, window);
		}
		finally
		{
			window.Close();
			Application.Current!.Styles.Remove(theme);
		}
	}

	private sealed class OneFileReader(FileDiff diff) : IGitWorkingTreeReader
	{
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(null);

		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges(
				"head",
				"main",
				[
					new GitFileEntry("src/x.cs", null, GitChangeKind.Modified),
					new GitFileEntry("docs/x.md", null, GitChangeKind.Modified)
				],
				1,
				1));

		public Task<FileDiff> ReadDiffAsync(
			string root,
			GitFileEntry entry,
			string? headCommit,
			CancellationToken cancellationToken) =>
			Task.FromResult(diff);
	}
}
