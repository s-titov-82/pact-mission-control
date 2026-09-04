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
		item.StartPromptTemplate.ShouldBe("");
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse(); // validation blocks incomplete scenario
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
		section.AddNewItem(); // make a valid one so save has something to validate
		FillValidScenario((ScenarioItemViewModel)section.Items[^1]);
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		var saved = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsArray();
		saved.ShouldContain(n => (string?)n!["kind"] == "futureKind" && (int?)n["payload"] == 123);
	}

	[Test]
	public async Task Removing_default_instruction_repoints_default_and_last_instruction_is_protected()
	{
		var section = await LoadDefaultSeededSectionAsync(); // EnsureDefaultFilesAsync seeds real defaults
		var item = section.Items.OfType<ScenarioItemViewModel>().First();
		var defaultId = item.DefaultReviewerInstructionId;
		var defaultVm = item.ReviewerInstructions.First(i => i.Id == defaultId);
		if (item.ReviewerInstructions.Count == 1)
		{ item.AddInstruction(); item.ReviewerInstructions[^1].Id = "extra"; item.ReviewerInstructions[^1].Name = "n"; item.ReviewerInstructions[^1].Text = "t"; }
		item.RemoveInstruction(defaultVm);
		item.DefaultReviewerInstructionId.ShouldNotBe(defaultId);
		item.DefaultReviewerInstructionId.ShouldBe(item.ReviewerInstructions[0].Id);
		while (item.ReviewerInstructions.Count > 1)
		{
			item.RemoveInstruction(item.ReviewerInstructions[^1]);
		}

		(await section.SaveAsync(CancellationToken.None) && item.ReviewerInstructions.Count == 0).ShouldBeFalse(); // ≥1 enforced
	}

	[Test]
	public async Task MaxIterations_below_1_blocks_save()
	{
		var section = await LoadDefaultSeededSectionAsync();
		((ScenarioItemViewModel)section.Items[0]).MaxIterationsText = "0";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
	}

	private static void FillValidScenario(ScenarioItemViewModel item)
	{
		item.Id = "new-scenario";
		item.Name = "New scenario";
		item.MaxIterationsText = "3";
		item.StopMarker = "DONE";
		item.DefaultTarget = "target";
		item.StartPromptTemplate = "start {target}";
		item.FirstFeedbackTemplate = "first {reviewerOutput}";
		item.AuthorReturnTemplate = "author {authorOutput}";
		item.FeedbackTemplate = "feedback {reviewerOutput}";
		item.AddInstruction();
		item.ReviewerInstructions[0].Id = "strict";
		item.ReviewerInstructions[0].Name = "Strict";
		item.ReviewerInstructions[0].Text = "Be strict.";
		item.DefaultReviewerInstructionId = "strict";
	}

	private async Task<ScenariosSectionViewModel> LoadDefaultSeededSectionAsync()
	{
		await Store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new ScenariosSectionViewModel(Store);
		await section.LoadAsync(CancellationToken.None);
		return section;
	}
}
