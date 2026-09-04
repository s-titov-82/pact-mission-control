using Pact.Core.Terminal;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Backend ownership is transferred to SessionRuntime, which is disposed by the test.")]
public sealed class SessionRuntimeTests
{
	[Test]
	public async Task DetachControllerIfSame_does_not_detach_a_replacement()
	{
		SessionRuntime runtime = new("session-1");
		await using var first = CreateController();
		await using var replacement = CreateController();
		runtime.AttachController(first).ShouldBeNull();
		runtime.AttachController(replacement).ShouldBeSameAs(first);

		runtime.DetachControllerIfSame(first).ShouldBeFalse();
		runtime.TryGetController(out var current).ShouldBeTrue();
		current.ShouldBeSameAs(replacement);
	}

	[Test]
	public async Task Concurrent_output_and_mode_access_remains_consistent_and_bounded()
	{
		SessionRuntime runtime = new("session-1");
		var enable = $"{(char)0x1b}[?9001h";
		var disable = $"{(char)0x1b}[?9001l";

		Task[] operations =
		[
			Task.Run(() =>
			{
				for (var index = 0; index < 10_000; index++)
				{
					runtime.AppendRecentOutput("0123456789");
				}
			}),
			Task.Run(() =>
			{
				for (var index = 0; index < 10_000; index++)
				{
					runtime.Win32InputMode.Scan(index % 2 == 0 ? enable : disable);
				}
			}),
			Task.Run(() =>
			{
				for (var index = 0; index < 10_000; index++)
				{
					_ = runtime.GetRecentOutput();
					_ = runtime.Win32InputMode.IsActive;
				}
			})
		];

		await Task.WhenAll(operations);

		runtime.GetRecentOutput().Length.ShouldBeLessThanOrEqualTo(32_768);
		runtime.Win32InputMode.IsActive.ShouldBeFalse();
	}

	private static TerminalController CreateController() => new(new IdleTerminalBackend());

	private sealed class IdleTerminalBackend : ITerminalBackend
	{
		public Task<TerminalSession> StartAsync(
			TerminalStartOptions options,
			CancellationToken cancellationToken) =>
			Task.FromResult(new TerminalSession("fake", 1, options.Columns, options.Rows));

		public Task WriteAsync(byte[] input, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public async IAsyncEnumerable<byte[]> ReadOutputAsync(
			[System.Runtime.CompilerServices.EnumeratorCancellation]
			CancellationToken cancellationToken)
		{
			await Task.CompletedTask;
			yield break;
		}

		public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;
	}
}
