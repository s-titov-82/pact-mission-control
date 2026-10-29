using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Diff;
using Pact.App.Avalonia.Views.Remarks;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>The project screen: overview, changes and review logs of one project.</summary>
internal sealed partial class ProjectScreenView : UserControl
{
	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);
	private ProjectScreenViewModel? _screen;
	private ProjectChangesViewModel? _changes;
	private ChangesRemarksViewModel? _remarks;

	private static readonly TimeSpan LogReloadInterval = TimeSpan.FromSeconds(2);
	private static readonly IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> NoThreads =
		new Dictionary<int, IReadOnlyList<RemarkThreadViewModel>>();
	private const double DiffLineHeightFactor = 1.3;
	private const double OverlayStartWidth = 720;
	private const double OverlayWidthStep = 80;
	private const double OverlayWidthShare = 0.7;
	private const double OverlayMinHeight = 80;
	private readonly DispatcherTimer _logReloadTimer;
	private static readonly TimeSpan LinkNoticeTime = TimeSpan.FromSeconds(5);
	private readonly DispatcherTimer _linkNoticeTimer;

	private readonly DiffLineNumberMargin _diffNumbers = new();
	private readonly DiffRemarkMargin _remarkMargin = new();
	private readonly DiffLineBackgroundRenderer _diffBackground = new();
	private IReadOnlyList<DiffRowViewModel>? _shownRows;
	private string? _shownPath;

	// Where the overlay is drawn: the row it shows, moved along when the file is edited.
	private int? _overlayRow;
	// What the overlay was opened on, never moved: a new remark anchors to it and follows it from there.
	private ShownDiff? _overlayDiff;
	private int _overlayCapturedRow;
	// Counts overlay openings, so a save that finishes late clears only the box it came from.
	private int _overlayActivation;

	public ProjectScreenView()
	{
		InitializeComponent();
		DiffEditor.Options.LineHeightFactor = DiffLineHeightFactor;
		DiffEditor.TextArea.LeftMargins.Insert(0, _diffNumbers);
		DiffEditor.TextArea.LeftMargins.Insert(1, _remarkMargin);
		DiffEditor.TextArea.TextView.BackgroundRenderers.Add(_diffBackground);
		DiffEditor.TextArea.TextView.ScrollOffsetChanged += (_, _) => PositionOverlay();
		DiffEditor.TextArea.TextView.VisualLinesChanged += (_, _) => PositionOverlay();
		_remarkMargin.RowClicked += (_, row) => OpenOverlay(row);
		_diffNumbers.RowClicked += (_, row) => OpenOverlay(row);
		// The "+" shows for the line under the pointer wherever it is on the line, and a click in
		// the text closes an open thread so reading goes on without a separate close.
		var textArea = DiffEditor.TextArea;
		textArea.AddHandler(PointerMovedEvent, (_, e) => _remarkMargin.SetHoverRow(RowAt(e)), RoutingStrategies.Tunnel, handledEventsToo: true);
		textArea.AddHandler(PointerExitedEvent, (_, _) => _remarkMargin.SetHoverRow(null), RoutingStrategies.Direct | RoutingStrategies.Bubble, handledEventsToo: true);
		textArea.TextView.AddHandler(PointerPressedEvent, OnTextPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
		RemarkOverlay.CloseRequested += (_, _) => CloseOverlay();
		RemarkOverlay.AddRequested += OnAddRemarkRequested;
		RemarkOverlay.SizeChanged += (_, _) => PositionOverlay();
		RemarkLayer.SizeChanged += (_, _) => PositionOverlay();
		AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		MarkdownClickGuard.Attach(ReviewLogPreview);
		MarkdownPreviewHook.Attach(
			ReviewLogPreview,
			() => _screen?.SelectedReviewLog is { } log ? (_screen.RootPath, log) : null,
			target => FollowLink(target, ReviewLogPreview));
		RenderedView.LinkFollowed += (_, target) => FollowLink(target, RenderedView);
		_linkNoticeTimer = new DispatcherTimer { Interval = LinkNoticeTime };
		_linkNoticeTimer.Tick += (_, _) =>
		{
			_linkNoticeTimer.Stop();
			LinkNotice.IsVisible = false;
		};
		DataContextChanged += (_, _) => Attach(DataContext as ProjectScreenViewModel);
		// A review still running appends turns to the open log; re-read it while it is on screen.
		_logReloadTimer = new DispatcherTimer { Interval = LogReloadInterval };
		_logReloadTimer.Tick += OnLogReloadTick;
		AttachedToVisualTree += (_, _) => _logReloadTimer.Start();
		DetachedFromVisualTree += (_, _) => _logReloadTimer.Stop();
	}

	// A press on a fold opens or closes its removed lines; anywhere else in the text it closes the thread.
	private void OnTextPressed(object? sender, PointerPressedEventArgs e)
	{
		if (RowAt(e) is { } row && row < (_shownRows?.Count ?? 0) && _shownRows![row].Kind == DiffRowKind.Fold)
		{
			e.Handled = true;
			_changes?.ToggleFold(row);
			return;
		}

		CloseOverlay();
	}

	private void OnLogReloadTick(object? sender, EventArgs e)
	{
		if (IsEffectivelyVisible && _screen is { SelectedTab: ProjectScreenTab.ReviewLogs } screen)
		{
			_eventTasks.TryRun("project-screen-log-reload", () => screen.ReloadReviewLogAsync(CancellationToken.None), static _ => Task.CompletedTask);
		}
	}

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks)
	{
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		RenderedView.ConfigureLifecycle(eventTasks);
	}

	private void Attach(ProjectScreenViewModel? screen)
	{
		if (ReferenceEquals(screen, _screen))
		{
			return;
		}

		_screen?.PropertyChanged -= OnScreenPropertyChanged;
		_changes?.PropertyChanged -= OnChangesPropertyChanged;
		_remarks?.PropertyChanged -= OnRemarksPropertyChanged;
		_screen = screen;
		_changes = screen?.Changes;
		_remarks = screen?.Remarks;
		_screen?.PropertyChanged += OnScreenPropertyChanged;
		_changes?.PropertyChanged += OnChangesPropertyChanged;
		_remarks?.PropertyChanged += OnRemarksPropertyChanged;
		CloseOverlay();
		RefreshTab();
		RefreshChanges();
		RefreshReviewLog();
	}

	// The log's images come from its folder, and a missing one shows its alt text, as in Docs.
	private void RefreshReviewLog()
	{
		MarkdownPreviewHook.SetDocument(ReviewLogPreview, _screen?.RootPath, _screen?.SelectedReviewLog);
		var text = MarkdownPreviewHook.Prepare(_screen?.ReviewLogText ?? string.Empty, ReviewLogPreview.AssetPathRoot);
		if (!string.Equals(ReviewLogPreview.Markdown, text, StringComparison.Ordinal))
		{
			ReviewLogPreview.Markdown = text;
		}
	}

	private void OnScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectScreenViewModel.SelectedTab))
		{
			RefreshTab();
		}
		else if (e.PropertyName is nameof(ProjectScreenViewModel.SelectedReviewLog) or nameof(ProjectScreenViewModel.ReviewLogText))
		{
			RefreshReviewLog();
		}
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.DiffRows)
			or nameof(ProjectChangesViewModel.SelectedFile)
			or nameof(ProjectChangesViewModel.DiffPlaceholder)
			or nameof(ProjectChangesViewModel.HiddenLineCount))
		{
			RefreshChanges();
		}
		else if (e.PropertyName is nameof(ProjectChangesViewModel.EffectiveMode)
			or nameof(ProjectChangesViewModel.CanShowWholeFile)
			or nameof(ProjectChangesViewModel.CanRender))
		{
			RefreshModeBar();
		}
		else if (e.PropertyName == nameof(ProjectChangesViewModel.ShownDiff))
		{
			RefreshRemarkMarks();
			RefreshRendered();
		}
	}

	private void OnRemarksPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ChangesRemarksViewModel.ThreadsByRow))
		{
			RefreshRemarkMarks();
			RefreshOverlay();
		}
	}

	private void RefreshTab()
	{
		var tab = _screen?.SelectedTab ?? ProjectScreenTab.Overview;
		OverviewTab.IsChecked = tab == ProjectScreenTab.Overview;
		ChangesTab.IsChecked = tab == ProjectScreenTab.Changes;
		ReviewLogsTab.IsChecked = tab == ProjectScreenTab.ReviewLogs;
		OverviewPanel.IsVisible = tab == ProjectScreenTab.Overview;
		ChangesPanel.IsVisible = tab == ProjectScreenTab.Changes;
		ReviewLogsPanel.IsVisible = tab == ProjectScreenTab.ReviewLogs;
	}

	private void RefreshChanges()
	{
		DiffPlaceholderText.IsVisible = _changes?.DiffPlaceholder is not null;
		ShowAllButton.IsVisible = _changes?.HiddenLineCount > 0;
		RefreshModeBar();
		RefreshDiffText();
		RefreshRemarkMarks();
	}

	private void RefreshModeBar()
	{
		var whole = _changes?.CanShowWholeFile == true;
		var mode = _changes?.EffectiveMode ?? DiffViewMode.Diff;
		DiffModeButton.IsVisible = whole;
		WholeFileModeButton.IsVisible = whole;
		RenderedModeButton.IsVisible = _changes?.CanRender == true;
		ModeBar.IsVisible = whole;
		DiffModeButton.IsChecked = mode == DiffViewMode.Diff;
		WholeFileModeButton.IsChecked = mode == DiffViewMode.WholeFile;
		RenderedModeButton.IsChecked = mode == DiffViewMode.Rendered;
		var rendered = mode == DiffViewMode.Rendered;
		RenderedHost.IsVisible = rendered;
		DiffEditor.IsVisible = !rendered;
		if (rendered)
		{
			CloseOverlay();
		}

		RefreshRendered();
	}

	private void RefreshRendered()
	{
		if (_changes is { EffectiveMode: DiffViewMode.Rendered, ShownDiff: { } shown })
		{
			RenderedView.ProjectRoot = _screen?.RootPath;
			RenderedView.Show(shown, _remarks);
		}
	}

	// A project document opens in the Docs pane; a heading is looked for in what is shown here.
	private void FollowLink(MarkdownLinkTarget target, Control shown)
	{
		switch (target)
		{
			case ProjectDocumentLink document when _screen is { } screen:
				_eventTasks.TryRun(
					"project-screen-link",
					async () =>
					{
						if (!await screen.OpenDocumentAsync(document.RelativePath, document.Heading, CancellationToken.None))
						{
							ShowLinkNotice($"Not found: {document.RelativePath}");
						}
					},
					static _ => Task.CompletedTask);
				break;
			case HeadingLink heading:
				if (!MarkdownPreviewHook.ScrollToHeading(shown, heading.Heading))
				{
					ShowLinkNotice($"No such heading: #{heading.Heading}");
				}

				break;
			case WebLink web:
				MarkdownPreviewHook.OpenWeb(web.Uri);
				break;
			case RefusedLink refused:
				ShowLinkNotice(refused.Notice);
				break;
		}
	}

	private void ShowLinkNotice(string notice)
	{
		LinkNoticeText.Text = notice;
		LinkNotice.IsVisible = true;
		_linkNoticeTimer.Stop();
		_linkNoticeTimer.Start();
	}

	private void OnDiffModeClicked(object? sender, RoutedEventArgs e) => SelectMode(DiffViewMode.Diff);

	private void OnWholeFileModeClicked(object? sender, RoutedEventArgs e) => SelectMode(DiffViewMode.WholeFile);

	private void OnRenderedModeClicked(object? sender, RoutedEventArgs e) => SelectMode(DiffViewMode.Rendered);

	private void SelectMode(DiffViewMode mode)
	{
		_changes?.Mode = mode;
		// The toggle flips itself on click; the model decides what is checked.
		RefreshModeBar();
	}

	// One document holds the whole diff, a line per row: the editor draws only what is on screen
	// and selects across lines, and the margin and tint read the row of each line.
	private void RefreshDiffText()
	{
		// Two files can have equal diffs, which keep the same rows, so the colouring follows the
		// picked file on its own.
		DiffEditor.SetSyntaxFor(_changes?.SelectedFile?.Entry.Path);
		var rows = _changes?.DiffRows ?? [];
		var path = _changes?.SelectedFile?.Entry.Path;
		if (ReferenceEquals(rows, _shownRows) && string.Equals(path, _shownPath, StringComparison.Ordinal))
		{
			return;
		}

		// The same file redrawn after an edit, a fold or a mode switch keeps its place and an open
		// thread; another file starts at the top.
		var samePath = path is not null && string.Equals(path, _shownPath, StringComparison.Ordinal);
		_shownRows = rows;
		_shownPath = path;
		if (!samePath)
		{
			CloseOverlay();
		}

		var scroller = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(DiffEditor).OfType<ScrollViewer>().FirstOrDefault();
		var offset = scroller?.Offset ?? default;
		_diffNumbers.SetRows(rows);
		_diffBackground.SetRows(rows);
		DiffEditor.Document.Text = string.Join('\n', rows.Select(row => row.Text));
		if (samePath && scroller is not null)
		{
			DiffEditor.UpdateLayout();
			scroller.Offset = offset;
			FollowOverlay();
		}
		else
		{
			DiffEditor.ScrollToHome();
		}
	}

	// After the file was redrawn: the overlay moves to where its line is now, or stays put when the
	// line is gone; what it was opened on does not change.
	private void FollowOverlay()
	{
		if (_overlayRow is not { } row || _overlayDiff is not { } captured || _changes?.ShownDiff is not { } shown)
		{
			return;
		}

		_overlayRow = _remarks?.MapRow(captured, _overlayCapturedRow, shown)
			?? Math.Clamp(row, 0, Math.Max(0, DiffEditor.Document.LineCount - 1));
		RefreshOverlay();
	}

	// Marks go on the editor's rows only while the remarks were placed on that very row list.
	private void RefreshRemarkMarks()
	{
		var shown = _changes?.ShownDiff;
		if (_remarks is not { } remarks || shown is null || !ReferenceEquals(shown.Rows, _shownRows))
		{
			_remarkMargin.SetMarks(static _ => false, NoThreads);
			return;
		}

		var placed = remarks.ThreadsByRow;
		_remarkMargin.SetMarks(
			row => remarks.CanRemarkOn(shown, row),
			placed is not null && ReferenceEquals(placed.ForDiff.Rows, _shownRows) ? placed.ByRow : NoThreads);
	}

	private IReadOnlyList<RemarkThreadViewModel> ThreadsAt(int row) =>
		_remarks?.ThreadsByRow is { } placed
		&& ReferenceEquals(placed.ForDiff.Rows, _shownRows)
		&& placed.ByRow.TryGetValue(row, out var threads)
			? threads
			: [];

	private void OpenOverlay(int row)
	{
		if (_remarks is not { } remarks || _changes?.ShownDiff is not { } shown || !ReferenceEquals(shown.Rows, _shownRows))
		{
			return;
		}

		var threads = ThreadsAt(row);
		var canAdd = threads.Count == 0 && remarks.CanRemarkOn(shown, row);
		if (threads.Count == 0 && !canAdd)
		{
			return;
		}

		SaveTypedDrafts();
		_overlayRow = row;
		_overlayDiff = shown;
		_overlayCapturedRow = row;
		_overlayActivation++;
		RemarkOverlay.Show(threads, canAdd);
		RemarkOverlay.IsVisible = true;
		PositionOverlay();
		// Typing starts at once: the box takes the focus once the overlay is laid out.
		Dispatcher.UIThread.Post(RemarkOverlay.FocusInput, DispatcherPriority.Loaded);
	}

	private int? RowAt(PointerEventArgs e)
	{
		var textView = DiffEditor.TextArea.TextView;
		if (!textView.VisualLinesValid)
		{
			return null;
		}

		var line = textView.GetVisualLineFromVisualTop(e.GetPosition(textView).Y + textView.VerticalOffset);
		return line is null ? null : line.FirstDocumentLine.LineNumber - 1;
	}

	// After the threads moved: the overlay shows its row's threads, and closes when its thread is gone.
	private void RefreshOverlay()
	{
		if (_overlayRow is not { } row)
		{
			return;
		}

		var threads = ThreadsAt(row);
		// A new remark is judged on what it was opened on, so edits of the file leave it open.
		var adding = threads.Count == 0 && _overlayDiff is { } shown && _remarks?.CanRemarkOn(shown, _overlayCapturedRow) == true
			&& RemarkOverlay.Threads.Count == 0;
		if (threads.Count == 0 && !adding)
		{
			CloseOverlay();
			return;
		}

		if (!threads.SequenceEqual(RemarkOverlay.Threads))
		{
			RemarkOverlay.Show(threads, adding);
		}

		PositionOverlay();
	}

	// Under its line, or above it when there is no room below; hidden while the line is scrolled away.
	private void PositionOverlay()
	{
		if (_overlayRow is not { } row)
		{
			return;
		}

		var textView = DiffEditor.TextArea.TextView;
		var line = row + 1;
		var lineCount = DiffEditor.Document.LineCount;
		if (line > lineCount)
		{
			CloseOverlay();
			return;
		}

		var scroll = textView.ScrollOffset.Y;
		var top = textView.GetVisualTopByDocumentLine(line);
		var next = line < lineCount
			? textView.GetVisualTopByDocumentLine(line + 1)
			: top + (line > 1 ? top - textView.GetVisualTopByDocumentLine(line - 1) : textView.DefaultLineHeight);
		var lineTop = top - scroll;
		var lineBottom = next - scroll;
		if (lineBottom <= 0 || lineTop >= textView.Bounds.Height || textView.TranslatePoint(default, RemarkLayer) is not { } origin)
		{
			RemarkOverlay.IsVisible = false;
			return;
		}

		var layerHeight = RemarkLayer.Bounds.Height;
		var below = layerHeight - (origin.Y + lineBottom);
		var above = origin.Y + lineTop;
		// The narrowest width that fits the thread whole on one side of its line, widening up to a
		// share of the panel before the thread has to cover its line or scroll.
		var maxWidth = Math.Max(160, Math.Min(RemarkLayer.Bounds.Width * OverlayWidthShare, RemarkLayer.Bounds.Width - origin.X - 12));
		var width = Math.Min(OverlayStartWidth, maxWidth);
		RemarkOverlay.MaxHeight = double.PositiveInfinity;
		var height = MeasureOverlay(width);
		while (height > Math.Max(below, above) && width < maxWidth)
		{
			width = Math.Min(maxWidth, width + OverlayWidthStep);
			height = MeasureOverlay(width);
		}

		double y;
		if (height <= below)
		{
			y = origin.Y + lineBottom;
		}
		else if (height <= above)
		{
			y = origin.Y + lineTop - height;
		}
		else
		{
			// Fits neither side whole: it takes the whole height of the diff, over its line, and scrolls only past that.
			RemarkOverlay.MaxHeight = Math.Max(OverlayMinHeight, layerHeight - 4);
			height = Math.Min(height, RemarkOverlay.MaxHeight);
			y = below >= above ? origin.Y + lineBottom : origin.Y + lineTop - height;
		}

		y = Math.Clamp(y, 0, Math.Max(0, layerHeight - height));

		Canvas.SetLeft(RemarkOverlay, origin.X);
		Canvas.SetTop(RemarkOverlay, y);
		RemarkOverlay.IsVisible = true;
	}

	private double MeasureOverlay(double width)
	{
		RemarkOverlay.Width = width;
		RemarkOverlay.Measure(new Size(width, double.PositiveInfinity));
		return RemarkOverlay.DesiredSize.Height;
	}

	private void CloseOverlay()
	{
		SaveTypedDrafts();
		_overlayRow = null;
		_overlayDiff = null;
		RemarkOverlay.IsVisible = false;
		RemarkOverlay.Show([], canAdd: false);
	}

	// Typed but unsaved replies are kept when the overlay closes or moves to another row.
	private void SaveTypedDrafts()
	{
		foreach (var thread in RemarkOverlay.Threads.Where(thread => thread.HasUnsavedDraft))
		{
			_eventTasks.TryRun("remark-save", () => thread.SaveDraftAsync(CancellationToken.None), static _ => Task.CompletedTask);
		}
	}

	private void OnAddRemarkRequested(object? sender, string text)
	{
		if (_remarks is not { } remarks || _overlayDiff is not { } shown || _overlayRow is null)
		{
			return;
		}

		var row = _overlayCapturedRow;
		var activation = _overlayActivation;
		_eventTasks.TryRun(
			"remark-add",
			async () =>
			{
				if (await remarks.AddRemarkAsync(shown, row, text, CancellationToken.None) is not null
					&& activation == _overlayActivation)
				{
					RemarkOverlay.ClearNewRemark();
				}
			},
			static _ => Task.CompletedTask);
	}

	private void OnRemarkActionRequested(object? sender, RemarkActionEventArgs e)
	{
		e.Handled = true;
		_eventTasks.TryRun(e.Name, () => e.Action(CancellationToken.None), static _ => Task.CompletedTask);
	}

	private void OnOverviewTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.Overview);

	private void OnChangesTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.Changes);

	private void OnReviewLogsTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.ReviewLogs);

	private void SelectTab(ProjectScreenTab tab)
	{
		// The toggle flips itself on click; the view model decides what is checked.
		RefreshTab();
		if (_screen is { } screen)
		{
			_eventTasks.TryRun("project-screen-tab", () => screen.SelectTabAsync(tab, CancellationToken.None), static _ => Task.CompletedTask);
		}
	}

	private void OnShowAllClicked(object? sender, RoutedEventArgs e) => _changes?.ShowAllLines();
}
