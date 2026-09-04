using Pact.App.Avalonia.Web;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class TerminalOutputBridgeBatcherTests
{
	[Test]
	public async Task Active_and_hidden_output_use_distinct_delays_and_one_bridge_write_per_batch()
	{
		ControlledDelay delays = new();
		List<(string SessionId, string Text)> writes = [];
		await using TerminalOutputBridgeBatcher batcher = new(
			(sessionId, text) =>
			{
				writes.Add((sessionId, text));
				return Task.CompletedTask;
			},
			delays.WaitAsync);

		await batcher.ActivateAndFlushAsync("active");
		var first = batcher.EnqueueAsync("active", "a");
		var second = batcher.EnqueueAsync("active", "b");
		var hidden = batcher.EnqueueAsync("hidden", "c");

		delays.Scheduled.ShouldBe([TimeSpan.FromMilliseconds(33), TimeSpan.FromMilliseconds(100)]);
		writes.ShouldBeEmpty();

		delays.Release(TimeSpan.FromMilliseconds(33));
		await Task.WhenAll(first, second);
		writes.ShouldBe([("active", "ab")]);

		delays.Release(TimeSpan.FromMilliseconds(100));
		await hidden;
		writes.ShouldBe([("active", "ab"), ("hidden", "c")]);
		batcher.PerformanceSnapshot.ShouldBe(new TerminalOutputPerformanceSnapshot(
			ReceivedChunks: 3,
			ReceivedCharacters: 3,
			BridgeBatches: 2,
			BridgeCharacters: 3,
			MaximumPendingCharacters: 2));
	}

	[Test]
	public async Task Activating_session_flushes_its_pending_output_without_waiting_for_hidden_delay()
	{
		ControlledDelay delays = new();
		List<(string SessionId, string Text)> writes = [];
		await using TerminalOutputBridgeBatcher batcher = new(
			(sessionId, text) =>
			{
				writes.Add((sessionId, text));
				return Task.CompletedTask;
			},
			delays.WaitAsync);

		var pending = batcher.EnqueueAsync("hidden", "pending");

		await batcher.ActivateAndFlushAsync("hidden");
		await pending;

		writes.ShouldBe([("hidden", "pending")]);
	}

	private sealed class ControlledDelay
	{
		private readonly Dictionary<TimeSpan, TaskCompletionSource> _pending = [];

		public List<TimeSpan> Scheduled { get; } = [];

		public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
		{
			Scheduled.Add(delay);
			TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			_pending.Add(delay, completion);
			cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
			return completion.Task;
		}

		public void Release(TimeSpan delay) => _pending[delay].TrySetResult();
	}
}