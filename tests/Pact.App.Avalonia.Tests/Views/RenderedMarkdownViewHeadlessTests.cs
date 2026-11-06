using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Markdown.Avalonia.Full;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Diff;
using Pact.App.Avalonia.Views.Remarks;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class RenderedMarkdownViewHeadlessTests
{
	private const string FilePath = "docs/x.md";

	// 1 title, 3-5 a paragraph whose line 4 changed, 7 an item, 9 an added line after a removed paragraph.
	private const string Text = "# Title\n\npara one\npara two\npara three\n\n- item\n\ntail\n";

	[AvaloniaTest]
	public void Historical_view_replaces_local_images_with_single_quoted_titles_and_html_images()
	{
		using var folder = TemporaryDirectory.Create();
		var file = Path.Combine(folder.Path, "current.png");
		File.WriteAllBytes(file, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg=="));
		var uri = new Uri(file).AbsoluteUri;
		RenderedMarkdownView view = new() { ProjectRoot = folder.Path };
		view.Show(new ShownDiff("doc.md", "base", [], [$"![old illustration]({uri} 'title')", "", $"<img src=\"{uri}\" alt=\"old HTML\">"], [], new string('a', 40)), null);
		var viewers = view.GetLogicalDescendants().OfType<MarkdownScrollViewer>().ToArray();
		viewers.ShouldNotBeEmpty();
		string.Join('\n', viewers.Select(viewer => viewer.Markdown)).ShouldNotContain(uri);
		string.Join('\n', viewers.Select(viewer => viewer.Markdown)).ShouldContain("old illustration (image unavailable in commit view)");
		string.Join('\n', viewers.Select(viewer => viewer.Markdown)).ShouldContain("old HTML (image unavailable in commit view)");
		view.GetLogicalDescendants().OfType<Image>().ShouldBeEmpty();
	}

	[AvaloniaTest]
	public void A_retained_historical_link_cannot_navigate_a_new_commit_of_the_same_file()
	{
		RenderedMarkdownView view = new() { ProjectRoot = "C:/repo" };
		List<Pact.Presentation.Services.MarkdownLinkTarget> followed = [];
		view.LinkFollowed += (_, target) => followed.Add(target);
		var first = new ShownDiff("doc.md", "base", [], ["[heading](#title)"], [], new string('a', 40));
		view.Show(first, null);
		var oldViewer = view.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Single();
		var command = ((global::Markdown.Avalonia.Markdown)oldViewer.Engine).HyperlinkCommand.ShouldNotBeNull();
		command.Execute("#title");
		followed.ShouldHaveSingleItem().ShouldBe(new Pact.Presentation.Services.HeadingLink("title"));
		view.Show(first with { TargetCommit = new string('b', 40) }, null);
		command.Execute("#title");
		followed.Count.ShouldBe(1);
		var current = view.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Single();
		((global::Markdown.Avalonia.Markdown)current.Engine).HyperlinkCommand.ShouldNotBeNull().Execute("local.md");
		followed.Count.ShouldBe(2);
		followed[1].ShouldBeOfType<Pact.Presentation.Services.RefusedLink>().Notice.ShouldContain("commit view");
	}

	[AvaloniaTest]
	public async Task Changed_and_added_blocks_carry_a_bar_and_unchanged_ones_do_not()
	{
		await WithRenderedAsync(fixture =>
		{
			Bar(fixture.Block(3)).ShouldBe(RenderedMarkdownView.ChangedBrush);
			Bar(fixture.Block(9)).ShouldBe(RenderedMarkdownView.AddedBrush);
			Bar(fixture.Block(1)).ShouldBeNull();
			Bar(fixture.Block(7)).ShouldBeNull();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task A_removed_block_expands_to_its_old_text_rendered()
	{
		await WithRenderedAsync(fixture =>
		{
			var removed = fixture.Find<Button>("rendered-removed").Single();
			removed.Content.ShouldBe("▸ 2 lines removed");

			fixture.Click(removed);

			removed.Content.ShouldBe("▾ 2 lines removed");
			var old = removed.Parent!.GetLogicalDescendants().OfType<MarkdownScrollViewer>().Single();
			old.IsVisible.ShouldBeTrue();
			old.Markdown.ShouldBe("gone para\n");
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Adding_a_remark_on_a_block_anchors_it_to_the_blocks_first_line()
	{
		await WithRenderedAsync(async fixture =>
		{
			fixture.Click(AddButton(fixture.Block(3)));
			var box = fixture.NewRemarkBox(3);
			box.Text = "why?";

			CtrlEnter(box);

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 1);
			var anchor = fixture.Book.Threads.Single().Anchor;
			(anchor.Side, anchor.Line, anchor.LineText).ShouldBe((RemarkSide.New, 3, "para one"));
			fixture.Changes.Mode = DiffViewMode.Diff;
			var row = fixture.Changes.DiffRows.ToList().FindIndex(item => item.NewNumber == "3");
			var margin = fixture.View.FindControl<global::AvaloniaEdit.TextEditor>("DiffEditor")!.TextArea.LeftMargins.OfType<DiffRemarkMargin>().Single();
			await EventuallyAsync(fixture.Window, () => margin.MarkOf(row + 1) == "thread");
		});
	}

	[AvaloniaTest]
	public async Task A_thread_inside_a_paragraph_shows_under_that_paragraph()
	{
		await WithRenderedAsync(async fixture =>
		{
			await fixture.AddOnLineAsync(5, "on line five");

			await EventuallyAsync(fixture.Window, () => Threads(fixture.Block(3)).Count == 1);
			Threads(fixture.Block(1)).ShouldBeEmpty();
		});
	}

	[AvaloniaTest]
	public async Task A_block_with_a_thread_carries_a_mark_and_a_removed_block_has_no_add_button()
	{
		await WithRenderedAsync(async fixture =>
		{
			await fixture.AddOnLineAsync(4, "mark me");

			await EventuallyAsync(fixture.Window, () => Mark(fixture.Block(3)).IsVisible);
			Mark(fixture.Block(1)).IsVisible.ShouldBeFalse();
			var removed = fixture.Find<Button>("rendered-removed").Single();
			removed.Parent!.GetLogicalDescendants().OfType<Button>().ShouldNotContain(button => button.Classes.Contains("block-add"));
		});
	}

	[AvaloniaTest]
	public async Task An_agent_edit_redraws_the_blocks_and_keeps_an_open_thread()
	{
		await WithRenderedAsync(async fixture =>
		{
			await fixture.AddOnLineAsync(3, "first");
			await EventuallyAsync(fixture.Window, () => Threads(fixture.Block(3)).Count == 1);
			var draft = Threads(fixture.Block(3)).Single().FindControl<TextBox>("DraftBox")!;
			draft.Text = "a reply";

			await fixture.EditAsync("intro\n\n" + Text);

			await EventuallyAsync(fixture.Window, () => Threads(fixture.Block(5)).Count == 1);
			Threads(fixture.Block(5)).Single().FindControl<TextBox>("DraftBox")!.Text.ShouldBe("a reply");
		});
	}

	[AvaloniaTest]
	public async Task Adding_and_resolving_a_thread_updates_its_block_without_redrawing_the_text()
	{
		await WithRenderedAsync(async fixture =>
		{
			var text = fixture.Block(3).GetLogicalDescendants().OfType<MarkdownScrollViewer>().First();

			var thread = await fixture.AddOnLineAsync(3, "resolve me");
			await EventuallyAsync(fixture.Window, () => Threads(fixture.Block(3)).Count == 1);
			await thread.ResolveAsync(CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => thread.CanReopen);

			fixture.Block(3).GetLogicalDescendants().OfType<MarkdownScrollViewer>().First().ShouldBeSameAs(text);
		});
	}

	[AvaloniaTest]
	public async Task Threads_placed_after_the_first_draw_appear_under_their_blocks()
	{
		await WithRenderedAsync(
			async fixture =>
			{
				fixture.Changes.Mode = DiffViewMode.Rendered;
				fixture.Window.UpdateLayout();

				await EventuallyAsync(fixture.Window, () => Threads(fixture.Block(3)).Count == 1);
			},
			before: async (changes, remarks) =>
			{
				var shown = changes.ShownDiff!;
				var row = shown.Rows.ToList().FindIndex(item => item.NewNumber == "3");
				await remarks.AddRemarkAsync(shown, row, "made in diff mode", CancellationToken.None);
			});
	}

	[AvaloniaTest]
	public async Task A_first_remark_being_typed_on_a_block_survives_an_edit_of_the_file()
	{
		await WithRenderedAsync(async fixture =>
		{
			fixture.Click(AddButton(fixture.Block(3)));
			fixture.NewRemarkBox(3).Text = "draft";

			await fixture.EditAsync("intro\n\n" + Text);

			var box = fixture.NewRemarkBox(5);
			box.Text.ShouldBe("draft");
			CtrlEnter(box);
			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 1);
			fixture.Book.Threads.Single().Anchor.Line.ShouldBe(3);
		});
	}

	// The save of a first remark finishes after the user moved on to another block and typed there.
	[AvaloniaTest]
	public async Task A_finished_add_leaves_a_newer_editor_open()
	{
		await WithRenderedAsync(async fixture =>
		{
			fixture.Store.Hold();
			fixture.Click(AddButton(fixture.Block(3)));
			var first = fixture.NewRemarkBox(3);
			first.Text = "first";
			CtrlEnter(first);
			await fixture.Store.Entered;
			fixture.Click(AddButton(fixture.Block(9)));
			fixture.NewRemarkBox(9).Text = "second";

			fixture.Store.Release();
			await fixture.EventTasks.WaitForIdleAsync();

			fixture.NewRemarkBox(9).Text.ShouldBe("second");
		});
	}

	[AvaloniaTest]
	public async Task A_finished_add_keeps_text_typed_after_it_was_sent()
	{
		await WithRenderedAsync(async fixture =>
		{
			fixture.Store.Hold();
			fixture.Click(AddButton(fixture.Block(3)));
			var box = fixture.NewRemarkBox(3);
			box.Text = "first";
			CtrlEnter(box);
			await fixture.Store.Entered;
			box.Text = "first and more";

			fixture.Store.Release();
			await fixture.EventTasks.WaitForIdleAsync();

			fixture.NewRemarkBox(3).Text.ShouldBe("first and more");
		});
	}

	[AvaloniaTest]
	public async Task A_link_to_a_heading_of_another_document_carries_the_heading()
	{
		await WithRenderedAsync(
			async fixture =>
			{
				var viewer = fixture.Block(1).GetLogicalDescendants().OfType<MarkdownScrollViewer>().First();
				((global::Markdown.Avalonia.Markdown)viewer.Engine).HyperlinkCommand!.Execute("guide.md#usage");

				await EventuallyAsync(fixture.Window, () => fixture.Opened.Count == 1);
				fixture.Opened.ShouldBe([("docs/guide.md", "usage")]);
			},
			files: ["docs/guide.md"]);
	}

	[AvaloniaTest]
	public async Task A_nested_list_and_an_ordered_item_render_as_in_the_docs_preview()
	{
		const string lists = "- one\n  - sub\n\n3. three\n";
		await WithRenderedAsync(
			fixture =>
			{
				var nested = fixture.Block(1).GetLogicalDescendants().OfType<MarkdownScrollViewer>().First();
				nested.Markdown.ShouldBe("- one\n  - sub");
				TextsUnder(fixture.Block(4)).ShouldContain(text => text.StartsWith("3.", StringComparison.Ordinal));
				return Task.CompletedTask;
			},
			text: lists,
			diff: FileDiff.AllAdded(lists));
	}

	private static IBrush? Bar(Control block) =>
		block.GetLogicalDescendants().OfType<Border>().Single(border => border.Classes.Contains("rendered-bar")).Background;

	private static Button AddButton(Control block) =>
		block.GetLogicalDescendants().OfType<Button>().Single(button => button.Classes.Contains("block-add"));

	private static Control Mark(Control block) =>
		block.GetLogicalDescendants().OfType<Control>().Single(control => control.Classes.Contains("block-mark"));

	private static List<RemarkThreadView> Threads(Control block) =>
		block.GetLogicalDescendants().OfType<RemarkThreadView>().ToList();

	private static List<string> TextsUnder(Control root) =>
		global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root)
			.Select(control => control.GetType().GetProperty("Text")?.GetValue(control) as string)
			.OfType<string>()
			.ToList();

	private static void CtrlEnter(TextBox box) =>
		box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control, Source = box });

	// Line 4 changed from "old two"; "gone para" and its blank line removed above the added "tail".
	private static FileDiff Changes() =>
		new(null,
		[
			new DiffHunk(3, 3, 3, 3, string.Empty,
			[
				new DiffLine(DiffLineKind.Context, 3, 3, "para one"),
				new DiffLine(DiffLineKind.Removed, 4, null, "old two"),
				new DiffLine(DiffLineKind.Added, null, 4, "para two"),
				new DiffLine(DiffLineKind.Context, 5, 5, "para three")
			]),
			new DiffHunk(7, 4, 7, 3, string.Empty,
			[
				new DiffLine(DiffLineKind.Context, 7, 7, "- item"),
				new DiffLine(DiffLineKind.Context, 8, 8, string.Empty),
				new DiffLine(DiffLineKind.Removed, 9, null, "gone para"),
				new DiffLine(DiffLineKind.Removed, 10, null, string.Empty),
				new DiffLine(DiffLineKind.Added, null, 9, "tail")
			])
		]);

	private static async Task WithRenderedAsync(
		Func<Fixture, Task> act,
		string text = Text,
		FileDiff? diff = null,
		Func<ProjectChangesViewModel, ChangesRemarksViewModel, Task>? before = null,
		string[]? files = null)
	{
		FluentTheme theme = new();
		StyleInclude editorStyle = new(new Uri("avares://Pact.App.Avalonia.Tests/"))
		{
			Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
		};
		Application.Current!.Styles.Add(theme);
		Application.Current.Styles.Add(editorStyle);
		using var root = TemporaryDirectory.Create();
		foreach (var file in files ?? [])
		{
			var full = Path.Combine(root.Path, file.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(full)!);
			await File.WriteAllTextAsync(full, "# File");
		}

		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root.Path, now, now, null), _ => true);
		Reader reader = new() { Text = text, Diff = diff ?? Changes() };
		List<(string Path, string? Heading)> opened = [];
		ManualTimeProvider time = new();
		using ProjectChangesViewModel changes = new(root.Path, reader, time, action => action());
		using var observation = changes.Observe();
		GatedRemarkStore store = new(new RemarkFileStore(root.Path));
		using RemarkBook book = new(root.Path, store, TimeProvider.System);
		using ChangesRemarksViewModel remarks = new(
			changes,
			book,
			() => [new SessionTarget("s1", "Claude")],
			(_, _, _) => Task.FromResult(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true)),
			action => global::Avalonia.Threading.Dispatcher.UIThread.Post(action));
		await book.EnsureLoadedAsync(CancellationToken.None);
		ProjectScreenViewModel screen = new(
			workspace,
			changes,
			new ProjectMarkdownFileStore(),
			remarks: remarks,
			openDocument: (path, heading, _) =>
			{
				opened.Add((path, heading));
				return Task.FromResult(true);
			});
		ProjectScreenView view = new() { DataContext = screen };
		Pact.App.Avalonia.Lifecycle.ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		view.ConfigureLifecycle(eventTasks);
		Window window = new() { Content = view, Width = 1000, Height = 900 };
		window.Show();
		try
		{
			await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
			await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
			if (before is null)
			{
				changes.Mode = DiffViewMode.Rendered;
			}
			else
			{
				await before(changes, remarks);
			}

			window.UpdateLayout();
			await act(new Fixture(window, view, book, remarks, changes, reader, time, opened, store, eventTasks));
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(editorStyle);
			Application.Current.Styles.Remove(theme);
		}
	}

	private static async Task EventuallyAsync(Window window, Func<bool> condition)
	{
		for (var attempt = 0; attempt < 300 && !condition(); attempt++)
		{
			global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			window.UpdateLayout();
			await Task.Delay(10);
		}

		condition().ShouldBeTrue();
	}

	private sealed record Fixture(
		Window Window,
		ProjectScreenView View,
		RemarkBook Book,
		ChangesRemarksViewModel Remarks,
		ProjectChangesViewModel Changes,
		Reader Reader,
		ManualTimeProvider Time,
		List<(string Path, string? Heading)> Opened,
		GatedRemarkStore Store,
		Pact.App.Avalonia.Lifecycle.ObservedTaskGroup EventTasks)
	{
		public RenderedMarkdownView Rendered => View.FindControl<RenderedMarkdownView>("RenderedView")!;

		public IEnumerable<T> Find<T>(string className)
			where T : Control =>
			Rendered.GetLogicalDescendants().OfType<T>().Where(control => control.Classes.Contains(className));

		// The row of a block, by its first source line.
		public Border Block(int firstLine)
		{
			Window.UpdateLayout();
			return Find<Border>("rendered-block").Single(block => Equals(block.Tag, firstLine));
		}

		public TextBox NewRemarkBox(int firstLine) =>
			Block(firstLine).GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "NewRemarkBox");

		public void Click(Button button)
		{
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Window.UpdateLayout();
		}

		public async Task<RemarkThreadViewModel> AddOnLineAsync(int line, string text)
		{
			var shown = Changes.ShownDiff!;
			var row = shown.Rows.ToList().FindIndex(item => item.NewNumber == line.ToString(System.Globalization.CultureInfo.InvariantCulture));
			return (await Remarks.AddRemarkAsync(shown, row, text, CancellationToken.None))!;
		}

		// The agent rewrites the file, keeping the changes it had and shifting them down.
		public async Task EditAsync(string text)
		{
			var shown = Changes.ShownDiff;
			Reader.Text = text;
			var shift = text.Split('\n').Length - Text.Split('\n').Length;
			Reader.Diff = Shift(Reader.Diff!, shift);
			Time.Advance(ProjectChangesViewModel.ReconcileInterval);
			await EventuallyAsync(Window, () => !ReferenceEquals(Changes.ShownDiff, shown));
		}

		private static FileDiff Shift(FileDiff diff, int by) =>
			new(null, diff.Hunks.Select(hunk => hunk with
			{
				NewStart = hunk.NewStart + by,
				Lines = hunk.Lines.Select(line => line with { NewNumber = line.NewNumber + by }).ToArray()
			}).ToArray());
	}

	private sealed class Reader : IGitWorkingTreeReader
	{
		public string Text { get; set; } = RenderedMarkdownViewHeadlessTests.Text;

		public FileDiff? Diff { get; set; }

		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges("head1", "main", [new GitFileEntry(FilePath, null, GitChangeKind.Modified)], 2, 2));

		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) =>
			Task.FromResult(Diff ?? FileDiff.AllAdded(Text));

		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(Text);
	}
}