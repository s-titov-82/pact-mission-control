namespace Pact.Infrastructure.Tests.Settings;

public sealed class ReviewProfileReaderTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();

	public void Dispose() => _directory.Dispose();

	[Test]
	public async Task ReadAsync_ReturnsEmptyWhenFileMissing()
	{
		var profiles = await ReviewProfileReader.ReadAsync(
			Path.Combine(_directory.Path, "missing.json"),
			CancellationToken.None);

		profiles.ShouldBeEmpty();
	}

	[Test]
	public async Task ReadAsync_SkipsEntriesWithoutAnId()
	{
		var path = Path.Combine(_directory.Path, "review-profiles.json");
		await File.WriteAllTextAsync(
			path,
			/*lang=json,strict*/
			"""[{"displayName":"nameless","kind":"claude","commandTemplate":"claude"},{"id":"ok","displayName":"Ok","kind":"claude","commandTemplate":"claude"}]""");

		var profiles = await ReviewProfileReader.ReadAsync(path, CancellationToken.None);

		profiles.Select(profile => profile.Id).ShouldBe(["ok"]);
	}
}
