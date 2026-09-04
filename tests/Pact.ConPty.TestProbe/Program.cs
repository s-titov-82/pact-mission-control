using System.Runtime.InteropServices;
using System.Text;

var delay = GetArgument(args, "--delay");
var delayMilliseconds = int.TryParse(delay, out var parsedDelay)
		? parsedDelay
		: 0;
var resultPath = GetArgument(args, "--result")
	?? throw new ArgumentException("A --result path is required.", nameof(args));
var inputReadyPath = GetArgument(args, "--input-ready");

if (inputReadyPath is not null)
{
	File.WriteAllText(inputReadyPath, "READY", Encoding.UTF8);
	var input = ReadConsoleLine();
	var output = $"PACT_CONPTY_ECHO:{input}";
	var inputModeOutputHandle = OpenConsoleHandle("CONOUT$");
	try
	{
		if (!NativeMethods.WriteConsole(
				inputModeOutputHandle,
				output,
				checked((uint)output.Length),
				out var charactersWritten,
				IntPtr.Zero)
			|| charactersWritten != checked((uint)output.Length))
		{
			throw new InvalidOperationException(
				$"WriteConsoleW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
		}
	}
	finally
	{
		NativeMethods.CloseHandle(inputModeOutputHandle);
	}

	File.WriteAllText(resultPath, input, Encoding.UTF8);
	return;
}

Thread.Sleep(delayMilliseconds);
var outputHandle = OpenConsoleHandle("CONOUT$");
var originAccepted = NativeMethods.SetConsoleCursorPosition(
	outputHandle,
	new ConsoleCoordinate(0, 0));
var boundaryAccepted = NativeMethods.SetConsoleCursorPosition(
	outputHandle,
	new ConsoleCoordinate(100, 36));
var outsideWidthAccepted = NativeMethods.SetConsoleCursorPosition(
	outputHandle,
	new ConsoleCoordinate(101, 36));
var outsideHeightAccepted = NativeMethods.SetConsoleCursorPosition(
	outputHandle,
	new ConsoleCoordinate(100, 37));
NativeMethods.CloseHandle(outputHandle);
File.WriteAllText(
	resultPath,
	$"PACT_ORIGIN={Convert.ToInt32(originAccepted)};"
	+ $"BOUNDARY={Convert.ToInt32(boundaryAccepted)};"
	+ $"OUTSIDE_WIDTH={Convert.ToInt32(outsideWidthAccepted)};"
	+ $"OUTSIDE_HEIGHT={Convert.ToInt32(outsideHeightAccepted)}",
	Encoding.UTF8);

static string? GetArgument(string[] arguments, string name)
{
	for (var index = 0; index < arguments.Length - 1; index++)
	{
		if (string.Equals(arguments[index], name, StringComparison.Ordinal))
		{
			return arguments[index + 1];
		}
	}

	return null;
}

static unsafe string ReadConsoleLine()
{
	var inputHandle = OpenConsoleHandle("CONIN$");
	var buffer = new char[128];
	try
	{
		fixed (char* bufferPointer = buffer)
		{
			if (!NativeMethods.ReadConsole(
					inputHandle,
					bufferPointer,
					checked((uint)buffer.Length),
					out var charactersRead,
					IntPtr.Zero))
			{
				throw new InvalidOperationException(
					$"ReadConsoleW failed with Win32 error {Marshal.GetLastPInvokeError()}.");
			}

			return new string(buffer, 0, checked((int)charactersRead)).TrimEnd('\r', '\n');
		}
	}
	finally
	{
		NativeMethods.CloseHandle(inputHandle);
	}
}

static IntPtr OpenConsoleHandle(string name)
{
	var handle = NativeMethods.CreateFile(
		name,
		NativeMethods.GenericRead | NativeMethods.GenericWrite,
		NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
		IntPtr.Zero,
		NativeMethods.OpenExisting,
		0,
		IntPtr.Zero);
	if (handle == IntPtr.Zero || handle == new IntPtr(-1))
	{
		throw new InvalidOperationException(
			$"Opening {name} failed with Win32 error {Marshal.GetLastPInvokeError()}.");
	}

	return handle;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct ConsoleCoordinate(short x, short y)
{
	internal readonly short X = x;
	internal readonly short Y = y;
}

internal static partial class NativeMethods
{
	internal const uint GenericRead = 0x80000000;
	internal const uint GenericWrite = 0x40000000;
	internal const uint FileShareRead = 0x00000001;
	internal const uint FileShareWrite = 0x00000002;
	internal const uint OpenExisting = 3;

	[LibraryImport(
		"kernel32.dll",
		EntryPoint = "CreateFileW",
		SetLastError = true,
		StringMarshalling = StringMarshalling.Utf16)]
	internal static partial IntPtr CreateFile(
		string fileName,
		uint desiredAccess,
		uint shareMode,
		IntPtr securityAttributes,
		uint creationDisposition,
		uint flagsAndAttributes,
		IntPtr templateFile);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool CloseHandle(IntPtr handle);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool SetConsoleCursorPosition(
		IntPtr consoleOutput,
		ConsoleCoordinate cursorPosition);

	[LibraryImport(
		"kernel32.dll",
		EntryPoint = "WriteConsoleW",
		SetLastError = true,
		StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static partial bool WriteConsole(
		IntPtr consoleOutput,
		string buffer,
		uint charactersToWrite,
		out uint charactersWritten,
		IntPtr reserved);

	[LibraryImport("kernel32.dll", EntryPoint = "ReadConsoleW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	internal static unsafe partial bool ReadConsole(
		IntPtr consoleInput,
		char* buffer,
		uint charactersToRead,
		out uint charactersRead,
		IntPtr inputControl);
}