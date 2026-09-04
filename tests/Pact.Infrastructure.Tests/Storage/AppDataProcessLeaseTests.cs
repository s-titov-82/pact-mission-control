using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class AppDataProcessLeaseTests
{
	[Test]
	public void TryAcquire_SameNormalizedRoot_AllowsOnlyOneOwner()
	{
		using var root = TemporaryDirectory.Create();

		AppDataProcessLease.TryAcquire(root.Path, out var first).ShouldBeTrue();
		using (first)
		{
			AppDataProcessLease.TryAcquire(
				root.Path + Path.DirectorySeparatorChar,
				out _).ShouldBeFalse();
		}

		AppDataProcessLease.TryAcquire(root.Path, out var second).ShouldBeTrue();
		second!.Dispose();
	}

	[Test]
	public void GetMutexName_PathCaseAndTrailingSeparator_AreEquivalentOnWindows()
	{
		var root = Path.Combine(Path.GetTempPath(), "PactLease");

		AppDataProcessLease.GetMutexName(
				root.ToLowerInvariant() + Path.DirectorySeparatorChar).ShouldBe(AppDataProcessLease.GetMutexName(root.ToUpperInvariant()));
	}

	[Test]
	public void GetMutexName_UsesCrossSessionNamespace()
	{
		using var root = TemporaryDirectory.Create();

		AppDataProcessLease.GetMutexName(root.Path).ShouldStartWith("Global\\Pact.DataRoot.");
	}

	[Test]
	public async Task Acquire_RejectsARootHeldByAnotherProcess()
	{
		using var root = TemporaryDirectory.Create();
		var dataRoot = Path.Combine(root.Path, "data");
		var readyPath = Path.Combine(root.Path, "ready");
		Directory.CreateDirectory(dataRoot);
		await using var holder =
			await ProfileToolLeaseHolder.StartAsync(
				dataRoot,
				readyPath,
				TimeSpan.FromSeconds(10));

		AppDataProcessLease.TryAcquire(dataRoot, out _).ShouldBeFalse();
		await holder.ReleaseAsync();
		AppDataProcessLease.TryAcquire(dataRoot, out var reacquired).ShouldBeTrue();
		reacquired!.Dispose();
	}

	[Test]
	public async Task Dispose_FromAnotherThread_ReleasesTheMutex()
	{
		using var root = TemporaryDirectory.Create();
		AppDataProcessLease.TryAcquire(root.Path, out var lease).ShouldBeTrue();
		try
		{
			await Task.Run(lease!.Dispose);

			AppDataProcessLease.TryAcquire(root.Path, out var reacquired).ShouldBeTrue();
			reacquired!.Dispose();
		}
		finally
		{
			lease?.Dispose();
		}
	}

	[Test]
	public async Task HolderCrash_LeavesAnAbandonedMutexThatCanBeReacquired()
	{
		using var root = TemporaryDirectory.Create();
		var dataRoot = Path.Combine(root.Path, "data");
		var readyPath = Path.Combine(root.Path, "ready");
		Directory.CreateDirectory(dataRoot);
		await using var holder =
			await ProfileToolLeaseHolder.StartAsync(
				dataRoot,
				readyPath,
				TimeSpan.FromSeconds(10));

		await holder.CrashAsync();

		AppDataProcessLease.TryAcquire(dataRoot, out var reacquired).ShouldBeTrue();
		reacquired!.Dispose();
	}
}
