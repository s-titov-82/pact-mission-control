using System.Text.Json.Nodes;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class ReviewProfilesSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();

	public void Dispose() => _directory.Dispose();

	[Test]
	public async Task SaveAsync_PreservesUnknownNodes()
	{
		var paths = new AppPaths(_directory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.ReviewProfilesPath,
			/*lang=json,strict*/
			"""[{"id":"claude-opus","displayName":"Opus","kind":"claude","commandTemplate":"claude","futureField":42}]""");
		ReviewProfilesSectionViewModel section = new(new SettingsFileStore(paths));

		await section.LoadAsync(CancellationToken.None);
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();

		var saved = JsonNode.Parse(await File.ReadAllTextAsync(paths.ReviewProfilesPath))!.AsArray();
		((int?)saved[0]!["futureField"]).ShouldBe(42);
	}

	[Test]
	public async Task SaveAsync_RejectsDuplicateIds()
	{
		var section = await CreateLoadedSectionAsync(
			/*lang=json,strict*/
			"""[{"id":"a","displayName":"A","kind":"claude","commandTemplate":"claude"},{"id":"a","displayName":"B","kind":"codex","commandTemplate":"codex"}]""");

		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		section.StatusText.ShouldNotBeNull().ShouldContain("unique");
	}

	[Test]
	public async Task ProviderSeesSavedEditsWithoutRestart()
	{
		var paths = new AppPaths(_directory.Path);
		var store = new SettingsFileStore(paths);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		ReviewProfileProvider provider = new(paths.ReviewProfilesPath);
		await provider.RefreshAsync(CancellationToken.None);
		ReviewProfilesSectionViewModel section = new(store);
		await section.LoadAsync(CancellationToken.None);
		var first = section.Items[0].ShouldBeOfType<ReviewProfileItemViewModel>();
		first.Id = "new-reviewer";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		await provider.RefreshAsync(CancellationToken.None);

		provider.Current.Select(profile => profile.Id).ShouldContain("new-reviewer");
	}

	private async Task<ReviewProfilesSectionViewModel> CreateLoadedSectionAsync(string json)
	{
		var paths = new AppPaths(_directory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(paths.ReviewProfilesPath, json);
		ReviewProfilesSectionViewModel section = new(new SettingsFileStore(paths));
		await section.LoadAsync(CancellationToken.None);
		return section;
	}
}
