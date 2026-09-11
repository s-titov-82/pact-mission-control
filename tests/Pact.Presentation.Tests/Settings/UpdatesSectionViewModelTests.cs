using Pact.Core.Updates;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.Updates;

namespace Pact.Presentation.Tests.Settings;

public sealed class UpdatesSectionViewModelTests
{
	[Test]
	public async Task Section_reports_running_version_and_tracks_coordinator_state()
	{
		UpdateRelease release = CreateRelease();
		await using UpdateCoordinator coordinator = new(
			new FixedReleaseClient(new GitHubReleaseResponse.Available(release)),
			TimeProvider.System,
			new StableReleaseVersion(1, 2, 3));
		using UpdatesSectionViewModel section = new(coordinator);

		section.RunningVersion.ShouldBe("1.2.3");
		section.StateText.ShouldBe("Ready to check for updates.");
		section.CanCheck.ShouldBeTrue();
		section.SupportsFileOperations.ShouldBeFalse();

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Manual, CancellationToken.None);

		section.StateText.ShouldContain("1.3.0");
		section.CanCheck.ShouldBeTrue();
		section.CanOpenReleaseNotes.ShouldBeTrue();
	}

	[Test]
	public async Task Section_raises_explicit_actions_and_never_saves_a_file()
	{
		await using UpdateCoordinator coordinator = new(
			new FixedReleaseClient(new GitHubReleaseResponse.NoUpdate()),
			TimeProvider.System,
			new StableReleaseVersion(1, 2, 3));
		using UpdatesSectionViewModel section = new(coordinator);
		var checks = 0;
		var notes = 0;
		section.CheckRequested += (_, _) => checks++;
		section.OpenReleaseNotesRequested += (_, _) => notes++;

		section.RequestCheck();
		section.RequestOpenReleaseNotes();

		checks.ShouldBe(1);
		notes.ShouldBe(0);
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
	}

	[Test]
	public async Task Section_exposes_restart_action_only_for_a_safe_prepared_package()
	{
		var release = CreateRelease();
		PreparedUpdatePackage package = new(
			release,
			@"C:\updates\setup.exe",
			new string('a', 64),
			"NotSigned");
		await using UpdateCoordinator coordinator = new(
			new FixedReleaseClient(new GitHubReleaseResponse.Available(release)),
			TimeProvider.System,
			new StableReleaseVersion(1, 2, 3),
			new FixedPackageStore(package));
		using UpdatesSectionViewModel section = new(coordinator);
		var requests = 0;
		section.RestartAndUpdateRequested += (_, _) => requests++;
		await coordinator.CheckNowAsync(UpdateCheckOrigin.Manual, CancellationToken.None);
		await coordinator.PrepareUpdateAsync(release, null, CancellationToken.None);

		section.IsRestartActionVisible.ShouldBeFalse();
		section.RequestRestartAndUpdate();
		requests.ShouldBe(0);

		coordinator.UpdateRestartSafety(canRestart: true);

		section.IsRestartActionVisible.ShouldBeTrue();
		section.RestartActionText.ShouldBe("Restart and update");
		section.RequestRestartAndUpdate();
		requests.ShouldBe(1);
	}

	private static UpdateRelease CreateRelease()
	{
		StableReleaseVersion version = new(1, 3, 0);
		return new UpdateRelease(
			version,
			"v1.3.0",
			new Uri("https://github.com/s-titov-82/pact-mission-control/releases/tag/v1.3.0"),
			new UpdateAsset("setup.exe", new Uri("https://github.com/setup.exe"), 42),
			new UpdateAsset("SHA256SUMS.txt", new Uri("https://github.com/SHA256SUMS.txt"), 65));
	}

	private sealed class FixedReleaseClient(GitHubReleaseResponse response) : IGitHubReleaseClient
	{
		public Task<GitHubReleaseResponse> GetLatestStableAsync(
			StableReleaseVersion runningVersion,
			CancellationToken cancellationToken) => Task.FromResult(response);
	}

	private sealed class FixedPackageStore(PreparedUpdatePackage package) : IUpdatePackageStore
	{
		public Task<PreparedUpdatePackage?> TryGetVerifiedAsync(
			UpdateRelease release,
			CancellationToken cancellationToken) => Task.FromResult<PreparedUpdatePackage?>(package);

		public Task<PreparedUpdatePackage> DownloadAndVerifyAsync(
			UpdateRelease release,
			IProgress<long>? progress,
			CancellationToken cancellationToken) => Task.FromResult(package);
	}
}
