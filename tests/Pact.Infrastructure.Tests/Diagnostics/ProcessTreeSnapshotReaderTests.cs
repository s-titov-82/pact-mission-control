using Pact.Infrastructure.Diagnostics;

namespace Pact.Infrastructure.Tests.Diagnostics;

public sealed class ProcessTreeSnapshotReaderTests
{
	private static readonly DateTimeOffset SampledAt =
		new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public void Windows_reader_reads_the_current_process_tree()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("Windows process snapshots are Windows-only.");
		}

		ProcessTreeSnapshotReader reader = new();

		var snapshot = reader.Read(Environment.ProcessId);

		snapshot.RootProcessId.ShouldBe(Environment.ProcessId);
		snapshot.ProcessCount.ShouldBeGreaterThanOrEqualTo(1);
		snapshot.WorkingSetBytes.ShouldBeGreaterThan(0);
		snapshot.ProcessorTimes.ShouldContainKey(Environment.ProcessId);
	}

	[Test]
	public void Read_aggregates_only_the_requested_process_tree()
	{
		FakeProcessSnapshotSource source = new([
			new(10, 1, 100, TimeSpan.FromSeconds(1)),
			new(11, 10, 200, TimeSpan.FromSeconds(2)),
			new(12, 11, 300, TimeSpan.FromSeconds(3)),
			new(20, 1, 900, TimeSpan.FromSeconds(9))
		]);
		ProcessTreeSnapshotReader reader = new(
			source,
			new FixedTimeProvider(SampledAt));

		var snapshot = reader.Read(10);

		snapshot.RootProcessId.ShouldBe(10);
		snapshot.ProcessCount.ShouldBe(3);
		snapshot.WorkingSetBytes.ShouldBe(600);
		snapshot.ProcessorTimes.ShouldBe(new Dictionary<int, TimeSpan>
		{
			[10] = TimeSpan.FromSeconds(1),
			[11] = TimeSpan.FromSeconds(2),
			[12] = TimeSpan.FromSeconds(3)
		});
		snapshot.SampledAt.ShouldBe(SampledAt);
		source.MetricProcessIds.ShouldBe([10, 11, 12], ignoreOrder: true);
	}

	[Test]
	public void Read_counts_descendants_even_when_some_metrics_are_unavailable()
	{
		FakeProcessSnapshotSource source = new([
			new(10, 1, 100, TimeSpan.FromSeconds(1)),
			new(11, 10, null, null),
			new(12, 11, 300, TimeSpan.FromSeconds(3))
		]);
		ProcessTreeSnapshotReader reader = new(
			source,
			new FixedTimeProvider(SampledAt));

		var snapshot = reader.Read(10);

		snapshot.ProcessCount.ShouldBe(3);
		snapshot.WorkingSetBytes.ShouldBe(400);
		snapshot.ProcessorTimes.Keys.ShouldBe([10, 12], ignoreOrder: true);
	}

	[Test]
	public void Read_rejects_a_process_missing_from_the_operating_system_snapshot()
	{
		ProcessTreeSnapshotReader reader = new(
			new FakeProcessSnapshotSource([]),
			new FixedTimeProvider(SampledAt));

		Should.Throw<InvalidOperationException>(() => reader.Read(10))
			.Message.ShouldContain("10");
	}

	private sealed class FakeProcessSnapshotSource(
		IReadOnlyList<ProcessSnapshotEntry> entries) : IProcessSnapshotSource
	{
		internal List<int> MetricProcessIds { get; } = [];

		public IReadOnlyList<ProcessSnapshotEntry> ReadAll() => entries;

		public ProcessSnapshotEntry ReadMetrics(ProcessSnapshotEntry entry)
		{
			MetricProcessIds.Add(entry.ProcessId);
			return entry;
		}
	}

	private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
	{
		public override DateTimeOffset GetUtcNow() => utcNow;
	}
}
