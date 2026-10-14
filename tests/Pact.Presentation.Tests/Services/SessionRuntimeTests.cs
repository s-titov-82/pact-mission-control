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
	public async Task Input_units_run_one_at_a_time_in_queue_order()
	{
		SessionRuntime runtime = new("session-1");
		TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
		List<string> events = [];
		var first = runtime.RunInputExclusiveAsync(
			async () =>
			{
				events.Add("first started");
				await releaseFirst.Task;
				events.Add("first finished");
				return true;
			},
			CancellationToken.None);
		var second = runtime.RunInputExclusiveAsync(
			() =>
			{
				events.Add("second started");
				return Task.FromResult(true);
			},
			CancellationToken.None);

		events.ShouldBe(["first started"]);
		releaseFirst.SetResult();
		await Task.WhenAll(first, second);
		events.ShouldBe(["first started", "first finished", "second started"]);
	}

	[Test]
	public async Task A_cancelled_waiting_unit_does_not_let_the_next_one_overtake_the_running_one()
	{
		SessionRuntime runtime = new("session-1");
		TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
		using CancellationTokenSource cancelSecond = new();
		var thirdStarted = false;
		var first = runtime.RunInputExclusiveAsync(
			async () =>
			{
				await releaseFirst.Task;
				return true;
			},
			CancellationToken.None);
		var second = runtime.RunInputExclusiveAsync(() => Task.FromResult(true), cancelSecond.Token);
		var third = runtime.RunInputExclusiveAsync(
			() =>
			{
				thirdStarted = true;
				return Task.FromResult(true);
			},
			CancellationToken.None);

		await cancelSecond.CancelAsync();
		await Should.ThrowAsync<OperationCanceledException>(second);
		thirdStarted.ShouldBeFalse();

		releaseFirst.SetResult();
		await Task.WhenAll(first, third);
		thirdStarted.ShouldBeTrue();
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
					runtime.InputModes.Scan(index % 2 == 0 ? enable : disable);
				}
			}),
			Task.Run(() =>
			{
				for (var index = 0; index < 10_000; index++)
				{
					_ = runtime.GetRecentOutput();
					_ = runtime.InputModes.IsWin32InputActive;
				}
			})
		];

		await Task.WhenAll(operations);

		runtime.GetRecentOutput().Length.ShouldBeLessThanOrEqualTo(32_768);
		runtime.InputModes.IsWin32InputActive.ShouldBeFalse();
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
