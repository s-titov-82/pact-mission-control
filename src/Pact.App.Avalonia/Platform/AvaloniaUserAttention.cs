using System.Runtime.InteropServices;
using Avalonia.Controls;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Platform;

/// <summary>Flashes the window's taskbar button via FlashWindowEx to signal that
/// a background session finished. Never activates the window — stealing focus
/// from whatever the user is doing would defeat the purpose of the hint.</summary>
internal sealed partial class AvaloniaUserAttention(Window window) : IUserAttention
{
	/// <summary>Flashes the taskbar button until the window becomes foreground.</summary>
	public void RequestAttention() =>
		SetAttention(FlashWindowFlags.Tray | FlashWindowFlags.TimerNoForeground);

	/// <summary>Stops an active taskbar flash.</summary>
	public void ClearAttention() => SetAttention(FlashWindowFlags.Stop);

	private void SetAttention(FlashWindowFlags flags)
	{
		var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
		if (handle == IntPtr.Zero || !OperatingSystem.IsWindows())
		{
			return;
		}

		FlashWindowInfo info = new()
		{
			Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
			Handle = handle,
			Flags = flags
		};
		_ = FlashWindowEx(ref info);
	}

	[LibraryImport("user32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool FlashWindowEx(ref FlashWindowInfo info);

	[StructLayout(LayoutKind.Sequential)]
	private struct FlashWindowInfo
	{
		public uint Size;
		public IntPtr Handle;
		public FlashWindowFlags Flags;
		public uint Count;
		public uint Timeout;
	}

	[Flags]
	private enum FlashWindowFlags : uint
	{
		Stop = 0,
		Tray = 0x00000002,
		TimerNoForeground = 0x0000000C
	}
}