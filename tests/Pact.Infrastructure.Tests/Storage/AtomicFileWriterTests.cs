using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class AtomicFileWriterTests
{
	[Test]
	public async Task WriteTextAsync_creates_parent_directory_and_replaces_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "nested", "projects.json");

		await AtomicFileWriter.WriteTextAsync(path, "first", CancellationToken.None);
		await AtomicFileWriter.WriteTextAsync(path, "second", CancellationToken.None);

		(await File.ReadAllTextAsync(path)).ShouldBe("second");
		Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").ShouldBeEmpty();
	}

	[Test]
	public async Task WriteTextAsync_deletes_temp_file_when_replace_fails()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "projects.json");
		Directory.CreateDirectory(root);
		await File.WriteAllTextAsync(path, "original");

		using FileStream lockedDestination = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

		await Should.ThrowAsync<IOException>(
			() => AtomicFileWriter.WriteTextAsync(path, "second", CancellationToken.None));

		Directory.GetFiles(root, "*.tmp").ShouldBeEmpty();
	}

	[Test]
	public async Task WriteTextAsync_uses_the_requested_staging_directory()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "Settings", "projects.json");
		var stagingDirectory = Path.Combine(root, "Temp", "atomic");

		await AtomicFileWriter.WriteTextAsync(
			path,
			"content",
			stagingDirectory,
			CancellationToken.None);

		(await File.ReadAllTextAsync(path)).ShouldBe("content");
		Directory.GetFiles(stagingDirectory).ShouldBeEmpty();
	}
}
