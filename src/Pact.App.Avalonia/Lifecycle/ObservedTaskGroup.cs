namespace Pact.App.Avalonia.Lifecycle;

/// <summary>
/// Owns asynchronous work started by synchronous event adapters and observes every accepted
/// operation before application shutdown can complete.
/// </summary>
internal sealed class ObservedTaskGroup(
	Func<string, Exception, Task> reportFailureAsync)
{
	private readonly Lock _gate = new();
	private readonly HashSet<Task> _tasks = [];
	private bool _sealed;

	public bool TryRun(
		string operationName,
		Func<Task> operation,
		Func<Exception, Task>? reportUserFailureAsync = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
		ArgumentNullException.ThrowIfNull(operation);

		TaskCompletionSource completion = new(
			TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate)
		{
			if (_sealed)
			{
				return false;
			}

			_tasks.Add(completion.Task);
			_ = completion.Task.ContinueWith(
				completed =>
				{
					lock (_gate)
					{
						_tasks.Remove(completed);
					}
				},
				CancellationToken.None,
				TaskContinuationOptions.ExecuteSynchronously,
				TaskScheduler.Default);
		}

		_ = ObserveAndCompleteAsync(
			completion,
			operationName,
			operation,
			reportUserFailureAsync);
		return true;
	}

	public Task CompleteAndDrainAsync()
	{
		lock (_gate)
		{
			_sealed = true;
			return Task.WhenAll(_tasks.ToArray());
		}
	}

	internal Task WaitForIdleAsync()
	{
		lock (_gate)
		{
			return Task.WhenAll(_tasks.ToArray());
		}
	}

	private async Task ObserveAndCompleteAsync(
		TaskCompletionSource completion,
		string operationName,
		Func<Task> operation,
		Func<Exception, Task>? reportUserFailureAsync)
	{
		try
		{
			await operation().ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// Cancellation is an expected lifetime outcome.
		}
		catch (Exception exception)
		{
			await ReportBestEffortAsync(
				() => reportFailureAsync(operationName, exception))
				.ConfigureAwait(false);
			if (reportUserFailureAsync is not null)
			{
				await ReportBestEffortAsync(
					() => reportUserFailureAsync(exception))
					.ConfigureAwait(false);
			}
		}
		finally
		{
			completion.TrySetResult();
		}
	}

	private static async Task ReportBestEffortAsync(Func<Task> reportAsync)
	{
		try
		{
			await reportAsync().ConfigureAwait(false);
		}
		catch (Exception)
		{
			// Failure reporting must not re-fault the observer or the shutdown drain.
		}
	}
}