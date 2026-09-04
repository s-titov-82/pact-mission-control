using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Storage;

public sealed class ProjectNotesStoreTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _tempRoot => _temporaryDirectory.Path;
	private ProjectNotesStore CreateStore() => new(new AppPaths(_tempRoot));
	public void Dispose() => _temporaryDirectory.Dispose();

	[Test]
	public async Task Load_MissingFile_ReturnsEmpty() =>
		(await CreateStore().LoadAsync(@"D:\proj", CancellationToken.None)).ShouldBeEmpty();

	[Test]
	public async Task SaveThenLoad_RoundTrips()
	{
		var store = CreateStore();
		await store.SaveAsync(@"D:\proj", "hello\nworld", CancellationToken.None);
		(await store.LoadAsync(@"D:\proj", CancellationToken.None)).ShouldBe("hello\nworld");
	}

	[Test]
	public async Task Load_UsesRootPathKey_NotExactString()
	{
		var store = CreateStore();
		await store.SaveAsync(@"D:\proj", "content", CancellationToken.None);
		(await store.LoadAsync(@"d:\PROJ\", CancellationToken.None)).ShouldBe("content");
	}

	[Test]
	[TestCase("", "first block", "first block\n")]
	[TestCase("existing", "appended", "existing\n\nappended\n")]
	[TestCase("existing\n\n", "appended", "existing\n\nappended\n")]
	public async Task Append_UsesBlankLineSeparation(string existing, string appended, string expected)
	{
		ArgumentNullException.ThrowIfNull(existing);
		ArgumentNullException.ThrowIfNull(appended);
		ArgumentNullException.ThrowIfNull(expected);
		var store = CreateStore();
		if (existing.Length > 0)
		{
			await store.SaveAsync(@"D:\proj", existing, CancellationToken.None);
		}

		await store.AppendAsync(@"D:\proj", appended, CancellationToken.None);
		(await store.LoadAsync(@"D:\proj", CancellationToken.None)).ShouldBe(expected);
	}

	[Test]
	public async Task Append_WhitespaceOnlyText_IsIgnored()
	{
		var store = CreateStore();
		await store.SaveAsync(@"D:\proj", "existing", CancellationToken.None);
		await store.AppendAsync(@"D:\proj", "   \n ", CancellationToken.None);
		(await store.LoadAsync(@"D:\proj", CancellationToken.None)).ShouldBe("existing");
	}
}
