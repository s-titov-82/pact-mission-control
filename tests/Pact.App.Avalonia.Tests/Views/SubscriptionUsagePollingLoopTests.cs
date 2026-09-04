using Pact.App.Avalonia.Views;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class SubscriptionUsagePollingLoopTests
{
	[Test]
	public async Task RunAsync_continues_polling_after_refresh_failure()
	{
		using CancellationTokenSource cancellation = new();
		var attempts = 0;
		List<Exception> failures = [];

		await SubscriptionUsagePollingLoop.RunAsync(
			refreshAsync: _ =>
			{
				attempts++;
				if (attempts == 1)
				{
					throw new InvalidOperationException("transient");
				}

				cancellation.Cancel();
				return Task.CompletedTask;
			},
			refreshTimeout: TimeSpan.FromSeconds(1),
			getDelay: () => TimeSpan.FromMinutes(2),
			delayAsync: (_, _) => Task.CompletedTask,
			onFailure: failures.Add,
			cancellationToken: cancellation.Token);

		attempts.ShouldBe(2);
		failures.ShouldHaveSingleItem();
		failures[0].Message.ShouldBe("transient");
	}

	[Test]
	public async Task RunAsync_cancels_timed_out_refresh_and_continues_polling()
	{
		using CancellationTokenSource cancellation = new();
		var attempts = 0;
		List<Exception> failures = [];

		await SubscriptionUsagePollingLoop.RunAsync(
			refreshAsync: async refreshCancellationToken =>
			{
				attempts++;
				if (attempts == 1)
				{
					await Task.Delay(Timeout.InfiniteTimeSpan, refreshCancellationToken);
				}
				else
				{
					cancellation.Cancel();
				}
			},
			refreshTimeout: TimeSpan.FromMilliseconds(10),
			getDelay: () => TimeSpan.FromMinutes(2),
			delayAsync: (_, _) => Task.CompletedTask,
			onFailure: failures.Add,
			cancellationToken: cancellation.Token);

		attempts.ShouldBe(2);
		var failure =
			failures.ShouldHaveSingleItem().ShouldBeAssignableTo<OperationCanceledException>();
		cancellation.Token.Equals(failure.CancellationToken).ShouldBeFalse();
	}
}