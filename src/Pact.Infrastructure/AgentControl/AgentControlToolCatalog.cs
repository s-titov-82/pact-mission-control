using System.Text.Json.Nodes;
using Pact.Core.AgentControl;
using Pact.Core.Scenarios;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Builds MCP declarations from the live scenario and reviewer settings snapshots.</summary>
public static class AgentControlToolCatalog
{
	/// <summary>Builds the result returned by <c>tools/list</c>.</summary>
	public static JsonNode BuildToolsListResult(
		IReadOnlyList<ScenarioDefinition> scenarios,
		IReadOnlyList<ReviewProfile> reviewProfiles)
	{
		ArgumentNullException.ThrowIfNull(scenarios);
		ArgumentNullException.ThrowIfNull(reviewProfiles);
		return new JsonObject
		{
			["tools"] = new JsonArray(
				BuildRequestReviewTool(scenarios, reviewProfiles),
				BuildAppendNoteTool(),
				BuildOpenWebTabTool(),
				BuildGetNotesTool(),
				BuildReplaceNotesTool())
		};
	}

	private static JsonObject BuildRequestReviewTool(
		IReadOnlyList<ScenarioDefinition> scenarios,
		IReadOnlyList<ReviewProfile> profiles) => new()
		{
			["name"] = "pact_request_review",
			["description"] = "Start an automated cross-agent review and return its run id.",
			["inputSchema"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = new JsonObject
				{
					["scenarioId"] = Enumerated(
					"Review scenario.",
					scenarios.Select(value => value.Id)),
					["reviewProfileId"] = Enumerated(
					"Reviewer profile.",
					profiles.Select(value => value.Id)),
					["target"] = StringProperty("Path, branch, diff, or text under review."),
					["maxIterations"] = new JsonObject
					{
						["type"] = "integer",
						["minimum"] = 1
					}
				},
				["required"] = new JsonArray("scenarioId", "reviewProfileId", "target")
			}
		};

	private static JsonObject BuildAppendNoteTool() => new()
	{
		["name"] = "pact_append_note",
		["description"] = "Append text to this session's project notes.",
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject { ["text"] = StringProperty("Markdown to append.") },
			["required"] = new JsonArray("text")
		}
	};

	private static JsonObject BuildOpenWebTabTool() => new()
	{
		["name"] = "pact_open_web_tab",
		["description"] = "Open an HTTP(S) address under this session's owner.",
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["url"] = StringProperty("Absolute HTTP or HTTPS address."),
				["title"] = StringProperty("Optional tab label.")
			},
			["required"] = new JsonArray("url")
		}
	};

	private static JsonObject BuildGetNotesTool() => new()
	{
		["name"] = "pact_get_notes",
		["description"] = "Read this session's current project Notes text and revision.",
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject()
		}
	};

	private static JsonObject BuildReplaceNotesTool() => new()
	{
		["name"] = "pact_replace_notes",
		["description"] = "Replace this session's project Notes when the supplied revision is current.",
		["inputSchema"] = new JsonObject
		{
			["type"] = "object",
			["properties"] = new JsonObject
			{
				["text"] = StringProperty("Complete replacement Markdown; may be empty."),
				["expectedRevision"] = StringProperty("Revision returned by pact_get_notes.")
			},
			["required"] = new JsonArray("text", "expectedRevision")
		}
	};

	private static JsonObject StringProperty(string description) => new()
	{
		["type"] = "string",
		["description"] = description
	};

	private static JsonObject Enumerated(
		string description,
		IEnumerable<string> values)
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
}
