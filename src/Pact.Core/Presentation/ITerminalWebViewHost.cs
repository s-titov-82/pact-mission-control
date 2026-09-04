namespace Pact.Core.Presentation;

/// <summary>
/// The xterm.js surface hosting every terminal session. One host owns all terminals and shows
/// one at a time, so session ids appear on nearly every member.
/// </summary>
public interface ITerminalWebViewHost
{
	/// <summary>
	/// Raised when the user types into a terminal. The payload is raw terminal input, already
	/// encoded by the browser side.
	/// </summary>
	event EventHandler<(string SessionId, string Data)>? InputReceived;

	/// <summary>Raised when a terminal's cell dimensions change and the backend must be resized.</summary>
	event EventHandler<(string SessionId, int Columns, int Rows)>? ResizeReceived;

	/// <summary>
	/// Reports the visible xterm viewport text. Stable snapshots arrive after the
	/// browser-side debounce interval; a snapshot with Stable=false is posted once
	/// per churn episode while the screen keeps repainting and is only trustworthy
	/// as busy evidence.
	/// </summary>
	event EventHandler<(string SessionId, string Text, bool Stable)>? ScreenSnapshotReceived;

	/// <summary>
	/// Raised when a terminal's selection appears or clears. Agents that own their own
	/// selection (Claude) never report one, because xterm never sees those drags.
	/// </summary>
	event EventHandler<(string SessionId, bool HasSelection)>? SelectionChanged;

	/// <summary>
	/// Raised only after an xterm mouse selection completes, with the terminal-local anchor for
	/// the completed gesture. Selection presence and removal continue to use <see cref="SelectionChanged"/>.
	/// </summary>
	event EventHandler<TerminalSelectionCompleted>? SelectionCompleted;

	/// <summary>
	/// Raised when the user presses or types inside a terminal, which ends any selection the
	/// session still shows. Agents that own the mouse keep their selection outside xterm and
	/// inside a native web view, so neither <see cref="SelectionChanged"/> nor an Avalonia light
	/// dismiss can report that the selection is gone.
	/// </summary>
	event EventHandler<string>? SelectionDismissed;

	/// <summary>Raised when the user activates an HTTP(S) hyperlink rendered by xterm.</summary>
	event EventHandler<(string SessionId, Uri Uri)>? LinkRequested;

	/// <summary>Raised when the user asks to paste, typically by right-clicking with no selection.</summary>
	event EventHandler? PasteRequested;

	/// <summary>
	/// Raised with a session-scoped request to place terminal text on the clipboard, including
	/// payloads decoded from an agent's OSC 52 copy request and its optional pointer anchor.
	/// </summary>
	event EventHandler<TerminalCopyRequest>? CopyRequested;

	/// <summary>Raised when the user activates the busy overlay's action button.</summary>
	event EventHandler? BusyOverlayActionRequested;

	/// <summary>
	/// Loads the terminal host page. Must complete before any terminal is created.
	/// </summary>
	Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken);

	/// <summary>
	/// Returns the terminal's current size in cells, used to size a backend at launch.
	/// </summary>
	(int Columns, int Rows) GetCurrentSize(string sessionId);

	/// <summary>Creates a terminal instance for the session without showing it.</summary>
	Task CreateTerminalAsync(string sessionId);

	/// <summary>
	/// Brings the session's terminal to the front. Other terminals stay alive and keep
	/// receiving output, so switching never interrupts a background agent.
	/// </summary>
	Task ShowTerminalAsync(string sessionId);

	/// <summary>Writes backend output into the session's terminal.</summary>
	Task WriteOutputAsync(string sessionId, string text);
	/// <summary>
	/// Starts a new submitted activity so browser-side stable-screen dedupe is
	/// scoped to that activity rather than the lifetime of the terminal.
	/// </summary>
	Task ResetSnapshotBaselineAsync(string sessionId);
	/// <summary>Destroys the session's terminal and releases its browser-side resources.</summary>
	Task DisposeTerminalAsync(string sessionId);

	/// <summary>
	/// Returns the visible terminal's selected text, or an empty string when nothing is
	/// selected. Empty is also returned when a mouse-tracking agent holds the selection
	/// internally, so callers fall back to the clipboard rather than treating it as an error.
	/// </summary>
	Task<string> GetSelectedTextAsync();

	/// <summary>Refits the visible terminal to its container, emitting a resize if the cell grid changed.</summary>
	Task FitAsync();

	/// <summary>Moves keyboard focus into the visible terminal.</summary>
	Task FocusAsync();

	/// <summary>
	/// Shows or hides the overlay covering the terminal during long operations.
	/// </summary>
	/// <param name="message">Text to display.</param>
	/// <param name="isVisible">Whether the overlay is shown.</param>
	/// <param name="dimBackground">Whether to dim the terminal behind the overlay.</param>
	/// <param name="actionLabel">
	/// Label for an escape-hatch button such as "Force close", or <see langword="null"/> for an
	/// overlay the user cannot dismiss.
	/// </param>
	Task SetBusyOverlayAsync(string message, bool isVisible, bool dimBackground, string? actionLabel = null);
}
