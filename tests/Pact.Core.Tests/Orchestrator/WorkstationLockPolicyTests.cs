using Pact.Core.Orchestrator;

namespace Pact.Core.Tests.Orchestrator;

public sealed class WorkstationLockPolicyTests
{
	[Test]
	public void Try_build_prompt_returns_the_lock_prompt_on_lock()
	{
		WorkstationLockPolicy.TryBuildPrompt(
			Ready(),
			locked: true,
			out var prompt).ShouldBeTrue();

		prompt.ShouldBe(Ready().LockPrompt);
	}

	[Test]
	public void Try_build_prompt_returns_the_unlock_prompt_on_unlock()
	{
		WorkstationLockPolicy.TryBuildPrompt(
			Ready(),
			locked: false,
			out var prompt).ShouldBeTrue();

		prompt.ShouldBe(Ready().UnlockPrompt);
	}

	[Test]
	public void Try_build_prompt_sends_nothing_when_lock_detection_is_off()
	{
		var record = Ready() with { LockDetectionEnabled = false };

		WorkstationLockPolicy.TryBuildPrompt(
			record,
			locked: true,
			out _).ShouldBeFalse();
	}

	[Test]
	public void Try_build_prompt_sends_nothing_when_the_orchestrator_is_disabled()
	{
		var record = Ready() with { Enabled = false };

		WorkstationLockPolicy.TryBuildPrompt(
			record,
			locked: true,
			out _).ShouldBeFalse();
	}

	[Test]
	public void Try_build_prompt_sends_nothing_when_not_provisioned()
	{
		var record = OrchestratorRecord.CreateDefault() with
		{
			Enabled = true,
			LockDetectionEnabled = true
		};

		WorkstationLockPolicy.TryBuildPrompt(
			record,
			locked: true,
			out _).ShouldBeFalse();
	}

	[Test]
	public void Try_build_prompt_sends_nothing_for_a_blank_prompt()
	{
		var record = Ready() with { LockPrompt = "  " };

		WorkstationLockPolicy.TryBuildPrompt(
			record,
			locked: true,
			out _).ShouldBeFalse();
	}

	private static OrchestratorRecord Ready() => OrchestratorRecord.CreateDefault() with
	{
		Enabled = true,
		LockDetectionEnabled = true,
		LaunchCommand = "hermes -p pact",
		WorkingDirectory = @"C:\repo",
		Credential = "token"
	};
}
