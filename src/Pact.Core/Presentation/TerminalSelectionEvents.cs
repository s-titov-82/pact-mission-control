namespace Pact.Core.Presentation;

/// <summary>
/// Identifies the container-local CSS pixel position and xterm selection revision that
/// completed a terminal selection. The revision is scoped to one terminal instance.
/// </summary>
public sealed record TerminalSelectionAnchor(double X, double Y, long Revision);

/// <summary>
/// Publishes a completed xterm mouse selection for one terminal session without exposing an
/// Avalonia-specific input type across the presentation boundary.
/// </summary>
public sealed record TerminalSelectionCompleted(
	string SessionId,
	TerminalSelectionAnchor Anchor);

/// <summary>
/// Requests a clipboard copy from one terminal session. OSC 52 copies can optionally retain
/// the container-local CSS pixel selection anchor from the pointer gesture that preceded them.
/// </summary>
public sealed record TerminalCopyRequest(
	string SessionId,
	string Text,
	TerminalSelectionAnchor? Anchor);

/// <summary>
/// Reports a mouse release inside a terminal whose application tracks the mouse. Such an agent
/// keeps its selection outside xterm and may copy it straight to the native clipboard, so the
/// release point is the only anchor the host learns for that copy.
/// </summary>
public sealed record TerminalMouseReleased(
	string SessionId,
	TerminalSelectionAnchor Anchor);
