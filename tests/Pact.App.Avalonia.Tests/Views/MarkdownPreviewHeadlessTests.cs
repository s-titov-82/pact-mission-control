using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Themes.Fluent;
using Markdown.Avalonia.Full;
using Pact.App.Avalonia.Views;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class MarkdownPreviewHeadlessTests
{
	private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==";

	// The engine's own command ran Process.Start on the UI thread with the bare relative path, which
	// the shell could not find; the click threw out of the message loop. Links now stay in Pact.
	[AvaloniaTest]
	public async Task A_link_to_another_project_document_opens_it_in_the_same_pane()
	{
		await WithDocsAsync(
			("docs/a.md", "[other](other.md)"),
			("docs/other.md", "# Other"),
			async (workspace, notes, window) =>
			{
				Link(notes.FindControl<MarkdownScrollViewer>("Preview")!, "other.md");

				await EventuallyAsync(window, () => workspace.SelectedNode?.RelativePath == "docs/other.md"
					&& notes.FindControl<MarkdownScrollViewer>("Preview")!.Markdown == "# Other");
			});
	}

	[AvaloniaTest]
	public async Task A_link_to_a_file_outside_the_project_shows_a_notice_and_stays()
	{
		await WithDocsAsync(
			("docs/a.md", "[out](../../x.md)"),
			null,
			(workspace, notes, window) =>
			{
				Link(notes.FindControl<MarkdownScrollViewer>("Preview")!, "../../x.md");
				window.UpdateLayout();

				var notice = notes.FindControl<TextBlock>("LinkNoticeText")!;
				notice.IsVisible.ShouldBeTrue();
				notice.Text.ShouldBe("Outside the project: ../../x.md");
				workspace.SelectedNode!.RelativePath.ShouldBe("docs/a.md");
				return Task.CompletedTask;
			});
	}

	[AvaloniaTest]
	public async Task A_heading_link_scrolls_the_preview_to_the_heading()
	{
		var filler = string.Join("\n\n", Enumerable.Range(1, 80).Select(index => $"Paragraph {index}."));
		await WithDocsAsync(
			("docs/a.md", $"# Top\n\n{filler}\n\n## Part Two\n\nEnd."),
			null,
			async (_, notes, window) =>
			{
				var preview = notes.FindControl<MarkdownScrollViewer>("Preview")!;
				window.UpdateLayout();
				var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(preview).OfType<ScrollViewer>().First();

				Link(preview, "#part-two");

				await EventuallyAsync(window, () => scroller.Offset.Y > 100);
			});
	}

	[AvaloniaTest]
	public async Task A_relative_image_is_drawn_from_the_documents_folder()
	{
		await WithDocsAsync(
			("docs/a.md", "![pic](img/pic.png)"),
			null,
			async (_, notes, window) =>
			{
				var preview = notes.FindControl<MarkdownScrollViewer>("Preview")!;

				await EventuallyAsync(window, () => Images(preview).Any(image => image.Task is { IsCompletedSuccessfully: true, Result: not null }));
			},
			image: "docs/img/pic.png");
	}

	[AvaloniaTest]
	public async Task A_missing_image_leaves_its_alt_text_and_the_rest_renders()
	{
		await WithDocsAsync(
			("docs/a.md", "![alt words](nope.png)\n\nAfter the image."),
			null,
			async (_, notes, window) =>
			{
				var preview = notes.FindControl<MarkdownScrollViewer>("Preview")!;

				await EventuallyAsync(window, () => Texts(preview).Any(text => text.Contains("After the image.", StringComparison.Ordinal)));
				Texts(preview).ShouldContain(text => text.Contains("alt words", StringComparison.Ordinal));
			});
	}

	[AvaloniaTest]
	public async Task A_review_log_link_opens_the_document_in_the_docs_pane()
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		using var root = TemporaryDirectory.Create();
		var folder = Path.Combine(root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		const string log = ".pact/reviews/2026-10-29-1200-code-review-a.md";
		await File.WriteAllTextAsync(Path.Combine(folder, "2026-10-29-1200-code-review-a.md"), "[readme](../../README.md)");
		await File.WriteAllTextAsync(Path.Combine(root.Path, "README.md"), "# Readme");
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root.Path, now, now, null), _ => true);
		List<string> opened = [];
		ProjectScreenViewModel screen = new(
			workspace,
			null,
			new ProjectMarkdownFileStore(),
			openDocument: (path, _) =>
			{
				opened.Add(path);
				return Task.FromResult(true);
			});
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view, Width = 900, Height = 600 };
		window.Show();
		try
		{
			await screen.OpenReviewLogAsync(log, CancellationToken.None);
			window.UpdateLayout();

			Link(view.FindControl<MarkdownScrollViewer>("ReviewLogPreview")!, "../../README.md");

			await EventuallyAsync(window, () => opened.Count == 1);
			opened.ShouldBe(["README.md"]);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	// What a click on a link runs.
	private static void Link(MarkdownScrollViewer preview, string href) =>
		((global::Markdown.Avalonia.Markdown)preview.Engine).HyperlinkCommand!.Execute(href);

	// Images are drawn inside the text, as inlines of its blocks.
	private static List<ColorTextBlock.Avalonia.CImage> Images(Control root)
	{
		List<ColorTextBlock.Avalonia.CImage> images = [];
		void Collect(object? node)
		{
			if (node is ColorTextBlock.Avalonia.CImage image)
			{
				images.Add(image);
			}

			if (node?.GetType().GetProperty("Content")?.GetValue(node) is System.Collections.IEnumerable children and not string)
			{
				foreach (var child in children)
				{
					Collect(child);
				}
			}
		}

		foreach (var block in global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<ColorTextBlock.Avalonia.CTextBlock>())
		{
			Collect(block);
		}

		return images;
	}

	private static List<string> Texts(Control root) =>
		global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
			.Select(control => control.GetType().GetProperty("Text")?.GetValue(control) as string)
			.OfType<string>()
			.ToList();

	private static async Task WithDocsAsync(
		(string Path, string Text) document,
		(string Path, string Text)? other,
		Func<DocsAndNotesWorkspaceViewModel, NotesPaneView, Window, Task> act,
		string? image = null)
	{
		FluentTheme theme = new();
		Application.Current!.Styles.Add(theme);
		using var root = TemporaryDirectory.Create();
		foreach (var (path, text) in other is { } second ? new[] { document, second } : [document])
		{
			Write(root.Path, path, text);
		}

		if (image is not null)
		{
			var full = Path.Combine(root.Path, image.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			await File.WriteAllBytesAsync(full, Convert.FromBase64String(OnePixelPng));
		}

		DocsAndNotesWorkspaceViewModel workspace = new(
			root.Path,
			new ProjectNoteDocument(new FakeNotesStore(), root.Path, TimeSpan.FromHours(1)),
			new ProjectMarkdownFileStore(),
			TimeSpan.FromHours(1));
		await workspace.RefreshAsync(CancellationToken.None);
		NotesPaneView notes = new() { Workspace = workspace };
		Window window = new() { Content = notes, Width = 900, Height = 600 };
		window.Show();
		try
		{
			(await workspace.OpenDocumentAsync(document.Path, CancellationToken.None)).ShouldBeTrue();
			window.UpdateLayout();
			await act(workspace, notes, window);
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(theme);
		}
	}

	private static void Write(string root, string relativePath, string text)
	{
		var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
	}

	private static async Task EventuallyAsync(Window window, Func<bool> condition)
	{
		for (var attempt = 0; attempt < 200 && !condition(); attempt++)
		{
			global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			await Task.Delay(10);
		}

		condition().ShouldBeTrue();
	}

	private sealed class FakeNotesStore : IProjectNotesStore
	{
		public Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken) =>
			Task.FromResult(string.Empty);

		public Task SaveAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task AppendAsync(string projectRootPath, string value, CancellationToken cancellationToken) =>
			Task.CompletedTask;
	}
}
