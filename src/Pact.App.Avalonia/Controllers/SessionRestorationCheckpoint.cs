using Pact.Core.Updates;

namespace Pact.App.Avalonia.Controllers;

/// <summary>Serializes immutable startup projections and freezes them before shutdown mutates runtime facts.</summary>
internal sealed class SessionRestorationCheckpoint(
	Func<SessionRestorationState> capture,
	Func<SessionRestorationState, Task> save,
	Action<Exception> reportFailure)
{
	private bool _enabled;
	private bool _frozen;
	private SessionRestorationState? _lastQueued;
	private Task _pending = Task.CompletedTask;

	public void Start()
	{
		_enabled = true;
		Publish();
	}
	public void Publish()
	{
		if (!_enabled || _frozen)
		{
			return;
		}
		var state = capture();
		if (_lastQueued is { } last && last.Selection == state.Selection
			&& last.OrchestratorWasRunning == state.OrchestratorWasRunning
			&& last.LiveTerminalIds.SequenceEqual(state.LiveTerminalIds)
			&& last.LoadedWebPageIds.SequenceEqual(state.LoadedWebPageIds)
			&& last.UnreadTerminalIds.SequenceEqual(state.UnreadTerminalIds))
		{
			return;
		}
		_lastQueued = state;
		_pending = SaveAfterAsync(_pending, state);
	}

	public Task FreezeAsync()
	{
		Publish();
		_frozen = true;
		return _pending;
	}

	private async Task SaveAfterAsync(Task previous, SessionRestorationState state)
	{
		await previous;
		try
		{
			await save(state);
		}
		catch (Exception exception)
		{
			if (ReferenceEquals(_lastQueued, state))
			{
				_lastQueued = null;
			}
			reportFailure(exception);
		}
	}
}
