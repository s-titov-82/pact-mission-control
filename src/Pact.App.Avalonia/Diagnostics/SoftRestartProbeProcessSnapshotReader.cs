using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pact.App.Avalonia.Diagnostics;

internal sealed record SoftRestartProbeProcessEntry(
	int ProcessId,
	int ParentProcessId,
	string ImageName);

internal sealed partial class SoftRestartProbeProcessSnapshotReader
{
	private const uint SnapshotProcesses = 0x00000002;
	private readonly Func<IReadOnlyList<SoftRestartProbeProcessEntry>> _readAll;

	public SoftRestartProbeProcessSnapshotReader()
		: this(ReadWindowsSnapshot)
	{
	}

	internal SoftRestartProbeProcessSnapshotReader(
		Func<IReadOnlyList<SoftRestartProbeProcessEntry>> readAll)
	{
		_readAll = readAll ?? throw new ArgumentNullException(nameof(readAll));
	}

	public int[] ReadOpenConsoleChildren(int parentProcessId)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(parentProcessId);
		return _readAll()
			.Where(entry => entry.ParentProcessId == parentProcessId
				&& string.Equals(
					entry.ImageName,
					"OpenConsole.exe",
					StringComparison.OrdinalIgnoreCase))
			.Select(entry => entry.ProcessId)
			.Where(processId => processId > 0)
			.Distinct()
			.Order()
			.ToArray();
	}

	private static unsafe IReadOnlyList<SoftRestartProbeProcessEntry> ReadWindowsSnapshot()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"The soft-restart process probe requires Windows.");
		}

		using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
		if (snapshot.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}

		List<SoftRestartProbeProcessEntry> entries = [];
		ProcessEntry32 entry = new()
		{
			Size = (uint)Marshal.SizeOf<ProcessEntry32>()
		};
		if (!Process32First(snapshot, ref entry))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}

		do
		{
			entries.Add(new SoftRestartProbeProcessEntry(
				checked((int)entry.ProcessId),
				checked((int)entry.ParentProcessId),
				new string(entry.ExecutableFile).TrimEnd('\0')));
			entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
		}
		while (Process32Next(snapshot, ref entry));
		return entries;
	}

	[LibraryImport("kernel32.dll", SetLastError = true)]
	private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

	[LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32First(
		SafeFileHandle snapshot,
		ref ProcessEntry32 entry);

	[LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool Process32Next(
		SafeFileHandle snapshot,
		ref ProcessEntry32 entry);

	[StructLayout(LayoutKind.Sequential)]
	private unsafe struct ProcessEntry32
	{
		internal uint Size;
		internal uint Usage;
		internal uint ProcessId;
		internal nint DefaultHeapId;
		internal uint ModuleId;
		internal uint Threads;
		internal uint ParentProcessId;
		internal int BasePriority;
		internal uint Flags;
		internal fixed char ExecutableFile[260];
	}
}
