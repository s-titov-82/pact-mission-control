using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>Describes the command actually selected for one terminal start attempt.</summary>
/// <param name="CommandLine">The saved command to resolve and launch.</param>
/// <param name="Mode">Whether this is a normal or concrete conversation-resume start.</param>
/// <param name="FellBackToColdStart">Whether a requested resume became a normal start.</param>
/// <param name="FallbackReason">Stable summary category for a cold-start fallback.</param>
public sealed record SessionStartPlan(
	string CommandLine,
	TerminalStartMode Mode,
	bool FellBackToColdStart,
	string? FallbackReason);
