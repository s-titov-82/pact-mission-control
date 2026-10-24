using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>One MCP tool: what <c>tools/list</c> says about it and what a call does.</summary>
public sealed record AgentControlTool(
	string Name,
	string Description,
	Func<JsonObject> BuildInputSchema,
	Func<AgentControlToolCall, CancellationToken, Task<AgentControlResultData>> InvokeAsync);

/// <summary>The tools one kind of caller may use, listed and invoked from the same descriptions.</summary>
public sealed class AgentControlToolSet(IReadOnlyList<AgentControlTool> tools)
{
	private readonly IReadOnlyList<AgentControlTool> _tools = tools ?? throw new ArgumentNullException(nameof(tools));

	/// <summary>Builds the result returned by <c>tools/list</c>.</summary>
	public JsonNode BuildToolsList()
	{
		JsonArray items = [];
		foreach (var tool in _tools)
		{
			items.Add(new JsonObject
			{
				["name"] = tool.Name,
				["description"] = tool.Description,
				["inputSchema"] = tool.BuildInputSchema()
			});
		}

		return new JsonObject { ["tools"] = items };
	}

	/// <summary>Runs the named tool, or reports that no such tool exists.</summary>
	public Task<AgentControlResultData> InvokeAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(call);
		foreach (var tool in _tools)
		{
			if (string.Equals(tool.Name, call.ToolName, StringComparison.Ordinal))
			{
				return tool.InvokeAsync(call, cancellationToken);
			}
		}

		return Task.FromResult(new AgentControlResultData(
			$"Unknown tool '{call.ToolName}'.",
			IsError: true));
	}
}

/// <summary>JSON Schema fragments shared by the tool descriptions.</summary>
internal static class AgentControlSchema
{
	public static JsonObject Object(JsonObject properties, params string[] required)
	{
		JsonObject schema = new()
		{
			["type"] = "object",
			["properties"] = properties
		};
		if (required.Length > 0)
		{
			JsonArray requiredProperties = [];
			foreach (var property in required)
			{
				requiredProperties.Add(property);
			}

			schema["required"] = requiredProperties;
		}

		return schema;
	}

	public static JsonObject String(string description) => new()
	{
		["type"] = "string",
		["description"] = description
	};

	public static JsonObject Enumerated(string description, IEnumerable<string> values)
	{
		JsonArray items = [];
		foreach (var value in values)
		{
			items.Add(value);
		}

		return new JsonObject
		{
			["type"] = "string",
			["enum"] = items,
			["description"] = description
		};
	}

	public static JsonObject Integer(
		string description,
		int defaultValue,
		int minimum,
		int? maximum = null)
	{
		JsonObject property = new()
		{
			["type"] = "integer",
			["description"] = description,
			["default"] = defaultValue,
			["minimum"] = minimum
		};
		if (maximum is { } value)
		{
			property["maximum"] = value;
		}

		return property;
	}
}
