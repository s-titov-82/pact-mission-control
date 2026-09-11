using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Platform;
using Pact.Core.Updates;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Updates;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaUpdateControllerTests
{
	private static readonly string TestRoot = Path.Combine(
		Path.GetTempPath(),
		"pact-update-controller-tests");
	[Test]
	public async Task Available_release_shows_one_dialog_and_later_defers_it()
	{
		var release = CreateRelease();
		await using UpdateCoordinator coordinator = CreateCoordinator(
			new GitHubReleaseResponse.Available(release));
		ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		RecordingLauncher launcher = new();
		var dialogs = 0;
		AvaloniaUpdateController controller = CreateController(coordinator, launcher, tasks);
		controller.ConfigureHost(
			shownRelease =>
			{
				shownRelease.ShouldBeSameAs(release);
				dialogs++;
				return Task.FromResult(UpdateAvailableDialogResult.Later);
			},
			static _ => Task.CompletedTask);
		await controller.StartAsync(CancellationToken.None);

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		await tasks.WaitForIdleAsync();
		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		await tasks.WaitForIdleAsync();

		dialogs.ShouldBe(1);
		coordinator.Status.State.ShouldBe(UpdateState.Idle);
		controller.Stop();
	}

	[Test]
	public async Task Automatic_failure_is_logged_without_showing_a_modal_message()
	{
		await using UpdateCoordinator coordinator = new(
			new ThrowingReleaseClient(),
			TimeProvider.System,
			new StableReleaseVersion(1, 2, 3));
		ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		List<string> logs = [];
		var messages = 0;
		AvaloniaUpdateController controller = new(
			coordinator,
			new RecordingLauncher(),
			new UpdatePathPolicy(new AppPaths(TestRoot)),
			new Fakes.ImmediateUiTaskDispatcher(),
			tasks,
			(phase, _) =>
			{
				logs.Add(phase);
				return Task.CompletedTask;
			});
		controller.ConfigureHost(
			static _ => Task.FromResult(UpdateAvailableDialogResult.Later),
			_ =>
			{
				messages++;
				return Task.CompletedTask;
			});
		await controller.StartAsync(CancellationToken.None);

		await coordinator.CheckNowAsync(UpdateCheckOrigin.Automatic, CancellationToken.None);
		await tasks.WaitForIdleAsync();

		logs.ShouldHaveSingleItem().ShouldContain("update check", Case.Insensitive);
		messages.ShouldBe(0);
		controller.Stop();
	}

	[Test]
	public async Task Manual_no_update_rate_limit_and_network_failure_are_reported()
	{
		await AssertManualMessageAsync(
			new GitHubReleaseResponse.NoUpdate(),
			"up to date");
		await AssertManualMessageAsync(
			new GitHubReleaseResponse.RateLimited(
				DateTimeOffset.UtcNow.AddMinutes(10),
				"rate limit"),
			"try again after");
		await AssertManualFailureMessageAsync("could not check");
	}

	[Test]
	public async Task Release_notes_use_external_launcher_only_for_https()
	{
		foreach (var scheme in new[] { "https", "http" })
		{
			var release = CreateRelease(scheme);
			await using UpdateCoordinator coordinator = CreateCoordinator(
				new GitHubReleaseResponse.Available(release));
			ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
			RecordingLauncher launcher = new();
			Queue<UpdateAvailableDialogResult> results = new([
				UpdateAvailableDialogResult.OpenReleaseNotes,
				UpdateAvailableDialogResult.Later
			]);
			AvaloniaUpdateController controller = CreateController(coordinator, launcher, tasks);
			controller.ConfigureHost(
				_ => Task.FromResult(results.Dequeue()),
				static _ => Task.CompletedTask);
			await controller.StartAsync(CancellationToken.None);

			await controller.CheckNowAsync(CancellationToken.None);
			await tasks.WaitForIdleAsync();

			launcher.OpenedUris.Count.ShouldBe(scheme == "https" ? 1 : 0);
			controller.Stop();
		}
	}

	[Test]
	public async Task Download_prepares_verified_package_without_running_it_and_folder_can_be_opened()
	{
		var release = CreateRelease();
		var setupPath = Path.Combine(
			new AppPaths(TestRoot).UpdatePackagesDirectory,
			release.Version.ToString(),
			release.Setup.Name);
		PreparedUpdatePackage package = new(
			release,
			setupPath,
			new string('a', 64),
			"NotSigned");
		RecordingPackageStore packages = new(package);
		await using UpdateCoordinator coordinator = CreateCoordinator(
			new GitHubReleaseResponse.Available(release),
			packages);
		ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		List<string> messages = [];
		RecordingLauncher launcher = new();
		AvaloniaUpdateController controller = CreateController(
			coordinator,
			launcher,
			tasks);
		controller.ConfigureHost(
			static _ => Task.FromResult(UpdateAvailableDialogResult.Download),
			message =>
			{
				messages.Add(message);
				return Task.CompletedTask;
			});
		await controller.StartAsync(CancellationToken.None);

		await controller.CheckNowAsync(CancellationToken.None);
		await tasks.WaitForIdleAsync();

		messages.ShouldHaveSingleItem().ShouldContain("downloaded and verified");
		packages.DownloadCount.ShouldBe(1);
		coordinator.Status.State.ShouldBe(UpdateState.ReadyWaitingForSafeState);
		await controller.OpenContainingFolderAsync(CancellationToken.None);
		launcher.OpenedFiles.ShouldBe([Path.GetDirectoryName(setupPath)!]);
		controller.Stop();
	}

	private static async Task AssertManualMessageAsync(
		GitHubReleaseResponse response,
		string expected)
	{
		await using UpdateCoordinator coordinator = CreateCoordinator(response);
		ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		List<string> messages = [];
		AvaloniaUpdateController controller = CreateController(
			coordinator,
			new RecordingLauncher(),
			tasks);
		controller.ConfigureHost(
			static _ => Task.FromResult(UpdateAvailableDialogResult.Later),
			message =>
			{
				messages.Add(message);
				return Task.CompletedTask;
			});
		await controller.StartAsync(CancellationToken.None);

		await controller.CheckNowAsync(CancellationToken.None);

		messages.ShouldHaveSingleItem().ShouldContain(expected, Case.Insensitive);
		controller.Stop();
	}

	private static async Task AssertManualFailureMessageAsync(string expected)
	{
		await using UpdateCoordinator coordinator = new(
			new ThrowingReleaseClient(),
			TimeProvider.System,
			new StableReleaseVersion(1, 2, 3));
		ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		List<string> messages = [];
		AvaloniaUpdateController controller = CreateController(
			coordinator,
			new RecordingLauncher(),
			tasks);
		controller.ConfigureHost(
			static _ => Task.FromResult(UpdateAvailableDialogResult.Later),
			message =>
			{
				messages.Add(message);
				return Task.CompletedTask;
			});
		await controller.StartAsync(CancellationToken.None);

		await controller.CheckNowAsync(CancellationToken.None);

		messages.ShouldHaveSingleItem().ShouldContain(expected, Case.Insensitive);
		controller.Stop();
	}

	private static AvaloniaUpdateController CreateController(
		UpdateCoordinator coordinator,
		IExternalLauncher launcher,
		ObservedTaskGroup tasks) => new(
			coordinator,
			launcher,
			new UpdatePathPolicy(new AppPaths(TestRoot)),
			new Fakes.ImmediateUiTaskDispatcher(),
			tasks,
			static (_, _) => Task.CompletedTask);

	private static UpdateCoordinator CreateCoordinator(
		GitHubReleaseResponse response,
		IUpdatePackageStore? packageStore = null) => new(
		new FixedReleaseClient(response),
		TimeProvider.System,
		new StableReleaseVersion(1, 2, 3),
		packageStore);

	private static UpdateRelease CreateRelease(string scheme = "https") => new(
		new StableReleaseVersion(1, 3, 0),
		"v1.3.0",
		new Uri($"{scheme}://github.com/s-titov-82/pact-mission-control/releases/tag/v1.3.0"),
		new UpdateAsset("setup.exe", new Uri("https://github.com/setup.exe"), 42),
		new UpdateAsset("SHA256SUMS.txt", new Uri("https://github.com/SHA256SUMS.txt"), 65));

	private sealed class FixedReleaseClient(GitHubReleaseResponse response) : IGitHubReleaseClient
	{
		public Task<GitHubReleaseResponse> GetLatestStableAsync(
			StableReleaseVersion runningVersion,
			CancellationToken cancellationToken) => Task.FromResult(response);
	}

	private sealed class ThrowingReleaseClient : IGitHubReleaseClient
	{
		public Task<GitHubReleaseResponse> GetLatestStableAsync(
			StableReleaseVersion runningVersion,
			CancellationToken cancellationToken) =>
			Task.FromException<GitHubReleaseResponse>(new HttpRequestException("offline"));
	}

	private sealed class RecordingPackageStore(PreparedUpdatePackage package)
		: IUpdatePackageStore
	{
		public int DownloadCount { get; private set; }

		public Task<PreparedUpdatePackage?> TryGetVerifiedAsync(
			UpdateRelease release,
			CancellationToken cancellationToken) => Task.FromResult<PreparedUpdatePackage?>(null);

		public Task<PreparedUpdatePackage> DownloadAndVerifyAsync(
			UpdateRelease release,
			IProgress<long>? progress,
			CancellationToken cancellationToken)
		{
			DownloadCount++;
			return Task.FromResult(package);
		}
	}

	private sealed class RecordingLauncher : IExternalLauncher
	{
		public List<Uri> OpenedUris { get; } = [];
		public List<string> OpenedFiles { get; } = [];
		public Task OpenFileAsync(string path)
		{
			OpenedFiles.Add(path);
			return Task.CompletedTask;
		}
		public Task OpenHttpUriAsync(Uri uri)
		{
			OpenedUris.Add(uri);
			return Task.CompletedTask;
		}
	}
}
