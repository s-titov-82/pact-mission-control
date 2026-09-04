namespace Pact.App.Avalonia.Tests.Lifecycle;

public sealed class WindowShutdownCoordinatorTests
{
	[Test]
	public async Task Layout_failure_is_reported_without_skipping_shutdown_or_close()
	{
		IOException layoutFailure = new("layout unavailable");
		Exception? reportedFailure = null;
		var shutdownCalled = false;
		var closeApproved = false;

		await WindowShutdownCoordinator.CompleteAsync(
			saveLayoutAsync: () => Task.FromException(layoutFailure),
			showProgressAsync: () => Task.CompletedTask,
			shutdownAsync: () =>
			{
				shutdownCalled = true;
				return Task.CompletedTask;
			},
			reportFailureAsync: failure =>
			{
				reportedFailure = failure;
				return Task.CompletedTask;
			},
			approveAndClose: () => closeApproved = true);

		shutdownCalled.ShouldBeTrue();
		reportedFailure.ShouldBeSameAs(layoutFailure);
		closeApproved.ShouldBeTrue();
	}

	[Test]
	public async Task Faulted_shutdown_is_reported_and_still_approves_close()
	{
		AggregateException shutdownFailure = new("cleanup failed", new InvalidOperationException("session stop failed"));
		Exception? reportedFailure = null;
		var closeApproved = false;

		await WindowShutdownCoordinator.CompleteAsync(
			saveLayoutAsync: () => Task.CompletedTask,
			showProgressAsync: () => Task.CompletedTask,
			shutdownAsync: () => Task.FromException(shutdownFailure),
			reportFailureAsync: failure =>
			{
				reportedFailure = failure;
				return Task.CompletedTask;
			},
			approveAndClose: () => closeApproved = true);

		reportedFailure.ShouldBeSameAs(shutdownFailure);
		closeApproved.ShouldBeTrue();
	}

	[Test]
	public async Task Progress_failure_does_not_skip_shutdown_or_close()
	{
		InvalidOperationException progressFailure = new("terminal overlay unavailable");
		Exception? reportedFailure = null;
		var shutdownCalled = false;
		var closeApproved = false;

		await WindowShutdownCoordinator.CompleteAsync(
			saveLayoutAsync: () => Task.CompletedTask,
			showProgressAsync: () => Task.FromException(progressFailure),
			shutdownAsync: () =>
			{
				shutdownCalled = true;
				return Task.CompletedTask;
			},
			reportFailureAsync: failure =>
			{
				reportedFailure = failure;
				return Task.CompletedTask;
			},
			approveAndClose: () => closeApproved = true);

		shutdownCalled.ShouldBeTrue();
		reportedFailure.ShouldBeSameAs(progressFailure);
		closeApproved.ShouldBeTrue();
	}

	[Test]
	public async Task Reporting_failure_is_best_effort_and_still_approves_close()
	{
		var closeApproved = false;

		await WindowShutdownCoordinator.CompleteAsync(
			saveLayoutAsync: () => Task.CompletedTask,
			showProgressAsync: () => Task.CompletedTask,
			shutdownAsync: () => Task.FromException(new InvalidOperationException("cleanup failed")),
			reportFailureAsync: _ => Task.FromException(new IOException("log unavailable")),
			approveAndClose: () => closeApproved = true);

		closeApproved.ShouldBeTrue();
	}
}
