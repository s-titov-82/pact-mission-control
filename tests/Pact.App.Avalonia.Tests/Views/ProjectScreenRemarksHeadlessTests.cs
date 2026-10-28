using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaEdit;
using Pact.App.Avalonia.Views;
using Pact.App.Avalonia.Views.Diff;
using Pact.App.Avalonia.Views.Remarks;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class ProjectScreenRemarksHeadlessTests
{
	private const string FilePath = "src/x.cs";
	private static readonly string Text = string.Join('\n', Enumerable.Range(1, 60).Select(index => $"line {index}")) + "\n";

	[AvaloniaTest]
	public async Task The_margin_marks_rows_with_threads()
	{
		await WithScreenAsync(async (fixture) =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 3, "why?", CancellationToken.None);

			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(4) == "thread");
			fixture.Margin.MarkOf(5).ShouldBe(string.Empty);
		});
	}

	[AvaloniaTest]
	public async Task Clicking_the_margin_opens_the_overlay_under_its_line()
	{
		await WithScreenAsync(fixture =>
		{
			fixture.ClickMargin(5);

			fixture.Overlay.IsVisible.ShouldBeTrue();
			Canvas.GetTop(fixture.Overlay).ShouldBe(fixture.LineBottomInLayer(5), tolerance: 1);
			fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!.IsVisible.ShouldBeTrue();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task The_overlay_follows_scrolling()
	{
		await WithScreenAsync(fixture =>
		{
			fixture.ClickMargin(10);
			var before = Canvas.GetTop(fixture.Overlay);
			var lineHeight = fixture.LineHeight;

			fixture.ScrollTo(3 * lineHeight);

			Canvas.GetTop(fixture.Overlay).ShouldBe(before - (3 * lineHeight), tolerance: 1);
			fixture.Overlay.IsVisible.ShouldBeTrue();

			fixture.ScrollTo(40 * lineHeight);

			fixture.Overlay.IsVisible.ShouldBeFalse();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Adding_a_remark_from_the_overlay_creates_a_draft()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.ClickMargin(5);
			fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!.Text = "why five?";

			fixture.Overlay.FindControl<Button>("AddRemarkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 1);
			var thread = fixture.Book.Threads.Single();
			thread.EditableDraft!.Text.ShouldBe("why five?");
			// Document line 5 is row 4: the hunk header takes the first line.
			thread.Anchor.Line.ShouldBe(4);
			await EventuallyAsync(fixture.Window, () => fixture.Overlay.GetLogicalDescendants().OfType<RemarkThreadView>().Any());
		});
	}

	[AvaloniaTest]
	public async Task An_agent_reply_keeps_the_text_being_typed()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
			await fixture.Remarks.SendAsync("s1", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(5) == "thread");
			fixture.ClickMargin(5);
			var threadView = fixture.Overlay.GetLogicalDescendants().OfType<RemarkThreadView>().Single();
			var draftBox = threadView.FindControl<TextBox>("DraftBox")!;
			draftBox.Text = "half typed";

			await fixture.Book.AgentReplyAsync("C1", "because", resolve: false, CancellationToken.None);

			await EventuallyAsync(fixture.Window, () => ((RemarkThreadViewModel)threadView.DataContext!).Messages.Count == 2);
			draftBox.Text.ShouldBe("half typed");
		});
	}

	[AvaloniaTest]
	public async Task Esc_closes_the_overlay()
	{
		await WithScreenAsync(fixture =>
		{
			fixture.ClickMargin(5);
			fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!.Focus();

			fixture.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);

			fixture.Overlay.IsVisible.ShouldBeFalse();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task A_click_in_the_text_still_selects()
	{
		await WithScreenAsync(fixture =>
		{
			var textView = fixture.Editor.TextArea.TextView;
			var from = textView.TranslatePoint(new Point(5, fixture.LineTop(2) + 2), fixture.Window)!.Value;
			var to = textView.TranslatePoint(new Point(60, fixture.LineTop(3) + 2), fixture.Window)!.Value;

			fixture.Window.MouseDown(from, MouseButton.Left);
			fixture.Window.MouseMove(to);
			fixture.Window.MouseUp(to, MouseButton.Left);

			fixture.Editor.SelectionLength.ShouldBeGreaterThan(0);
			fixture.Overlay.IsVisible.ShouldBeFalse();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Hovering_a_line_of_text_shows_its_plus()
	{
		await WithScreenAsync(fixture =>
		{
			var textView = fixture.Editor.TextArea.TextView;
			var point = textView.TranslatePoint(new Point(80, fixture.LineTop(6) + 2), fixture.Window)!.Value;

			fixture.Window.MouseMove(point);

			fixture.Margin.MarkOf(6).ShouldBe("+");
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Clicking_a_line_number_opens_the_overlay()
	{
		await WithScreenAsync(fixture =>
		{
			var numbers = fixture.Editor.TextArea.LeftMargins.OfType<DiffLineNumberMargin>().Single();
			var point = numbers.TranslatePoint(new Point(numbers.Bounds.Width / 2, fixture.LineTop(5) + 2), fixture.Window)!.Value;

			fixture.Window.MouseDown(point, MouseButton.Left);
			fixture.Window.MouseUp(point, MouseButton.Left);
			fixture.Window.UpdateLayout();

			fixture.Overlay.IsVisible.ShouldBeTrue();
			Canvas.GetTop(fixture.Overlay).ShouldBe(fixture.LineBottomInLayer(5), tolerance: 1);
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Opening_the_overlay_puts_the_cursor_in_its_box()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.ClickMargin(5);

			await EventuallyAsync(fixture.Window, () => fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!.IsFocused);
		});
	}

	[AvaloniaTest]
	public async Task Ctrl_enter_adds_the_remark()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.ClickMargin(5);
			var box = fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!;
			box.Focus();
			box.Text = "why five?";
			fixture.Window.UpdateLayout();

			CtrlEnter(box);

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 1);
			fixture.Book.Threads.Single().EditableDraft!.Text.ShouldBe("why five?");
		});
	}

	[AvaloniaTest]
	public async Task Ctrl_enter_saves_a_reply()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
			await fixture.Remarks.SendAsync("s1", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(5) == "thread");
			fixture.ClickMargin(5);
			var draftBox = fixture.Overlay.GetLogicalDescendants().OfType<RemarkThreadView>().Single().FindControl<TextBox>("DraftBox")!;
			draftBox.Focus();
			draftBox.Text = "and also";
			fixture.Window.UpdateLayout();

			fixture.Window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Single().Unsent.Any(message => message.Text == "and also"));
			draftBox.Text.ShouldBeNullOrEmpty();
		});
	}

	[AvaloniaTest]
	public async Task The_overlay_takes_the_width_and_height_it_has()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 1, "why?", CancellationToken.None);
				await fixture.Remarks.SendAsync("s1", CancellationToken.None);
				var answer = string.Join('\n', Enumerable.Range(1, 8).Select(index => $"reason {index}"));
				await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(2) == "thread");

				fixture.ClickMargin(2);
				await EventuallyAsync(fixture.Window, () => fixture.Overlay.Bounds.Height > 0);

				fixture.Overlay.Bounds.Width.ShouldBeGreaterThan(700);
				var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(fixture.Overlay).OfType<ScrollViewer>().First();
				scroller.Extent.Height.ShouldBeLessThanOrEqualTo(scroller.Viewport.Height + 1);
			},
			height: 900);
	}

	[AvaloniaTest]
	public async Task The_pencil_and_the_cross_act_on_an_unsent_reply()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(5) == "thread");
			fixture.ClickMargin(5);
			var threadView = fixture.Overlay.GetLogicalDescendants().OfType<RemarkThreadView>().Single();
			var draftBox = threadView.FindControl<TextBox>("DraftBox")!;
			Button Action(string tip)
			{
				return threadView.GetLogicalDescendants().OfType<Button>().Single(button => ToolTip.GetTip(button) as string == tip);
			}

			Action("Edit").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			draftBox.Text.ShouldBe("why?");

			draftBox.Text = string.Empty;
			threadView.FindControl<Button>("DiscardButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await EventuallyAsync(fixture.Window, () => threadView.GetLogicalDescendants().OfType<Button>().Any(button => ToolTip.GetTip(button) as string == "Remove"));
			Action("Remove").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 0);
		});
	}

	[AvaloniaTest]
	public async Task A_click_in_the_text_closes_the_overlay()
	{
		await WithScreenAsync(fixture =>
		{
			fixture.ClickMargin(5);
			var point = fixture.Editor.TextArea.TextView.TranslatePoint(new Point(40, fixture.LineTop(2) + 2), fixture.Window)!.Value;

			fixture.Window.MouseDown(point, MouseButton.Left);
			fixture.Window.MouseUp(point, MouseButton.Left);

			fixture.Overlay.IsVisible.ShouldBeFalse();
			return Task.CompletedTask;
		});
	}

	[AvaloniaTest]
	public async Task Other_remarks_fit_without_a_horizontal_scroll()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Book.AddAsync(
				new Pact.Core.Remarks.RemarkAnchor("src/gone.cs", Pact.Core.Remarks.RemarkSide.New, 1, new string('x', 200), [], [], "head1", null),
				"x\n",
				string.Join(' ', Enumerable.Repeat("a fairly long remark", 20)),
				CancellationToken.None);
			var other = fixture.View.FindControl<Button>("OtherRemarksButton")!;
			await EventuallyAsync(fixture.Window, () => other.IsVisible);

			other.Flyout!.ShowAt(other);
			fixture.Window.UpdateLayout();
			await EventuallyAsync(fixture.Window, () => fixture.View.FindControl<ItemsControl>("OtherRemarksList")!.IsEffectivelyVisible);

			var list = fixture.View.FindControl<ItemsControl>("OtherRemarksList")!;
			var scrollers = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(list).OfType<ScrollViewer>().ToArray();
			scrollers.Length.ShouldBeGreaterThan(1, "the flyout presenter should hold the list");
			foreach (var scroller in global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(list).OfType<ScrollViewer>())
			{
				scroller.Extent.Width.ShouldBeLessThanOrEqualTo(scroller.Viewport.Width + 1);
			}
		});
	}

	[AvaloniaTest]
	public async Task The_bar_counts_drafts_and_sends_to_the_only_session()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 3, "why?", CancellationToken.None);
			var barText = fixture.View.FindControl<TextBlock>("RemarkBarText")!;
			await EventuallyAsync(fixture.Window, () => barText.Text == "1 remark to send");

			fixture.View.FindControl<Button>("SendRemarksButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			await EventuallyAsync(fixture.Window, () => fixture.DeliveredTo.Count == 1);
			fixture.DeliveredTo.ShouldBe(["s1"]);
		});
	}

	[AvaloniaTest]
	public async Task With_several_sessions_send_asks_which_one()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 3, "why?", CancellationToken.None);
				await EventuallyAsync(fixture.Window, () => fixture.Remarks.CanSend);

				fixture.View.FindControl<Button>("SendRemarksButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

				var items = fixture.View.SendMenu.ShouldNotBeNull().Items.OfType<MenuItem>().ToArray();
				items.Select(item => item.Header as string).ShouldBe(["Claude", "Codex"]);
				fixture.DeliveredTo.ShouldBeEmpty();
				items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
				await EventuallyAsync(fixture.Window, () => fixture.DeliveredTo.Count == 1);
				fixture.DeliveredTo.ShouldBe(["s2"]);
			},
			[new SessionTarget("s1", "Claude"), new SessionTarget("s2", "Codex")]);
	}

	[AvaloniaTest]
	public async Task A_typed_reply_enables_the_send_button()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
			await fixture.Remarks.SendAsync("s1", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(5) == "thread");
			var send = fixture.View.FindControl<Button>("SendRemarksButton")!;
			fixture.ClickMargin(5);
			var draftBox = fixture.Overlay.GetLogicalDescendants().OfType<RemarkThreadView>().Single().FindControl<TextBox>("DraftBox")!;
			send.IsEnabled.ShouldBeFalse();

			draftBox.Text = "and also";

			await EventuallyAsync(fixture.Window, () => send.IsEnabled);
			send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			await EventuallyAsync(fixture.Window, () => fixture.DeliveredTo.Count == 2);
			fixture.Book.Threads.Single().Messages.ShouldContain(message => message.Text == "and also");
		});
	}

	[AvaloniaTest]
	public async Task A_long_thread_in_a_short_window_stays_inside_the_diff()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 6, "why?", CancellationToken.None);
				await fixture.Remarks.SendAsync("s1", CancellationToken.None);
				var answer = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"reason {index}"));
				for (var reply = 0; reply < 4; reply++)
				{
					await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				}

				await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(7) == "thread");

				fixture.ClickMargin(7);
				await EventuallyAsync(fixture.Window, () => fixture.Overlay.Bounds.Height > 0);

				var top = Canvas.GetTop(fixture.Overlay);
				top.ShouldBeGreaterThanOrEqualTo(-1);
				(top + fixture.Overlay.Bounds.Height).ShouldBeLessThanOrEqualTo(fixture.Layer.Bounds.Height + 1);
			},
			height: 320);
	}

	[AvaloniaTest]
	public async Task Send_is_disabled_without_a_session()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 3, "why?", CancellationToken.None);
				var send = fixture.View.FindControl<Button>("SendRemarksButton")!;

				await EventuallyAsync(fixture.Window, () => fixture.Remarks.DraftCount == 1);

				send.IsEnabled.ShouldBeFalse();
				ToolTip.GetTip(send).ShouldBe("No session in this project to send to");
			},
			[]);
	}

	[AvaloniaTest]
	public async Task Other_remarks_lists_outdated_threads()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Book.AddAsync(
				new Pact.Core.Remarks.RemarkAnchor("src/gone.cs", Pact.Core.Remarks.RemarkSide.New, 1, "x", [], [], "head1", null),
				"x\n",
				"where did it go?",
				CancellationToken.None);
			var other = fixture.View.FindControl<Button>("OtherRemarksButton")!;

			await EventuallyAsync(fixture.Window, () => other.IsVisible && (other.Content as string) == "Other remarks (1)");

			fixture.View.FindControl<ItemsControl>("OtherRemarksList")!.ItemsSource.ShouldBeSameAs(fixture.Remarks.Elsewhere);
			fixture.Remarks.Elsewhere.Single().IsOutdated.ShouldBeTrue();
		});
	}

	// Sent straight to the box, after a layout pass: a TextBox never laid out wraps at zero width and
	// Avalonia then allocates line after line without end.
	private static void CtrlEnter(TextBox box) =>
		box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control, Source = box });

	private static async Task WithScreenAsync(Func<Fixture, Task> act, IReadOnlyList<SessionTarget>? sessions = null, double height = 500)
	{
		List<string> deliveredTo = [];
		FluentTheme theme = new();
		StyleInclude editorStyle = new(new Uri("avares://Pact.App.Avalonia.Tests/"))
		{
			Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
		};
		Application.Current!.Styles.Add(theme);
		Application.Current.Styles.Add(editorStyle);
		using var root = TemporaryDirectory.Create();
		Directory.CreateDirectory(Path.Combine(root.Path, "src"));
		await File.WriteAllTextAsync(Path.Combine(root.Path, "src", "x.cs"), Text);
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", root.Path, now, now, null), _ => true);
		using ProjectChangesViewModel changes = new(root.Path, new TextReader(), TimeProvider.System, action => action());
		using var observation = changes.Observe();
		await EventuallyAsync(null, () => changes.Files.Count == 1);
		using RemarkBook book = new(root.Path, new RemarkFileStore(root.Path), TimeProvider.System);
		using ChangesRemarksViewModel remarks = new(
			changes,
			book,
			() => sessions ?? [new SessionTarget("s1", "Claude")],
			(session, _, _) =>
			{
				deliveredTo.Add(session);
				return Task.FromResult(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true));
			},
			action => global::Avalonia.Threading.Dispatcher.UIThread.Post(action));
		ProjectScreenViewModel screen = new(workspace, changes, new ProjectMarkdownFileStore(), remarks: remarks);
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view, Width = 900, Height = height };
		window.Show();
		try
		{
			await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
			await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
			window.UpdateLayout();
			await act(new Fixture(window, view, book, remarks, changes.ShownDiff!, deliveredTo));
		}
		finally
		{
			window.Close();
			Application.Current.Styles.Remove(editorStyle);
			Application.Current.Styles.Remove(theme);
		}
	}

	private static async Task EventuallyAsync(Window? window, Func<bool> condition)
	{
		for (var attempt = 0; attempt < 300 && !condition(); attempt++)
		{
			global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
			window?.UpdateLayout();
			await Task.Delay(10);
		}

		condition().ShouldBeTrue();
	}

	private sealed record Fixture(
		Window Window,
		ProjectScreenView View,
		RemarkBook Book,
		ChangesRemarksViewModel Remarks,
		ShownDiff Shown,
		List<string> DeliveredTo)
	{
		public TextEditor Editor => View.FindControl<TextEditor>("DiffEditor")!;

		public Control Layer => View.FindControl<Control>("RemarkLayer")!;

		public RemarkOverlay Overlay => View.FindControl<RemarkOverlay>("RemarkOverlay")!;

		public DiffRemarkMargin Margin => Editor.TextArea.LeftMargins.OfType<DiffRemarkMargin>().Single();

		public double LineTop(int line) =>
			Editor.TextArea.TextView.GetVisualTopByDocumentLine(line) - Editor.TextArea.TextView.ScrollOffset.Y;

		public double LineHeight => LineTop(2) - LineTop(1);

		public double LineBottomInLayer(int line) =>
			Editor.TextArea.TextView.TranslatePoint(new Point(0, LineTop(line) + LineHeight), Layer)!.Value.Y;

		// The editor's own ScrollToVerticalOffset does not move it headless; its template ScrollViewer does.
		public void ScrollTo(double offset)
		{
			global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(Editor).OfType<ScrollViewer>().First().Offset = new Vector(0, offset);
			Window.UpdateLayout();
		}

		public void ClickMargin(int line)
		{
			Window.UpdateLayout();
			var point = Margin.TranslatePoint(new Point(Margin.Bounds.Width / 2, LineTop(line) + (LineHeight / 2)), Window)!.Value;
			Window.MouseMove(point);
			Window.MouseDown(point, MouseButton.Left);
			Window.MouseUp(point, MouseButton.Left);
			Window.UpdateLayout();
		}
	}

	private sealed class TextReader : IGitWorkingTreeReader
	{
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges(
				"head1",
				"main",
				[new GitFileEntry(FilePath, null, GitChangeKind.Untracked)],
				60,
				0));

		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) =>
			Task.FromResult(FileDiff.AllAdded(Text));

		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(Text);
	}
}
