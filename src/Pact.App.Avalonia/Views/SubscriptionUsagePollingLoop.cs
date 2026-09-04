namespace Pact.App.Avalonia.Views;

internal static class SubscriptionUsagePollingLoop
{
	internal static async Task RunAsync(
		Func<CancellationToken, Task> refreshAsync,
		TimeSpan refreshTimeout,
		Func<TimeSpan> getDelay,
		Func<TimeSpan, CancellationToken, Task> delayAsync,
		Action<Exception> onFailure,
		CancellationToken cancellationToken)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(refreshTimeout, TimeSpan.Zero);

		while (!cancellationToken.IsCancellationRequested)
		{
			using var refreshCancellation =
				CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
			refreshCancellation.CancelAfter(refreshTimeout);

			try
			{
				await refreshAsync(refreshCancellation.Token);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception exception)
			{
				onFailure(exception);
			}

			try
			{
				await delayAsync(getDelay(), cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				break;
			}
		}
	}
}