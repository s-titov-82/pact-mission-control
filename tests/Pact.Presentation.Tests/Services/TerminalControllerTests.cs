using System.Runtime.CompilerServices;
using Pact.Core.Terminal;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Backend ownership is transferred to the awaited TerminalController fixture.")]
public sealed class TerminalControllerTests
{
	[Test]
	public async Task StartAsync_exposes_started_process_id()
	{
		CapturingBackend backend = new();

		await using TerminalController controller = new(backend);

		controller.ProcessId.ShouldBeNull();
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);
		controller.ProcessId.ShouldBe(1);
	}

	[Test]
	public async Task StopAsync_stops_backend_before_waiting_for_cancellation_callbacks()
	{
		StopOrderingBackend backend = new();

		await using TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);
		await backend.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));

		var stopTask = controller.StopAsync();

		try
		{
			await backend.StopObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
			await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
			backend.StopCalled.ShouldBeTrue();
		}
		finally
		{
			backend.ReleaseCancellationCallback();
			await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
		}
	}

	[Test]
	public async Task StartAsync_publishes_output_without_terminal_view()
	{
		OutputBackend backend = new("hello");
		using var temporaryDirectory = TemporaryDirectory.Create();
		var unusedStorageRoot = temporaryDirectory.Path;
		List<string> output = [];

		await using TerminalController controller = new(backend);
		controller.OutputReceived += (_, text) => output.Add(text);

		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);
		await backend.ReadFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));

		output.ShouldContain("hello");
		Directory.EnumerateFileSystemEntries(unusedStorageRoot).ShouldBeEmpty();
	}

	[Test]
	public async Task WriteInputAsync_returns_false_before_session_is_started()
	{
		CapturingBackend backend = new();

		await using TerminalController controller = new(backend);

		(await controller.WriteInputAsync("hello")).ShouldBeFalse();
		backend.InputWrites.ShouldBeEmpty();
	}

	[Test]
	public async Task WriteInputAsync_returns_true_when_input_is_written()
	{
		CapturingBackend backend = new();

		await using TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);

		(await controller.WriteInputAsync("hello")).ShouldBeTrue();
		backend.InputWrites.ShouldBe(["hello"]);
	}

	[Test]
	public async Task ResizeAsync_serializes_requests_and_only_publishes_latest_dimensions()
	{
		DelayedResizeBackend backend = new();
		await using TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 25);
		List<(int Columns, int Rows)> published = [];
		controller.ViewportChanged += (_, args) =>
			published.Add((args.Columns, args.Rows));

		var first = controller.ResizeAsync(100, 30);
		await backend.FirstResizeStarted.Task;
		var second = controller.ResizeAsync(101, 37);

		try
		{
			backend.MaximumConcurrentResizes.ShouldBe(1);
			backend.ResizeRequests.ShouldBe([(100, 30)]);
		}
		finally
		{
			backend.ReleaseFirstResize.SetResult();
		}
		await Task.WhenAll(first, second);

		backend.ResizeRequests.ShouldBe([(100, 30), (101, 37)]);
		published.ShouldBe([(101, 37)]);

		await controller.ResizeAsync(101, 37);
		backend.ResizeRequests.ShouldBe([(100, 30), (101, 37)]);
	}

	[Test]
	public async Task ResizeAsync_skips_unchanged_dimensions()
	{
		CapturingBackend backend = new();

		await using TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 120, 36);

		await controller.ResizeAsync(120, 36);
		await controller.ResizeAsync(121, 36);

		backend.ResizeRequests.ShouldBe([(121, 36)]);
	}

	private sealed class StopOrderingBackend : ITerminalBackend
	{
		private readonly TaskCompletionSource _backendStopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
		private readonly TaskCompletionSource _allowCancellationCallback = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource StopObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public bool StopCalled { get; private set; }

		public Task<TerminalSession> StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			using var registration = cancellationToken.Register(
				state => ((Task)state!).GetAwaiter().GetResult(),
				_allowCancellationCallback.Task);

			ReadStarted.TrySetResult();
			await _backendStopped.Task;
			yield break;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			StopCalled = true;
			StopObserved.TrySetResult();
			_backendStopped.TrySetResult();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			_backendStopped.TrySetResult();
			_allowCancellationCallback.TrySetResult();
			return ValueTask.CompletedTask;
		}

		public void ReleaseCancellationCallback() => _allowCancellationCallback.TrySetResult();
	}

	private sealed class OutputBackend(string text) : ITerminalBackend
	{
		public TaskCompletionSource ReadFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task<TerminalSession> StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken) => Task.CompletedTask;

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) => Task.CompletedTask;

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			yield return System.Text.Encoding.UTF8.GetBytes(text);
			ReadFinished.TrySetResult();
			await Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}

	private sealed class CapturingBackend : ITerminalBackend
	{
		private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<string> InputWrites { get; } = [];
		public List<(int Columns, int Rows)> ResizeRequests { get; } = [];

		public Task<TerminalSession> StartAsync(TerminalStartOptions options, CancellationToken cancellationToken) => Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken)
		{
			InputWrites.Add(System.Text.Encoding.UTF8.GetString(input));
			return Task.CompletedTask;
		}

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
		{
			ResizeRequests.Add((columns, rows));
			return Task.CompletedTask;
		}

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await _stop.Task.WaitAsync(cancellationToken);
			yield break;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			_stop.TrySetResult();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			_stop.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}

	private sealed class DelayedResizeBackend : ITerminalBackend
	{
		private readonly TaskCompletionSource _stop =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		private int _concurrentResizes;
		public TaskCompletionSource FirstResizeStarted { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource ReleaseFirstResize { get; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public List<(int Columns, int Rows)> ResizeRequests { get; } = [];
		public int MaximumConcurrentResizes { get; private set; }

		public Task<TerminalSession> StartAsync(
			TerminalStartOptions options,
			CancellationToken cancellationToken) =>
			Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public async Task ResizeAsync(
			int columns,
			int rows,
			CancellationToken cancellationToken)
		{
			ResizeRequests.Add((columns, rows));
			var concurrent = Interlocked.Increment(ref _concurrentResizes);
			MaximumConcurrentResizes = Math.Max(MaximumConcurrentResizes, concurrent);
			try
			{
				if (ResizeRequests.Count == 1)
				{
					FirstResizeStarted.SetResult();
					await ReleaseFirstResize.Task;
				}
			}
			finally
			{
				Interlocked.Decrement(ref _concurrentResizes);
			}
		}

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[EnumeratorCancellation] CancellationToken cancellationToken)
		{
			await _stop.Task.WaitAsync(cancellationToken);
			yield break;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			_stop.TrySetResult();
			return Task.CompletedTask;
		}

		public ValueTask DisposeAsync()
		{
			_stop.TrySetResult();
			return ValueTask.CompletedTask;
		}
	}
}
