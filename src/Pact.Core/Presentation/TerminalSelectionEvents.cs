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
