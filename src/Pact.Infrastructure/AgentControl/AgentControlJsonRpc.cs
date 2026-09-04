using System.Text.Json.Nodes;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Text and error state returned for one tool invocation.</summary>
/// <param name="Text">Message shown to the agent.</param>
/// <param name="IsError">Whether the call was refused or failed.</param>
public sealed record AgentControlResultData(string Text, bool IsError);

/// <summary>Authenticated tool invocation passed to the stateless transport delegate.</summary>
/// <param name="SessionId">
/// Session resolved from the bearer token, or <see langword="null"/> for the orchestrator.
/// </param>
/// <param name="IsOrchestrator">Whether the caller may use cross-session tools.</param>
/// <param name="ToolName">Requested tool name.</param>
/// <param name="Arguments">Raw tool arguments.</param>
public sealed record AgentControlToolCall(
	string? SessionId,
	bool IsOrchestrator,
	string ToolName,
	JsonNode Arguments);

/// <summary>Handles Pact's initialize, tools/list, and tools/call MCP methods.</summary>
public sealed class AgentControlJsonRpc
{
	private const int MethodNotFound = -32601;
	private readonly Func<AgentControlCaller, JsonNode> _toolsListFactory;
	private readonly Func<AgentControlToolCall, CancellationToken, Task<AgentControlResultData>>
		_invokeAsync;

	/// <summary>Creates a handler with live tool-list and invocation delegates.</summary>
	public AgentControlJsonRpc(
		Func<AgentControlCaller, JsonNode> toolsListFactory,
		Func<AgentControlToolCall, CancellationToken, Task<AgentControlResultData>> invokeAsync)
	{
		ArgumentNullException.ThrowIfNull(toolsListFactory);
		ArgumentNullException.ThrowIfNull(invokeAsync);
		_toolsListFactory = toolsListFactory;
		_invokeAsync = invokeAsync;
	}

	/// <summary>Handles one request for its independently authenticated caller.</summary>
	public async Task<JsonNode?> HandleAsync(
		JsonNode request,
		AgentControlCaller caller,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(caller);
		var id = request["id"]?.DeepClone();
		if (id is null)
		{
			return null;
		}

		var method = (string?)request["method"] ?? string.Empty;
		return method switch
		{
			"initialize" => Success(id, BuildInitializeResult()),
			"tools/list" => Success(id, _toolsListFactory(caller)),
			"tools/call" => Success(
				id,
				await CallToolAsync(
					request["params"],
					caller,
					cancellationToken).ConfigureAwait(false)),
			_ => Error(id, MethodNotFound, $"Unsupported method '{method}'.")
		};
	}

	private async Task<JsonNode> CallToolAsync(
		JsonNode? parameters,
		AgentControlCaller caller,
		CancellationToken cancellationToken)
	{
		var name = (string?)parameters?["name"] ?? string.Empty;
		AgentControlResultData result;
		try
		{
			var arguments = parameters?["arguments"]?.DeepClone() ?? new JsonObject();
			result = await _invokeAsync(
				new AgentControlToolCall(
					caller.SessionId,
					caller.IsOrchestrator,
					name,
					arguments),
				cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			result = new AgentControlResultData(
				$"'{name}' failed: {exception.Message}",
				IsError: true);
		}

		return new JsonObject
		{
			["content"] = new JsonArray(new JsonObject
			{
				["type"] = "text",
				["text"] = result.Text
			}),
			["isError"] = result.IsError
		};
	}

	private static JsonObject BuildInitializeResult() => new()
	{
		["protocolVersion"] = "2025-06-18",
		["capabilities"] = new JsonObject
		{
			["tools"] = new JsonObject { ["listChanged"] = true }
		},
		["serverInfo"] = new JsonObject
		{
			["name"] = "pact",
			["version"] = "1"
		}
	};

	private static JsonObject Success(JsonNode id, JsonNode result) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id,
		["result"] = result
	};

	private static JsonObject Error(JsonNode id, int code, string message) => new()
	{
		["jsonrpc"] = "2.0",
		["id"] = id,
		["error"] = new JsonObject
		{
			["code"] = code,
			["message"] = message
		}
	};
}
