using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Orchestrator;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Orchestrator;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.Tests.Services.Reviews;
using Pact.Presentation.Tests.Services.Orchestrator;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class OrchestratorToolsTests
{
	[Test]
	public void Tools_list_every_orchestrator_tool()
	{
		var result = CreateTools().BuildToolsList();

		ToolNames(result).ShouldBe(
			[
				"pact_list_workspaces",
				"pact_get_session",
				"pact_send_message",
				"pact_start_session",
				"pact_get_subscription_usage",
				"pact_list_active_runs",
				"pact_get_review_run",
				"pact_get_project_notes",
				"pact_replace_project_notes",
				"pact_append_project_note",
				"pact_list_web_tabs",
				"pact_resume_web_tab",
				"pact_get_web_tab_html"
			],
			ignoreOrder: true);
	}

	[TestCase("pact_get_project_notes", new[] { "workspaceId" })]
	[TestCase(
		"pact_replace_project_notes",
		new[] { "workspaceId", "text", "expectedRevision" })]
	[TestCase("pact_append_project_note", new[] { "workspaceId", "text" })]
	public void Project_notes_tools_declare_explicit_project_target(
		string toolName,
		string[] required)
	{
		var tool = FindTool(toolName);

		tool["inputSchema"]!["properties"]!["workspaceId"]!["type"]!
			.GetValue<string>().ShouldBe("string");
		tool["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(required, ignoreOrder: true);
	}

	[Test]
	public void Web_html_tool_declares_bounded_pagination_defaults()
	{
		var tool = FindTool("pact_get_web_tab_html");
		var properties = tool["inputSchema"]!["properties"]!;

		properties["offset"]!["default"]!.GetValue<int>().ShouldBe(0);
		properties["offset"]!["minimum"]!.GetValue<int>().ShouldBe(0);
		properties["maxChars"]!["default"]!.GetValue<int>().ShouldBe(100_000);
		properties["maxChars"]!["minimum"]!.GetValue<int>().ShouldBe(1);
		properties["maxChars"]!["maximum"]!.GetValue<int>().ShouldBe(200_000);
		tool["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["pageId"]);
	}

	[Test]
	public void Start_session_requires_a_session_id()
	{
		var tool = FindTool("pact_start_session");

		tool["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["sessionId"]);
	}

	[Test]
	public void Send_message_uses_canonical_message_property()
	{
		var tool = FindTool("pact_send_message");

		tool["inputSchema"]!["properties"]!["message"]!["type"]!
			.GetValue<string>().ShouldBe("string");
		tool["inputSchema"]!["properties"]!["text"].ShouldBeNull();
	}

	[TestCase("pact_get_review_run")]
	public void Review_run_tools_require_a_run_id(string toolName)
	{
		var result = CreateTools().BuildToolsList();

		var tool = result["tools"]!.AsArray()
			.Single(entry => (string?)entry!["name"] == toolName);
		tool!["inputSchema"]!["properties"]!["runId"]!["type"]!
			.GetValue<string>().ShouldBe("string");
		tool["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["runId"]);
	}

	[Test]
	public void Get_session_offers_message_and_screen_content_modes()
	{
		var result = CreateTools().BuildToolsList();

		var tool = result["tools"]!.AsArray()
			.Single(entry => (string?)entry!["name"] == "pact_get_session");
		tool!["inputSchema"]!["properties"]!["content"]!["enum"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["message", "screen"], ignoreOrder: true);
		tool["inputSchema"]!["required"]!.AsArray()
			.Select(value => (string?)value)
			.ShouldBe(["sessionId"]);
	}

	[Test]
	public async Task Rpc_lists_orchestrator_tools_only_for_the_orchestrator()
	{
		var rpc = CreateRpcWithBothToolSets();

		var ordinary = await ListToolsAsync(
			rpc,
			new AgentControlCaller("session-1", IsOrchestrator: false));
		var orchestrator = await ListToolsAsync(
			rpc,
			new AgentControlCaller(null, IsOrchestrator: true));

		ToolNames(ordinary).ShouldNotContain("pact_list_workspaces");
		ToolNames(orchestrator).ShouldContain("pact_list_workspaces");
		ToolNames(orchestrator).ShouldContain("pact_get_review_run");
		ToolNames(orchestrator).ShouldNotContain("pact_request_review");
	}

	private static AgentControlJsonRpc CreateRpcWithBothToolSets() => new(
		caller => caller.IsOrchestrator
			? CreateTools().BuildToolsList()
			: CreateSessionTools().BuildToolsList(),
		(_, _) => Task.FromResult(new AgentControlResultData("ok", IsError: false)));

	private static async Task<JsonNode> ListToolsAsync(
		AgentControlJsonRpc rpc,
		AgentControlCaller caller)
	{
		var response = await rpc.HandleAsync(
			JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!,
			caller,
			CancellationToken.None);
		return response!["result"]!;
	}

	private static string?[] ToolNames(JsonNode result) => result["tools"]!.AsArray()
		.Select(tool => (string?)tool!["name"])
		.ToArray();

	[Test]
	public void Hermes_soul_documents_every_orchestrator_tool()
	{
		foreach (var toolName in ToolNames(CreateTools().BuildToolsList()))
		{
			HermesTemplates.SoulMarkdown.ShouldContain(toolName!);
		}
	}

	private static AgentControlToolSet CreateTools() =>
		OrchestratorTools.Create(
			new OrchestratorDispatcher(new OrchestratorDispatcherTests.FakeOrchestratorHost()));

	private static AgentControlToolSet CreateSessionTools() =>
		CreateSessionTools(new AgentControlDispatcher(new AgentControlDispatcherTests.FakeAgentControlHost()));

	private static AgentControlToolSet CreateSessionTools(AgentControlDispatcher dispatcher) =>
		SessionTools.Create(
			dispatcher,
			new ReviewRelay(
				new ReviewRelayTests.FakeReviewRelayHost(Path.GetTempPath()),
				() => [],
				() => [],
				SharedLog,
				TimeProvider.System),
			() => [],
			() => [],
			_ => Task.CompletedTask,
			() => AgentControlResult.Ok("[]"),
			new RemarkTools(dispatcher, SharedBooks, (_, _) => Task.FromResult<string?>(null), _ => Task.CompletedTask));

	private static readonly Pact.Presentation.Services.Remarks.RemarkBooks SharedBooks = new(TimeProvider.System);

	private static readonly Pact.Infrastructure.Reviews.ReviewLogWriter SharedLog = new();

	private static JsonNode FindTool(string toolName) =>
		CreateTools().BuildToolsList()["tools"]!.AsArray()
			.Single(entry => (string?)entry!["name"] == toolName)!;
}
