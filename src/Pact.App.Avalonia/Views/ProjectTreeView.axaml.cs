using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class ProjectTreeView : UserControl
{
	private MainWindowViewModel? _viewModel;
	private readonly DispatcherTimer _busySpinnerTimer;
	private PointerPressedEventArgs? _dragPointerPressed;
	private Point _dragStart;
	private Size _dragThreshold;
	private object? _dragCandidate;
	private object? _draggedTreeItem;
	private bool _selectionActivationEnabled = true;
	private bool _userSelectionInputPending;
	private int _busySpinnerFrameIndex;

	public ProjectTreeView()
	{
		InitializeComponent();
		_busySpinnerTimer = new DispatcherTimer { Interval = BusySpinner.Interval };
		_busySpinnerTimer.Tick += (_, _) => AdvanceBusySpinnerFrame();
		AttachedToVisualTree += (_, _) => _busySpinnerTimer.Start();
		DetachedFromVisualTree += (_, _) => _busySpinnerTimer.Stop();
		ProjectTree.AddHandler(InputElement.PointerPressedEvent, OnProjectTreePointerPressed, RoutingStrategies.Tunnel);
		ProjectTree.AddHandler(InputElement.KeyDownEvent, OnProjectTreeKeyDown, RoutingStrategies.Tunnel);
		ProjectTree.SelectionChanged += OnSelectionChanged;
		RootTree.AddHandler(InputElement.PointerPressedEvent, OnProjectTreePointerPressed, RoutingStrategies.Tunnel);
		RootTree.AddHandler(InputElement.KeyDownEvent, OnProjectTreeKeyDown, RoutingStrategies.Tunnel);
		RootTree.SelectionChanged += OnSelectionChanged;
		DataContextChanged += OnDataContextChanged;
	}

	internal string AdvanceBusySpinnerFrame()
	{
		var frame = BusySpinner.Advance(ref _busySpinnerFrameIndex);
		foreach (var indicator in ProjectTree.GetVisualDescendants()
					 .Concat(RootTree.GetVisualDescendants())
					 .OfType<TextBlock>()
					 .Where(text => text.IsVisible && text.Classes.Contains("busy")))
		{
			indicator.Text = frame;
		}

		return frame;
	}

	public event EventHandler<object?>? SelectedItemChanged;
	public event EventHandler? SelectOrchestratorRequested;
	public event EventHandler? StartOrchestratorRequested;
	public event EventHandler? StopOrchestratorRequested;
	public event EventHandler<WorkspaceViewModel>? PauseProjectRequested;
	public event EventHandler<WorkspaceViewModel>? CloseProjectRequested;
	public event EventHandler? AddProjectRequested;
	public event EventHandler<WorkspaceViewModel>? ResumePausedProjectRequested;
	public event EventHandler<GitFlyoutRequest>? GitRequested;
	public event EventHandler<WorkspaceActionFlyoutRequest>? AddSessionRequested;
	public event EventHandler<WorkspaceActionFlyoutRequest>? AddWebPageRequested;
	public event EventHandler<RootActionFlyoutRequest>? AddRootSessionRequested;
	public event EventHandler<RootActionFlyoutRequest>? AddRootWebPageRequested;
	public event EventHandler<WorkspaceViewModel>? NotesToggleRequested;
	public event EventHandler<(SessionViewModel Session, bool PreferResumeCommand)>? RestartSessionRequested;
	public event EventHandler<SessionViewModel>? CloseSessionRequested;
	public event EventHandler<SessionViewModel>? PauseRootSessionRequested;
	public event EventHandler<SessionViewModel>? ResumeRootSessionRequested;
	public event EventHandler<WebPageViewModel>? ReloadWebPageRequested;
	/// <summary>
	/// Requests copying the current full address of a web-page row.
	/// </summary>
	public event EventHandler<WebPageViewModel>? CopyWebPageAddressRequested;
	public event EventHandler<WebPageViewModel>? CloseWebPageRequested;
	public event EventHandler<WebPageViewModel>? PauseRootWebPageRequested;
	public event EventHandler<WebPageViewModel>? ResumeRootWebPageRequested;
	public event EventHandler<ProjectNoteViewModel>? CloseNoteRequested;
	public event EventHandler<WorkspaceViewModel>? EditProjectRequested;
	public event EventHandler<SessionViewModel>? EditSessionRequested;
	public event EventHandler<WebPageViewModel>? EditWebPageRequested;
	public event EventHandler<TreeItemDropRequest>? TreeItemDropRequested;

	private void OnOrchestratorPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (e.GetCurrentPoint(this).Properties.PointerUpdateKind
			== PointerUpdateKind.LeftButtonPressed)
		{
			SelectOrchestratorRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	private void OnStartOrchestratorClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		StartOrchestratorRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OnStopOrchestratorClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		StopOrchestratorRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (sender is not TreeView tree)
		{
			return;
		}

		var selectedItem = tree.SelectedItem;
		if (selectedItem is null)
		{
			return;
		}

		var activate = _selectionActivationEnabled && _userSelectionInputPending;
		_userSelectionInputPending = false;
		tree.SelectedItem = null;
		if (activate)
		{
			SelectedItemChanged?.Invoke(this, selectedItem);
		}
	}

	internal void SetSelectionActivationEnabled(bool enabled)
	{
		_selectionActivationEnabled = enabled;
		if (!enabled)
		{
			_userSelectionInputPending = false;
			ProjectTree.SelectedItem = null;
			RootTree.SelectedItem = null;
		}
	}

	internal void NotifyUserSelectionInput()
	{
		_userSelectionInputPending = true;
		Dispatcher.UIThread.Post(
			() => _userSelectionInputPending = false,
			DispatcherPriority.Input);
	}

	private void OnProjectTreePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not TreeView tree
			|| e.GetCurrentPoint(tree).Properties.IsRightButtonPressed)
		{
			return;
		}

		if (e.Source is Control source
			&& (source is Button || source.FindAncestorOfType<Button>() is not null))
		{
			return;
		}

		NotifyUserSelectionInput();
	}

	private void OnProjectTreeKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
		{
			NotifyUserSelectionInput();
		}
	}

	private void OnPauseProjectClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace })
		{
			PauseProjectRequested?.Invoke(this, workspace);
		}
	}

	private void OnCloseProjectClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace })
		{
			CloseProjectRequested?.Invoke(this, workspace);
		}
	}

	private void OnAddProjectClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		AddProjectRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OnProjectGitClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace } anchor)
		{
			GitRequested?.Invoke(this, new GitFlyoutRequest(workspace, anchor));
		}
	}

	private void OnAddSessionClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace } anchor)
		{
			AddSessionRequested?.Invoke(this, new WorkspaceActionFlyoutRequest(workspace, anchor));
		}
	}

	private void OnAddWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace } anchor)
		{
			AddWebPageRequested?.Invoke(this, new WorkspaceActionFlyoutRequest(workspace, anchor));
		}
	}

	private void OnAddRootSessionClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control anchor)
		{
			AddRootSessionRequested?.Invoke(this, new RootActionFlyoutRequest(anchor));
		}
	}

	private void OnAddRootWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control anchor)
		{
			AddRootWebPageRequested?.Invoke(this, new RootActionFlyoutRequest(anchor));
		}
	}

	private void OnNotesToggleClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace })
		{
			NotesToggleRequested?.Invoke(this, workspace);
		}
	}

	private void OnStartNewSessionClicked(object? sender, RoutedEventArgs e) =>
		RaiseRestartSession(sender, e, preferResumeCommand: false);

	private void OnRestartSessionMenuClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is not Button { DataContext: SessionViewModel session, Flyout: MenuFlyout flyout })
		{
			return;
		}

		foreach (var item in flyout.Items.OfType<MenuItem>())
		{
			item.DataContext = session;
		}
	}

	private void OnRestartCurrentSessionClicked(object? sender, RoutedEventArgs e) =>
		RaiseRestartSession(sender, e, preferResumeCommand: true);

	private void RaiseRestartSession(object? sender, RoutedEventArgs e, bool preferResumeCommand)
	{
		e.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{
			RestartSessionRequested?.Invoke(this, (session, preferResumeCommand));
		}
	}

	private void OnCloseSessionRowClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{
			CloseSessionRequested?.Invoke(this, session);
		}
	}

	private void OnPauseRootSessionClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{
			PauseRootSessionRequested?.Invoke(this, session);
		}
	}

	private void OnResumeRootSessionClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{
			ResumeRootSessionRequested?.Invoke(this, session);
		}
	}

	private void OnEditProjectClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WorkspaceViewModel workspace })
		{
			EditProjectRequested?.Invoke(this, workspace);
		}
	}

	private void OnEditSessionClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: SessionViewModel session })
		{
			EditSessionRequested?.Invoke(this, session);
		}
	}

	private void OnReloadWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WebPageViewModel webPage })
		{
			ReloadWebPageRequested?.Invoke(this, webPage);
		}
	}

	private void OnEditWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WebPageViewModel webPage })
		{
			EditWebPageRequested?.Invoke(this, webPage);
		}
	}

	private void OnTreeItemDragPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		var properties = e.GetCurrentPoint(this).Properties;
		if (properties.IsRightButtonPressed)
		{
			if (sender is Control { DataContext: WebPageViewModel webPage })
			{
				e.Handled = true;
				CopyWebPageAddressRequested?.Invoke(this, webPage);
			}

			return;
		}

		if (!properties.IsLeftButtonPressed
			|| sender is not Control { DataContext: SessionViewModel or WebPageViewModel } control
			|| e.Source is Control sourceControl
				&& (sourceControl is Button
					|| sourceControl.GetVisualAncestors().OfType<Button>().Any()))
		{
			return;
		}

		_dragCandidate = control.DataContext;
		_dragPointerPressed = e;
		_dragStart = e.GetPosition(this);
		_dragThreshold = this.GetPlatformSettings()?.GetTapSize(e.Pointer.Type) ?? new Size(8, 8);
	}

	private async void OnTreeItemDragPointerMoved(object? sender, PointerEventArgs e)
	{
		if (_dragCandidate is null
			|| _dragPointerPressed is null
			|| !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
			|| !HasExceededDragThreshold(_dragStart, e.GetPosition(this), _dragThreshold))
		{
			return;
		}

		var draggedItem = _dragCandidate;
		var pointerPressed = _dragPointerPressed;
		ClearDragCandidate();
		using DataTransfer dataTransfer = new();
		DataTransferItem dataItem = new();
		dataItem.SetText("Pact tree item");
		dataTransfer.Add(dataItem);
		_draggedTreeItem = draggedItem;
		try
		{
			await DragDrop.DoDragDropAsync(pointerPressed, dataTransfer, DragDropEffects.Move);
		}
		finally
		{
			_draggedTreeItem = null;
		}
	}

	private void OnTreeItemDragPointerReleased(object? sender, PointerReleasedEventArgs e) =>
		ClearDragCandidate();

	private void OnTreeItemDragOver(object? sender, DragEventArgs e)
	{
		e.DragEffects = CanDropOn(sender) ? DragDropEffects.Move : DragDropEffects.None;
		e.Handled = true;
	}

	private void OnTreeItemDrop(object? sender, DragEventArgs e)
	{
		if (!CanDropOn(sender) || sender is not Control control || _draggedTreeItem is null)
		{
			e.DragEffects = DragDropEffects.None;
			e.Handled = true;
			return;
		}

		var insertAfter = IsInsertAfter(e.GetPosition(control).Y, control.Bounds.Height);
		TreeItemDropRequested?.Invoke(
			this,
			new TreeItemDropRequest(_draggedTreeItem, control.DataContext!, insertAfter));
		e.DragEffects = DragDropEffects.Move;
		e.Handled = true;
	}

	private bool CanDropOn(object? sender) =>
		_draggedTreeItem is not null
		&& sender is Control { DataContext: { } target }
		&& CanDropTreeItem(_draggedTreeItem, target);

	internal bool CanDropTreeItem(object source, object target)
	{
		if (ReferenceEquals(source, target) || source.GetType() != target.GetType())
		{
			return false;
		}

		if (source is SessionViewModel sourceSession && target is SessionViewModel targetSession)
		{
			return sourceSession.IsRootItem && targetSession.IsRootItem
				|| sourceSession.IsRootItem == targetSession.IsRootItem
				&& _viewModel?.Workspaces.Any(workspace =>
					workspace.Sessions.Contains(sourceSession)
					&& workspace.Sessions.Contains(targetSession)) == true;
		}

		if (source is WebPageViewModel sourcePage && target is WebPageViewModel targetPage)
		{
			return sourcePage.IsRootItem && targetPage.IsRootItem
				|| sourcePage.IsRootItem == targetPage.IsRootItem
				&& _viewModel?.Workspaces.Any(workspace =>
					workspace.WebPages.Contains(sourcePage)
					&& workspace.WebPages.Contains(targetPage)) == true;
		}

		return false;
	}

	internal static bool HasExceededDragThreshold(Point start, Point current, Size threshold) =>
		Math.Abs(current.X - start.X) > threshold.Width / 2
		|| Math.Abs(current.Y - start.Y) > threshold.Height / 2;

	internal static bool IsInsertAfter(double pointerY, double rowHeight) =>
		pointerY >= rowHeight / 2;

	private void ClearDragCandidate()
	{
		_dragCandidate = null;
		_dragPointerPressed = null;
	}

	private void OnCloseWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WebPageViewModel webPage })
		{
			CloseWebPageRequested?.Invoke(this, webPage);
		}
	}

	private void OnPauseRootWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WebPageViewModel webPage })
		{
			PauseRootWebPageRequested?.Invoke(this, webPage);
		}
	}

	private void OnResumeRootWebPageClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: WebPageViewModel webPage })
		{
			ResumeRootWebPageRequested?.Invoke(this, webPage);
		}
	}

	private void OnCloseNoteClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: ProjectNoteViewModel note })
		{
			CloseNoteRequested?.Invoke(this, note);
		}
	}

	private void OnPausedProjectClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is not Control { DataContext: WorkspaceViewModel workspace })
		{
			return;
		}

		PausedProjectsButton.Flyout?.Hide();
		ResumePausedProjectRequested?.Invoke(this, workspace);
	}

	/// <summary>
	/// Enables the header actions once the shell has finished loading, mirroring the WPF
	/// behavior of shipping these buttons disabled until startup completes.
	/// </summary>
	public void SetProjectActionsEnabled(bool enabled)
	{
		AddProjectButton.IsEnabled = enabled;
		AddRootSessionButton.IsEnabled = enabled;
		AddRootWebPageButton.IsEnabled = enabled;
		RefreshPausedProjectsButton();
	}

	private void OnDataContextChanged(object? sender, EventArgs e)
	{
		_viewModel?.PausedWorkspaces.CollectionChanged -= OnPausedWorkspacesChanged;

		_viewModel = DataContext as MainWindowViewModel;

		_viewModel?.PausedWorkspaces.CollectionChanged += OnPausedWorkspacesChanged;

		RefreshPausedProjectsButton();
	}

	private void OnPausedWorkspacesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
		RefreshPausedProjectsButton();

	private void RefreshPausedProjectsButton() => PausedProjectsButton.IsEnabled = _viewModel?.PausedWorkspaces.Count > 0;
}

internal sealed record GitFlyoutRequest(WorkspaceViewModel Workspace, Control Anchor);
internal sealed record WorkspaceActionFlyoutRequest(WorkspaceViewModel Workspace, Control Anchor);
internal sealed record RootActionFlyoutRequest(Control Anchor);
internal sealed record TreeItemDropRequest(object Source, object Target, bool InsertAfter);
