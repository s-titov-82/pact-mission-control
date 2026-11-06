using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Markdown.Avalonia.Full;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Remarks;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Diff;

/// <summary>
/// A Markdown file's new version rendered block by block: changed blocks carry a bar, removed ones
/// fold, and each block takes remarks on its first line and shows the threads on its lines.
/// </summary>
internal sealed partial class RenderedMarkdownView : UserControl
{
	/// <summary>The bar of a block whose every line is new.</summary>
	public static readonly IBrush AddedBrush = new SolidColorBrush(Color.Parse("#22C55E"));

	/// <summary>The bar of a block partly changed.</summary>
	public static readonly IBrush ChangedBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);
	private readonly RemarkOverlay _editor = new() { Margin = new(0, 4, 0, 8) };
	private readonly Dictionary<int, BlockParts> _blocks = [];
	private ShownDiff? _shown;
	private ChangesRemarksViewModel? _remarks;
	private IReadOnlyList<RenderedItem> _items = [];

	// What the open editor was opened on, never moved, and the block it is drawn under now.
	private (ShownDiff Diff, int Row)? _capture;
	private int? _editorBlock;
	// Counts editor openings, so a save that finishes late closes only the editor it came from.
	private int _editorActivation;
	private IDisposable? _branchInteractionHold;

	public RenderedMarkdownView()
	{
		InitializeComponent();
		_editor.CloseRequested += (_, _) => CloseEditor();
		_editor.AddRequested += OnAddRequested;
		DetachedFromVisualTree += (_, _) => CloseEditor();
	}
	internal void EndInteraction() => CloseEditor();

	/// <summary>The project the shown file is in, for its links and images.</summary>
	public string? ProjectRoot { get; set; }

	/// <summary>Raised when a link in a block is followed, resolved against the shown file.</summary>
	public event EventHandler<MarkdownLinkTarget>? LinkFollowed;

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks) =>
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

	/// <summary>
	/// Shows a whole-file diff of a Markdown file. The blocks are drawn again only for another diff;
	/// threads follow the remarks as they are placed.
	/// </summary>
	public void Show(ShownDiff shown, ChangesRemarksViewModel? remarks)
	{
		ArgumentNullException.ThrowIfNull(shown);
		// Another project's remarks: nothing typed here belongs to it, whatever the file is called.
		var sameOwner = ReferenceEquals(remarks, _remarks);
		if (!sameOwner)
		{
			_remarks?.PropertyChanged -= OnRemarksPropertyChanged;
			_remarks = remarks;
			_remarks?.PropertyChanged += OnRemarksPropertyChanged;
		}

		if (!ReferenceEquals(shown, _shown))
		{
			var samePath = sameOwner && string.Equals(shown.Path, _shown?.Path, StringComparison.Ordinal)
				&& string.Equals(shown.TargetCommit, _shown?.TargetCommit, StringComparison.Ordinal);
			var offset = Scroller.Offset;
			_shown = shown;
			_items = RenderedMarkdown.Build(shown);
			Rebuild();
			if (samePath)
			{
				UpdateLayout();
				Scroller.Offset = offset;
				FollowEditor();
			}
			else
			{
				CloseEditor();
				Scroller.Offset = default;
			}
		}

		RefreshThreads();
	}

	private void OnRemarksPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ChangesRemarksViewModel.ThreadsByRow))
		{
			RefreshThreads();
		}
	}

	private void Rebuild()
	{
		DetachEditor();
		Items.Children.Clear();
		_blocks.Clear();
		foreach (var item in _items)
		{
			Items.Children.Add(item switch
			{
				RenderedBlock block => BlockRow(block),
				RenderedRemoved removed => RemovedRow(removed),
				_ => new Panel()
			});
		}
	}

	private Border BlockRow(RenderedBlock block)
	{
		Button add = new() { Content = "+", Opacity = 0, IsVisible = CanRemark(block) };
		add.Classes.Add("block-add");
		ToolTip.SetTip(add, "Remark on this block");
		add.Click += (_, _) => OpenEditor(block);
		Ellipse mark = new() { Width = 8, Height = 8, Fill = ChangedBrush, Margin = new(0, 6, 0, 0), IsVisible = false };
		mark.Classes.Add("block-mark");
		StackPanel gutter = new() { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
		gutter.Children.Add(add);
		gutter.Children.Add(mark);

		Border bar = new()
		{
			Width = 4,
			Background = block.Change switch
			{
				RenderedChange.Added => AddedBrush,
				RenderedChange.Changed => ChangedBrush,
				_ => null
			}
		};
		bar.Classes.Add("rendered-bar");
		Grid.SetColumn(bar, 1);

		ItemsControl threads = new()
		{
			ItemTemplate = new FuncDataTemplate<RemarkThreadViewModel>((_, _) => new RemarkThreadView { Margin = new(0, 4, 0, 4) }),
			IsVisible = false
		};
		Panel editorSlot = new();
		StackPanel content = new() { Margin = new(8, 0, 0, 0) };
		content.Children.Add(Viewer(KeepNumber(block.Markdown)));
		content.Children.Add(threads);
		content.Children.Add(editorSlot);
		Grid.SetColumn(content, 2);

		Grid grid = new() { ColumnDefinitions = new ColumnDefinitions("26,4,*") };
		grid.Children.Add(gutter);
		grid.Children.Add(bar);
		grid.Children.Add(content);
		Border root = new() { Tag = block.FirstLine, Background = Brushes.Transparent, Child = grid };
		root.Classes.Add("rendered-block");
		root.PointerEntered += (_, _) => add.Opacity = 1;
		root.PointerExited += (_, _) => add.Opacity = 0;
		_blocks[block.FirstLine] = new BlockParts(block, threads, mark, editorSlot);
		return root;
	}

	// A viewer for one block: its links and images resolve against the shown file.
	private MarkdownScrollViewer Viewer(string markdown)
	{
		MarkdownScrollViewer viewer = new() { SelectionEnabled = true };
		var captured = _shown;
		var root = ProjectRoot;
		MarkdownPreviewHook.Attach(
			viewer,
			() => root is not null && captured is not null ? (root, captured.Path) : null,
			target => { if (ReferenceEquals(captured, _shown) && string.Equals(root, ProjectRoot, StringComparison.Ordinal)) { LinkFollowed?.Invoke(this, target); } },
			historical: captured?.TargetCommit is not null);
		MarkdownPreviewHook.SetDocument(viewer, captured?.TargetCommit is null ? root : null, captured?.Path);
		viewer.Markdown = MarkdownPreviewHook.Prepare(markdown, viewer.AssetPathRoot, historical: captured?.TargetCommit is not null);
		return viewer;
	}

	private StackPanel RemovedRow(RenderedRemoved removed)
	{
		var old = Viewer(removed.Markdown);
		old.IsVisible = false;
		old.Opacity = 0.7;
		old.Margin = new(34, 0, 0, 0);
		Button toggle = new() { Content = FoldText(removed.LineCount, expanded: false), Margin = new(30, 0, 0, 0) };
		toggle.Classes.Add("rendered-removed");
		toggle.Click += (_, _) =>
		{
			old.IsVisible = !old.IsVisible;
			toggle.Content = FoldText(removed.LineCount, old.IsVisible);
		};
		StackPanel row = new();
		row.Children.Add(toggle);
		row.Children.Add(old);
		return row;
	}

	// Rendered alone, an ordered item would restart its list at 1; one numbered otherwise keeps its
	// number as text. The engine shows a backslash escape as typed, so a zero-width space after the
	// marker is what stops it reading a list.
	private static string KeepNumber(string markdown)
	{
		var match = OrderedItemPattern().Match(markdown);
		return match.Success && match.Groups["number"].Value != "1"
			? markdown[..(match.Groups["mark"].Index + 1)] + "​" + markdown[(match.Groups["mark"].Index + 1)..]
			: markdown;
	}

	[GeneratedRegex(@"^\s*(?<number>\d{1,9})(?<mark>[.)])\s")]
	private static partial Regex OrderedItemPattern();

	private static string FoldText(int count, bool expanded) => string.Create(
		CultureInfo.InvariantCulture,
		$"{(expanded ? "▾" : "▸")} {count} {(count == 1 ? "line" : "lines")} removed");

	private bool CanRemark(RenderedBlock block) =>
		_remarks is { } remarks && _shown is { } shown && block.Row >= 0 && remarks.CanRemarkOn(shown, block.Row);

	// Threads change without the text: only the lists under the blocks and their marks move.
	private void RefreshThreads()
	{
		var placed = _remarks?.ThreadsByRow is { } rows && _shown is { } shown && ReferenceEquals(rows.ForDiff, shown)
			? RenderedMarkdown.ThreadsByBlock(_items, shown, rows.ByRow)
			: new Dictionary<int, IReadOnlyList<RemarkThreadViewModel>>();
		foreach (var (firstLine, parts) in _blocks)
		{
			var threads = placed.GetValueOrDefault(firstLine) ?? [];
			if (parts.Threads.ItemsSource is not IReadOnlyList<RemarkThreadViewModel> current || !current.SequenceEqual(threads))
			{
				parts.Threads.ItemsSource = threads;
			}

			parts.Threads.IsVisible = threads.Count > 0;
			parts.Mark.IsVisible = threads.Count > 0;
		}
	}

	private void OpenEditor(RenderedBlock block)
	{
		if (_shown is not { } shown)
		{
			return;
		}

		CloseEditor();
		_editorActivation++;
		_branchInteractionHold = _remarks?.RetainCurrentComparison();
		_capture = (shown, block.Row);
		_editor.Show([], canAdd: true);
		AttachEditor(block.FirstLine);
		Dispatcher.UIThread.Post(_editor.FocusInput, DispatcherPriority.Loaded);
	}

	// After the file was redrawn: the editor goes under the block holding its line now, or stays at
	// the place it had when the line is gone; what it was opened on does not change.
	private void FollowEditor()
	{
		if (_capture is not { } capture || _shown is not { } shown || _editorBlock is not { } previous)
		{
			return;
		}

		var blocks = _items.OfType<RenderedBlock>().ToArray();
		if (blocks.Length == 0)
		{
			CloseEditor();
			return;
		}

		RenderedBlock? holder = null;
		if (_remarks?.MapRow(capture.Diff, capture.Row, shown) is { } row
			&& int.TryParse(shown.Rows[row].NewNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
		{
			holder = Array.Find(blocks, block => block.FirstLine <= line && line <= block.LastLine);
		}

		holder ??= blocks.LastOrDefault(block => block.FirstLine <= previous) ?? blocks[0];
		AttachEditor(holder.FirstLine);
	}

	private void AttachEditor(int firstLine)
	{
		DetachEditor();
		if (_blocks.TryGetValue(firstLine, out var parts))
		{
			parts.EditorSlot.Children.Add(_editor);
			_editorBlock = firstLine;
		}
	}

	private void DetachEditor()
	{
		if (_editor.Parent is Panel slot)
		{
			slot.Children.Remove(_editor);
		}
	}

	private void CloseEditor()
	{
		_branchInteractionHold?.Dispose();
		_branchInteractionHold = null;
		DetachEditor();
		_capture = null;
		_editorBlock = null;
		_editor.Show([], canAdd: false);
	}

	private void OnAddRequested(object? sender, string text)
	{
		if (_remarks is not { } remarks || _capture is not { } capture)
		{
			return;
		}

		var activation = _editorActivation;
		_eventTasks.TryRun(
			"rendered-remark-add",
			async () =>
			{
				// Only the editor as it was sent is closed: one opened since, or text typed on, is newer.
				if (await remarks.AddRemarkAsync(capture.Diff, capture.Row, text, CancellationToken.None) is not null
					&& activation == _editorActivation
					&& string.Equals(_editor.NewRemarkText, text, StringComparison.Ordinal))
				{
					CloseEditor();
				}
			},
			static _ => Task.CompletedTask);
	}

	private sealed record BlockParts(RenderedBlock Block, ItemsControl Threads, Control Mark, Panel EditorSlot);
}