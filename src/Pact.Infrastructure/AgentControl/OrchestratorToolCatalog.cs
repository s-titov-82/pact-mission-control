using System.Text.Json.Nodes;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Declares the cross-session MCP tools available only to the orchestrator.</summary>
public static class OrchestratorToolCatalog
{
	/// <summary>Builds the result returned by <c>tools/list</c>.</summary>
	public static JsonNode BuildToolsListResult() => new JsonObject
	{
		["tools"] = new JsonArray(
			Tool(
				"pact_list_workspaces",
				"Every project and ROOT with their sessions: status, what each agent is doing, "
					+ "and when it entered that state. Start here for any question about who is "
					+ "working or what exists.",
				new JsonObject()),
			Tool(
				"pact_get_session",
				"One session in detail. `content: message` (default) returns the agent's last "
					+ "message together with `lastMessageIsCurrent`; when that flag is false the "
					+ "message is the agent's last known words and the screen has moved on, so "
					+ "say so rather than presenting it as the current state. `content: screen` "
					+ "returns the whole last stable screen, which is the fallback when the "
					+ "message looks wrong or empty.",
				new JsonObject
				{
					["sessionId"] = StringProperty("Session to inspect."),
					["content"] = new JsonObject
					{
						["type"] = "string",
						["enum"] = new JsonArray("message", "screen"),
						["default"] = "message",
						["description"] = "Content detail to return; defaults to message."
					}
				},
				"sessionId"),
			Tool(
				"pact_send_message",
				"Submit a prompt to a session, exactly as a human would type it.",
				new JsonObject
				{
					["sessionId"] = StringProperty("Session that receives the prompt."),
					["message"] = StringProperty("Prompt to submit.")
				},
				"sessionId",
				"message"),
			Tool(
				"pact_get_subscription_usage",
				"Remaining subscription budget per agent. Use it to decide when to act rather "
					+ "than acting immediately.",
				new JsonObject()),
			Tool(
				"pact_list_active_runs",
				"Scenario runs in progress, including pause state, current step, and the durable "
					+ "response file each run awaits. Sessions taking part are input-locked; "
					+ "report them as under review, not merely busy.",
				new JsonObject()),
			Tool(
				"pact_get_review_run",
				"One active review run in detail, including its in-memory journal and the exact "
					+ "task/response file exchange currently expected.",
				new JsonObject
				{
					["runId"] = StringProperty("Active review run to inspect.")
				},
				"runId"),
			Tool(
				"pact_pause_review",
				"Request a manual pause at the current safe boundary. A pending pause is retained; "
					+ "an attention pause is escalated so automatic writes stay blocked until Resume.",
				new JsonObject
				{
					["runId"] = StringProperty("Active review run to pause.")
				},
				"runId"),
			Tool(
				"pact_resume_review",
				"Resume an established review pause. Calling this while a pause is only requested "
					+ "does not cancel the pending pause.",
				new JsonObject
				{
					["runId"] = StringProperty("Active review run to resume.")
				},
				"runId"),
			Tool(
				"pact_get_project_notes",
				"Read the exact Notes buffer and revision for a running project workspace.",
				new JsonObject
				{
					["workspaceId"] = StringProperty("Running project workspace to inspect.")
				},
				"workspaceId"),
			Tool(
				"pact_replace_project_notes",
				"Replace the complete Notes buffer for a running project. Supply the revision "
					+ "returned by the latest read; an empty replacement deletes all existing text.",
				new JsonObject
				{
					["workspaceId"] = StringProperty("Running project workspace to update."),
					["text"] = StringProperty("Complete replacement Notes text; may be empty."),
					["expectedRevision"] = StringProperty(
						"Revision returned by pact_get_project_notes.")
				},
				"workspaceId",
				"text",
				"expectedRevision"),
			Tool(
				"pact_append_project_note",
				"Append non-blank text to the current Notes buffer for a running project.",
				new JsonObject
				{
					["workspaceId"] = StringProperty("Running project workspace to update."),
					["text"] = StringProperty("Non-blank text to append.")
				},
				"workspaceId",
				"text"),
			Tool(
				"pact_list_web_tabs",
				"List saved web tabs owned by running projects and ROOT. Active tabs have a "
					+ "loaded browser host; paused tabs must be resumed before reading HTML.",
				new JsonObject()),
			Tool(
				"pact_resume_web_tab",
				"Load a known paused web tab in the background without selecting or focusing it.",
				new JsonObject
				{
					["pageId"] = StringProperty("Saved web tab to load.")
				},
				"pageId"),
			Tool(
				"pact_get_web_tab_html",
				"Read a bounded UTF-16 slice of an active tab's live documentElement HTML. "
					+ "Continue with nextOffset; restart at offset 0 if URL or totalLength changes "
					+ "between calls.",
				new JsonObject
				{
					["pageId"] = StringProperty("Active saved web tab to inspect."),
					["offset"] = IntegerProperty(
						"Zero-based UTF-16 offset.",
						defaultValue: 0,
						minimum: 0),
					["maxChars"] = IntegerProperty(
						"Maximum UTF-16 code units to return.",
						defaultValue: 100_000,
						minimum: 1,
						maximum: 200_000)
				},
				"pageId"))
	};

	private static JsonObject Tool(
		string name,
		string description,
		JsonObject properties,
		params string[] required)
	{
		JsonArray requiredProperties = [];
		foreach (var property in required)
		{
			requiredProperties.Add(property);
		}

		return new JsonObject
		{
			["name"] = name,
			["description"] = description,
			["inputSchema"] = new JsonObject
			{
				["type"] = "object",
				["properties"] = properties,
				["required"] = requiredProperties
			}
		};
	}

	private static JsonObject StringProperty(string description) => new()
	{
		["type"] = "string",
		["description"] = description
	};

	private static JsonObject IntegerProperty(
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
