using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace Pact.App.Avalonia.Platform;

/// <summary>
/// Destroys the child window Avalonia creates to host a WebView2 controller. Avalonia disposes
/// the controller when the view is removed but leaves that window to a finalizer, which calls
/// <c>DestroyWindow</c> from the finalizer thread on a window the UI thread owns. That call can
/// hold the finalizer thread for minutes while terminal scripts and tab switches stall.
/// </summary>
internal static partial class NativeWebViewHolderWindow
{
	private const string HolderClassPrefix = "AvaloniaDumbWindow-";

	/// <summary>
	/// Returns the holder window of a WebView2-backed view, or null when the view has no native
	/// controller yet.
	/// </summary>
	public static nint? TryCapture(NativeWebView webView)
	{
		ArgumentNullException.ThrowIfNull(webView);
		return webView.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle { Handle: not 0 } handle
			? handle.Handle
			: null;
	}

	/// <summary>
	/// Destroys <paramref name="window"/> when it is still a live holder window owned by the
	/// calling thread; anything else is left alone.
	/// </summary>
	public static unsafe void DestroyIfOwned(nint window)
	{
		if (window == 0
			|| !IsWindow(window)
			|| GetWindowThreadProcessId(window, out _) != GetCurrentThreadId())
		{
			return;
		}

		const int capacity = 64;
		var className = stackalloc char[capacity];
		var length = GetClassName(window, className, capacity);
		if (length <= 0
			|| !new ReadOnlySpan<char>(className, length).StartsWith(HolderClassPrefix, StringComparison.Ordinal))
		{
			return;
		}

		_ = DestroyWindow(window);
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool IsWindow(nint window);

	[LibraryImport("user32.dll")]
	private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

	[LibraryImport("kernel32.dll")]
	private static partial uint GetCurrentThreadId();

	[LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
	private static unsafe partial int GetClassName(nint window, char* className, int maxCount);

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DestroyWindow(nint window);
}
