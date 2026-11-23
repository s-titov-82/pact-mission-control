using System.Text.Json.Nodes;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class ScenariosSectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private string _dir => _directory.Path;
	private SettingsFileStore Store => new(_dir);
	private AppPaths Paths => new(_dir);
	public void Dispose()
	{
		_directory.Dispose();
		GC.SuppressFinalize(this);
	}

	[Test]
	public async Task Load_of_malformed_file_does_not_rewrite_it()
	{
		Directory.CreateDirectory(Paths.SettingsDirectory);
		var path = Paths.ScenariosPath;
		var malformed = /*lang=json,strict*/ """[{"id":"legacy","kind":"reviewLoop","name":"Old"}]"""; // missing templates/instructions
		await File.WriteAllTextAsync(path, malformed);
		var section = new ScenariosSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		(await File.ReadAllTextAsync(path)).ShouldBe(malformed); // regression guard vs ScenarioDefinitionStore reseed
		var item = section.Items[0].ShouldBeOfType<ScenarioItemViewModel>();
		item.ReviewRules.ShouldBe("");
		item.DefaultStrict.ShouldBeTrue();
	}

	[Test]
	public async Task Unknown_kind_entries_are_unrecognized_and_survive_save()
	{
		Directory.CreateDirectory(Paths.SettingsDirectory);
		var path = Paths.ScenariosPath;
		await File.WriteAllTextAsync(path, /*lang=json,strict*/ """[{"id":"x","kind":"futureKind","payload":123}]""");
		var section = new ScenariosSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		section.Items[0].ShouldBeOfType<UnrecognizedItemViewModel>();
		section.AddNewItem();
		var review = section.SelectedItem.ShouldBeOfType<ScenarioItemViewModel>();
		review.Name = "Database migrations";
		review.ReviewRules = "Check backwards compatibility.";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		saved.ShouldContain(n => (string?)n!["kind"] == "futureKind" && (int?)n["payload"] == 123);
	}

	[Test]
	public async Task Review_rules_and_default_mode_round_trip()
	{
		var section = await LoadDefaultSeededSectionAsync();
		var item = section.Items.OfType<ScenarioItemViewModel>().First();
		item.DefaultStrict = false;
		item.ReviewRules = "Check backwards compatibility.";

		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		await section.LoadAsync(CancellationToken.None);
		var saved = section.Items.OfType<ScenarioItemViewModel>().First();
		saved.ReviewRules.ShouldBe("Check backwards compatibility.");
		saved.DefaultStrict.ShouldBeFalse();
	}

	private async Task<ScenariosSectionViewModel> LoadDefaultSeededSectionAsync()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new ScenariosSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		return section;
	}
}
