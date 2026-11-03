using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Pact.Infrastructure.Platform;

// Keeps HICON, GDI+ and COM ownership inside one short-lived Windows boundary.
// No managed imaging package, disk staging or shell execution is involved.
internal static partial class WindowsExecutableIcon
{
	internal static byte[]? ReadPng(string executable) => OperatingSystem.IsWindows() ? ReadWindowsPng(executable) : null;

	[SupportedOSPlatform("windows")]
	private static unsafe byte[]? ReadWindowsPng(string executable)
	{
		nint large = 0, small = 0, token = 0, image = 0, stream = 0;
		try
		{
			if (ExtractIconExW(executable, 0, out large, out small, 1) == 0 || large == 0)
			{
				return null;
			}
			var input = new StartupInput { Version = 1 };
			if (GdiplusStartup(out token, ref input, 0) != 0
				|| GdipCreateBitmapFromHICON(large, out image) != 0
				|| CreateStreamOnHGlobal(0, 1, out stream) < 0)
			{
				return null;
			}
			var pngEncoder = new Guid("557cf406-1a04-11d3-9a73-0000f81ef32e");
			if (GdipSaveImageToStream(image, stream, ref pngEncoder, 0) != 0)
			{
				return null;
			}
			// IStream::Seek supplies the actual stream length, not HGLOBAL's rounded allocation size.
			var vtable = *(nint**)stream;
			var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)vtable[5];
			ulong length = 0;
			if (seek(stream, 0, 2, &length) < 0 || length is 0 or > 1024 * 1024
				|| GetHGlobalFromStream(stream, out var buffer) < 0)
			{
				return null;
			}
			var data = GlobalLock(buffer);
			if (data == 0)
			{
				return null;
			}
			try
			{
				var png = new byte[(int)length];
				Marshal.Copy(data, png, 0, png.Length);
				return png;
			}
			finally
			{
				_ = GlobalUnlock(buffer);
			}
		}
		finally
		{
			if (stream != 0)
			{
				_ = Marshal.Release(stream); // Also frees the stream-owned HGLOBAL.
			}
			if (image != 0)
			{
				_ = GdipDisposeImage(image);
			}
			if (token != 0)
			{
				GdiplusShutdown(token);
			}
			if (large != 0)
			{
				_ = DestroyIcon(large);
			}
			if (small != 0)
			{
				_ = DestroyIcon(small);
			}
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct StartupInput
	{
		internal uint Version;
		internal nint DebugCallback;
		internal int SuppressBackgroundThread;
		internal int SuppressExternalCodecs;
	}

	[LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial uint ExtractIconExW(string file, int index, out nint large, out nint small, uint count);

	[LibraryImport("user32.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int DestroyIcon(nint icon);

	[LibraryImport("gdiplus.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GdiplusStartup(out nint token, ref StartupInput input, nint output);

	[LibraryImport("gdiplus.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial void GdiplusShutdown(nint token);

	[LibraryImport("gdiplus.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GdipCreateBitmapFromHICON(nint icon, out nint bitmap);

	[LibraryImport("gdiplus.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GdipSaveImageToStream(nint image, nint stream, ref Guid encoder, nint parameters);

	[LibraryImport("gdiplus.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GdipDisposeImage(nint image);

	[LibraryImport("ole32.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int CreateStreamOnHGlobal(nint buffer, int deleteOnRelease, out nint stream);

	[LibraryImport("ole32.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GetHGlobalFromStream(nint stream, out nint buffer);

	[LibraryImport("kernel32.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial nint GlobalLock(nint memory);

	[LibraryImport("kernel32.dll")]
	[DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
	private static partial int GlobalUnlock(nint memory);
}
