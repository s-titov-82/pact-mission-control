using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Controllers;

/// <summary>Identifies one current fact that prevents a graceful soft restart.</summary>
/// <param name="Id">Stable blocker key suitable for UI reconciliation.</param>
/// <param name="Description">Bounded user-facing explanation.</param>
internal sealed record SoftRestartBlocker(string Id, string Description);

/// <summary>Reports whether the cockpit is safe to interrupt at one instant.</summary>
/// <param name="CanRestart">Whether no blocker was observed.</param>
/// <param name="Blockers">The complete deterministic blocker snapshot.</param>
internal sealed record SoftRestartSafetyResult(
	bool CanRestart,
	IReadOnlyList<SoftRestartBlocker> Blockers);

/// <summary>
/// Evaluates only terminal classifier, scenario lifecycle, and shutdown facts. Browser work
/// intentionally does not participate in restart safety.
/// </summary>
internal sealed class SoftRestartSafetyPolicy
{
	private readonly Func<IReadOnlyList<string>> _getActiveSessionIds;
	private readonly Func<IReadOnlyDictionary<string, TerminalClassifierDiagnostics>>
		_getDiagnostics;
	private readonly Func<bool> _hasActiveScenario;
	private readonly Func<bool> _shutdownBegun;

	public SoftRestartSafetyPolicy(
		Func<IReadOnlyList<string>> getActiveSessionIds,
		Func<IReadOnlyDictionary<string, TerminalClassifierDiagnostics>> getDiagnostics,
		Func<bool> hasActiveScenario,
		Func<bool> shutdownBegun)
	{
		_getActiveSessionIds = getActiveSessionIds
			?? throw new ArgumentNullException(nameof(getActiveSessionIds));
		_getDiagnostics = getDiagnostics
			?? throw new ArgumentNullException(nameof(getDiagnostics));
		_hasActiveScenario = hasActiveScenario
			?? throw new ArgumentNullException(nameof(hasActiveScenario));
		_shutdownBegun = shutdownBegun
			?? throw new ArgumentNullException(nameof(shutdownBegun));
	}

	public SoftRestartSafetyResult Evaluate()
	{
		var diagnostics = _getDiagnostics();
		List<SoftRestartBlocker> blockers = [];
		foreach (var sessionId in _getActiveSessionIds()
			.Distinct(StringComparer.Ordinal)
			.Order(StringComparer.Ordinal))
		{
			if (!diagnostics.TryGetValue(sessionId, out var sessionDiagnostics))
			{
				blockers.Add(new SoftRestartBlocker(
					$"terminal-status:{sessionId}",
					$"Terminal '{sessionId}' status is not ready."));
			}
			else if (sessionDiagnostics.ActivityInProgress)
			{
				blockers.Add(new SoftRestartBlocker(
					$"terminal:{sessionId}",
					$"Terminal '{sessionId}' is busy."));
			}
		}

		if (_hasActiveScenario())
		{
			blockers.Add(new SoftRestartBlocker(
				"scenario",
				"A review scenario is still active or paused."));
		}
		if (_shutdownBegun())
		{
			blockers.Add(new SoftRestartBlocker(
				"shutdown",
				"Application shutdown has already begun."));
		}

		return new SoftRestartSafetyResult(blockers.Count == 0, blockers);
	}
}
