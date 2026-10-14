using System.Collections.Concurrent;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Web;

/// <summary>
/// Screen captures awaiting the web view's answer. A request that was cancelled or timed out
/// is removed, so its late answer is dropped instead of completing a newer request.
/// </summary>
internal sealed class ScreenCaptureRequests
{
	private readonly ConcurrentDictionary<long, TaskCompletionSource<TerminalScreenSnapshot?>> _pending = new();
	private long _nextId;

	public (long Id, Task<TerminalScreenSnapshot?> Answer) Register()
	{
		var id = Interlocked.Increment(ref _nextId);
		TaskCompletionSource<TerminalScreenSnapshot?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = answer;
		return (id, answer.Task);
	}

	public bool Complete(long id, TerminalScreenSnapshot? snapshot) =>
		_pending.TryRemove(id, out var answer) && answer.TrySetResult(snapshot);

	public void Remove(long id) => _pending.TryRemove(id, out _);
}
