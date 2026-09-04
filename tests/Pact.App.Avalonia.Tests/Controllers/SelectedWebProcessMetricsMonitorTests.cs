using Pact.App.Avalonia.Controllers;
using Pact.Core.Platform;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SelectedWebProcessMetricsMonitorTests
{
	private static readonly DateTimeOffset StartedAt =
		new(2026, 8, 25, 10, 0, 0, TimeSpan.Zero);

	[Test]
	public async Task Disabled_monitor_does_not_read_webview_processes()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeWebProcessMetricsSnapshotReader reader = new();
		await using SelectedWebProcessMetricsMonitor monitor = new(
			reader,
			time,
			processorCount: 4);

		monitor.SetTarget("web-1", enabled: false);
		time.Advance(TimeSpan.FromMinutes(1));

		reader.PageIds.ShouldBeEmpty();
		monitor.Current.ShouldBeNull();
	}

	[Test]
	public async Task Enabled_monitor_publishes_both_groups_and_cpu_after_next_sample()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeWebProcessMetricsSnapshotReader reader = new(
			Snapshot(
				ProcessSet(2, 2 * 1024 * 1024, StartedAt, (20, 1), (21, 2)),
				ProcessSet(3, 3 * 1024 * 1024, StartedAt, (100, 4), (101, 5))),
			Snapshot(
				ProcessSet(2, 4 * 1024 * 1024, StartedAt.AddSeconds(2), (20, 1.2), (21, 2.2)),
				ProcessSet(3, 6 * 1024 * 1024, StartedAt.AddSeconds(2), (100, 4.4), (101, 5.4))));
		await using SelectedWebProcessMetricsMonitor monitor = new(
			reader,
			time,
			processorCount: 4);
		MetricsChangeWaiter changes = new(monitor);

		monitor.SetTarget("web-1", enabled: true);
		await changes.WaitForCountAsync(1);
		var first = monitor.Current.ShouldNotBeNull();
		first.PageRenderers.ProcessCount.ShouldBe(2);
		first.PageRenderers.WorkingSetBytes.ShouldBe(2 * 1024 * 1024);
		first.PageRenderers.CpuPercent.ShouldBeNull();
		first.SharedRuntime.ProcessCount.ShouldBe(3);
		first.SharedRuntime.WorkingSetBytes.ShouldBe(3 * 1024 * 1024);

		await time.WaitForTimerCountAsync(
			SelectedWebProcessMetricsMonitor.PollInterval,
			minimumCount: 1);
		time.Advance(SelectedWebProcessMetricsMonitor.PollInterval);
		await changes.WaitForCountAsync(2);

		var second = monitor.Current.ShouldNotBeNull();
		second.PageRenderers.CpuPercent.ShouldNotBeNull().ShouldBe(5, tolerance: 0.001);
		second.SharedRuntime.CpuPercent.ShouldNotBeNull().ShouldBe(10, tolerance: 0.001);
		second.PageRenderers.WorkingSetBytes.ShouldBe(4 * 1024 * 1024);
		second.SharedRuntime.WorkingSetBytes.ShouldBe(6 * 1024 * 1024);
	}

	[Test]
	public async Task Failed_read_publishes_unavailable_metrics_and_reports_original_exception()
	{
		ManualTimeProvider time = new(StartedAt);
		InvalidOperationException failure = new("method missing");
		FakeWebProcessMetricsSnapshotReader reader = new(failure);
		TaskCompletionSource<Exception> reported =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		await using SelectedWebProcessMetricsMonitor monitor = new(
			reader,
			time,
			processorCount: 4,
			reportFailureAsync: exception =>
			{
				reported.TrySetResult(exception);
				return Task.CompletedTask;
			});
		MetricsChangeWaiter changes = new(monitor);

		monitor.SetTarget("web-1", enabled: true);
		await changes.WaitForCountAsync(1);

		monitor.Current.ShouldNotBeNull().Error.ShouldBe("method missing");
		(await reported.Task.WaitAsync(TimeSpan.FromSeconds(5))).ShouldBeSameAs(failure);
	}

	[Test]
	public async Task Repeated_identical_failure_is_reported_once_until_a_successful_sample()
	{
		ManualTimeProvider time = new(StartedAt);
		InvalidOperationException failure = new("method missing");
		FakeWebProcessMetricsSnapshotReader reader = new(failure, failure);
		List<Exception> reported = [];
		await using SelectedWebProcessMetricsMonitor monitor = new(
			reader,
			time,
			processorCount: 4,
			reportFailureAsync: exception =>
			{
				reported.Add(exception);
				return Task.CompletedTask;
			});
		MetricsChangeWaiter changes = new(monitor);

		monitor.SetTarget("web-1", enabled: true);
		await changes.WaitForCountAsync(1);
		await time.WaitForTimerCountAsync(
			SelectedWebProcessMetricsMonitor.PollInterval,
			minimumCount: 1);
		time.Advance(SelectedWebProcessMetricsMonitor.PollInterval);
		await changes.WaitForCountAsync(2);

		reported.ShouldBe([failure]);
	}

	[Test]
	public async Task Changing_to_an_unloaded_target_stops_polling_and_clears_metrics()
	{
		ManualTimeProvider time = new(StartedAt);
		FakeWebProcessMetricsSnapshotReader reader = new(
			Snapshot(
				ProcessSet(1, 100, StartedAt, (20, 1)),
				ProcessSet(1, 200, StartedAt, (100, 2))));
		await using SelectedWebProcessMetricsMonitor monitor = new(reader, time, 4);
		MetricsChangeWaiter changes = new(monitor);

		monitor.SetTarget("web-1", enabled: true);
		await changes.WaitForCountAsync(1);
		monitor.Current.ShouldNotBeNull();
		monitor.SetTarget(pageId: null, enabled: true);
		time.Advance(TimeSpan.FromMinutes(1));

		monitor.Current.ShouldBeNull();
		reader.PageIds.ShouldBe(["web-1"]);
	}

	private static WebProcessMetricsSnapshot Snapshot(
		ProcessSetSnapshot page,
		ProcessSetSnapshot shared) => new(page, shared);

	private static ProcessSetSnapshot ProcessSet(
		int processCount,
		long workingSetBytes,
		DateTimeOffset sampledAt,
		params (int ProcessId, double ProcessorSeconds)[] processorTimes) =>
		new(
			processCount,
			workingSetBytes,
			processorTimes.ToDictionary(
				value => value.ProcessId,
				value => TimeSpan.FromSeconds(value.ProcessorSeconds)),
			sampledAt);

	private sealed class FakeWebProcessMetricsSnapshotReader(
		params object[] results) : IWebProcessMetricsSnapshotReader
	{
		private readonly Queue<object> _results = new(results);

		internal List<string> PageIds { get; } = [];

		public Task<WebProcessMetricsSnapshot> ReadAsync(
			string pageId,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			PageIds.Add(pageId);
			var result = _results.Dequeue();
			return result is Exception exception
				? Task.FromException<WebProcessMetricsSnapshot>(exception)
				: Task.FromResult((WebProcessMetricsSnapshot)result);
		}
	}

	private sealed class MetricsChangeWaiter
	{
		private readonly Lock _gate = new();
		private readonly List<(int Count, TaskCompletionSource Completion)> _waiters = [];
		private int _count;

		internal MetricsChangeWaiter(SelectedWebProcessMetricsMonitor monitor)
		{
			monitor.MetricsChanged += OnMetricsChanged;
		}

		internal Task WaitForCountAsync(int expected)
		{
			lock (_gate)
			{
				if (_count >= expected)
				{
					return Task.CompletedTask;
				}

				TaskCompletionSource completion =
					new(TaskCreationOptions.RunContinuationsAsynchronously);
				_waiters.Add((expected, completion));
				return completion.Task;
			}
		}

		private void OnMetricsChanged(object? sender, EventArgs args)
		{
			lock (_gate)
			{
				_count++;
				foreach (var waiter in _waiters.Where(waiter => waiter.Count <= _count))
				{
					waiter.Completion.TrySetResult();
				}

				_waiters.RemoveAll(waiter => waiter.Count <= _count);
			}
		}
	}
}
