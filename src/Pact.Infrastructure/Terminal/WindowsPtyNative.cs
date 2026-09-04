using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pact.Infrastructure.Terminal;

internal static partial class WindowsPtyNative
{
	internal const uint CreateUnicodeEnvironment = 0x00000400;
	internal const uint ExtendedStartupInfoPresent = 0x00080000;
	internal const uint ProcThreadAttributePseudoConsole = 0x00020016;
	internal const uint StillActive = 259;
	internal const uint WaitObject0 = 0;

	[StructLayout(LayoutKind.Sequential)]
	internal readonly struct Coord
	{
		internal Coord(int columns, int rows)
		{
			X = checked((short)columns);
			Y = checked((short)rows);
		}

		internal short X { get; }

		internal short Y { get; }
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct StartupInfo
	{
		internal uint cb;
		internal IntPtr lpReserved;
		internal IntPtr lpDesktop;
		internal IntPtr lpTitle;
		internal uint dwX;
		internal uint dwY;
		internal uint dwXSize;
		internal uint dwYSize;
		internal uint dwXCountChars;
		internal uint dwYCountChars;
		internal uint dwFillAttribute;
		internal uint dwFlags;
		internal ushort wShowWindow;
		internal ushort cbReserved2;
		internal IntPtr lpReserved2;
		internal IntPtr hStdInput;
		internal IntPtr hStdOutput;
		internal IntPtr hStdError;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct StartupInfoEx
	{
		internal StartupInfo StartupInfo;
		internal IntPtr lpAttributeList;
	}

	[StructLayout(LayoutKind.Sequential)]
	internal struct ProcessInformation
	{
		internal IntPtr hProcess;
		internal IntPtr hThread;
		internal uint dwProcessId;
		internal uint dwThreadId;
	}

	[LibraryImport(ConptyLibrary.LibraryName, EntryPoint = "ConptyCreatePseudoConsole")]
	internal static partial int CreatePseudoConsole(
		Coord size,
		SafeFileHandle hInput,
		SafeFileHandle hOutput,
		uint dwFlags,
		out IntPtr phPC);

	[LibraryImport(ConptyLibrary.LibraryName, EntryPoint = "ConptyResizePseudoConsole")]
	internal static partial int ResizePseudoConsole(IntPtr hPC, Coord size);

	[LibraryImport(ConptyLibrary.LibraryName, EntryPoint = "ConptyClosePseudoConsole")]
	internal static partial void ClosePseudoConsole(IntPtr hPC);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CreatePipe(
		out SafeFileHandle hReadPipe,
		out SafeFileHandle hWritePipe,
		IntPtr lpPipeAttributes,
		uint nSize);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool InitializeProcThreadAttributeList(
		IntPtr lpAttributeList,
		uint dwAttributeCount,
		uint dwFlags,
		ref nuint lpSize);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool UpdateProcThreadAttribute(
		IntPtr lpAttributeList,
		uint dwFlags,
		nuint attribute,
		IntPtr lpValue,
		nuint cbSize,
		IntPtr lpPreviousValue,
		IntPtr lpReturnSize);

	[LibraryImport("kernel32.dll")]
	internal static partial void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool CreateProcess(
		char* lpApplicationName,
		char* lpCommandLine,
		IntPtr lpProcessAttributes,
		IntPtr lpThreadAttributes,
		[MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
		uint dwCreationFlags,
		IntPtr lpEnvironment,
		char* lpCurrentDirectory,
		ref StartupInfoEx lpStartupInfo,
		out ProcessInformation lpProcessInformation);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CloseHandle(IntPtr hObject);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool TerminateProcess(IntPtr hProcess, uint uExitCode);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}