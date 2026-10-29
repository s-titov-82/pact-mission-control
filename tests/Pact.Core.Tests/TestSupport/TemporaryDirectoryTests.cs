namespace Pact.Core.Tests.TestSupport;

public sealed class TemporaryDirectoryTests
{
	// Headless UI tests dispose it on the UI thread. A retry that resumed on that thread's context
	// would wait for the thread Dispose is blocking, and the test run hangs.
	[Test]
	public void Dispose_on_a_single_threaded_context_retries_a_locked_file_without_deadlocking()
	{
		// Both are disposed again at the end only as a safety net; disposing twice does nothing.
		using var directory = TemporaryDirectory.Create();
		var file = Path.Combine(directory.Path, "locked.txt");
		using var handle = new FileStream(file, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
		using var released = Task.Delay(TimeSpan.FromMilliseconds(30)).ContinueWith(_ => handle.Dispose(), TaskScheduler.Default);

		Exception? failure = null;
		Thread ui = new(() =>
		{
			SynchronizationContext.SetSynchronizationContext(new SingleThreadContext());
			try
			{
				directory.Dispose();
			}
			catch (Exception exception)
			{
				failure = exception;
			}
		})
		{ IsBackground = true };
		ui.Start();

		ui.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue("Dispose deadlocked on its own thread's context");
		failure.ShouldBeNull();
		Directory.Exists(directory.Path).ShouldBeFalse();
	}

	// Like a UI dispatcher: work posted to it runs only on its thread, which is blocked here.
	private sealed class SingleThreadContext : SynchronizationContext
	{
		public override void Post(SendOrPostCallback d, object? state)
		{
		}
	}
}
