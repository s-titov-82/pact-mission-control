using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Pact.App.Avalonia.Views;
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
				var texts = TextsUnder(view.FindControl<Control>("DiffRowsList")!);
				texts.ShouldContain("old line");
				texts.ShouldContain("new line");
				texts.ShouldContain("4");
				texts.ShouldContain("@@ -4,2 +4,2 @@ class X");
				view.FindControl<Control>("DiffPlaceholderText")!.IsVisible.ShouldBeFalse();
			});
	}

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
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges(
				"head",
				"main",
				[new GitFileEntry("src/x.cs", null, GitChangeKind.Modified)],
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
