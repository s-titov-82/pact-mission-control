using System.Text;

namespace Pact.App.Avalonia.Web;

internal sealed record TerminalOutputPerformanceSnapshot(
	long ReceivedChunks,
	long ReceivedCharacters,
	long BridgeBatches,
	long BridgeCharacters,
	int MaximumPendingCharacters);

internal sealed class TerminalOutputBridgeBatcher : IAsyncDisposable
{
	private static readonly TimeSpan ActiveDelay = TimeSpan.FromMilliseconds(33);
	private static readonly TimeSpan HiddenDelay = TimeSpan.FromMilliseconds(100);
	private readonly Lock _sync = new();
	private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
	private readonly Func<string, string, Task> _writeBatchAsync;
	private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
	private string? _activeSessionId;
	private long _receivedChunks;
	private long _receivedCharacters;
	private long _bridgeBatches;
	private long _bridgeCharacters;
	private int _maximumPendingCharacters;
	private bool _disposed;

	public TerminalOutputBridgeBatcher(
		Func<string, string, Task> writeBatchAsync,
		Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
	{
		_writeBatchAsync = writeBatchAsync ?? throw new ArgumentNullException(nameof(writeBatchAsync));
		_delayAsync = delayAsync ?? ((delay, cancellationToken) => Task.Delay(delay, cancellationToken));
	}

	public TerminalOutputPerformanceSnapshot PerformanceSnapshot
	{
		get
		{
			lock (_sync)
			{
				return new(
					_receivedChunks,
					_receivedCharacters,
					_bridgeBatches,
					_bridgeCharacters,
					_maximumPendingCharacters);
			}
		}
	}

	public Task EnqueueAsync(string sessionId, string text)
	{
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		ArgumentNullException.ThrowIfNull(text);
		if (text.Length == 0)
		{
			return Task.CompletedTask;
		}

		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			var state = GetOrCreateState(sessionId);
			state.Pending.Append(text);
			TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
			state.Completions.Add(completion);
			_receivedChunks++;
			_receivedCharacters += text.Length;
			_maximumPendingCharacters = Math.Max(_maximumPendingCharacters, state.Pending.Length);
			Schedule(state);
			return completion.Task;
		}
	}

	public Task ActivateAndFlushAsync(string sessionId)
	{
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		SessionState? state;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			_activeSessionId = sessionId;
			if (!_sessions.TryGetValue(sessionId, out state))
			{
				return Task.CompletedTask;
			}

			state.ScheduleVersion++;
			state.FlushScheduled = false;
		}

		return FlushAsync(state);
	}

	public async Task RemoveSessionAsync(string sessionId)
	{
		SessionState? state;
		lock (_sync)
		{
			if (!_sessions.TryGetValue(sessionId, out state))
			{
				return;
			}

			state.ScheduleVersion++;
			state.FlushScheduled = false;
		}

		await FlushAsync(state);
		lock (_sync)
		{
			_sessions.Remove(sessionId);
			if (_activeSessionId == sessionId)
			{
				_activeSessionId = null;
			}
		}
		state.Gate.Dispose();
	}

	private SessionState GetOrCreateState(string sessionId)
	{
		if (_sessions.TryGetValue(sessionId, out var state))
		{
			return state;
		}

		state = new SessionState(sessionId);
		_sessions.Add(sessionId, state);
		return state;
	}

	private void Schedule(SessionState state)
	{
		if (state.FlushScheduled)
		{
			return;
		}

		state.FlushScheduled = true;
		var version = ++state.ScheduleVersion;
		var delay = state.SessionId == _activeSessionId ? ActiveDelay : HiddenDelay;
		_ = FlushAfterDelayAsync(state, version, delay);
	}

	private async Task FlushAfterDelayAsync(SessionState state, int version, TimeSpan delay)
	{
		try
		{
			await _delayAsync(delay, CancellationToken.None);
			lock (_sync)
			{
				if (_disposed || version != state.ScheduleVersion)
				{
					return;
				}

				state.FlushScheduled = false;
			}
			await FlushAsync(state);
		}
		catch (Exception exception)
		{
			FailPending(state, exception);
		}
	}

	private async Task FlushAsync(SessionState state)
	{
		await state.Gate.WaitAsync();
		try
		{
			string text;
			TaskCompletionSource[] completions;
			lock (_sync)
			{
				if (state.Pending.Length == 0)
				{
					return;
				}

				text = state.Pending.ToString();
				state.Pending.Clear();
				completions = [.. state.Completions];
				state.Completions.Clear();
			}

			try
			{
				await _writeBatchAsync(state.SessionId, text);
				lock (_sync)
				{
					_bridgeBatches++;
					_bridgeCharacters += text.Length;
				}
				foreach (var completion in completions)
				{
					completion.TrySetResult();
				}
			}
			catch (Exception exception)
			{
				foreach (var completion in completions)
				{
					completion.TrySetException(exception);
				}
			}
		}
		finally
		{
			state.Gate.Release();
			lock (_sync)
			{
				if (!_disposed && state.Pending.Length > 0)
				{
					Schedule(state);
				}
			}
		}
	}

	private void FailPending(SessionState state, Exception exception)
	{
		TaskCompletionSource[] completions;
		lock (_sync)
		{
			completions = [.. state.Completions];
			state.Completions.Clear();
			state.Pending.Clear();
			state.FlushScheduled = false;
		}
		foreach (var completion in completions)
		{
			completion.TrySetException(exception);
		}
	}

	public ValueTask DisposeAsync()
	{
		SessionState[] states;
		lock (_sync)
		{
			if (_disposed)
			{
				return ValueTask.CompletedTask;
			}

			_disposed = true;
			states = [.. _sessions.Values];
			_sessions.Clear();
		}

		ObjectDisposedException exception = new(nameof(TerminalOutputBridgeBatcher));
		foreach (var state in states)
		{
			state.ScheduleVersion++;
			FailPending(state, exception);
		}
		return ValueTask.CompletedTask;
	}

	private sealed class SessionState(string sessionId)
	{
		public string SessionId { get; } = sessionId;
		public StringBuilder Pending { get; } = new();
		public List<TaskCompletionSource> Completions { get; } = [];
		public SemaphoreSlim Gate { get; } = new(1, 1);
		public bool FlushScheduled { get; set; }
		public int ScheduleVersion { get; set; }
	}
}