using Pact.Core.Updates;

namespace Pact.Core.Tests.Updates;

public sealed class UpdateStatusTests
{
	[Test]
	public void Status_preserves_the_complete_update_snapshot()
	{
		var running = new StableReleaseVersion(1, 2, 3);
		UpdateRelease release = new(
			new StableReleaseVersion(1, 3, 0),
			"v1.3.0",
			new Uri("https://github.example/releases/v1.3.0"),
			new UpdateAsset(
				"Pact-Mission-Control-Setup-1.3.0.exe",
				new Uri("https://github.example/setup.exe"),
				42),
			new UpdateAsset(
				"SHA256SUMS.txt",
				new Uri("https://github.example/SHA256SUMS.txt"),
				65));
		PreparedUpdatePackage package = new(
			release,
			@"D:\updates\setup.exe",
			new string('a', 64),
			"NotSigned");

		UpdateStatus status = new(
			UpdateState.ReadyToRestart,
			running,
			release,
			package,
			Error: null);

		status.State.ShouldBe(UpdateState.ReadyToRestart);
		status.RunningVersion.ShouldBe(running);
		status.AvailableRelease.ShouldBeSameAs(release);
		status.PreparedPackage.ShouldBeSameAs(package);
		status.Error.ShouldBeNull();
	}

	[Test]
	public void Update_states_cover_discovery_download_restart_and_failure()
	{
		Enum.GetValues<UpdateState>().ShouldBe([
			UpdateState.Idle,
			UpdateState.Checking,
			UpdateState.Available,
			UpdateState.Downloading,
			UpdateState.ReadyWaitingForSafeState,
			UpdateState.ReadyToRestart,
			UpdateState.Applying,
			UpdateState.Failed
		]);
	}
}
