using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Core.AgentControl;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Reviews;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.Tests.Services.Reviews;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class SessionToolsTests
{
	[Test]
	public async Task Project_link_tools_save_and_list_the_callers_resources()
	{
		var tools = CreateTools();
		var added = await tools.InvokeAsync(new AgentControlToolCall("session-project", false, "pact_add_project_link",
			new JsonObject { ["title"] = "Build", ["url"] = "https://example.com/job" }), CancellationToken.None);
		added.IsError.ShouldBeFalse(added.Text);
		var listed = await tools.InvokeAsync(new AgentControlToolCall("session-project", false, "pact_list_project_links", new JsonObject()), CancellationToken.None);
		listed.IsError.ShouldBeFalse(listed.Text);
		var link = JsonNode.Parse(listed.Text)!.AsArray().ShouldHaveSingleItem();
		((string?)link!["title"]).ShouldBe("Build");
		((string?)link["url"]).ShouldBe("https://example.com/job");
		((string?)link["kind"]).ShouldBe("other");
	}

	[Test]
	public void Project_link_schema_requires_title_and_url_and_limits_kind()
	{
		var tool = FindTool(CreateTools(), "pact_add_project_link");
		tool["inputSchema"]!["required"]!.AsArray().Select(node => (string?)node).ShouldBe(["title", "url"]);
		tool["inputSchema"]!["properties"]!["kind"]!["enum"]!.AsArray().Select(node => (string?)node)
			.ShouldBe(["ci", "merge_request", "issue", "other"]);
	}

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
					"pact_open_document",
					"pact_add_file_link",
					"pact_add_project_link",
					"pact_list_project_links",
					"pact_get_subscription_usage",
					"pact_reply_remark",
					"pact_list_remarks"
				],
				ignoreOrder: true);
	}

	[Test]
	public async Task Subscription_usage_returns_what_pact_shows()
	{
		var tools = CreateTools(usage: () => AgentControlResult.Ok("""[{"profileId":"claude-opus","fiveHourResetsAt":"2026-10-26T18:00:00+03:00"}]"""));

		var result = await tools.InvokeAsync(
			new AgentControlToolCall("session-1", IsOrchestrator: false, "pact_get_subscription_usage", new JsonObject()),
			CancellationToken.None);

		result.IsError.ShouldBeFalse(result.Text);
		result.Text.ShouldContain("claude-opus");
		result.Text.ShouldContain("fiveHourResetsAt");
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
	public void Add_file_link_requires_a_path()
	{
		var tool = FindTool(CreateTools(), "pact_add_file_link");

		tool["inputSchema"]!["required"]!.AsArray().Select(value => (string?)value).ShouldBe(["path"]);
		((string?)tool["inputSchema"]!["properties"]!["title"]!["type"]).ShouldBe("string");
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
		ReviewRelay? relay = null,
		Func<AgentControlResult>? usage = null)
	{
		AgentControlDispatcher dispatcher = new(new AgentControlDispatcherTests.FakeAgentControlHost());
		return SessionTools.Create(
			dispatcher,
			relay ?? CreateRelay(Path.GetTempPath(), scenarios ?? [], profiles ?? []),
			() => scenarios ?? [],
			() => profiles ?? [],
			reportStatusAsync ?? (_ => Task.CompletedTask),
			usage ?? (() => AgentControlResult.Ok("[]")),
			new RemarkTools(dispatcher, SharedBooks, (_, _) => Task.FromResult<string?>(null), _ => Task.CompletedTask));
	}

	private static readonly RemarkBooks SharedBooks = new(TimeProvider.System);

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
		StartPromptTemplate: string.Empty,
		FirstFeedbackTemplate: string.Empty,
		AuthorReturnTemplate: string.Empty,
		FeedbackTemplate: string.Empty,
		ReviewerInstructions: [],
		DefaultReviewerInstructionId: string.Empty);
}
