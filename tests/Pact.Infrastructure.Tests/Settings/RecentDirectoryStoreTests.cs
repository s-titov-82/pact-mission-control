namespace Pact.Infrastructure.Tests.Settings;

public sealed class RecentDirectoryStoreTests
{
	[Test]
	public async Task LoadAsync_returns_empty_list_when_file_is_missing()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "recent-directories.json");
		RecentDirectoryStore store = new(path);

		var directories = await store.LoadAsync(CancellationToken.None);

		directories.ShouldBeEmpty();
	}

	[Test]
	public async Task AddAsync_moves_directory_to_front_deduplicates_and_keeps_twenty_items()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "recent-directories.json");
		RecentDirectoryStore store = new(path);

		for (var index = 0; index < 25; index++)
		{
			await store.AddAsync($@"C:\Work\Project{index}", CancellationToken.None);
		}

		await store.AddAsync(@"c:\work\project10", CancellationToken.None);

		var directories = await store.LoadAsync(CancellationToken.None);

		directories.Count.ShouldBe(20);
		directories[0].ShouldBe(@"c:\work\project10");
		directories.ShouldNotContain(@"C:\Work\Project0");
		directories.Count(directory => string.Equals(
				directory,
				@"C:\Work\Project10",
				StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
	}
}
