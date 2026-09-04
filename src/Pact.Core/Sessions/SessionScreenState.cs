namespace Pact.Core.Sessions;

/// <summary>The retained view of one terminal session's stable screen.</summary>
/// <param name="Screen">Full text of the last stable screen.</param>
/// <param name="LastMessage">
/// The agent's last recognized message; empty when none has ever been recognized.
/// </param>
/// <param name="LastMessageIsCurrent">
/// Whether <paramref name="LastMessage"/> came from <paramref name="Screen"/>. When
/// <see langword="false"/>, callers must identify it as an older retained message.
/// </param>
/// <param name="InputRequested">Whether the agent is waiting for a human answer.</param>
/// <param name="StatusLine">Description of the pending input request, or an empty string.</param>
/// <param name="PromptIsEmpty">
/// Whether the visible composer is blank; <see langword="null"/> when the screen cannot say.
/// </param>
/// <param name="ActivityEpoch">Monotonic activity cycle observed for the session.</param>
/// <param name="IsBusy">Whether the session is in an active work cycle.</param>
public sealed record SessionScreenState(
	string Screen,
	string LastMessage,
	bool LastMessageIsCurrent,
	bool InputRequested = false,
	string StatusLine = "",
	bool? PromptIsEmpty = null,
	long ActivityEpoch = 0,
	bool IsBusy = false);
