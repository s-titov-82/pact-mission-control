using Pact.Infrastructure.Diagnostics;

namespace Pact.Infrastructure.Tests.Diagnostics;

public sealed class ProcessSetSnapshotReaderTests
{
	private static readonly DateTimeOffset SampledAt =
		new(2026, 8, 25, 9, 0, 0, TimeSpan.Zero);

	[Test]
	public void Read_deduplicates_ids_and_aggregates_only_readable_processes()
	{
		FakeProcessSnapshotSource source = new(new Dictionary<int, ProcessSnapshotEntry>
		{
			[10] = new(10, 1, 100, TimeSpan.FromSeconds(1)),
			[11] = new(11, 1, null, null),
			[12] = new(12, 1, 300, TimeSpan.FromSeconds(3))
		});
		ProcessSetSnapshotReader reader = new(
			source,
			new FixedTimeProvider(SampledAt));

		var snapshot = reader.Read([10, 11, 10, 12]);

		snapshot.ProcessCount.ShouldBe(3);
		snapshot.WorkingSetBytes.ShouldBe(400);
		snapshot.ProcessorTimes.ShouldBe(new Dictionary<int, TimeSpan>
		{
			[10] = TimeSpan.FromSeconds(1),
			[12] = TimeSpan.FromSeconds(3)
		});
		snapshot.SampledAt.ShouldBe(SampledAt);
	}

	private sealed class FakeProcessSnapshotSource(
		IReadOnlyDictionary<int, ProcessSnapshotEntry> entries) : IProcessSnapshotSource
	{
		public IReadOnlyList<ProcessSnapshotEntry> ReadAll() => entries.Values.ToArray();

		public ProcessSnapshotEntry ReadMetrics(ProcessSnapshotEntry entry) =>
			entries.GetValueOrDefault(entry.ProcessId, entry);
	}

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}
}
