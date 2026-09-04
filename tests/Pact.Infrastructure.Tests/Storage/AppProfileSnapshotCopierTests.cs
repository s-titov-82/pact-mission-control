using Pact.Infrastructure.Storage;
using Pact.ProfileTool;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class AppProfileSnapshotCopierTests : IDisposable
{
	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();

	private string SourceRoot => Path.Combine(_root.Path, "source");
	private string DestinationRoot => Path.Combine(_root.Path, "destination");

	[Test]
	public async Task CopyAsync_CopiesDurableSettingsButExcludesDisposableData()
	{
		await SnapshotFixture.WriteSourceAsync(SourceRoot);

		await AppProfileSnapshotCopier.CopyAsync(
			SourceRoot,
			DestinationRoot,
			replace: false,
			CancellationToken.None);

		File.Exists(Path.Combine(DestinationRoot, "Settings", "projects.json")).ShouldBeTrue();
		(await File.ReadAllTextAsync(
			Path.Combine(DestinationRoot, "Settings", "projects.json")))
			.ShouldBe("{\"file\":\"projects.json\"}");
		File.Exists(Path.Combine(DestinationRoot, "Settings", "Notes", "note.md")).ShouldBeTrue();
		Directory.EnumerateFileSystemEntries(Path.Combine(DestinationRoot, "WebView")).ShouldBeEmpty();
		Directory.EnumerateFileSystemEntries(Path.Combine(DestinationRoot, "Logs")).ShouldBeEmpty();
		Directory.EnumerateFileSystemEntries(Path.Combine(DestinationRoot, "Temp", "Session")).ShouldBeEmpty();
		Directory.EnumerateFileSystemEntries(Path.Combine(DestinationRoot, "Temp", "Retained")).ShouldBeEmpty();
		Directory.GetDirectories(DestinationRoot)
				.Select(path => Path.GetFileName(path))
				.Order()
				.ToArray().ShouldBe(["Logs", "Settings", "Temp", "WebView"]);

		AppDataProcessLease.TryAcquire(DestinationRoot, out var destinationLease)
			.ShouldBeTrue();
		destinationLease!.Dispose();
	}

	[Test]
	public async Task CopyAsync_NonEmptyDestinationWithoutReplace_Throws()
	{
		await SnapshotFixture.WriteSourceAsync(SourceRoot);
		Directory.CreateDirectory(DestinationRoot);
		await File.WriteAllTextAsync(Path.Combine(DestinationRoot, "keep.txt"), "keep");

		await Should.ThrowAsync<IOException>(() =>
			AppProfileSnapshotCopier.CopyAsync(
				SourceRoot,
				DestinationRoot,
				replace: false,
				CancellationToken.None));

		(await File.ReadAllTextAsync(Path.Combine(DestinationRoot, "keep.txt"))).ShouldBe("keep");
	}

	[Test]
	public async Task CopyAsync_OccupiedSourceLease_ThrowsBeforeCreatingDestination()
	{
		await SnapshotFixture.WriteSourceAsync(SourceRoot);
		AppDataProcessLease.TryAcquire(SourceRoot, out var lease).ShouldBeTrue();

		using (lease)
		{
			await Should.ThrowAsync<IOException>(() =>
				AppProfileSnapshotCopier.CopyAsync(
					SourceRoot,
					DestinationRoot,
					replace: false,
					CancellationToken.None));
		}

		Directory.Exists(DestinationRoot).ShouldBeFalse();
	}

	[Test]
	public async Task CopyAsync_ReplaceAtomicallyReplacesExistingDestination()
	{
		await SnapshotFixture.WriteSourceAsync(SourceRoot);
		Directory.CreateDirectory(DestinationRoot);
		await File.WriteAllTextAsync(Path.Combine(DestinationRoot, "old.txt"), "old");

		await AppProfileSnapshotCopier.CopyAsync(
			SourceRoot,
			DestinationRoot,
			replace: true,
			CancellationToken.None);

		File.Exists(Path.Combine(DestinationRoot, "old.txt")).ShouldBeFalse();
		File.Exists(Path.Combine(DestinationRoot, "Settings", "projects.json")).ShouldBeTrue();
		Directory.EnumerateDirectories(
			_root.Path,
			"destination.staging-*",
			SearchOption.TopDirectoryOnly).ShouldBeEmpty();
		Directory.EnumerateDirectories(
			_root.Path,
			"destination.backup-*",
			SearchOption.TopDirectoryOnly).ShouldBeEmpty();
	}

	[Test]
	public async Task CopyAsync_OccupiedDestinationLeaseInAnotherProcess_ThrowsBeforeStaging()
	{
		await SnapshotFixture.WriteSourceAsync(SourceRoot);
		Directory.CreateDirectory(DestinationRoot);
		var readyPath = Path.Combine(_root.Path, "destination-ready");
		await using var holder =
			await ProfileToolLeaseHolder.StartAsync(
				DestinationRoot,
				readyPath,
				TimeSpan.FromSeconds(10));

		await Should.ThrowAsync<IOException>(() =>
			AppProfileSnapshotCopier.CopyAsync(
				SourceRoot,
				DestinationRoot,
				replace: true,
				CancellationToken.None));

		Directory.EnumerateDirectories(
			_root.Path,
			"destination.staging-*",
			SearchOption.TopDirectoryOnly).ShouldBeEmpty();
	}

	[Test]
	public async Task CopyAsync_CreatesNoCoordinationArtifactInEitherDataRoot()
	{
		var sourceSettings = Path.Combine(SourceRoot, "Settings");
		Directory.CreateDirectory(sourceSettings);
		await File.WriteAllTextAsync(
			Path.Combine(sourceSettings, "projects.json"),
			"durable");

		await AppProfileSnapshotCopier.CopyAsync(
			SourceRoot,
			DestinationRoot,
			replace: false,
			CancellationToken.None);

		RelativeFiles(SourceRoot).ShouldBe(["Settings/projects.json"]);
		RelativeFiles(DestinationRoot).ShouldBe(["Settings/projects.json"]);
	}

	public void Dispose() => _root.Dispose();

	private static string[] RelativeFiles(string root) =>
		Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
			.Order(StringComparer.Ordinal)
			.ToArray();
}