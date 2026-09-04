using System.Threading.Channels;
using Pact.Core.Terminal;

namespace Pact.App.Avalonia.Tests.Fakes;

internal sealed class FakeTerminalBackend : ITerminalBackend
{
	private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();
	public int StartCount { get; private set; }
	public TerminalStartOptions? LastStartOptions { get; private set; }
	public List<string> Inputs { get; } = [];
	public List<(int Columns, int Rows)> ResizeRequests { get; } = [];
	public Func<int, int, Task>? ResizeHandler { get; set; }
	public Action<string>? InputWritten { get; set; }
	public TaskCompletionSource InputWriteStarted { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource? InputWriteBlocker { get; set; }
	public Exception? InputWriteFailure { get; set; }
	public string? ExitResponse { get; set; }
	public TaskCompletionSource<bool>? StopBlocker { get; set; }
	public TaskCompletionSource<bool> StopStarted { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource FirstOutputProcessed { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource StartStarted { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource? StartBlocker { get; set; }
	public Exception? StartFailure { get; set; }
	public int? ProcessId { get; set; }
	public void EmitOutput(string text) => _output.Writer.TryWrite(System.Text.Encoding.UTF8.GetBytes(text));
	public void CompleteOutput() => _output.Writer.TryComplete();

	public async Task<TerminalSession> StartAsync(
		TerminalStartOptions options,
		CancellationToken cancellationToken)
	{
		StartCount++;
		LastStartOptions = options;
		StartStarted.TrySetResult();
		if (StartBlocker is not null)
		{
			await StartBlocker.Task.WaitAsync(cancellationToken);
		}

		if (StartFailure is not null)
		{
			throw StartFailure;
		}

		return new TerminalSession("fake", ProcessId, options.Columns, options.Rows);
	}

	public async Task WriteAsync(byte[] input, CancellationToken cancellationToken)
	{
		var text = System.Text.Encoding.UTF8.GetString(input);
		Inputs.Add(text);
		InputWritten?.Invoke(text);
		InputWriteStarted.TrySetResult();
		if (InputWriteBlocker is not null)
		{
			await InputWriteBlocker.Task.WaitAsync(cancellationToken);
		}

		if (InputWriteFailure is not null)
		{
			throw InputWriteFailure;
		}

		if (text == "/exit" && ExitResponse is not null)
		{
			EmitOutput(ExitResponse);
		}
	}
	public async Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken)
	{
		ResizeRequests.Add((columns, rows));
		if (ResizeHandler is not null)
		{
			await ResizeHandler(columns, rows);
		}
	}

	public async IAsyncEnumerable<byte[]> ReadOutputAsync(
		[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
	{
		await foreach (var chunk in _output.Reader.ReadAllAsync(cancellationToken))
		{
			yield return chunk;
			FirstOutputProcessed.TrySetResult();
		}
	}

	public async Task StopAsync(CancellationToken cancellationToken)
	{
		StopStarted.TrySetResult(true);
		if (StopBlocker is not null)
		{
			await StopBlocker.Task;
		}

		_output.Writer.TryComplete();
	}
	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
