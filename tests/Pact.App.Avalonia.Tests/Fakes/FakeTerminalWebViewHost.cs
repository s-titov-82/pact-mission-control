using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Core.Presentation;

namespace Pact.App.Avalonia.Tests.Fakes;

internal sealed class FakeTerminalWebViewHost : ITerminalWebViewHost
{
	public TaskCompletionSource<bool>? InitializeBlocker { get; set; }
	public TaskCompletionSource InitializeStarted { get; } =
		new(TaskCreationOptions.RunContinuationsAsynchronously);
	public TaskCompletionSource<string>? SelectedTextBlocker { get; set; }
	public List<string> CreatedSessions { get; } = [];
	public List<string> ShownSessions { get; } = [];
	public List<string> DisposedSessions { get; } = [];
	public List<(string SessionId, string Text)> WrittenOutput { get; } = [];
	public List<string> SnapshotBaselineResetSessions { get; } = [];
	public int FocusCallCount { get; private set; }
	public List<(string Message, bool IsVisible, bool DimBackground, string? ActionLabel)> BusyOverlayCalls { get; } = [];
	public Dictionary<string, TaskCompletionSource<bool>> ShowBlockers { get; } = new(StringComparer.Ordinal);
	public Dictionary<string, TaskCompletionSource> ShowStarted { get; } = new(StringComparer.Ordinal);
	public event EventHandler<(string SessionId, string Data)>? InputReceived;
	public event EventHandler<(string SessionId, int Columns, int Rows)>? ResizeReceived;
	public event EventHandler<(string SessionId, TerminalScreenSnapshot Snapshot)>? ScreenSnapshotReceived;
	public event EventHandler<(string SessionId, bool HasSelection)>? SelectionChanged;
	public event EventHandler<TerminalSelectionCompleted>? SelectionCompleted;
	public event EventHandler<string>? SelectionDismissed;
	public event EventHandler<TerminalMouseReleased>? AgentMouseReleased;
	public event EventHandler<(string SessionId, Uri Uri)>? LinkRequested;
	public event EventHandler? PasteRequested;
	public event EventHandler<TerminalCopyRequest>? CopyRequested;
	public event EventHandler? BusyOverlayActionRequested;

	public Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken)
	{
		InitializeStarted.TrySetResult();
		return InitializeBlocker?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
	}
	public (int Columns, int Rows) GetCurrentSize(string sessionId) => (120, 36);
	public void RaiseResize(string sessionId, int columns, int rows) =>
		ResizeReceived?.Invoke(this, (sessionId, columns, rows));
	public Task CreateTerminalAsync(string sessionId)
	{
		CreatedSessions.Add(sessionId);
		return Task.CompletedTask;
	}
	public async Task ShowTerminalAsync(string sessionId)
	{
		ShownSessions.Add(sessionId);
		if (ShowStarted.TryGetValue(sessionId, out var started))
		{
			started.TrySetResult();
		}

		if (ShowBlockers.TryGetValue(sessionId, out var blocker))
		{
			await blocker.Task;
		}
	}
	public Task WriteOutputAsync(string sessionId, string text)
	{
		WrittenOutput.Add((sessionId, text));
		return Task.CompletedTask;
	}
	// By default the capture shows the agent's empty composer, as after a submitted prompt.
	public Func<string, TerminalScreenSnapshot?>? CaptureScreen { get; set; }

	// Lets the default capture draw the right agent's composer; a fixture maps ids to kinds.
	public Func<string, AgentKind?> KindOf { get; set; } = static _ => null;

	public static TerminalScreenSnapshot ClaudeEmptyComposer { get; } = TerminalScreenSnapshot.FromRows(
		[
			new("Worked for 1s", false, []),
			new(ClaudeRule, false, []),
			new("❯ ", false, []),
			new(ClaudeRule, false, []),
			new("  GPT-6.1-Sol medium · main · Context 30% used", false, [])
		],
		80,
		true);

	private const string ClaudeRule = "────────────────────────────────────────────────";

	public static TerminalScreenSnapshot EmptyComposer { get; } = TerminalScreenSnapshot.FromRows(
		[
			new("Worked for 1s", false, []),
			new("› ", false, []),
			new("  GPT-6.1-Sol medium · main · Context 30% used", false, [])
		],
		80,
		true);

	public Task<TerminalScreenSnapshot?> CaptureScreenAsync(string sessionId, CancellationToken cancellationToken) =>
		Task.FromResult(CaptureScreen is { } capture
			? capture(sessionId)
			: KindOf(sessionId) == AgentKind.Claude ? ClaudeEmptyComposer : EmptyComposer);

	public Task ResetSnapshotBaselineAsync(string sessionId)
	{
		SnapshotBaselineResetSessions.Add(sessionId);
		return Task.CompletedTask;
	}
	public Task DisposeTerminalAsync(string sessionId)
	{
		DisposedSessions.Add(sessionId);
		return Task.CompletedTask;
	}
	public Task<string> GetSelectedTextAsync() => SelectedTextBlocker?.Task ?? Task.FromResult(string.Empty);
	public Task FitAsync() => Task.CompletedTask;
	public Task FocusAsync()
	{
		FocusCallCount++;
		return Task.CompletedTask;
	}
	public Task SetBusyOverlayAsync(
		string message,
		bool isVisible,
		bool dimBackground,
		string? actionLabel = null)
	{
		BusyOverlayCalls.Add((message, isVisible, dimBackground, actionLabel));
		return Task.CompletedTask;
	}

	public void RaiseBusyOverlayActionRequested() => BusyOverlayActionRequested?.Invoke(this, EventArgs.Empty);
	public void RaiseInputReceived(string sessionId, string data) =>
		InputReceived?.Invoke(this, (sessionId, data));
	public void RaiseScreenSnapshotReceived(string sessionId, string text, bool stable = true) =>
		ScreenSnapshotReceived?.Invoke(this, (sessionId, TerminalScreenSnapshot.FromText(text, stable)));
	public void RaiseSelectionChanged(string sessionId, bool hasSelection) =>
		SelectionChanged?.Invoke(this, (sessionId, hasSelection));
	public void RaiseSelectionCompleted(TerminalSelectionCompleted completion) =>
		SelectionCompleted?.Invoke(this, completion);
	public void RaiseSelectionDismissed(string sessionId) =>
		SelectionDismissed?.Invoke(this, sessionId);
	public void RaiseLinkRequested(string sessionId, Uri uri) =>
		LinkRequested?.Invoke(this, (sessionId, uri));
	public void RaiseCopyRequested(TerminalCopyRequest request) => CopyRequested?.Invoke(this, request);
	public void RaiseAgentMouseReleased(TerminalMouseReleased released) =>
		AgentMouseReleased?.Invoke(this, released);
	public void RaisePasteRequested() => PasteRequested?.Invoke(this, EventArgs.Empty);
}
