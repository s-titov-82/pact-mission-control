using Pact.Core.Orchestrator;

namespace Pact.Core.Tests.Orchestrator;

public sealed class OrchestratorRecordTests
{
	[Test]
	public void Default_record_is_disabled()
	{
		var record = OrchestratorRecord.CreateDefault();

		record.Enabled.ShouldBeFalse();
		record.LockDetectionEnabled.ShouldBeFalse();
	}

	[Test]
	public void Default_record_supplies_lock_and_unlock_prompts()
	{
		var record = OrchestratorRecord.CreateDefault();

		record.LockPrompt.ShouldNotBeNullOrWhiteSpace();
		record.UnlockPrompt.ShouldNotBeNullOrWhiteSpace();
	}

	[Test]
	public void Default_record_is_not_provisioned()
	{
		OrchestratorRecord.CreateDefault().IsProvisioned.ShouldBeFalse();
	}

	[Test]
	public void Command_working_directory_and_credential_provision_the_record()
	{
		var record = OrchestratorRecord.CreateDefault() with
		{
			LaunchCommand = "hermes -p pact",
			WorkingDirectory = @"C:\repo",
			Credential = "token"
		};

		record.IsProvisioned.ShouldBeTrue();
	}
}
