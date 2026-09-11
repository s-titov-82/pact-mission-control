namespace Pact.Core.Updates;

/// <summary>Summarizes best-effort restoration after consuming one soft-restart ticket.</summary>
/// <param name="RestoredTerminalIds">Terminal ids started from the snapshot.</param>
/// <param name="ColdStartFallbacks">Stable per-terminal reasons that resume was unavailable.</param>
/// <param name="RestoredWebPageIds">Browser ids whose native hosts were reloaded.</param>
/// <param name="Failures">Sanitized failures for individual restoration items.</param>
/// <param name="OrchestratorRestored">Whether the prior live orchestrator was restored.</param>
/// <param name="SelectionRestored">Whether the prior selection was restored.</param>
/// <param name="UpdateFailureCategory">Helper category when Setup did not apply.</param>
public sealed record RestorationSummary(
	IReadOnlyList<string> RestoredTerminalIds,
	IReadOnlyList<string> ColdStartFallbacks,
	IReadOnlyList<string> RestoredWebPageIds,
	IReadOnlyList<string> Failures,
	bool OrchestratorRestored,
	bool SelectionRestored,
	string? UpdateFailureCategory);
