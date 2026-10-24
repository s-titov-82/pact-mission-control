using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Tests.Services.AgentControl;

public sealed class AgentControlToolSetTests
{
	[Test]
	public async Task Unknown_tool_is_an_error()
	{
		AgentControlToolSet tools = new([]);

		var result = await tools.InvokeAsync(
			new AgentControlToolCall("session-1", IsOrchestrator: false, "pact_nope", new JsonObject()),
			CancellationToken.None);

		result.IsError.ShouldBeTrue();
		result.Text.ShouldBe("Unknown tool 'pact_nope'.");
	}

	[Test]
	public async Task Each_tool_lists_and_answers_from_one_description()
	{
		AgentControlToolSet tools = new(
		[
			new AgentControlTool(
				"pact_echo",
				"Echo.",
				() => new JsonObject { ["type"] = "object" },
				(call, _) => Task.FromResult(new AgentControlResultData(call.ToolName, IsError: false)))
		]);

		var listed = tools.BuildToolsList()["tools"]!.AsArray().Single()!;
		var result = await tools.InvokeAsync(
			new AgentControlToolCall("session-1", IsOrchestrator: false, "pact_echo", new JsonObject()),
			CancellationToken.None);

		((string?)listed["name"]).ShouldBe("pact_echo");
		((string?)listed["description"]).ShouldBe("Echo.");
		((string?)listed["inputSchema"]!["type"]).ShouldBe("object");
		result.Text.ShouldBe("pact_echo");
	}
}
