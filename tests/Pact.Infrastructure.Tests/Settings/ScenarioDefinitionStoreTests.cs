using System.Text.Json;

namespace Pact.Infrastructure.Tests.Settings;

public sealed class ScenarioDefinitionStoreTests
{
	[Test]
	public async Task LoadAsync_returns_default_review_loop_scenarios_when_file_is_missing()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "scenarios.json");
		ScenarioDefinitionStore store = new(path);

		var scenarios = await store.LoadAsync(CancellationToken.None);

		scenarios.Select(scenario => scenario.Id).ToArray().ShouldBe(["plan-review", "code-review"]);
		var planReview = scenarios.Single(scenario => scenario.Id == "plan-review");
		planReview.Kind.ShouldBe(ScenarioKind.ReviewLoop);
		planReview.ReviewRules.ShouldContain("Architecture necessity and reuse gate");
		planReview.ReviewRules.ShouldNotContain("{target}");
		planReview.DefaultStrict.ShouldBeTrue();

		var codeReview = scenarios.Single(scenario => scenario.Id == "code-review");
		codeReview.Kind.ShouldBe(ScenarioKind.ReviewLoop);
		codeReview.ReviewRules.Contains("bugs and incorrect behavior", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
	}

	[Test]
	public async Task LoadAsync_keeps_custom_id_entry_with_known_kind()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		ScenarioDefinition[] expected =
		[
			CreateDefinition("plan-review", "Plan Review"),
			CreateDefinition("experimental-scenario", "Experimental Scenario")
		];
		await File.WriteAllTextAsync(
			path,
			JsonSerializer.Serialize(expected, SettingsFileStore.JsonOptions),
			CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var actual = await store.LoadAsync(CancellationToken.None);

		actual.Select(definition => definition.Id).ToArray().ShouldBe(["plan-review", "experimental-scenario"]);
	}

	[Test]
	public async Task LoadAsync_drops_unknown_kind_entries_from_a_mixed_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		var known = CreateDefinition("code-review", "Code Review");
		var knownJson = JsonSerializer.Serialize(known, SettingsFileStore.JsonOptions);
		var mixedJson = """
            [
              {"id":"parallel-opinions","kind":"parallelOpinions","name":"Parallel opinions"},
              __KNOWN__
            ]
            """.Replace("__KNOWN__", knownJson, StringComparison.Ordinal);
		await File.WriteAllTextAsync(
			path,
			mixedJson,
			CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		var definition = definitions.ShouldHaveSingleItem();
		definition.Id.ShouldBe("code-review");
	}

	[Test]
	public async Task LoadAsync_empty_array_stays_empty()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		await File.WriteAllTextAsync(path, "[]", CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		definitions.ShouldBeEmpty();
	}

	[Test]
	public async Task Legacy_rules_and_mode_are_read_without_rewriting_custom_entries()
	{
		using var root = TemporaryDirectory.Create();
		var path = Path.Combine(root.Path, "scenarios.json");
		const string json = """
			[{"id":"custom","kind":"reviewLoop","name":"Custom","startPromptTemplate":"If it is pasted text, review the text as-is.\n\nCheck database compatibility.\n\nRespond with a numbered list.","defaultReviewerInstructionId":"allow-minors","firstFeedbackTemplate":"obsolete transport","extra":123}]
			""";
		await File.WriteAllTextAsync(path, json);
		var definitions = await new ScenarioDefinitionStore(path).LoadAsync(CancellationToken.None);

		var review = definitions.Single();
		review.ReviewRules.ShouldBe("Check database compatibility.");
		review.DefaultStrict.ShouldBeFalse();
		(await File.ReadAllTextAsync(path)).ShouldBe(json);
	}

	[Test]
	public async Task Custom_legacy_preset_becomes_opening_criteria()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var path = Path.Combine(temporaryDirectory.Path, "scenarios.json");
		var json = /*lang=json,strict*/ """
        [
          {
            "id": "custom-review",
            "kind": "reviewLoop",
            "name": "Custom review",
            "maxIterations": 3,
            "stopMarker": "DONE",
            "startPromptTemplate": "review {target}",
            "firstFeedbackTemplate": "feedback {target}",
            "authorReturnTemplate": "author return {authorOutput}",
            "feedbackTemplate": "feedback {reviewerOutput}",
            "reviewerInstructions": [ { "id": "database", "name": "Database", "text": "Check migration order." } ],
            "defaultReviewerInstructionId": "database"
          }
        ]
        """;
		await File.WriteAllTextAsync(path, json, CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		var review = definitions.ShouldHaveSingleItem();
		review.Id.ShouldBe("custom-review");
		review.ReviewRules.ShouldBe("review {target}\n\nCheck migration order.");
	}

	[Test]
	public async Task Save_and_load_round_trip_the_criteria_and_defaults()
	{
		using var root = TemporaryDirectory.Create();
		var store = new ScenarioDefinitionStore(Path.Combine(root.Path, "scenarios.json"));
		var definition = new ScenarioDefinition("custom", ScenarioKind.ReviewLoop, "Custom", "Inspect migrations.", false);
		await store.SaveAsync([definition], CancellationToken.None);
		(await store.LoadAsync(CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(definition);
	}

	private static ScenarioDefinition CreateDefinition(string id, string name) =>
		new(id, ScenarioKind.ReviewLoop, name, "Inspect changes.");
}
