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
	[AvaloniaTest]
	public async Task Cleanup_question_can_be_declined_and_explicitly_opened_later_to_clear_the_saved_list()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.Panel.ConfigureLifecycle(fixture.EventTasks);
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 3, "review question", CancellationToken.None);
			await fixture.Book.AddAsync(fixture.Book.Threads[0].Anchor with { TargetCommit = new string('a', 40) }, "historical text\n", "historical review question", CancellationToken.None);
			await fixture.Remarks.SendAsync("s1", CancellationToken.None);
			TaskCompletionSource offered = new(TaskCreationOptions.RunContinuationsAsynchronously);
			void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
			{ if (fixture.Remarks.PendingCleanup is not null) { offered.TrySetResult(); } }
			fixture.Remarks.PropertyChanged += Changed;
			try
			{
				await fixture.Book.ResolveAsync("C1", CancellationToken.None);
				await fixture.Book.ResolveAsync("C2", CancellationToken.None);
				await offered.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
			finally { fixture.Remarks.PropertyChanged -= Changed; }
			var question = fixture.Panel.FindControl<Border>("CleanupQuestion").ShouldNotBeNull();
			question.IsVisible.ShouldBeTrue();
			fixture.Click(fixture.Panel.FindControl<Button>("DeclineCleanupButton")!);
			question.IsVisible.ShouldBeFalse();
			fixture.Book.Threads.Count.ShouldBe(2);
			var manual = fixture.Panel.FindControl<Button>("CleanupRemarksButton").ShouldNotBeNull();
			manual.IsEnabled.ShouldBeTrue();
			fixture.Click(manual);
			question.IsVisible.ShouldBeTrue();
			fixture.Click(fixture.Panel.FindControl<Button>("ConfirmCleanupButton")!);
			await fixture.EventTasks.WaitForIdleAsync();
			fixture.Book.Threads.ShouldBeEmpty();
			question.IsVisible.ShouldBeFalse();
			manual.IsEnabled.ShouldBeFalse();
		});
	}
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

				// Short enough to fit whole at the starting width: it stays that narrow and does not scroll.
				fixture.Overlay.Bounds.Width.ShouldBe(Math.Min(720, fixture.Layer.Bounds.Width * 0.7), 1);
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
	public async Task The_remarks_list_fits_without_a_horizontal_scroll()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Book.AddAsync(
				new Pact.Core.Remarks.RemarkAnchor("src/gone.cs", Pact.Core.Remarks.RemarkSide.New, 1, new string('x', 200), [], [], "head1", null),
				"x\n",
				string.Join(' ', Enumerable.Repeat("a fairly long remark", 20)),
				CancellationToken.None);
			var other = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
			await EventuallyAsync(fixture.Window, () => other.IsEnabled);

			other.Flyout!.ShowAt(other);
			fixture.Window.UpdateLayout();
			await EventuallyAsync(fixture.Window, () => fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!.IsEffectivelyVisible);

			var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
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
			var barText = fixture.Panel.FindControl<TextBlock>("RemarkBarText")!;
			await EventuallyAsync(fixture.Window, () => barText.Text == "1 remark to send");

			fixture.Panel.FindControl<Button>("SendRemarksButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

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

				fixture.Panel.FindControl<Button>("SendRemarksButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

				var items = fixture.Panel.SendMenu.ShouldNotBeNull().Items.OfType<MenuItem>().ToArray();
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
			var send = fixture.Panel.FindControl<Button>("SendRemarksButton")!;
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

	// Too tall for either side of its line even at its widest, it takes the whole height of the diff, covering the line.
	[AvaloniaTest]
	public async Task A_thread_taller_than_both_sides_uses_the_whole_diff_height()
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

				fixture.Overlay.Bounds.Width.ShouldBe(fixture.Layer.Bounds.Width * 0.7, 1);
				fixture.Overlay.Bounds.Height.ShouldBeGreaterThan(fixture.Layer.Bounds.Height - 12);
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
				var send = fixture.Panel.FindControl<Button>("SendRemarksButton")!;

				await EventuallyAsync(fixture.Window, () => fixture.Remarks.DraftCount == 1);

				send.IsEnabled.ShouldBeFalse();
				ToolTip.GetTip(send).ShouldBe("No session in this project to send to");
			},
			[]);
	}

	[AvaloniaTest]
	public async Task The_remarks_list_holds_every_open_thread_and_its_buttons_work()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Book.AddAsync(
				new Pact.Core.Remarks.RemarkAnchor("src/gone.cs", Pact.Core.Remarks.RemarkSide.New, 1, "x", [], [], "head1", null),
				"x\n",
				"where did it go?",
				CancellationToken.None);
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "on the shown file", CancellationToken.None);
			var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
			await EventuallyAsync(fixture.Window, () => open.IsEnabled && (open.Content as string) == "Open (2)");
			var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
			list.ItemsSource.ShouldBeSameAs(fixture.Remarks.OpenThreads);

			open.Flyout!.ShowAt(open);
			fixture.Window.UpdateLayout();
			await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 2);
			var first = list.GetLogicalDescendants().OfType<RemarkThreadView>()
				.First(view => ((RemarkThreadViewModel)view.DataContext!).Id == "C1");
			first.FindControl<Button>("ResolveButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Single(thread => thread.Id == "C1").IsResolved);
			await EventuallyAsync(fixture.Window, () => (open.Content as string) == "Open (1)");
		});
	}

	[AvaloniaTest]
	public async Task The_resolved_list_shows_the_review_done_and_reopens_a_thread()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "fixed?", CancellationToken.None);
			var resolvedButton = fixture.Panel.FindControl<Button>("ResolvedRemarksButton")!;
			var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
			await EventuallyAsync(fixture.Window, () => (open.Content as string) == "Open (1)");
			resolvedButton.IsEnabled.ShouldBeFalse();
			(resolvedButton.Content as string).ShouldBe("Resolved (0)");

			await fixture.Book.ResolveAsync("C1", CancellationToken.None);

			await EventuallyAsync(fixture.Window, () => resolvedButton.IsEnabled && (resolvedButton.Content as string) == "Resolved (1)");
			(open.Content as string).ShouldBe("Open (0)");
			open.IsEnabled.ShouldBeFalse();
			var list = fixture.Panel.FindControl<ItemsControl>("ResolvedRemarksList")!;
			resolvedButton.Flyout!.ShowAt(resolvedButton);
			fixture.Window.UpdateLayout();
			await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 1);
			list.GetLogicalDescendants().OfType<RemarkThreadView>().Single()
				.FindControl<Button>("ReopenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

			await EventuallyAsync(fixture.Window, () => !fixture.Book.Threads.Single().IsResolved);
			await EventuallyAsync(fixture.Window, () => (open.Content as string) == "Open (1)" && (resolvedButton.Content as string) == "Resolved (0)");
		});
	}

	[AvaloniaTest]
	public async Task The_open_list_takes_the_room_left_of_the_panel_before_it_scrolls()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
				await fixture.Remarks.SendAsync("s1", CancellationToken.None);
				var answer = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"reason {index}"));
				for (var reply = 0; reply < 2; reply++)
				{
					await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				}

				var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
				await EventuallyAsync(fixture.Window, () => open.IsEnabled);
				var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
				open.Flyout!.ShowAt(open);
				fixture.Window.UpdateLayout();
				await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 1);

				var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(list).OfType<ScrollViewer>().First();
				scroller.Bounds.Width.ShouldBeGreaterThan(600);
				scroller.Extent.Height.ShouldBeLessThanOrEqualTo(scroller.Viewport.Height + 1);
			},
			height: 1000);
	}

	// The buttons can sit mid-window when the column has more below them; the list still takes the
	// window's height, and starts as wide as a thread over a line does.
	[AvaloniaTest]
	public async Task The_open_list_takes_the_window_height_not_just_the_room_above_its_button()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
				await fixture.Remarks.SendAsync("s1", CancellationToken.None);
				var answer = string.Join('\n', Enumerable.Range(1, 12).Select(index => $"reason {index}"));
				for (var reply = 0; reply < 3; reply++)
				{
					await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				}

				var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
				await EventuallyAsync(fixture.Window, () => open.IsEnabled);
				var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
				open.Flyout!.ShowAt(open);
				fixture.Window.UpdateLayout();
				await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 1);
				fixture.Window.UpdateLayout();

				var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(list).OfType<ScrollViewer>().First();
				var buttonBottom = open.TranslatePoint(new Point(0, open.Bounds.Height), fixture.Window)!.Value.Y;
				list.Bounds.Height.ShouldBeGreaterThan(buttonBottom);
				scroller.Extent.Height.ShouldBeLessThanOrEqualTo(scroller.Viewport.Height + 1);
				scroller.Bounds.Width.ShouldBeLessThanOrEqualTo(721);
			},
			height: 1000,
			width: 1600,
			panelAlignment: global::Avalonia.Layout.VerticalAlignment.Center);
	}

	// Rows that would scroll at the start width widen the list, all the way to its share of the room
	// when even that is not enough.
	[AvaloniaTest]
	public async Task A_list_too_long_for_the_window_widens_to_its_full_share_before_it_scrolls()
	{
		await WithScreenAsync(
			async fixture =>
			{
				await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "why?", CancellationToken.None);
				await fixture.Remarks.SendAsync("s1", CancellationToken.None);
				var answer = string.Join(' ', Enumerable.Range(1, 60).Select(index => $"reason{index}"));
				for (var reply = 0; reply < 12; reply++)
				{
					await fixture.Book.AgentReplyAsync("C1", answer, resolve: false, CancellationToken.None);
				}

				var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
				await EventuallyAsync(fixture.Window, () => open.IsEnabled);
				var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
				open.Flyout!.ShowAt(open);
				fixture.Window.UpdateLayout();
				await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 1);

				var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(list).OfType<ScrollViewer>().First();
				var share = (1600 - 280 - 20) * 0.7;
				await EventuallyAsync(fixture.Window, () => scroller.Bounds.Width >= share - 1);
				// Only the list scrolls: the popup's own scroller would leave it unbounded, scrolling in a scroller.
				foreach (var outer in global::Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(scroller).OfType<ScrollViewer>())
				{
					outer.Extent.Height.ShouldBeLessThanOrEqualTo(outer.Viewport.Height + 1);
				}

				scroller.Extent.Height.ShouldBeGreaterThan(scroller.Viewport.Height);
			},
			height: 600,
			width: 1600);
	}

	[AvaloniaTest]
	public async Task The_mode_switch_shows_whole_file_and_back()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.EditAsync(Text, ChangedLine(5));

			fixture.Click(fixture.View.FindControl<Button>("WholeFileModeButton")!);
			fixture.Editor.Document.LineCount.ShouldBe(61);
			LineText(fixture, 1).ShouldBe("line 1");
			LineText(fixture, 5).ShouldBe("▸ 1 line removed");

			fixture.Click(fixture.View.FindControl<Button>("DiffModeButton")!);
			LineText(fixture, 1).ShouldStartWith("@@");
		});
	}

	[AvaloniaTest]
	public async Task Clicking_a_fold_line_expands_it_and_clicking_again_folds_it()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.EditAsync(Text, ChangedLine(5));
			fixture.Click(fixture.View.FindControl<Button>("WholeFileModeButton")!);

			fixture.PressText(5);
			LineText(fixture, 5).ShouldBe("▾ 1 line removed");
			LineText(fixture, 6).ShouldBe("old 5");

			fixture.PressText(5);
			LineText(fixture, 6).ShouldBe("line 5");
		});
	}

	[AvaloniaTest]
	public async Task A_remark_added_in_whole_file_mode_on_an_unchanged_line_shows_its_mark()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.EditAsync(Text, ChangedLine(5));
			fixture.Click(fixture.View.FindControl<Button>("WholeFileModeButton")!);
			var shown = fixture.Changes.ShownDiff!;
			var row = shown.Rows.ToList().FindIndex(item => item.NewNumber == "40");

			await fixture.Remarks.AddRemarkAsync(shown, row, "far", CancellationToken.None);

			await EventuallyAsync(fixture.Window, () => fixture.Margin.MarkOf(row + 1) == "thread");
			fixture.Book.Threads.Single().Anchor.Line.ShouldBe(40);
		});
	}

	[AvaloniaTest]
	public async Task An_edit_of_the_open_file_keeps_the_scroll_position_in_diff_and_whole_file()
	{
		await WithScreenAsync(async fixture =>
		{
			var lineHeight = fixture.LineHeight;
			fixture.ScrollTo(20 * lineHeight);

			await fixture.EditAsync("x\n" + Text);
			fixture.Window.UpdateLayout();
			fixture.Editor.TextArea.TextView.ScrollOffset.Y.ShouldBe(20 * lineHeight, tolerance: 1);

			fixture.Click(fixture.View.FindControl<Button>("WholeFileModeButton")!);
			fixture.ScrollTo(20 * lineHeight);
			await fixture.EditAsync("y\nx\n" + Text);
			fixture.Window.UpdateLayout();
			fixture.Editor.TextArea.TextView.ScrollOffset.Y.ShouldBe(20 * lineHeight, tolerance: 1);
		});
	}

	[AvaloniaTest]
	public async Task A_first_remark_being_typed_survives_an_edit_of_its_file_and_follows_its_line()
	{
		await WithScreenAsync(async fixture =>
		{
			// Row 10 shows line 10, document line 11 under the hunk header.
			fixture.ClickMargin(11);
			var box = fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!;
			box.Text = "draft";

			await fixture.EditAsync("x\ny\n" + Text);

			fixture.Overlay.IsVisible.ShouldBeTrue();
			box.Text.ShouldBe("draft");
			Canvas.GetTop(fixture.Overlay).ShouldBe(fixture.LineBottomInLayer(13), tolerance: 1);
			CtrlEnter(box);
			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 1);
			fixture.Book.Threads.Single().Anchor.Line.ShouldBe(10);
		}, height: 650);
	}

	[AvaloniaTest]
	public async Task A_first_remark_survives_two_edits_and_a_late_placement_and_anchors_to_the_original_line()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.ClickMargin(11);
			var box = fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!;
			box.Text = "draft";

			await fixture.EditAsync("x\ny\n" + Text);
			await fixture.EditAsync("z\nx\ny\n" + Text);
			// Another thread's placement is published while the box is open.
			await fixture.Remarks.AddRemarkAsync(fixture.Changes.ShownDiff!, 2, "other", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Remarks.ThreadsByRow?.ByRow.Count == 1);

			fixture.Overlay.IsVisible.ShouldBeTrue();
			box.Text.ShouldBe("draft");
			Canvas.GetTop(fixture.Overlay).ShouldBe(fixture.LineBottomInLayer(14), tolerance: 1);
			CtrlEnter(box);
			await EventuallyAsync(fixture.Window, () => fixture.Book.Threads.Count == 2);
			var anchor = fixture.Book.Threads.Single(thread => thread.Messages[0].Text == "draft").Anchor;
			anchor.Line.ShouldBe(10);
			(await fixture.Book.LoadSnapshotLinesAsync(anchor.SnapshotId!, CancellationToken.None))!.Count.ShouldBe(60);
		}, height: 650);
	}

	// The save of a first remark finishes after the user opened another line and typed there.
	[AvaloniaTest]
	public async Task A_finished_add_leaves_a_remark_typed_on_another_line()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.Store.Hold();
			fixture.ClickMargin(5);
			var box = fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!;
			box.Text = "first";
			CtrlEnter(box);
			await fixture.Store.Entered;
			fixture.ClickMargin(9);
			box.Text = "second";

			fixture.Store.Release();
			await fixture.EventTasks.WaitForIdleAsync();

			box.Text.ShouldBe("second");
			fixture.Book.Threads.Single().Anchor.Line.ShouldBe(4);
		});
	}

	// Text typed into the same box while its remark is saved is a new remark in the making.
	[AvaloniaTest]
	public async Task A_finished_add_keeps_text_typed_after_it_was_sent()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.Store.Hold();
			fixture.ClickMargin(5);
			var box = fixture.Overlay.FindControl<TextBox>("NewRemarkBox")!;
			box.Text = "first";
			CtrlEnter(box);
			await fixture.Store.Entered;
			box.Text = "first and more";

			fixture.Store.Release();
			await fixture.EventTasks.WaitForIdleAsync();

			box.Text.ShouldBe("first and more");
			fixture.Book.Threads.Single().Messages[0].Text.ShouldBe("first");
		});
	}

	[AvaloniaTest]
	public async Task Selecting_another_file_still_starts_at_the_top_and_closes_the_overlay()
	{
		await WithScreenAsync(async fixture =>
		{
			fixture.Reader.Paths = [FilePath, "src/y.cs"];
			fixture.Time.Advance(ProjectChangesViewModel.ReconcileInterval);
			await EventuallyAsync(fixture.Window, () => fixture.Changes.Files.Count == 2);
			fixture.ScrollTo(20 * fixture.LineHeight);
			fixture.ClickMargin(25);

			await fixture.Changes.SelectFileAsync(fixture.Changes.Files[1], CancellationToken.None);
			fixture.Window.UpdateLayout();

			fixture.Overlay.IsVisible.ShouldBeFalse();
			fixture.Editor.TextArea.TextView.ScrollOffset.Y.ShouldBe(0, tolerance: 1);
		});
	}

	private static string LineText(Fixture fixture, int line) =>
		fixture.Editor.Document.GetText(fixture.Editor.Document.GetLineByNumber(line));

	// Line n of Text replaced: "old n" removed above "line n".
	private static FileDiff ChangedLine(int line) =>
		new(null, [new DiffHunk(line - 1, 3, line - 1, 3, string.Empty,
		[
			new DiffLine(DiffLineKind.Context, line - 1, line - 1, $"line {line - 1}"),
			new DiffLine(DiffLineKind.Removed, line, null, $"old {line}"),
			new DiffLine(DiffLineKind.Added, null, line, $"line {line}"),
			new DiffLine(DiffLineKind.Context, line + 1, line + 1, $"line {line + 1}")
		])]);

	// A list rebuilt on each change would drop the focus of a reply being typed in it.
	[AvaloniaTest]
	public async Task A_reply_typed_in_the_open_list_keeps_its_row_when_remarks_change()
	{
		await WithScreenAsync(async fixture =>
		{
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 4, "one", CancellationToken.None);
			await fixture.Remarks.AddRemarkAsync(fixture.Shown, 6, "two", CancellationToken.None);
			var open = fixture.Panel.FindControl<Button>("OpenRemarksButton")!;
			await EventuallyAsync(fixture.Window, () => (open.Content as string) == "Open (2)");
			var list = fixture.Panel.FindControl<ItemsControl>("OpenRemarksList")!;
			open.Flyout!.ShowAt(open);
			fixture.Window.UpdateLayout();
			await EventuallyAsync(fixture.Window, () => list.GetLogicalDescendants().OfType<RemarkThreadView>().Count() == 2);
			var row = list.GetLogicalDescendants().OfType<RemarkThreadView>().First();

			await fixture.Book.SaveDraftAsync("C2", "x2", "and more", CancellationToken.None);
			await EventuallyAsync(fixture.Window, () => fixture.Remarks.OpenThreads.Single(thread => thread.Id == "C2").Messages.Count == 2);

			list.GetLogicalDescendants().OfType<RemarkThreadView>().First().ShouldBeSameAs(row);
		});
	}

	// Sent straight to the box, after a layout pass: a TextBox never laid out wraps at zero width and
	// Avalonia then allocates line after line without end.
	private static void CtrlEnter(TextBox box) =>
		box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter, KeyModifiers = KeyModifiers.Control, Source = box });

	private static async Task WithScreenAsync(Func<Fixture, Task> act, IReadOnlyList<SessionTarget>? sessions = null, double height = 500, double width = 1180,
		global::Avalonia.Layout.VerticalAlignment panelAlignment = global::Avalonia.Layout.VerticalAlignment.Bottom)
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
		TextReader reader = new();
		ManualTimeProvider time = new();
		using ProjectChangesViewModel changes = new(root.Path, reader, time, action => action());
		using var observation = changes.Observe();
		await EventuallyAsync(null, () => changes.Files.Count == 1);
		GatedRemarkStore store = new(new RemarkFileStore(root.Path));
		using RemarkBook book = new(root.Path, store, TimeProvider.System);
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
		Pact.App.Avalonia.Lifecycle.ObservedTaskGroup eventTasks = new(static (_, _) => Task.CompletedTask);
		view.ConfigureLifecycle(eventTasks);
		// At the bottom of the right column, as under the file list, unless a test moves it.
		RemarksPanel panel = new() { Remarks = remarks, Width = 280, VerticalAlignment = panelAlignment };
		DockPanel layout = new();
		DockPanel.SetDock(panel, Dock.Right);
		layout.Children.Add(panel);
		layout.Children.Add(view);
		Window window = new() { Content = layout, Width = width, Height = height };
		window.Show();
		try
		{
			await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
			await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
			window.UpdateLayout();
			await act(new Fixture(window, view, panel, book, remarks, changes.ShownDiff!, deliveredTo, changes, reader, time, store, eventTasks));
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
		RemarksPanel Panel,
		RemarkBook Book,
		ChangesRemarksViewModel Remarks,
		ShownDiff Shown,
		List<string> DeliveredTo,
		ProjectChangesViewModel Changes,
		TextReader Reader,
		ManualTimeProvider Time,
		GatedRemarkStore Store,
		Pact.App.Avalonia.Lifecycle.ObservedTaskGroup EventTasks)
	{
		// The agent rewrites the file; the next reconciliation reads it.
		public async Task EditAsync(string text, FileDiff? diff = null)
		{
			var before = Changes.ShownDiff;
			Reader.Text = text;
			Reader.Diff = diff;
			Time.Advance(ProjectChangesViewModel.ReconcileInterval);
			await EventuallyAsync(Window, () => !ReferenceEquals(Changes.ShownDiff, before));
		}

		public void Click(Button button)
		{
			button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
			Window.UpdateLayout();
		}

		// A press on the text of a line, as a click there.
		public void PressText(int line)
		{
			Window.UpdateLayout();
			var textView = Editor.TextArea.TextView;
			var point = textView.TranslatePoint(new Point(40, LineTop(line) + (LineHeight / 2)), Window)!.Value;
			Window.MouseDown(point, MouseButton.Left);
			Window.MouseUp(point, MouseButton.Left);
			Window.UpdateLayout();
		}

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
		public string Text { get; set; } = ProjectScreenRemarksHeadlessTests.Text;

		// The diff read; the whole text as added when not set.
		public FileDiff? Diff { get; set; }

		public string[] Paths { get; set; } = [FilePath];

		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges(
				"head1",
				"main",
				Paths.Select(path => new GitFileEntry(path, null, GitChangeKind.Untracked)).ToArray(),
				60,
				0));

		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) =>
			Task.FromResult(Diff ?? FileDiff.AllAdded(Text));

		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult<string?>(Text);
	}
}
