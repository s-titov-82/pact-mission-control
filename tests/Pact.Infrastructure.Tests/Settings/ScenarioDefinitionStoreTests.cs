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
		planReview.MaxIterations.ShouldBe(5);
		planReview.StopMarker.ShouldBe("AGENT_TERMINAL_DONE");
		string.IsNullOrWhiteSpace(planReview.DefaultTarget).ShouldBeFalse();
		planReview.DefaultTarget.ShouldNotContain(
			"docs/superpowers",
			Case.Insensitive);
		// Default templates use the file exchange protocol: response content
		// travels through {reviewResultFile}/{authorFeedbackFile}; the
		// completion-marker policy is engine-appended, not template-carried.
		planReview.StartPromptTemplate.ShouldContain("{target}");
		planReview.StartPromptTemplate.ShouldNotContain("{reviewerInstruction}");
		planReview.StartPromptTemplate.ShouldContain(
			"Architecture necessity and reuse gate");
		planReview.StartPromptTemplate.ShouldContain("Counterfactual check");
		planReview.StartPromptTemplate.ShouldContain("god-object");
		planReview.FirstFeedbackTemplate.ShouldContain("{target}");
		planReview.FirstFeedbackTemplate.ShouldContain("{reviewResultFile}");
		planReview.AuthorReturnTemplate.ShouldContain("{authorFeedbackFile}");
		planReview.FeedbackTemplate.ShouldContain("{reviewResultFile}");
		planReview.DefaultReviewerInstructionId.ShouldBe("strict");
		planReview.ReviewerInstructions.Select(item => item.Id).ToArray().ShouldBe(["strict", "allow-minors"]);

		var codeReview = scenarios.Single(scenario => scenario.Id == "code-review");
		codeReview.Kind.ShouldBe(ScenarioKind.ReviewLoop);
		codeReview.StartPromptTemplate.Contains("bugs and incorrect behavior", StringComparison.OrdinalIgnoreCase).ShouldBeTrue();
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
			CreateDefinition("plan-review", "Plan Review", 3, "DONE"),
			CreateDefinition("experimental-scenario", "Experimental Scenario", 1, "DONE")
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
		var known = CreateDefinition("code-review", "Code Review", 3, "DONE");
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
	public async Task LoadAsync_old_format_file_returns_defaults_and_rewrites_the_file_on_disk()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		var legacyJson = /*lang=json,strict*/ """
        [
          {
            "id": "code-review",
            "kind": "reviewLoop",
            "name": "Code Review",
            "maxIterations": 3,
            "stopMarker": "DONE",
            "startPrompt": "legacy prompt",
            "reviewPromptTemplate": "review {subject}",
            "revisionPromptTemplate": "revise {reviewerOutput}"
          }
        ]
        """;
		await File.WriteAllTextAsync(path, legacyJson, CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		definitions.Select(definition => definition.Id).ToArray().ShouldBe(["plan-review", "code-review"]);
		var rewrittenJson = await File.ReadAllTextAsync(path, CancellationToken.None);
		rewrittenJson.ShouldContain("\"startPromptTemplate\"");
		rewrittenJson.Contains("reviewPromptTemplate", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
		rewrittenJson.Contains("\"startPrompt\"", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
	}

	[Test]
	public async Task LoadAsync_entry_missing_reviewer_instructions_returns_defaults_and_rewrites_the_file_on_disk()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		var malformedJson = /*lang=json,strict*/ """
        [
          {
            "id": "code-review",
            "kind": "reviewLoop",
            "name": "Code Review",
            "maxIterations": 3,
            "stopMarker": "DONE",
            "defaultTarget": "target",
            "startPromptTemplate": "review {target}",
            "firstFeedbackTemplate": "feedback {target}",
            "authorReturnTemplate": "author return {authorOutput}",
            "feedbackTemplate": "feedback {reviewerOutput}"
          }
        ]
        """;
		await File.WriteAllTextAsync(path, malformedJson, CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		definitions.Select(definition => definition.Id).ToArray().ShouldBe(["plan-review", "code-review"]);
		var rewrittenJson = await File.ReadAllTextAsync(path, CancellationToken.None);
		rewrittenJson.ShouldContain("\"reviewerInstructions\"");
	}

	[Test]
	public async Task LoadAsync_entry_with_empty_reviewer_instructions_returns_defaults_and_rewrites_the_file_on_disk()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		Directory.CreateDirectory(root);
		var path = Path.Combine(root, "scenarios.json");
		var malformedJson = /*lang=json,strict*/ """
        [
          {
            "id": "code-review",
            "kind": "reviewLoop",
            "name": "Code Review",
            "maxIterations": 3,
            "stopMarker": "DONE",
            "defaultTarget": "target",
            "startPromptTemplate": "review {target}",
            "firstFeedbackTemplate": "feedback {target}",
            "authorReturnTemplate": "author return {authorOutput}",
            "feedbackTemplate": "feedback {reviewerOutput}",
            "reviewerInstructions": [],
            "defaultReviewerInstructionId": "strict"
          }
        ]
        """;
		await File.WriteAllTextAsync(path, malformedJson, CancellationToken.None);
		ScenarioDefinitionStore store = new(path);

		var definitions = await store.LoadAsync(CancellationToken.None);

		definitions.Select(definition => definition.Id).ToArray().ShouldBe(["plan-review", "code-review"]);
		var rewrittenJson = await File.ReadAllTextAsync(path, CancellationToken.None);
		rewrittenJson.ShouldContain("\"reviewerInstructions\"");
	}

	[Test]
	public async Task SaveAsync_RoundTripsTargetAndReviewerInstructionId()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "scenarios.json");
		ScenarioDefinitionStore store = new(path);
		var definition = ScenarioDefinitionStore.LoadDefaultDefinitions()
			.Single(scenario => scenario.Id == "code-review") with
		{
			DefaultTarget = "review last 3 commits",
			DefaultReviewerInstructionId = "allow-minors"
		};

		await store.SaveAsync([definition], CancellationToken.None);
		var loaded = await store.LoadAsync(CancellationToken.None);

		loaded.Single().DefaultTarget.ShouldBe("review last 3 commits");
		loaded.Single().DefaultReviewerInstructionId.ShouldBe("allow-minors");
	}

	[Test]
	public async Task SaveAsync_WritesNewShapeWithFourTemplates()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "scenarios.json");
		ScenarioDefinitionStore store = new(path);
		var definition = ScenarioDefinitionStore.LoadDefaultDefinitions()
			.Single(scenario => scenario.Id == "code-review") with
		{
			DefaultTarget = "saved target"
		};

		await store.SaveAsync([definition], CancellationToken.None);
		var json = await File.ReadAllTextAsync(path, CancellationToken.None);

		json.ShouldContain("\"kind\": \"reviewLoop\"");
		json.ShouldContain("\"startPromptTemplate\"");
		json.ShouldContain("\"firstFeedbackTemplate\"");
		json.ShouldContain("\"authorReturnTemplate\"");
		json.ShouldContain("\"feedbackTemplate\"");
		json.ShouldContain("\"reviewerInstructions\"");
		json.Contains("strictness", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
		json.Contains("requiredRoles", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
	}

	[Test]
	public async Task SaveAsync_ThenLoadAsync_RoundTripsNewShape()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		var path = Path.Combine(root, "scenarios.json");
		ScenarioDefinitionStore store = new(path);
		var definitions = ScenarioDefinitionStore.LoadDefaultDefinitions().ToArray();

		await store.SaveAsync(definitions, CancellationToken.None);
		var loaded = await store.LoadAsync(CancellationToken.None);

		loaded.Select(definition => definition.Id).ShouldBe(definitions.Select(definition => definition.Id));
		var expectedCodeReview = definitions.Single(definition => definition.Id == "code-review");
		var actualCodeReview = loaded.Single(definition => definition.Id == "code-review");
		actualCodeReview.Kind.ShouldBe(expectedCodeReview.Kind);
		actualCodeReview.DefaultTarget.ShouldBe(expectedCodeReview.DefaultTarget);
		actualCodeReview.StartPromptTemplate.ShouldBe(expectedCodeReview.StartPromptTemplate);
		actualCodeReview.FirstFeedbackTemplate.ShouldBe(expectedCodeReview.FirstFeedbackTemplate);
		actualCodeReview.AuthorReturnTemplate.ShouldBe(expectedCodeReview.AuthorReturnTemplate);
		actualCodeReview.FeedbackTemplate.ShouldBe(expectedCodeReview.FeedbackTemplate);
		actualCodeReview.DefaultReviewerInstructionId.ShouldBe(expectedCodeReview.DefaultReviewerInstructionId);
		actualCodeReview.ReviewerInstructions.Select(instruction => instruction.Id)
			.ShouldBe(expectedCodeReview.ReviewerInstructions.Select(instruction => instruction.Id));
	}

	private static ScenarioDefinition CreateDefinition(
		string id,
		string name,
		int maxIterations,
		string stopMarker) => new ScenarioDefinition(
			id,
			ScenarioKind.ReviewLoop,
			name,
			maxIterations,
			stopMarker,
			DefaultTarget: "target",
			StartPromptTemplate: "review {target} {reviewerInstruction} {stopMarkerPrefix} {stopMarkerSuffix}",
			FirstFeedbackTemplate: "feedback {target} {reviewerOutput}",
			AuthorReturnTemplate: "author return {authorOutput}",
			FeedbackTemplate: "feedback {reviewerOutput}",
			ReviewerInstructions:
			[
				new("strict", "Strict", "Strict tail")
			],
			DefaultReviewerInstructionId: "strict");
}
