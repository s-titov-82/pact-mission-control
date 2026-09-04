using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class ProjectMarkdownFileStoreTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public async Task Missing_file_can_be_created_when_expected_revision_matches()
	{
		var path = Path.Combine(_root, "README.md");
		ProjectMarkdownFileStore store = new();
		var missing = await store.LoadAsync(path, CancellationToken.None);

		var result = await store.TrySaveAsync(
			path,
			"# Project",
			missing.Revision,
			CancellationToken.None);

		result.Saved.ShouldBeTrue();
		(await File.ReadAllTextAsync(path)).ShouldBe("# Project");
		result.Snapshot.Revision.ShouldNotBe(missing.Revision);
	}

	[Test]
	public async Task Conditional_save_does_not_overwrite_external_change()
	{
		var path = Write("doc.md", "first");
		ProjectMarkdownFileStore store = new();
		var loaded = await store.LoadAsync(path, CancellationToken.None);
		await File.WriteAllTextAsync(path, "external");

		var result = await store.TrySaveAsync(
			path,
			"mine",
			loaded.Revision,
			CancellationToken.None);

		result.Saved.ShouldBeFalse();
		result.Snapshot.Text.ShouldBe("external");
		(await File.ReadAllTextAsync(path)).ShouldBe("external");
	}

	[Test]
	public async Task OverwriteAsync_replaces_conflicting_content()
	{
		var path = Write("doc.md", "external");
		ProjectMarkdownFileStore store = new();

		var saved = await store.OverwriteAsync(
			path,
			"mine",
			CancellationToken.None);

		saved.Text.ShouldBe("mine");
		(await File.ReadAllTextAsync(path)).ShouldBe("mine");
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}

	private string Write(string relativePath, string text)
	{
		var path = Path.Combine(_root, relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, text);
		return path;
	}
}
