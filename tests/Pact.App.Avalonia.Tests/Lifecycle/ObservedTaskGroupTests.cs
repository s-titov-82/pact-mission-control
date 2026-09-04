using Pact.App.Avalonia.Lifecycle;

namespace Pact.App.Avalonia.Tests.Lifecycle;

public sealed class ObservedTaskGroupTests
{
	[Test]
	public void TryRun_starts_the_operation_before_returning()
	{
		ObservedTaskGroup group = new(static (_, _) => Task.CompletedTask);
		var started = false;

		group.TryRun(
			"ui-event",
			() =>
			{
				started = true;
				return Task.CompletedTask;
			}).ShouldBeTrue();

		started.ShouldBeTrue();
	}

	[Test]
	public async Task TryRun_reports_failure_and_complete_drain_observes_completion()
	{
		TaskCompletionSource<Exception> reported = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup group = new((_, exception) =>
		{
			reported.TrySetResult(exception);
			return Task.CompletedTask;
		});

		group.TryRun(
			"terminal-exit",
			() => Task.FromException(new IOException("terminal exit failed")))
			.ShouldBeTrue();
		await group.CompleteAndDrainAsync();

		(await reported.Task).Message.ShouldBe("terminal exit failed");
	}

	[Test]
	public async Task CompleteAndDrainAsync_rejects_late_work_and_waits_for_accepted_work()
	{
		TaskCompletionSource release = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource started = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		ObservedTaskGroup group = new(static (_, _) => Task.CompletedTask);
		var lateStarted = false;

		group.TryRun("accepted", async () =>
		{
			started.TrySetResult();
			await release.Task;
		}).ShouldBeTrue();
		await started.Task;

		var drain = group.CompleteAndDrainAsync();
		group.TryRun(
			"late",
			() =>
			{
				lateStarted = true;
				return Task.CompletedTask;
			}).ShouldBeFalse();

		drain.IsCompleted.ShouldBeFalse();
		lateStarted.ShouldBeFalse();
		release.TrySetResult();
		await drain;
	}

	[Test]
	public async Task Concurrent_admission_is_either_drained_or_rejected_without_starting()
	{
		const int operationCount = 8;
		ObservedTaskGroup group = new(static (_, _) => Task.CompletedTask);
		using Barrier barrier = new(operationCount + 1);
		var accepted = 0;
		var started = 0;
		var completed = 0;

		var attempts = Enumerable.Range(0, operationCount)
			.Select(_ => Task.Factory.StartNew(
				() =>
				{
					barrier.SignalAndWait();
					var wasAccepted = group.TryRun("race", async () =>
					{
						Interlocked.Increment(ref started);
						await Task.Yield();
						Interlocked.Increment(ref completed);
					});
					if (wasAccepted)
					{
						Interlocked.Increment(ref accepted);
					}
					return wasAccepted;
				},
				CancellationToken.None,
				TaskCreationOptions.LongRunning,
				TaskScheduler.Default))
			.ToArray();

		barrier.SignalAndWait();
		var drain = group.CompleteAndDrainAsync();
		var results = await Task.WhenAll(attempts);
		await drain;

		results.Count(result => result).ShouldBe(accepted);
		started.ShouldBe(accepted);
		completed.ShouldBe(accepted);
		group.TryRun("after-drain", static () => Task.CompletedTask).ShouldBeFalse();
	}
}
