namespace Pact.Core.Terminal;

/// <summary>
/// Identifies a started pseudo-console and the dimensions it was created with.
/// </summary>
/// <param name="Id">Session id this backend was started for.</param>
/// <param name="ProcessId">
/// Child process id, or <see langword="null"/> when the platform did not report one. Absence
/// means the id is unknown, not that the process failed to start.
/// </param>
/// <param name="Columns">Console width at start; later resizes are not reflected here.</param>
/// <param name="Rows">Console height at start; later resizes are not reflected here.</param>
public sealed record TerminalSession(
	string Id,
	int? ProcessId,
	int Columns,
	int Rows);