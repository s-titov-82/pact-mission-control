using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Controllers;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	private const string MaximizedWindowState = "maximized";
	private const string NormalWindowState = "normal";
	private PixelPoint? _lastNormalPosition;
	private double _lastNormalWidth = double.NaN;
	private double _lastNormalHeight = double.NaN;
	private bool _pausedProjectsHintShown;
	private bool _rightPanelPinned = true;
	private bool _rightPanelOwnerDisabled;
	private double _expandedRightColumnWidth;

	private void ApplyWindowLayout(AppWindowLayout? layout)
	{
		if (layout is not null)
		{
			_pausedProjectsHintShown = layout.PausedProjectsHintShown;
			PixelPoint position = new((int)layout.Left, (int)layout.Top);
			if (IsWithinAnyScreen(position))
			{
				Position = position;
				_lastNormalPosition = position;
			}
			Width = layout.Width;
			Height = layout.Height;
			RootGrid.ColumnDefinitions[0].Width = new GridLength(layout.LeftColumnWidth);
			RootGrid.ColumnDefinitions[4].Width = new GridLength(layout.RightColumnWidth);
			_lastNormalWidth = layout.Width;
			_lastNormalHeight = layout.Height;
			if (string.Equals(layout.WindowState, MaximizedWindowState, StringComparison.OrdinalIgnoreCase))
			{
				WindowState = WindowState.Maximized;
			}
		}

		SetRightPanelPinned(layout?.RightPanelPinned ?? true);
		RightActions.TogglePinRequested += OnToggleRightPanelPin;
		CenterPane.AddHandler(PointerPressedEvent, OnCenterPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
		CenterPane.SizeChanged += OnCenterSizeChanged;
		TerminalPane.WebViewControl.Host.SelectionDismissed += OnNativeTerminalInput;
		BrowserPane.ConfigureNativeInteraction(OnNativeBrowserInput);
		Deactivated += OnRightPanelOwnerDeactivated;
		AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Bubble);
		RightPanelBody.KeyDown += OnPanelKeyDown;
		var hasNativeOwner = TryGetPlatformHandle() is { HandleDescriptor: "HWND" };
		if (hasNativeOwner)
		{
			Win32Properties.AddWndProcHookCallback(this, OnRightPanelOwnerMessage);
		}
		_eventDetachments.Add(() =>
		{
			RightPanelPopup.IsOpen = false;
			RightActions.TogglePinRequested -= OnToggleRightPanelPin;
			CenterPane.RemoveHandler(PointerPressedEvent, OnCenterPointerPressed);
			CenterPane.SizeChanged -= OnCenterSizeChanged;
			TerminalPane.WebViewControl.Host.SelectionDismissed -= OnNativeTerminalInput;
			BrowserPane.ConfigureNativeInteraction(null);
			Deactivated -= OnRightPanelOwnerDeactivated;
			RemoveHandler(KeyDownEvent, OnPanelKeyDown);
			RightPanelBody.KeyDown -= OnPanelKeyDown;
			if (hasNativeOwner)
			{
				Win32Properties.RemoveWndProcHookCallback(this, OnRightPanelOwnerMessage);
			}
		});

		// Maximizing rewrites Position/Width/Height with the maximized geometry,
		// so the last normal-state geometry has to be tracked separately.
		void OnPositionChanged(object? sender, PixelPointEventArgs args)
		{
			if (WindowState == WindowState.Normal)
			{
				_lastNormalPosition = args.Point;
			}
		}

		void OnSizeChanged(object? sender, SizeChangedEventArgs args)
		{
			if (WindowState != WindowState.Normal)
			{
				return;
			}

			_lastNormalWidth = args.NewSize.Width;
			_lastNormalHeight = args.NewSize.Height;
		}

		PositionChanged += OnPositionChanged;
		SizeChanged += OnSizeChanged;
		_eventDetachments.Add(() => PositionChanged -= OnPositionChanged);
		_eventDetachments.Add(() => SizeChanged -= OnSizeChanged);
	}

	internal AppWindowLayout CaptureWindowLayout()
	{
		var maximized = WindowState == WindowState.Maximized;
		var position = maximized ? _lastNormalPosition ?? Position : Position;
		var width = maximized && !double.IsNaN(_lastNormalWidth) ? _lastNormalWidth : Width;
		var height = maximized && !double.IsNaN(_lastNormalHeight) ? _lastNormalHeight : Height;
		return new AppWindowLayout(
			Left: position.X,
			Top: position.Y,
			Width: double.IsNaN(width) ? Bounds.Width : width,
			Height: double.IsNaN(height) ? Bounds.Height : height,
			WindowState: maximized ? MaximizedWindowState : NormalWindowState,
			LeftColumnWidth: CaptureColumnWidth(RootGrid.ColumnDefinitions[0]),
			RightColumnWidth: _rightPanelPinned ? CaptureColumnWidth(RootGrid.ColumnDefinitions[4]) : _expandedRightColumnWidth)
		{ PausedProjectsHintShown = _pausedProjectsHintShown, RightPanelPinned = _rightPanelPinned };
	}

	private void OnToggleRightPanelPin(object? sender, EventArgs args) => SetRightPanelPinned(!_rightPanelPinned);

	private void SetRightPanelPinned(bool pinned)
	{
		var column = RootGrid.ColumnDefinitions[4];
		if (_rightPanelPinned)
		{
			_expandedRightColumnWidth = Math.Max(100, CaptureColumnWidth(column));
		}
		RightPanelPopup.IsOpen = false;
		if (pinned)
		{
			RightPanelPopup.Child = null;
			RightPanelDock.Child = RightPanelBody;
		}
		else
		{
			RightPanelDock.Child = null;
			RightPanelPopup.Child = RightPanelBody;
		}
		_rightPanelPinned = pinned;
		column.MinWidth = pinned ? 100 : 0;
		column.Width = new GridLength(pinned ? _expandedRightColumnWidth : 24);
		RootGrid.ColumnDefinitions[3].Width = new GridLength(pinned ? 5 : 0);
		RightPanelDock.IsVisible = RightColumnSplitter.IsVisible = pinned;
		RightPanelRail.IsVisible = !pinned;
		RightActions.SetPanelPinned(pinned);
	}

	private void OnRightPanelRailEntered(object? sender, PointerEventArgs args) => OpenRightPanel();

	private void OnRightPanelRailClicked(object? sender, RoutedEventArgs args)
	{
		OpenRightPanel();
		Dispatcher.UIThread.Post(() =>
		{
			if (RightPanelPopup.IsOpen)
			{
				RightActions.RevealAttentionFeed();
			}
		}, DispatcherPriority.Loaded);
	}

	private void OpenRightPanel()
	{
		if (!_rightPanelPinned && !_rightPanelOwnerDisabled && !BusyOverlay.IsVisible)
		{
			SizeRightPanelPopup();
			RightPanelPopup.IsOpen = true;
		}
	}

	private void OnCenterSizeChanged(object? sender, SizeChangedEventArgs args)
	{
		if (RightPanelPopup.IsOpen)
		{
			SizeRightPanelPopup();
		}
	}

	private void SizeRightPanelPopup()
	{
		RightPanelPopup.Width = Math.Min(_expandedRightColumnWidth, RootGrid.Bounds.Width - RightPanelRail.Bounds.Width);
		RightPanelPopup.Height = RootGrid.Bounds.Height;
	}

	private void OnCenterPointerPressed(object? sender, PointerPressedEventArgs args) => RightPanelPopup.IsOpen = false;

	private void OnRightPanelOwnerDeactivated(object? sender, EventArgs args) => RightPanelPopup.IsOpen = false;

	private nint OnRightPanelOwnerMessage(nint window, uint message, nint enabled, nint lParam, ref bool handled)
	{
		const uint enableMessage = 0x000A; // WM_ENABLE
		if (message == enableMessage)
		{
			_rightPanelOwnerDisabled = enabled == 0;
			if (_rightPanelOwnerDisabled)
			{
				RightPanelPopup.IsOpen = false;
			}
		}
		return 0;
	}

	// Native WebView2 receives pointer input outside the Avalonia routed-event tree.
	private void OnNativeTerminalInput(object? sender, string sessionId)
	{
		if (EngineProbeController.PresentedSurface is PresentedSurface.Terminal terminal
			&& terminal.Session.Record.Id == sessionId)
		{
			RightPanelPopup.IsOpen = false;
		}
	}

	private void OnNativeBrowserInput(string pageId)
	{
		if (EngineProbeController.PresentedSurface is PresentedSurface.Web
			&& EngineProbeController.ViewModel.SelectedWebPage?.Record.Id == pageId)
		{
			RightPanelPopup.IsOpen = false;
		}
	}

	private void OnPanelKeyDown(object? sender, KeyEventArgs args)
	{
		if (!args.Handled && args.Key == Key.Escape && RightPanelPopup.IsOpen)
		{
			RightPanelPopup.IsOpen = false;
			RightPanelRail.Focus();
			args.Handled = true;
		}
	}

	private static double CaptureColumnWidth(ColumnDefinition column) =>
		column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;

	/// <summary>Rejects positions on monitors that are no longer attached, so a
	/// stored layout can't open the window outside the visible desktop.</summary>
	private bool IsWithinAnyScreen(PixelPoint position)
	{
		// Probe the title-bar grab area rather than the single top-left pixel.
		PixelRect probe = new(position, new PixelSize(160, 40));
		return Screens.All.Any(screen => screen.Bounds.Intersects(probe));
	}

	private Task SaveWindowLayoutAsync()
	{
		if (_windowLayoutStore is null)
		{
			return Task.CompletedTask;
		}

		return _windowLayoutStore.SaveAsync(CaptureWindowLayout(), CancellationToken.None);
	}
}
