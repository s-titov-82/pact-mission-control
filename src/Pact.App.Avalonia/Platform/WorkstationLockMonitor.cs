using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Pact.App.Avalonia.Platform;

internal sealed partial class WorkstationLockMonitor : IDisposable
{
	private const int WindowProcedureIndex = -4;
	private const uint SessionChangeMessage = 0x02B1;
	private const nuint SessionLock = 0x7;
	private const nuint SessionUnlock = 0x8;
	private const uint NotifyForThisSession = 0;
	private readonly IntPtr _windowHandle;
	private readonly WindowProcedure _windowProcedure;
	private readonly IntPtr _windowProcedurePointer;
	private readonly IntPtr _previousWindowProcedure;
	private bool _disposed;

	public WorkstationLockMonitor(IntPtr windowHandle)
	{
		if (windowHandle == IntPtr.Zero)
		{
			throw new ArgumentException("A native window handle is required.", nameof(windowHandle));
		}

		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException();
		}

		_windowHandle = windowHandle;
		_windowProcedure = HandleWindowMessage;
		_windowProcedurePointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
		_previousWindowProcedure = SetWindowLongPtr(
			_windowHandle,
			WindowProcedureIndex,
			_windowProcedurePointer);
		if (_previousWindowProcedure == IntPtr.Zero)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}

		if (!WtsRegisterSessionNotification(_windowHandle, NotifyForThisSession))
		{
			_ = SetWindowLongPtr(
				_windowHandle,
				WindowProcedureIndex,
				_previousWindowProcedure);
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}
	}

	public event EventHandler<bool>? LockStateChanged;

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_ = WtsUnregisterSessionNotification(_windowHandle);
		if (GetWindowLongPtr(_windowHandle, WindowProcedureIndex) == _windowProcedurePointer)
		{
			_ = SetWindowLongPtr(
				_windowHandle,
				WindowProcedureIndex,
				_previousWindowProcedure);
		}

		GC.KeepAlive(_windowProcedure);
	}

	private IntPtr HandleWindowMessage(
		IntPtr windowHandle,
		uint message,
		nuint wParam,
		nint lParam)
	{
		if (!_disposed && message == SessionChangeMessage
			&& wParam is SessionLock or SessionUnlock)
		{
			var locked = wParam == SessionLock;
			Dispatcher.UIThread.Post(
				() => LockStateChanged?.Invoke(this, locked));
		}

		return CallWindowProc(
			_previousWindowProcedure,
			windowHandle,
			message,
			wParam,
			lParam);
	}

	[UnmanagedFunctionPointer(CallingConvention.Winapi)]
	private delegate IntPtr WindowProcedure(
		IntPtr windowHandle,
		uint message,
		nuint wParam,
		nint lParam);

	[LibraryImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool WtsRegisterSessionNotification(
		IntPtr windowHandle,
		uint flags);

	[LibraryImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool WtsUnregisterSessionNotification(IntPtr windowHandle);

	[LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
	private static partial IntPtr SetWindowLongPtr(
		IntPtr windowHandle,
		int index,
		IntPtr newValue);

	[LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
	private static partial IntPtr GetWindowLongPtr(IntPtr windowHandle, int index);

	[LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
	private static partial IntPtr CallWindowProc(
		IntPtr previousWindowProcedure,
		IntPtr windowHandle,
		uint message,
		nuint wParam,
		nint lParam);
}
