using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Pact.App.Avalonia.Platform;

/// <summary>WebView2 is a separate HWND, so terminal focus flips Avalonia's
/// Avalonia window activation state to false. The app counts as active whenever the
/// foreground window's root HWND is our window, which covers the WebView2 child.</summary>
internal static partial class WindowForegroundProbe
{
	private const uint GaRoot = 2;

	/// <summary>Returns whether the window or one of its child HWNDs owns the foreground,
	/// falling back to Avalonia activation when no Win32 HWND is available.</summary>
	public static bool IsWindowForeground(Window window)
	{
		ArgumentNullException.ThrowIfNull(window);
		if (!OperatingSystem.IsWindows())
		{
			return window.IsActive;
		}

		return EvaluateWindowsForeground(
			window.TryGetPlatformHandle(),
			window.IsActive,
			GetForegroundWindow,
			GetAncestor);
	}

	internal static bool EvaluateWindowsForeground(
		IPlatformHandle? platform,
		bool isActive,
		Func<IntPtr> getForegroundWindow,
		Func<IntPtr, uint, IntPtr> getAncestor)
	{
		ArgumentNullException.ThrowIfNull(getForegroundWindow);
		ArgumentNullException.ThrowIfNull(getAncestor);
		if (platform is null
			|| platform.Handle == IntPtr.Zero
			|| !string.Equals(platform.HandleDescriptor, "HWND", StringComparison.Ordinal))
		{
			return isActive;
		}

		var foreground = getForegroundWindow();
		if (foreground == IntPtr.Zero)
		{
			return false;
		}

		return foreground == platform.Handle
			|| getAncestor(foreground, GaRoot) == platform.Handle;
	}

	[LibraryImport("user32.dll")]
	private static partial IntPtr GetForegroundWindow();

	[LibraryImport("user32.dll")]
	private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
