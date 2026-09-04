using Pact.Core.Scenarios;

namespace Pact.Core.Tests.Scenarios;

public sealed class ScenarioCompletionNoticeTests
{
	[Test]
	public void TryBuild_DescribesConsensus()
	{
		ScenarioCompletionNotice.TryBuild(
			ScenarioRunState.Completed,
			3,
			out var message).ShouldBeTrue();

		message.ShouldContain("approved");
		message.ShouldContain("3");
	}

	[Test]
	public void TryBuild_DescribesExhaustedIterations()
	{
		ScenarioCompletionNotice.TryBuild(
			ScenarioRunState.MaxIterationsReached,
			5,
			out var message).ShouldBeTrue();

		message.ShouldContain("without agreement");
	}

	[Test]
	public void TryBuild_DescribesFailure()
	{
		ScenarioCompletionNotice.TryBuild(
			ScenarioRunState.Failed,
			2,
			out var message).ShouldBeTrue();

		message.ShouldContain("failed");
	}

	[Test]
	public void TryBuild_DescribesAbort()
	{
		ScenarioCompletionNotice.TryBuild(
			ScenarioRunState.Aborted,
			1,
			out var message).ShouldBeTrue();

		message.ShouldContain("stopped");
	}

	[Test]
	public void TryBuild_IsSingleLine()
	{
		ScenarioCompletionNotice.TryBuild(
			ScenarioRunState.Completed,
			1,
			out var message);

		message.ShouldNotContain("\n");
	}

	[TestCase(ScenarioRunState.Running)]
	[TestCase(ScenarioRunState.Paused)]
	[TestCase(ScenarioRunState.StoppingAfterStep)]
	public void TryBuild_RefusesNonTerminalState(ScenarioRunState state)
	{
		ScenarioCompletionNotice.TryBuild(state, 1, out var message).ShouldBeFalse();

		message.ShouldBeEmpty();
	}
}
