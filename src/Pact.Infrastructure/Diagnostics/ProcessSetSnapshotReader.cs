using Pact.Core.Platform;

namespace Pact.Infrastructure.Diagnostics;

/// <summary>Reads resource counters for an explicitly attributed Windows process set.</summary>
public sealed class ProcessSetSnapshotReader : IProcessSetSnapshotReader
{
	private readonly IProcessSnapshotSource _source;
	private readonly TimeProvider _timeProvider;

	/// <summary>Creates a reader backed by Windows process counters.</summary>
	public ProcessSetSnapshotReader()
		: this(new WindowsProcessSnapshotSource(), TimeProvider.System)
	{
	}

	internal ProcessSetSnapshotReader(
		IProcessSnapshotSource source,
		TimeProvider timeProvider)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	}

	/// <inheritdoc />
	public ProcessSetSnapshot Read(IEnumerable<int> processIds)
	{
		ArgumentNullException.ThrowIfNull(processIds);
		var distinctIds = processIds
			.Where(processId => processId > 0)
			.Distinct()
			.ToArray();
		var entries = distinctIds
			.Select(processId => _source.ReadMetrics(new(
				processId,
				ParentProcessId: 0,
				WorkingSetBytes: null,
				TotalProcessorTime: null)))
			.ToArray();

		return new ProcessSetSnapshot(
			distinctIds.Length,
			entries.Sum(entry => entry.WorkingSetBytes ?? 0),
			entries
				.Where(entry => entry.TotalProcessorTime is not null)
				.ToDictionary(
					entry => entry.ProcessId,
					entry => entry.TotalProcessorTime!.Value),
			_timeProvider.GetUtcNow());
	}
}
