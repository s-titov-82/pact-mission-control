using Pact.Core.Updates;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;

namespace Pact.Infrastructure.Tests.Updates;

public sealed class UpdatePathPolicyTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public void Version_and_restart_id_have_deterministic_separate_directories()
	{
		AppPaths paths = new(_temporaryDirectory.Path);
		UpdatePathPolicy policy = new(paths);

		policy.GetPackageDirectory(new StableReleaseVersion(1, 2, 3)).ShouldBe(
			Path.Combine(paths.UpdatePackagesDirectory, "1.2.3"));
		policy.GetHandoffDirectory("restart_01-abc").ShouldBe(
			Path.Combine(paths.UpdateHandoffsDirectory, "restart_01-abc"));
	}

	[TestCase("../escape")]
	[TestCase("..\\escape")]
	[TestCase(".")]
	[TestCase("..")]
	[TestCase("")]
	[TestCase(" ")]
	[TestCase("abc ")]
	[TestCase("abc.")]
	public void Handoff_directory_rejects_nonopaque_path_segments(string restartId)
	{
		UpdatePathPolicy policy = new(new AppPaths(_temporaryDirectory.Path));

		Should.Throw<ArgumentException>(() => policy.GetHandoffDirectory(restartId));
	}

	[Test]
	public void EnsureOwnedPath_accepts_only_strict_descendants_of_updates_root()
	{
		AppPaths paths = new(_temporaryDirectory.Path);
		UpdatePathPolicy policy = new(paths);
		var owned = Path.Combine(paths.UpdatePackagesDirectory, "1.2.3", "setup.exe");
		var siblingPrefix = paths.UpdatesDirectory + "-other";
		var outside = Path.Combine(_temporaryDirectory.Path, "Settings", "projects.json");

		policy.EnsureOwnedPath(owned).ShouldBe(Path.GetFullPath(owned));
		Should.Throw<ArgumentException>(() => policy.EnsureOwnedPath(paths.UpdatesDirectory));
		Should.Throw<ArgumentException>(() => policy.EnsureOwnedPath(siblingPrefix));
		Should.Throw<ArgumentException>(() => policy.EnsureOwnedPath(outside));
		Should.Throw<ArgumentException>(() => policy.EnsureOwnedPath("relative.exe"));
	}
}
