namespace Pact.App.Avalonia;

internal static class WindowShutdownCoordinator
{
	internal static async Task CompleteAsync(
		Func<Task> saveLayoutAsync,
		Func<Task> showProgressAsync,
		Func<Task> shutdownAsync,
		Func<Exception, Task> reportFailureAsync,
		Action approveAndClose)
	{
		ArgumentNullException.ThrowIfNull(saveLayoutAsync);
		ArgumentNullException.ThrowIfNull(showProgressAsync);
		ArgumentNullException.ThrowIfNull(shutdownAsync);
		ArgumentNullException.ThrowIfNull(reportFailureAsync);
		ArgumentNullException.ThrowIfNull(approveAndClose);

		List<Exception> failures = [];
		await CaptureFailureAsync(saveLayoutAsync, failures);
		await CaptureFailureAsync(showProgressAsync, failures);
		await CaptureFailureAsync(shutdownAsync, failures);

		try
		{
			if (failures.Count > 0)
			{
				var failure = failures.Count == 1
					? failures[0]
					: new AggregateException("Window shutdown failed.", failures);
				await reportFailureAsync(failure);
			}
		}
		catch (Exception)
		{
			// Failure reporting is best-effort; cleanup has already completed,
			// so it must never strand the canceled window close.
		}
		finally
		{
			approveAndClose();
		}
	}

	private static async Task CaptureFailureAsync(Func<Task> action, List<Exception> failures)
	{
		try
		{
			await action();
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}
}
