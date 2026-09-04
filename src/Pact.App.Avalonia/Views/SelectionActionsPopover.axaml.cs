using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pact.App.Avalonia.SelectionActions;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class SelectionActionsPopover : UserControl
{
	private const double PopupWidth = 360;
	private Control? _pane;
	private Control? _source;
	private MainWindowViewModel? _viewModel;
	private SelectionActionAnchor _anchor;
	private bool _expandedTargets;
	private bool _requestCloseOnPopupClosed;

	public SelectionActionsPopover()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
		AttachViewModel(DataContext as MainWindowViewModel);
	}

	internal event EventHandler<SessionViewModel>? SendSelectionRequested;
	internal event EventHandler<ProjectNotesTargetViewModel>? SendSelectionToNotesRequested;
	internal event EventHandler? CloseRequested;

	internal bool IsOpen => SelectionPopup.IsOpen;

	internal void Open(Control source, Control pane, SelectionActionAnchor anchor)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(pane);

		Close();
		_pane = pane;
		_source = source;
		_anchor = anchor;
		_expandedTargets = false;
		RefreshTargetMode();
		pane.AddHandler(
			KeyDownEvent,
			OnPaneKeyDown,
			RoutingStrategies.Tunnel);
		RecalculatePlacement(source, pane, anchor);
		_requestCloseOnPopupClosed = true;
		SelectionPopup.IsOpen = true;
	}

	internal bool TryReposition(Control source, Control pane, SelectionActionAnchor anchor)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(pane);

		if (!SelectionPopup.IsOpen || !ReferenceEquals(_source, source))
		{
			return false;
		}

		if (!ReferenceEquals(_pane, pane))
		{
			_pane?.RemoveHandler(KeyDownEvent, OnPaneKeyDown);
			pane.AddHandler(
				KeyDownEvent,
				OnPaneKeyDown,
				RoutingStrategies.Tunnel);
		}

		_pane = pane;
		_source = source;
		_anchor = anchor;
		RecalculatePlacement(source, pane, anchor);
		return true;
	}

	internal void Close()
	{
		DetachPaneKeyHandler();
		_requestCloseOnPopupClosed = false;
		SelectionPopup.IsOpen = false;
	}

	internal void DetachEventProducers()
	{
		Close();
		DataContextChanged -= OnDataContextChanged;
		AttachViewModel(null);
	}

	private void OnDataContextChanged(object? sender, EventArgs e)
	{
		AttachViewModel(DataContext as MainWindowViewModel);
		RefreshTargetMode();
		RecalculatePlacementIfOpen();
	}

	private void AttachViewModel(MainWindowViewModel? viewModel)
	{
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged -= OnViewModelPropertyChanged;
			_viewModel.SelectionActionChoices.CollectionChanged -= OnViewModelCollectionChanged;
			_viewModel.SelectionActionTargetProjects.CollectionChanged -= OnViewModelCollectionChanged;
		}

		_viewModel = viewModel;
		if (_viewModel is not null)
		{
			_viewModel.PropertyChanged += OnViewModelPropertyChanged;
			_viewModel.SelectionActionChoices.CollectionChanged += OnViewModelCollectionChanged;
			_viewModel.SelectionActionTargetProjects.CollectionChanged += OnViewModelCollectionChanged;
		}
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		RefreshTargetMode();
		RecalculatePlacementIfOpen();
	}

	private void OnViewModelCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		RefreshTargetMode();
		RecalculatePlacementIfOpen();
	}

	private void RefreshTargetMode()
	{
		CompactTargetsTree.ItemsSource = _viewModel?.SelectionActionCompactTargetProject is { } compact
			? new[] { compact }
			: [];
		CompactTargetsTree.IsVisible = !_expandedTargets;
		CompactEmptyState.IsVisible =
			!_expandedTargets && _viewModel?.HasNoCompactSelectionActionTargets == true;
		AllTargetsTree.IsVisible = _expandedTargets;
		MoreTargetsButton.IsVisible =
			!_expandedTargets && _viewModel?.HasAdditionalSelectionActionTargets == true;
	}

	private void OnMoreTargetsClicked(object? sender, RoutedEventArgs e)
	{
		_expandedTargets = true;
		RefreshTargetMode();
		RecalculatePlacementIfOpen();
	}

	private void RecalculatePlacementIfOpen()
	{
		if (SelectionPopup.IsOpen && _source is not null && _pane is not null)
		{
			RecalculatePlacement(_source, _pane, _anchor);
		}
	}

	private void RecalculatePlacement(
		Control source,
		Control pane,
		SelectionActionAnchor anchor)
	{
		var paneSize = pane.Bounds.Size;
		var paneCenter = new Point(paneSize.Width / 2, paneSize.Height / 2);
		var translatedAnchor = anchor.IsAvailable
			? source.TranslatePoint(new Point(anchor.X, anchor.Y), pane)
			: null;
		var anchorPoint = translatedAnchor ?? paneCenter;

		ActionsScroll.MaxHeight = double.PositiveInfinity;
		TargetsScroll.MaxHeight = double.PositiveInfinity;
		ActionsScroll.Measure(new Size(PopupWidth, double.PositiveInfinity));
		TargetsScroll.Measure(new Size(PopupWidth, double.PositiveInfinity));
		PlacementDivider.Measure(new Size(PopupWidth, double.PositiveInfinity));

		var placement = SelectionPopoverPlacementCalculator.Calculate(
			anchorPoint,
			paneSize,
			PopupWidth,
			ActionsScroll.DesiredSize.Height,
			TargetsScroll.DesiredSize.Height,
			PlacementDivider.DesiredSize.Height);
		// An empty PlacementRect is discarded by the platform positioner, which then places the
		// popup at the pane origin; offsets from the anchor rectangle are honoured, including
		// while the popup stays open.
		SelectionPopup.PlacementTarget = pane;
		SelectionPopup.HorizontalOffset = placement.Bounds.X;
		SelectionPopup.VerticalOffset = placement.Bounds.Y;
		ActionsScroll.MaxHeight = placement.ActionsHeight;
		TargetsScroll.MaxHeight = placement.TargetsHeight;
	}

	private void OnPaneKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Escape || !SelectionPopup.IsOpen)
		{
			return;
		}

		e.Handled = true;
		CloseRequested?.Invoke(this, EventArgs.Empty);
	}

	private void OnPopupClosed(object? sender, EventArgs e)
	{
		DetachPaneKeyHandler();
		if (!_requestCloseOnPopupClosed)
		{
			return;
		}

		_requestCloseOnPopupClosed = false;
		CloseRequested?.Invoke(this, EventArgs.Empty);
	}

	private void DetachPaneKeyHandler()
	{
		_pane?.RemoveHandler(KeyDownEvent, OnPaneKeyDown);
		_pane = null;
		_source = null;
	}

	private void OnSendSelectionClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Control { DataContext: SessionViewModel session })
		{
			SendSelectionRequested?.Invoke(this, session);
		}
	}

	private void OnSelectionActionNotesClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Control { DataContext: ProjectNotesTargetViewModel target })
		{
			SendSelectionToNotesRequested?.Invoke(this, target);
		}
	}
}
