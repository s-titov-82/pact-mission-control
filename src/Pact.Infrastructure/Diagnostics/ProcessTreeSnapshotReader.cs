using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Pact.Core.Platform;

namespace Pact.Infrastructure.Diagnostics;

/// <summary>Reads process-tree resource counters from the Windows process snapshot.</summary>
public sealed class ProcessTreeSnapshotReader : IProcessTreeSnapshotReader
{
	private readonly IProcessSnapshotSource _source;
	private readonly TimeProvider _timeProvider;

	/// <summary>Creates a reader backed by the Windows Tool Help process snapshot.</summary>
	public ProcessTreeSnapshotReader()
		: this(new WindowsProcessSnapshotSource(), TimeProvider.System)
	{
	}

	internal ProcessTreeSnapshotReader(
		IProcessSnapshotSource source,
		TimeProvider timeProvider)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	}

	/// <inheritdoc />
	public ProcessTreeSnapshot Read(int rootProcessId)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rootProcessId);
		var entries = _source.ReadAll();
		if (!entries.Any(entry => entry.ProcessId == rootProcessId))
		{
			throw new InvalidOperationException(
				$"Process {rootProcessId} is no longer present.");
		}

		var childrenByParent = entries
			.GroupBy(entry => entry.ParentProcessId)
			.ToDictionary(group => group.Key, group => group.ToArray());
		var entriesById = entries.ToDictionary(entry => entry.ProcessId);
		HashSet<int> processIds = [];
		Queue<int> pending = new([rootProcessId]);
		while (pending.TryDequeue(out var processId))
		{
			if (!processIds.Add(processId)
				|| !childrenByParent.TryGetValue(processId, out var children))
			{
				continue;
			}

			foreach (var child in children)
			{
				pending.Enqueue(child.ProcessId);
			}
		}

		var treeEntries = processIds
			.Select(processId => _source.ReadMetrics(entriesById[processId]))
			.ToArray();
		return new ProcessTreeSnapshot(
			rootProcessId,
			treeEntries.Length,
			treeEntries.Sum(entry => entry.WorkingSetBytes ?? 0),
			treeEntries
				.Where(entry => entry.TotalProcessorTime is not null)
				.ToDictionary(
					entry => entry.ProcessId,
					entry => entry.TotalProcessorTime!.Value),
			_timeProvider.GetUtcNow());
	}
}

internal sealed record ProcessSnapshotEntry(
	int ProcessId,
	int ParentProcessId,
	long? WorkingSetBytes,
	TimeSpan? TotalProcessorTime);

internal interface IProcessSnapshotSource
{
	IReadOnlyList<ProcessSnapshotEntry> ReadAll();

	ProcessSnapshotEntry ReadMetrics(ProcessSnapshotEntry entry);
}

internal sealed partial class WindowsProcessSnapshotSource : IProcessSnapshotSource
{
	private const uint SnapshotProcesses = 0x00000002;

	public IReadOnlyList<ProcessSnapshotEntry> ReadAll()
	{
		if (!OperatingSystem.IsWindows())
		{
			throw new PlatformNotSupportedException(
				"Process-tree metrics are available only on Windows.");
		}

		using var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
		if (snapshot.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error());
		}

		List<ProcessSnapshotEntry> entries = [];
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
			entries.Add(new(
				checked((int)entry.ProcessId),
				checked((int)entry.ParentProcessId),
				null,
				null));
			entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
		}
		while (Process32Next(snapshot, ref entry));

		return entries;
	}

	public ProcessSnapshotEntry ReadMetrics(ProcessSnapshotEntry entry)
	{
		try
		{
			using var process = Process.GetProcessById(entry.ProcessId);
			return new(
				process.Id,
				entry.ParentProcessId,
				process.WorkingSet64,
				process.TotalProcessorTime);
		}
		catch (Exception exception) when (exception is ArgumentException
			or InvalidOperationException
			or NotSupportedException
			or Win32Exception)
		{
			return new(
				entry.ProcessId,
				entry.ParentProcessId,
				null,
				null);
		}
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
