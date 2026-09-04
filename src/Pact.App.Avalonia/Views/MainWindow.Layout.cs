using Avalonia;
using Avalonia.Controls;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow
{
	private const string MaximizedWindowState = "maximized";
	private const string NormalWindowState = "normal";
	private PixelPoint? _lastNormalPosition;
	private double _lastNormalWidth = double.NaN;
	private double _lastNormalHeight = double.NaN;

	private void ApplyWindowLayout(AppWindowLayout? layout)
	{
		if (layout is not null)
		{
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
			RightColumnWidth: CaptureColumnWidth(RootGrid.ColumnDefinitions[4]));
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
