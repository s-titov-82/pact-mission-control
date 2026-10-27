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

	private static async Task WithScreenAsync(Func<Fixture, Task> act)
	{
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
			() => [new SessionTarget("s1", "Claude")],
			(_, _, _) => Task.FromResult(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true)),
			action => global::Avalonia.Threading.Dispatcher.UIThread.Post(action));
		ProjectScreenViewModel screen = new(workspace, changes, new ProjectMarkdownFileStore(), remarks: remarks);
		ProjectScreenView view = new() { DataContext = screen };
		Window window = new() { Content = view, Width = 900, Height = 500 };
		window.Show();
		try
		{
			await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
			await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
			window.UpdateLayout();
			await act(new Fixture(window, view, book, remarks, changes.ShownDiff!));
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

	private sealed record Fixture(Window Window, ProjectScreenView View, RemarkBook Book, ChangesRemarksViewModel Remarks, ShownDiff Shown)
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
