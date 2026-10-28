using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Diff;
using Pact.App.Avalonia.Views.Remarks;
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
	private const double OverlayMaxWidth = 900;
	private const double OverlayMinHeight = 80;
	private readonly DispatcherTimer _logReloadTimer;

	private readonly DiffLineNumberMargin _diffNumbers = new();
	private readonly DiffRemarkMargin _remarkMargin = new();
	private readonly DiffLineBackgroundRenderer _diffBackground = new();
	private IReadOnlyList<DiffRowViewModel>? _shownRows;

	// The row the remark overlay is open on and the diff it was opened on.
	private int? _overlayRow;
	private ShownDiff? _overlayDiff;

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
		textArea.TextView.AddHandler(PointerPressedEvent, (_, _) => CloseOverlay(), RoutingStrategies.Tunnel, handledEventsToo: true);
		RemarkOverlay.CloseRequested += (_, _) => CloseOverlay();
		RemarkOverlay.AddRequested += OnAddRemarkRequested;
		RemarkOverlay.SizeChanged += (_, _) => PositionOverlay();
		RemarkLayer.SizeChanged += (_, _) => PositionOverlay();
		AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		// The open-remarks list lives in a popup, outside this view's tree, so its actions are caught on the list.
		OpenRemarksList.AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		MarkdownClickGuard.Attach(ReviewLogPreview);
		DataContextChanged += (_, _) => Attach(DataContext as ProjectScreenViewModel);
		// A review still running appends turns to the open log; re-read it while it is on screen.
		_logReloadTimer = new DispatcherTimer { Interval = LogReloadInterval };
		_logReloadTimer.Tick += OnLogReloadTick;
		AttachedToVisualTree += (_, _) => _logReloadTimer.Start();
		DetachedFromVisualTree += (_, _) => _logReloadTimer.Stop();
	}

	private void OnLogReloadTick(object? sender, EventArgs e)
	{
		if (IsEffectivelyVisible && _screen is { SelectedTab: ProjectScreenTab.ReviewLogs } screen)
		{
			_eventTasks.TryRun("project-screen-log-reload", () => screen.ReloadReviewLogAsync(CancellationToken.None), static _ => Task.CompletedTask);
		}
	}

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks) =>
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

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
		RefreshRemarkBar();
	}

	private void OnScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectScreenViewModel.SelectedTab))
		{
			RefreshTab();
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
		else if (e.PropertyName == nameof(ProjectChangesViewModel.ShownDiff))
		{
			RefreshRemarkMarks();
		}
	}

	private void OnRemarksPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ChangesRemarksViewModel.ThreadsByRow))
		{
			RefreshRemarkMarks();
			RefreshOverlay();
		}

		RefreshRemarkBar();
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
		RefreshDiffText();
		RefreshRemarkMarks();
	}

	// One document holds the whole diff, a line per row: the editor draws only what is on screen
	// and selects across lines, and the margin and tint read the row of each line.
	private void RefreshDiffText()
	{
		// Two files can have equal diffs, which keep the same rows, so the colouring follows the
		// picked file on its own.
		DiffEditor.SetSyntaxFor(_changes?.SelectedFile?.Entry.Path);
		var rows = _changes?.DiffRows ?? [];
		if (ReferenceEquals(rows, _shownRows))
		{
			return;
		}

		_shownRows = rows;
		CloseOverlay();
		_diffNumbers.SetRows(rows);
		_diffBackground.SetRows(rows);
		DiffEditor.Document.Text = string.Join('\n', rows.Select(row => row.Text));
		DiffEditor.ScrollToHome();
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
		var adding = threads.Count == 0 && _overlayDiff is { } shown && _remarks?.CanRemarkOn(shown, row) == true
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

		var width = Math.Max(160, Math.Min(OverlayMaxWidth, RemarkLayer.Bounds.Width - origin.X - 12));
		RemarkOverlay.Width = width;
		RemarkOverlay.MaxHeight = double.PositiveInfinity;
		RemarkOverlay.Measure(new Size(width, double.PositiveInfinity));
		var height = RemarkOverlay.DesiredSize.Height;
		var layerHeight = RemarkLayer.Bounds.Height;
		var below = layerHeight - (origin.Y + lineBottom);
		var above = origin.Y + lineTop;
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
			// Fits neither side whole: it takes the roomier side and scrolls inside.
			RemarkOverlay.MaxHeight = Math.Max(OverlayMinHeight, Math.Max(below, above) - 4);
			height = Math.Min(height, RemarkOverlay.MaxHeight);
			y = below >= above ? origin.Y + lineBottom : origin.Y + lineTop - height;
		}

		y = Math.Clamp(y, 0, Math.Max(0, layerHeight - height));

		Canvas.SetLeft(RemarkOverlay, origin.X);
		Canvas.SetTop(RemarkOverlay, y);
		RemarkOverlay.IsVisible = true;
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
		if (_remarks is not { } remarks || _overlayDiff is not { } shown || _overlayRow is not { } row)
		{
			return;
		}

		_eventTasks.TryRun(
			"remark-add",
			async () =>
			{
				if (await remarks.AddRemarkAsync(shown, row, text, CancellationToken.None) is not null)
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

	/// <summary>The menu of sessions Send opened when there were several; for tests.</summary>
	internal MenuFlyout? SendMenu { get; private set; }

	private void RefreshRemarkBar()
	{
		if (_remarks is not { } remarks)
		{
			RemarkBar.IsVisible = false;
			return;
		}

		var open = remarks.OpenThreads;
		RemarkBarText.Text = remarks.BarText;
		RemarkStatusText.Text = remarks.StatusText;
		OpenRemarksButton.IsVisible = open.Count > 0;
		OpenRemarksButton.Content = string.Create(CultureInfo.InvariantCulture, $"Open remarks ({open.Count})");
		OpenRemarksList.ItemsSource = open;
		SendRemarksButton.IsEnabled = remarks.CanSend;
		ToolTip.SetTip(SendRemarksButton, remarks.SendBlockedReason);
		RemarkBar.IsVisible = remarks.OpenCount > 0 || remarks.StatusText is not null;
	}

	// One session takes the remarks at once; with several, the user picks one.
	private void OnSendRemarksClicked(object? sender, RoutedEventArgs e)
	{
		if (_remarks is not { } remarks)
		{
			return;
		}

		var targets = remarks.SendTargets;
		if (targets.Count == 1)
		{
			SendRemarks(remarks, targets[0].SessionId);
			return;
		}

		MenuFlyout menu = new();
		foreach (var target in targets)
		{
			MenuItem item = new() { Header = target.Title };
			item.Click += (_, _) => SendRemarks(remarks, target.SessionId);
			menu.Items.Add(item);
		}

		SendMenu = menu;
		if (targets.Count > 1)
		{
			menu.ShowAt(SendRemarksButton);
		}
	}

	private void SendRemarks(ChangesRemarksViewModel remarks, string sessionId)
	{
		SendMenu?.Hide();
		_eventTasks.TryRun("remarks-send", () => remarks.SendAsync(sessionId, CancellationToken.None), static _ => Task.CompletedTask);
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
