using Pact.App.Avalonia.Controllers;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SelectedProcessMetricsMonitorTests
{
	private static readonly DateTimeOffset StartedAt =
		new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public void Disabled_monitor_does_not_read_processes()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeProcessTreeSnapshotReader reader = new();
		using SelectedProcessMetricsMonitor monitor = new(reader, time, processorCount: 4);

		monitor.SetTarget(rootProcessId: 10, enabled: false);
		time.Advance(TimeSpan.FromMinutes(1));

		reader.RootProcessIds.ShouldBeEmpty();
		monitor.Current.ShouldBeNull();
	}

	[Test]
	public void Enabled_monitor_publishes_memory_immediately_and_cpu_after_next_sample()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeProcessTreeSnapshotReader reader = new(
			Snapshot(10, 3, 1_572_864, StartedAt, (10, 1), (11, 2)),
			Snapshot(10, 3, 2_097_152, StartedAt.AddSeconds(2), (10, 1.2), (11, 2.2)));
		using SelectedProcessMetricsMonitor monitor = new(reader, time, processorCount: 4);

		monitor.SetTarget(rootProcessId: 10, enabled: true);
		time.Advance(TimeSpan.Zero);

		var first = monitor.Current.ShouldNotBeNull();
		first.ProcessCount.ShouldBe(3);
		first.WorkingSetBytes.ShouldBe(1_572_864);
		first.CpuPercent.ShouldBeNull();

		time.Advance(TimeSpan.FromSeconds(2));

		var second = monitor.Current.ShouldNotBeNull();
		second.WorkingSetBytes.ShouldBe(2_097_152);
		second.CpuPercent.ShouldNotBeNull().ShouldBe(5, tolerance: 0.001);
		reader.RootProcessIds.ShouldBe([10, 10]);
	}

	[Test]
	public void Changing_or_disabling_target_resets_sampling_and_stops_old_timer()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeProcessTreeSnapshotReader reader = new(
			Snapshot(10, 1, 100, StartedAt, (10, 1)),
			Snapshot(20, 1, 200, StartedAt, (20, 2)));
		using SelectedProcessMetricsMonitor monitor = new(reader, time, processorCount: 4);

		monitor.SetTarget(10, enabled: true);
		time.Advance(TimeSpan.Zero);
		monitor.SetTarget(20, enabled: true);
		monitor.Current.ShouldBeNull();
		time.Advance(TimeSpan.Zero);

		var current = monitor.Current.ShouldNotBeNull();
		current.RootProcessId.ShouldBe(20);
		current.CpuPercent.ShouldBeNull();
		monitor.SetTarget(20, enabled: false);
		time.Advance(TimeSpan.FromMinutes(1));

		monitor.Current.ShouldBeNull();
		reader.RootProcessIds.ShouldBe([10, 20]);
	}

	[Test]
	public void Read_failure_is_published_as_unavailable_without_escaping_timer_callback()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeProcessTreeSnapshotReader reader = new(new InvalidOperationException("gone"));
		using SelectedProcessMetricsMonitor monitor = new(reader, time, processorCount: 4);

		monitor.SetTarget(10, enabled: true);
		Should.NotThrow(() => time.Advance(TimeSpan.Zero));

		monitor.Current.ShouldNotBeNull();
		monitor.Current.IsAvailable.ShouldBeFalse();
		monitor.Current.Error.ShouldBe("gone");
	}

	private static ProcessTreeSnapshot Snapshot(
		int rootProcessId,
		int processCount,
		long workingSetBytes,
		DateTimeOffset sampledAt,
		params (int ProcessId, double ProcessorSeconds)[] processorTimes) =>
		new(
			rootProcessId,
			processCount,
			workingSetBytes,
			processorTimes.ToDictionary(
				value => value.ProcessId,
				value => TimeSpan.FromSeconds(value.ProcessorSeconds)),
			sampledAt);

	private sealed class FakeProcessTreeSnapshotReader : IProcessTreeSnapshotReader
	{
		private readonly Queue<object> _results;

		internal FakeProcessTreeSnapshotReader(params object[] results)
		{
			_results = new Queue<object>(results);
		}

		internal List<int> RootProcessIds { get; } = [];

		public ProcessTreeSnapshot Read(int rootProcessId)
		{
			RootProcessIds.Add(rootProcessId);
			var result = _results.Dequeue();
			return result is Exception exception
				? throw exception
				: (ProcessTreeSnapshot)result;
		}
	}
}
