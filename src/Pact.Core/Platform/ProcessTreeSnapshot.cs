namespace Pact.Core.Platform;

/// <summary>Point-in-time resource counters for one process tree.</summary>
/// <param name="RootProcessId">Process id used as the tree root.</param>
/// <param name="ProcessCount">Number of processes found in the tree.</param>
/// <param name="WorkingSetBytes">Combined readable working sets, in bytes.</param>
/// <param name="ProcessorTimes">Cumulative processor time for each readable process.</param>
/// <param name="SampledAt">UTC time at which the operating-system snapshot was read.</param>
public sealed record ProcessTreeSnapshot(
	int RootProcessId,
	int ProcessCount,
	long WorkingSetBytes,
	IReadOnlyDictionary<int, TimeSpan> ProcessorTimes,
	DateTimeOffset SampledAt);

/// <summary>Reads current resource counters for a process and all of its descendants.</summary>
public interface IProcessTreeSnapshotReader
{
	/// <summary>Reads one point-in-time snapshot rooted at <paramref name="rootProcessId"/>.</summary>
	/// <exception cref="InvalidOperationException">The root process is no longer present.</exception>
	ProcessTreeSnapshot Read(int rootProcessId);
}
