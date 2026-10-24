using System.Text.Json.Nodes;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.Orchestrator;
using static Pact.Presentation.Services.AgentControl.AgentControlToolArguments;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>The cross-session tools only the orchestrator may use, each described once.</summary>
public static class OrchestratorTools
{
	/// <summary>Builds the orchestrator tool set.</summary>
	public static AgentControlToolSet Create(OrchestratorDispatcher dispatcher)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		return new AgentControlToolSet(
		[
			Tool(
				"pact_list_workspaces",
				"Every project and ROOT with their sessions: status, what each agent is doing, "
					+ "and when it entered that state. Start here for any question about who is "
					+ "working or what exists.",
				new JsonObject(),
				(_, _) => Task.FromResult(Complete(dispatcher.ListWorkspaces()))),
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
					["sessionId"] = AgentControlSchema.String("Session to inspect."),
					["content"] = new JsonObject
					{
						["type"] = "string",
						["enum"] = new JsonArray("message", "screen"),
						["default"] = "message",
						["description"] = "Content detail to return; defaults to message."
					}
				},
				(call, _) =>
				{
					if (!TryGetRequiredString(call.Arguments, "sessionId", out var sessionId, out var error)
						|| !TryGetOptionalString(call.Arguments, "content", out var content, out error))
					{
						return Task.FromResult(error);
					}

					return Task.FromResult(Complete(dispatcher.GetSession(sessionId, content ?? "message")));
				},
				"sessionId"),
			Tool(
				"pact_send_message",
				"Submit a prompt to a session, exactly as a human would type it.",
				new JsonObject
				{
					["sessionId"] = AgentControlSchema.String("Session that receives the prompt."),
					["message"] = AgentControlSchema.String("Prompt to submit.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "sessionId", out var sessionId, out var error)
						|| !TryGetRequiredString(call.Arguments, "message", out var message, out error))
					{
						return error;
					}

					return Complete(await dispatcher.SendMessageAsync(sessionId, message, cancellationToken));
				},
				"sessionId",
				"message"),
			Tool(
				"pact_start_session",
				"Start a session that is not running, the same way opening its tab would, and wait "
					+ "until it can take a prompt. pact_send_message refuses sessions that are not running.",
				new JsonObject
				{
					["sessionId"] = AgentControlSchema.String("Session to start.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "sessionId", out var sessionId, out var error))
					{
						return error;
					}

					return Complete(await dispatcher.StartSessionAsync(sessionId, cancellationToken));
				},
				"sessionId"),
			Tool(
				"pact_get_subscription_usage",
				"Remaining subscription budget per agent. Use it to decide when to act rather "
					+ "than acting immediately.",
				new JsonObject(),
				(_, _) => Task.FromResult(Complete(dispatcher.GetSubscriptionUsage()))),
			Tool(
				"pact_list_active_runs",
				"Reviews in progress between two agents: participants, state, iteration, whose turn "
					+ "it is, and the log file under .pact/reviews. The user can still type into both tabs.",
				new JsonObject(),
				(_, _) => Task.FromResult(Complete(dispatcher.ListActiveRuns()))),
			Tool(
				"pact_get_review_run",
				"One active review: participants, state, iteration, whose turn it is and its log file.",
				new JsonObject
				{
					["runId"] = AgentControlSchema.String("Review id from pact_list_active_runs.")
				},
				(call, _) => Task.FromResult(
					TryGetRequiredString(call.Arguments, "runId", out var reviewId, out var error)
						? Complete(dispatcher.GetReviewRun(reviewId))
						: error),
				"runId"),
			Tool(
				"pact_get_project_notes",
				"Read the exact Notes buffer and revision for a running project workspace.",
				new JsonObject
				{
					["workspaceId"] = AgentControlSchema.String("Running project workspace to inspect.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "workspaceId", out var workspaceId, out var error))
					{
						return error;
					}

					return Complete(await dispatcher.GetProjectNotesAsync(workspaceId, cancellationToken));
				},
				"workspaceId"),
			Tool(
				"pact_replace_project_notes",
				"Replace the complete Notes buffer for a running project. Supply the revision "
					+ "returned by the latest read; an empty replacement deletes all existing text.",
				new JsonObject
				{
					["workspaceId"] = AgentControlSchema.String("Running project workspace to update."),
					["text"] = AgentControlSchema.String("Complete replacement Notes text; may be empty."),
					["expectedRevision"] = AgentControlSchema.String(
						"Revision returned by pact_get_project_notes.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "workspaceId", out var workspaceId, out var error)
						|| !TryGetRequiredString(call.Arguments, "text", out var text, out error)
						|| !TryGetRequiredString(call.Arguments, "expectedRevision", out var expectedRevision, out error))
					{
						return error;
					}

					return Complete(await dispatcher.ReplaceProjectNotesAsync(
						workspaceId,
						new ReplaceNoteRequest(text, expectedRevision),
						cancellationToken));
				},
				"workspaceId",
				"text",
				"expectedRevision"),
			Tool(
				"pact_append_project_note",
				"Append non-blank text to the current Notes buffer for a running project.",
				new JsonObject
				{
					["workspaceId"] = AgentControlSchema.String("Running project workspace to update."),
					["text"] = AgentControlSchema.String("Non-blank text to append.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "workspaceId", out var workspaceId, out var error)
						|| !TryGetRequiredString(call.Arguments, "text", out var text, out error))
					{
						return error;
					}

					return Complete(await dispatcher.AppendProjectNoteAsync(workspaceId, text, cancellationToken));
				},
				"workspaceId",
				"text"),
			Tool(
				"pact_list_web_tabs",
				"List saved web tabs owned by running projects and ROOT. Active tabs have a "
					+ "loaded browser host; paused tabs must be resumed before reading HTML.",
				new JsonObject(),
				(_, _) => Task.FromResult(Complete(dispatcher.ListWebTabs()))),
			Tool(
				"pact_resume_web_tab",
				"Load a known paused web tab in the background without selecting or focusing it.",
				new JsonObject
				{
					["pageId"] = AgentControlSchema.String("Saved web tab to load.")
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "pageId", out var pageId, out var error))
					{
						return error;
					}

					return Complete(await dispatcher.ResumeWebTabAsync(pageId, cancellationToken));
				},
				"pageId"),
			Tool(
				"pact_get_web_tab_html",
				"Read a bounded UTF-16 slice of an active tab's live documentElement HTML. "
					+ "Continue with nextOffset; restart at offset 0 if URL or totalLength changes "
					+ "between calls.",
				new JsonObject
				{
					["pageId"] = AgentControlSchema.String("Active saved web tab to inspect."),
					["offset"] = AgentControlSchema.Integer(
						"Zero-based UTF-16 offset.",
						defaultValue: 0,
						minimum: 0),
					["maxChars"] = AgentControlSchema.Integer(
						"Maximum UTF-16 code units to return.",
						defaultValue: 100_000,
						minimum: 1,
						maximum: 200_000)
				},
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "pageId", out var pageId, out var error)
						|| !TryGetOptionalInt32(call.Arguments, "offset", out var offset, out error)
						|| !TryGetOptionalInt32(call.Arguments, "maxChars", out var maxChars, out error))
					{
						return error;
					}

					return Complete(await dispatcher.GetWebTabHtmlAsync(
						pageId,
						offset ?? 0,
						maxChars ?? WebPageDocumentRange.DefaultMaxChars,
						cancellationToken));
				},
				"pageId")
		]);
	}

	private static AgentControlTool Tool(
		string name,
		string description,
		JsonObject properties,
		Func<AgentControlToolCall, CancellationToken, Task<AgentControlResultData>> invokeAsync,
		params string[] required) =>
		new(
			name,
			description,
			() => AgentControlSchema.Object(properties.DeepClone().AsObject(), required),
			invokeAsync);

	private static AgentControlResultData Complete(AgentControlResult result) =>
		result.Succeeded
			? new AgentControlResultData(result.Payload ?? "Done.", IsError: false)
			: new AgentControlResultData(
				result.Failure is { } failure
					? $"{failure.Code}: {failure.Message}"
					: "The orchestrator action was refused.",
				IsError: true);
}
