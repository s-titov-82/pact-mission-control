using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Lifecycle;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views.Remarks;

/// <summary>
/// The remarks of a project's Changes tab next to its file list: what is waiting to be sent, the
/// resolved and the open threads in lists of their own, and Send.
/// </summary>
internal sealed partial class RemarksPanel : UserControl
{
	private const double ListWidthShare = 0.7;
	private const double ListMinWidth = 480;
	private const double ListMinHeight = 160;
	private const double ListMargin = 20;

	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);

	public RemarksPanel()
	{
		InitializeComponent();
		// The lists live in popups, outside this view's tree, so their actions are caught on the lists.
		OpenRemarksList.AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		ResolvedRemarksList.AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		((PopupFlyoutBase)OpenRemarksButton.Flyout!).Opening += (_, _) => FitList(OpenRemarksButton, OpenRemarksScroller);
		((PopupFlyoutBase)ResolvedRemarksButton.Flyout!).Opening += (_, _) => FitList(ResolvedRemarksButton, ResolvedRemarksScroller);
		Refresh();
	}

	/// <summary>The control whose left edge the lists line up with, such as the panel holding this one; the button itself when unset.</summary>
	public Control? AlignTarget { get; set; }

	/// <summary>The remarks shown; <see langword="null"/> shows nothing to act on.</summary>
	public ChangesRemarksViewModel? Remarks
	{
		get;
		set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.PropertyChanged -= OnRemarksPropertyChanged;
			field = value;
			field?.PropertyChanged += OnRemarksPropertyChanged;
			Refresh();
		}
	}

	/// <summary>The menu of sessions Send opened when there were several; for tests.</summary>
	internal MenuFlyout? SendMenu { get; private set; }

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks) =>
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

	private void OnRemarksPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

	private void Refresh()
	{
		var remarks = Remarks;
		var open = remarks?.OpenThreads ?? [];
		var resolved = remarks?.ResolvedThreads ?? [];
		RemarkBarText.Text = remarks?.BarText;
		RemarkBarText.IsVisible = !string.IsNullOrEmpty(remarks?.BarText);
		RemarkStatusText.Text = remarks?.StatusText;
		RemarkStatusText.IsVisible = remarks?.StatusText is not null;
		OpenRemarksButton.Content = string.Create(CultureInfo.InvariantCulture, $"Open ({open.Count})");
		OpenRemarksButton.IsEnabled = open.Count > 0;
		ResolvedRemarksButton.Content = string.Create(CultureInfo.InvariantCulture, $"Resolved ({resolved.Count})");
		ResolvedRemarksButton.IsEnabled = resolved.Count > 0;
		// The same list is set again only when it changed: a new one rebuilds the rows and drops the focus.
		OpenRemarksList.ItemsSource = open;
		ResolvedRemarksList.ItemsSource = resolved;
		SendRemarksButton.IsEnabled = remarks?.CanSend == true;
		ToolTip.SetTip(SendRemarksButton, remarks?.SendBlockedReason);
	}

	// The list opens left of the panel, its right edge on the panel's edge, as wide as a share of the
	// room there and as tall as the window above the button allows, so it scrolls only past that.
	private void FitList(Button button, ScrollViewer scroller)
	{
		var anchor = AlignTarget ?? button;
		if (TopLevel.GetTopLevel(this) is not { } top
			|| button.TranslatePoint(default, top) is not { } at
			|| anchor.TranslatePoint(default, top) is not { } edge
			|| button.Flyout is not PopupFlyoutBase flyout)
		{
			return;
		}

		var room = Math.Max(0, edge.X - ListMargin);
		scroller.Width = Math.Max(Math.Min(ListMinWidth, room), room * ListWidthShare);
		scroller.MaxHeight = Math.Max(ListMinHeight, at.Y + button.Bounds.Height - (2 * ListMargin));
		flyout.HorizontalOffset = edge.X - at.X;
	}

	private void OnRemarkActionRequested(object? sender, RemarkActionEventArgs e)
	{
		e.Handled = true;
		_eventTasks.TryRun(e.Name, () => e.Action(CancellationToken.None), static _ => Task.CompletedTask);
	}

	// One session takes the remarks at once; with several, the user picks one.
	private void OnSendRemarksClicked(object? sender, RoutedEventArgs e)
	{
		if (Remarks is not { } remarks)
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
}
