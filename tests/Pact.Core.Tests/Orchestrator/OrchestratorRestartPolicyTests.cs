using Pact.Core.Orchestrator;

namespace Pact.Core.Tests.Orchestrator;

public sealed class OrchestratorRestartPolicyTests
{
	[Test]
	public void Next_delay_restarts_immediately_after_a_long_run()
	{
		OrchestratorRestartPolicy.NextDelay(
			consecutiveFailures: 0,
			ranFor: TimeSpan.FromHours(3)).ShouldBe(TimeSpan.Zero);
	}

	[Test]
	public void Next_delay_backs_off_after_repeated_immediate_failures()
	{
		var first = OrchestratorRestartPolicy.NextDelay(
			consecutiveFailures: 1,
			ranFor: TimeSpan.FromSeconds(1));
		var second = OrchestratorRestartPolicy.NextDelay(
			consecutiveFailures: 2,
			ranFor: TimeSpan.FromSeconds(1));

		second!.Value.ShouldBeGreaterThan(first!.Value);
	}

	[Test]
	public void Next_delay_gives_up_after_the_failure_budget()
	{
		OrchestratorRestartPolicy.NextDelay(
			consecutiveFailures: 6,
			ranFor: TimeSpan.FromSeconds(1)).ShouldBeNull();
	}

	[Test]
	public void Next_delay_resets_the_counter_when_the_slot_ran_long_enough()
	{
		OrchestratorRestartPolicy.NextDelay(
			consecutiveFailures: 6,
			ranFor: TimeSpan.FromMinutes(30)).ShouldBe(TimeSpan.Zero);
	}
}
