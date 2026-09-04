namespace Pact.Core.Platform;

/// <summary>Point-in-time resource counters for an explicitly attributed set of processes.</summary>
/// <param name="ProcessCount">Number of distinct process ids in the requested set.</param>
/// <param name="WorkingSetBytes">Combined readable working sets, in bytes.</param>
/// <param name="ProcessorTimes">Cumulative processor time for each readable process.</param>
/// <param name="SampledAt">UTC time at which the process counters were read.</param>
public sealed record ProcessSetSnapshot(
	int ProcessCount,
	long WorkingSetBytes,
	IReadOnlyDictionary<int, TimeSpan> ProcessorTimes,
	DateTimeOffset SampledAt);

/// <summary>Reads current resource counters for an explicitly attributed set of process ids.</summary>
public interface IProcessSetSnapshotReader
{
	/// <summary>Reads one point-in-time snapshot for the distinct positive process ids.</summary>
	ProcessSetSnapshot Read(IEnumerable<int> processIds);
}
