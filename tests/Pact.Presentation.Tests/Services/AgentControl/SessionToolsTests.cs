using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Core.AgentControl;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Reviews;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.Tests.Services.Reviews;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class SessionToolsTests
{
	[Test]
	public void BuildToolsList_lists_every_session_tool()
	{
		var result = CreateTools().BuildToolsList();

		result["tools"]!.AsArray().Select(tool => (string?)tool!["name"])
			.ShouldBe(
				[
					"pact_request_review",
					"pact_get_review_turn",
					"pact_submit_review_turn",
					"pact_append_note",
					"pact_open_web_tab",
					"pact_get_notes",
					"pact_replace_notes",
					"pact_open_document"
				],
				ignoreOrder: true);
	}

	[Test]
	public void BuildToolsList_declares_revision_safe_notes_schemas()
	{
		var tools = CreateTools().BuildToolsList()["tools"]!.AsArray();
		var get = tools.Single(tool => (string?)tool!["name"] == "pact_get_notes");
		var replace = tools.Single(tool => (string?)tool!["name"] == "pact_replace_notes");

		get!["inputSchema"]!["required"].ShouldBeNull();
		replace!["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["text", "expectedRevision"], ignoreOrder: true);
	}

	[Test]
	public void BuildToolsList_EmbedsReviewProfileIdsAsEnum()
	{
		ReviewProfile profile = new(
			"claude-opus",
			"Claude Opus reviewer",
			AgentKind.Claude,
			"claude --model opus");

		var review = FindTool(CreateTools(profiles: [profile]), "pact_request_review");

		review["inputSchema"]!["properties"]!["reviewProfileId"]!["enum"]!
			.AsArray().Select(value => (string?)value).ShouldBe(["claude-opus"]);
	}

	[Test]
	public void BuildToolsList_MarksReviewArgumentsRequired()
	{
		var review = FindTool(CreateTools(), "pact_request_review");

		review["inputSchema"]!["required"]!.AsArray().Select(value => (string?)value)
			.ShouldBe(
				["scenarioId", "reviewProfileId", "target"],
				ignoreOrder: true);
	}

	[Test]
	public void Listing_reads_the_snapshot_without_the_ui()
	{
		var review = FindTool(
			CreateTools(scenarios: [CreateDefinition("code-review")]),
			"pact_request_review");

		review["inputSchema"]!["properties"]!["scenarioId"]!["enum"]!
			.AsArray().Select(value => (string?)value).ShouldBe(["code-review"]);
	}

	[Test]
	public void Open_document_requires_a_path()
	{
		var tool = FindTool(CreateTools(), "pact_open_document");

		tool["inputSchema"]!["required"]!.AsArray().Select(value => (string?)value).ShouldBe(["path"]);
		((string?)tool["inputSchema"]!["properties"]!["path"]!["type"]).ShouldBe("string");
	}

	[Test]
	public void Submit_turn_requires_the_turn_and_the_text()
	{
		var tool = FindTool(CreateTools(), "pact_submit_review_turn");

		tool["inputSchema"]!["required"]!.AsArray().Select(value => (string?)value)
			.ShouldBe(["turnId", "text"], ignoreOrder: true);
		((string?)tool["inputSchema"]!["properties"]!["approved"]!["type"]).ShouldBe("boolean");
	}

	[Test]
	public async Task Review_tools_go_through_the_relay()
	{
		using var root = TemporaryDirectory.Create();
		ReviewProfile profile = new("codex-high", "Codex high", AgentKind.Codex, "codex");
		var tools = CreateTools(relay: CreateRelay(root.Path, [CreateDefinition("code-review")], [profile]));

		var started = await tools.InvokeAsync(
			new AgentControlToolCall(
				"author-1",
				IsOrchestrator: false,
				"pact_request_review",
				new JsonObject
				{
					["scenarioId"] = "code-review",
					["reviewProfileId"] = "codex-high",
					["target"] = "main..HEAD"
				}),
			CancellationToken.None);
		var turn = await tools.InvokeAsync(
			new AgentControlToolCall("reviewer-1", IsOrchestrator: false, "pact_get_review_turn", new JsonObject()),
			CancellationToken.None);
		var turnId = (string)JsonNode.Parse(turn.Text)!["turnId"]!;
		var submitted = await tools.InvokeAsync(
			new AgentControlToolCall(
				"reviewer-1",
				IsOrchestrator: false,
				"pact_submit_review_turn",
				new JsonObject { ["turnId"] = turnId, ["text"] = "Fine.", ["approved"] = true }),
			CancellationToken.None);

		started.IsError.ShouldBeFalse(started.Text);
		turn.IsError.ShouldBeFalse(turn.Text);
		submitted.IsError.ShouldBeFalse(submitted.Text);
		((string?)JsonNode.Parse(submitted.Text)!["state"]).ShouldBe("Completed");
	}

	[Test]
	public async Task Missing_required_argument_names_it()
	{
		var result = await CreateTools().InvokeAsync(
			new AgentControlToolCall(
				"session-project",
				IsOrchestrator: false,
				"pact_append_note",
				new JsonObject()),
			CancellationToken.None);

		result.IsError.ShouldBeTrue();
		result.Text.ShouldBe("Argument 'text' is required.");
	}

	[Test]
	public async Task A_successful_action_reports_its_status()
	{
		List<string> statuses = [];
		var tools = CreateTools(reportStatusAsync: status =>
		{
			statuses.Add(status);
			return Task.CompletedTask;
		});

		var result = await tools.InvokeAsync(
			new AgentControlToolCall(
				"session-project",
				IsOrchestrator: false,
				"pact_get_notes",
				new JsonObject()),
			CancellationToken.None);

		result.IsError.ShouldBeFalse();
		statuses.ShouldBe(["Agent action: notes read"]);
	}

	private static AgentControlToolSet CreateTools(
		IReadOnlyList<ScenarioDefinition>? scenarios = null,
		IReadOnlyList<ReviewProfile>? profiles = null,
		Func<string, Task>? reportStatusAsync = null,
		ReviewRelay? relay = null) =>
		SessionTools.Create(
			new AgentControlDispatcher(new AgentControlDispatcherTests.FakeAgentControlHost()),
			relay ?? CreateRelay(Path.GetTempPath(), scenarios ?? [], profiles ?? []),
			() => scenarios ?? [],
			() => profiles ?? [],
			reportStatusAsync ?? (_ => Task.CompletedTask));

	private static ReviewRelay CreateRelay(
		string root,
		IReadOnlyList<ScenarioDefinition> scenarios,
		IReadOnlyList<ReviewProfile> profiles) =>
		new(
			new ReviewRelayTests.FakeReviewRelayHost(root),
			() => scenarios,
			() => profiles,
			SharedLog,
			TimeProvider.System);

	private static readonly ReviewLogWriter SharedLog = new();

	private static JsonNode FindTool(AgentControlToolSet tools, string name) =>
		tools.BuildToolsList()["tools"]!.AsArray()
			.Single(tool => (string?)tool!["name"] == name)!;

	private static ScenarioDefinition CreateDefinition(string id) => new(
		id,
		ScenarioKind.ReviewLoop,
		"Code review",
		MaxIterations: 3,
		StopMarker: "DONE",
		DefaultTarget: string.Empty,
		StartPromptTemplate: string.Empty,
		FirstFeedbackTemplate: string.Empty,
		AuthorReturnTemplate: string.Empty,
		FeedbackTemplate: string.Empty,
		ReviewerInstructions: [],
		DefaultReviewerInstructionId: string.Empty);
}
