using System.ComponentModel;
using System.Globalization;
using Avalonia.Controls;
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
	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);

	public RemarksPanel()
	{
		InitializeComponent();
		// The lists live in popups, outside this view's tree, so their actions are caught on the lists.
		OpenRemarksList.AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		ResolvedRemarksList.AddHandler(RemarkThreadView.ActionRequestedEvent, OnRemarkActionRequested);
		Refresh();
	}

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
