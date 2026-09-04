namespace Pact.Core.Sessions;

/// <summary>
/// The kind of evidence fed into <see cref="TerminalTabStatusEngine"/>. Tab busy/idle and
/// unread state are derived only from these observations, never asserted directly.
/// </summary>
public enum TerminalTabEventKind
{
	/// <summary>The session's process lifecycle state changed.</summary>
	LifecycleChanged,

	/// <summary>The user selected or deselected this tab.</summary>
	SelectionChanged,

	/// <summary>Window visibility and activation were replaced as one fact set.</summary>
	WindowFactsChanged,

	/// <summary>A process was launched for the session.</summary>
	SessionStarted,

	/// <summary>The user sent input to the session.</summary>
	UserInput,

	/// <summary>
	/// A stable visible-screen snapshot was classified for activity evidence.
	/// </summary>
	ScreenSnapshot,

	/// <summary>The terminal viewport was resized or scrolled.</summary>
	ViewportChanged
}