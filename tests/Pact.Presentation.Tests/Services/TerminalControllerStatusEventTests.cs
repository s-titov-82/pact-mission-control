using System.Runtime.CompilerServices;
using Pact.Core.Terminal;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Backend ownership is transferred to the awaited TerminalController fixture.")]
public sealed class TerminalControllerStatusEventTests
{
	[Test]
	public async Task InputWritten_fires_once_after_successful_nonempty_write()
	{
		StatusEventBackend backend = new();
		await using var controller = CreateController(backend);
		List<string> observed = [];
		controller.InputWritten += (_, input) => observed.Add(input);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);

		var written = await controller.WriteInputAsync("hello");
		var emptyWritten = await controller.WriteInputAsync(string.Empty);

		written.ShouldBeTrue();
		emptyWritten.ShouldBeFalse();
		observed.ShouldBe(["hello"]);
	}

	[Test]
	public async Task InputWritten_does_not_fire_before_start_or_after_failed_write()
	{
		StatusEventBackend backend = new();
		await using var controller = CreateController(backend);
		List<string> observed = [];
		controller.InputWritten += (_, input) => observed.Add(input);
		(await controller.WriteInputAsync("before")).ShouldBeFalse();
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);
		backend.FailWrite = true;

		(await controller.WriteInputAsync("failed")).ShouldBeFalse();
		observed.ShouldBeEmpty();
	}

	[Test]
	public async Task InputWriting_is_awaited_before_the_backend_write()
	{
		StatusEventBackend backend = new();
		await using var controller = CreateController(backend);
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		controller.InputWriting += _ => release.Task;
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);

		var write = controller.WriteInputAsync("hello");

		backend.WriteCount.ShouldBe(0);
		write.IsCompleted.ShouldBeFalse();
		release.TrySetResult();
		(await write).ShouldBeTrue();
		backend.WriteCount.ShouldBe(1);
	}

	[Test]
	public async Task ViewportChanged_fires_once_after_successful_dimension_change()
	{
		StatusEventBackend backend = new();
		await using var controller = CreateController(backend);
		List<(int Columns, int Rows)> observed = [];
		controller.ViewportChanged += (_, args) => observed.Add((args.Columns, args.Rows));
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);

		await controller.ResizeAsync(80, 24);
		await controller.ResizeAsync(100, 30);

		observed.ShouldBe([(100, 30)]);
	}

	[Test]
	public async Task ViewportChanged_does_not_fire_before_start_or_after_failed_resize()
	{
		StatusEventBackend backend = new();
		await using var controller = CreateController(backend);
		List<(int Columns, int Rows)> observed = [];
		controller.ViewportChanged += (_, args) => observed.Add((args.Columns, args.Rows));
		await controller.ResizeAsync(100, 30);
		await controller.StartAsync("fake", Environment.CurrentDirectory, 80, 24);
		backend.FailResize = true;

		await controller.ResizeAsync(100, 30);

		observed.ShouldBeEmpty();
	}

	private static TerminalController CreateController(StatusEventBackend backend) =>
		new(backend);

	private sealed class StatusEventBackend : ITerminalBackend
	{
		private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public bool FailWrite { get; set; }
		public bool FailResize { get; set; }
		public int WriteCount { get; private set; }

		public Task<TerminalSession> StartAsync(
			TerminalStartOptions options,
			CancellationToken cancellationToken) =>
			Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken)
		{
			WriteCount++;
			return FailWrite ? Task.FromException(new IOException("write failed")) : Task.CompletedTask;
		}

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
			FailResize ? Task.FromException(new IOException("resize failed")) : Task.CompletedTask;

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
