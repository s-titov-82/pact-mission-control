using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlJsonRpcTests
{
	private static AgentControlJsonRpc CreateRpc(
		Func<AgentControlToolCall, Task<AgentControlResultData>>? invoke = null) =>
		new(
			_ => new JsonObject { ["tools"] = new JsonArray() },
			(call, _) => invoke?.Invoke(call)
				?? Task.FromResult(new AgentControlResultData("done", IsError: false)));

	[Test]
	public async Task HandleAsync_RejectsANullCaller()
	{
		var request = JsonNode.Parse(
			"""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!;

		await Should.ThrowAsync<ArgumentNullException>(() =>
			CreateRpc().HandleAsync(request, null!, CancellationToken.None));
	}

	[Test]
	public async Task HandleAsync_CarriesEachRequestsOwnSessionIdToTheInvoker()
	{
		List<string> seen = [];
		var rpc = CreateRpc(call =>
		{
			seen.Add(call.SessionId!);
			return Task.FromResult(new AgentControlResultData("ok", IsError: false));
		});
		var request = JsonNode.Parse(
			"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pact_append_note","arguments":{"text":"x"}}}""")!;

		await rpc.HandleAsync(request, SessionCaller("session-a"), CancellationToken.None);
		await rpc.HandleAsync(request, SessionCaller("session-b"), CancellationToken.None);

		seen.ShouldBe(["session-a", "session-b"]);
	}

	[Test]
	public async Task HandleAsync_PassesToolNameAndArgumentsThrough()
	{
		AgentControlToolCall? captured = null;
		var rpc = CreateRpc(call =>
		{
			captured = call;
			return Task.FromResult(new AgentControlResultData("ok", IsError: false));
		});

		await rpc.HandleAsync(
			JsonNode.Parse(
				"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pact_append_note","arguments":{"text":"hello"}}}""")!,
			SessionCaller("session-a"),
			CancellationToken.None);

		captured!.ToolName.ShouldBe("pact_append_note");
		((string?)captured.Arguments["text"]).ShouldBe("hello");
	}

	[Test]
	public async Task HandleAsync_carries_orchestrator_rights_to_the_invoker()
	{
		AgentControlToolCall? captured = null;
		var rpc = CreateRpc(call =>
		{
			captured = call;
			return Task.FromResult(new AgentControlResultData("ok", IsError: false));
		});

		await rpc.HandleAsync(
			JsonNode.Parse(
				"""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"pact_list_workspaces"}}""")!,
			new AgentControlCaller(null, IsOrchestrator: true),
			CancellationToken.None);

		captured!.SessionId.ShouldBeNull();
		captured.IsOrchestrator.ShouldBeTrue();
	}

	[Test]
	public async Task HandleAsync_AnswersInitialize()
	{
		var response = await CreateRpc().HandleAsync(
			JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""")!,
			SessionCaller("session-1"),
			CancellationToken.None);

		response!["result"]!["serverInfo"]!["name"]!.GetValue<string>().ShouldBe("pact");
	}

	[Test]
	public async Task HandleAsync_advertises_tool_list_change_notifications()
	{
		var response = await CreateRpc().HandleAsync(
			JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""")!,
			SessionCaller("session-1"),
			CancellationToken.None);

		response!["result"]!["capabilities"]!["tools"]!["listChanged"]!
			.GetValue<bool>().ShouldBeTrue();
	}

	[Test]
	public async Task HandleAsync_ReturnsNullForNotification()
	{
		var response = await CreateRpc().HandleAsync(
			JsonNode.Parse("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")!,
			SessionCaller("session-1"),
			CancellationToken.None);

		response.ShouldBeNull();
	}

	[Test]
	public async Task HandleAsync_ReportsRefusalAsToolError()
	{
		var rpc = CreateRpc(_ => Task.FromResult(
			new AgentControlResultData("This session is a ROOT tab.", IsError: true)));

		var response = await rpc.HandleAsync(
			JsonNode.Parse(
				"""{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"pact_append_note","arguments":{"text":"x"}}}""")!,
			SessionCaller("session-1"),
			CancellationToken.None);

		response!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
		response["result"]!["content"]![0]!["text"]!.GetValue<string>()
			.ShouldContain("ROOT tab");
	}

	[Test]
	public async Task HandleAsync_ReportsAThrowingOperationAsAToolError()
	{
		var rpc = CreateRpc(_ => throw new IOException("projects.json is locked"));

		var response = await rpc.HandleAsync(
			JsonNode.Parse(
				"""{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"pact_append_note","arguments":{"text":"x"}}}""")!,
			SessionCaller("session-a"),
			CancellationToken.None);

		response!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
		response["result"]!["content"]![0]!["text"]!.GetValue<string>()
			.ShouldContain("locked");
		response["error"].ShouldBeNull();
	}

	[Test]
	public async Task HandleAsync_ReportsMalformedArgumentsAsAToolError()
	{
		var rpc = CreateRpc(call => throw new JsonException(
			$"'{call.ToolName}' arguments could not be read."));

		var response = await rpc.HandleAsync(
			JsonNode.Parse(
				"""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"pact_open_web_tab","arguments":{"url":42}}}""")!,
			SessionCaller("session-a"),
			CancellationToken.None);

		response!["result"]!["isError"]!.GetValue<bool>().ShouldBeTrue();
	}

	[Test]
	public async Task HandleAsync_LetsShutdownCancellationPropagate()
	{
		using CancellationTokenSource cancellation = new();
		await cancellation.CancelAsync();
		var rpc = CreateRpc(_ => throw new OperationCanceledException());

		async Task Act()
		{
			await rpc.HandleAsync(
				JsonNode.Parse(
					"""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"pact_append_note","arguments":{"text":"x"}}}""")!,
				SessionCaller("session-a"),
				cancellation.Token);
		}

		await Should.ThrowAsync<OperationCanceledException>(Act);
	}

	[Test]
	public async Task HandleAsync_ReturnsMethodNotFoundForUnknownMethod()
	{
		var response = await CreateRpc().HandleAsync(
			JsonNode.Parse("""{"jsonrpc":"2.0","id":4,"method":"resources/list"}""")!,
			SessionCaller("session-1"),
			CancellationToken.None);

		response!["error"]!["code"]!.GetValue<int>().ShouldBe(-32601);
	}

	private static AgentControlCaller SessionCaller(string sessionId) =>
		new(sessionId, IsOrchestrator: false);
}
