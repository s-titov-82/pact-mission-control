using Pact.App.Avalonia.Controllers;
using Pact.Core.Agents;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class SoftRestartSafetyPolicyTests
{
	[TestCase("project")]
	[TestCase("root")]
	[TestCase("reviewer")]
	[TestCase("orchestrator")]
	public void Every_busy_terminal_category_blocks_restart(string sessionId)
	{
		var policy = CreatePolicy(
			[sessionId],
			new Dictionary<string, TerminalClassifierDiagnostics>(StringComparer.Ordinal)
			{
				[sessionId] = Diagnostics(sessionId, activityInProgress: true)
			});

		var result = policy.Evaluate();

		result.CanRestart.ShouldBeFalse();
		result.Blockers.ShouldHaveSingleItem().Id.ShouldBe($"terminal:{sessionId}");
	}

	[Test]
	public void Nonterminal_scenario_blocks_even_when_every_terminal_is_idle()
	{
		var policy = CreatePolicy(
			["idle"],
			new Dictionary<string, TerminalClassifierDiagnostics>(StringComparer.Ordinal)
			{
				["idle"] = Diagnostics("idle", activityInProgress: false)
			},
			hasActiveScenario: true);

		var result = policy.Evaluate();

		result.CanRestart.ShouldBeFalse();
		result.Blockers.ShouldContain(blocker => blocker.Id == "scenario");
	}

	[Test]
	public void Shutdown_blocks_restart_but_browser_activity_is_not_part_of_policy()
	{
		var policy = CreatePolicy([], new Dictionary<string, TerminalClassifierDiagnostics>(), shutdownBegun: true);

		var result = policy.Evaluate();

		result.CanRestart.ShouldBeFalse();
		result.Blockers.ShouldHaveSingleItem().Id.ShouldBe("shutdown");
	}

	[Test]
	public void Idle_terminals_without_scenarios_or_shutdown_are_safe()
	{
		var policy = CreatePolicy(
			["project", "root"],
			new Dictionary<string, TerminalClassifierDiagnostics>(StringComparer.Ordinal)
			{
				["project"] = Diagnostics("project", activityInProgress: false),
				["root"] = Diagnostics("root", activityInProgress: false)
			});

		var result = policy.Evaluate();
		result.CanRestart.ShouldBeTrue();
		result.Blockers.ShouldBeEmpty();
	}

	private static SoftRestartSafetyPolicy CreatePolicy(
		IReadOnlyList<string> activeSessionIds,
		IReadOnlyDictionary<string, TerminalClassifierDiagnostics> diagnostics,
		bool hasActiveScenario = false,
		bool shutdownBegun = false) => new(
			() => activeSessionIds,
			() => diagnostics,
			() => hasActiveScenario,
			() => shutdownBegun);

	private static TerminalClassifierDiagnostics Diagnostics(
		string sessionId,
		bool activityInProgress) => new(
			sessionId,
			AgentKind.Pwsh,
			SessionStatus.Running,
			VerdictState: null,
			VerdictDescription: string.Empty,
			activityInProgress ? TerminalTabIndicator.Busy : TerminalTabIndicator.None,
			IndicatorDescription: string.Empty,
			PromptIsEmpty: true,
			InputRequested: false,
			StatusLine: string.Empty,
			activityInProgress,
			ActivityEpoch: 1,
			HasUnreadCompletion: false,
			Columns: null,
			Rows: null,
			LastClassificationAt: null);
}
