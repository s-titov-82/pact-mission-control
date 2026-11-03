using System.Text.Json.Nodes;
using Pact.Core.AgentControl;
using Pact.Core.Scenarios;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.Reviews;
using static Pact.Presentation.Services.AgentControl.AgentControlToolArguments;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>The tools an ordinary agent session may use, each described once.</summary>
public static class SessionTools
{
	/// <summary>Builds the session tool set.</summary>
	/// <param name="dispatcher">Runs each action against the calling session's owner.</param>
	/// <param name="relay">Runs the reviews agents ask for.</param>
	/// <param name="scenarios">Current review scenarios; read on any thread when listing.</param>
	/// <param name="profiles">Current reviewer profiles; read on any thread when listing.</param>
	/// <param name="reportStatusAsync">Reports a successful action in the status bar.</param>
	/// <param name="subscriptionUsage">Reads the subscription usage Pact shows, as for the orchestrator.</param>
	/// <param name="remarks">The tools that answer and list the user's remark threads.</param>
	public static AgentControlToolSet Create(
		AgentControlDispatcher dispatcher,
		ReviewRelay relay,
		Func<IReadOnlyList<ScenarioDefinition>> scenarios,
		Func<IReadOnlyList<ReviewProfile>> profiles,
		Func<string, Task> reportStatusAsync,
		Func<AgentControlResult> subscriptionUsage,
		RemarkTools remarks)
	{
		ArgumentNullException.ThrowIfNull(remarks);
		ArgumentNullException.ThrowIfNull(subscriptionUsage);
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(relay);
		ArgumentNullException.ThrowIfNull(scenarios);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(reportStatusAsync);

		async Task<AgentControlResultData> CompleteAsync(
			AgentControlResult result,
			string successText,
			string? statusText)
		{
			if (!result.Succeeded)
			{
				var failure = result.Failure;
				return new AgentControlResultData(
					failure is null
						? "The agent action was refused."
						: $"{failure.Code}: {failure.Message}",
					IsError: true);
			}

			if (statusText is not null)
			{
				await reportStatusAsync(statusText);
			}

			return new AgentControlResultData(result.Payload ?? successText, IsError: false);
		}

		return new AgentControlToolSet(
		[
			new AgentControlTool(
				"pact_add_project_link",
				"Save an HTTP(S) resource in this session's project Overview. Does not open a browser or fetch status.",
				() => AgentControlSchema.Object(new()
				{
					["title"] = AgentControlSchema.String("Short label, 1 to 200 characters."),
					["url"] = AgentControlSchema.String("Absolute HTTP(S) URL without user information."),
					["kind"] = AgentControlSchema.Enumerated("Resource kind; omitted means other.", ["ci", "merge_request", "issue", "other"])
				}, "title", "url"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "title", out var title, out var error)
						|| !TryGetRequiredString(call.Arguments, "url", out var url, out error)
						|| !TryGetOptionalString(call.Arguments, "kind", out var kind, out error))
					{
						return error;
					}

					return await CompleteAsync(
						await dispatcher.AddProjectLinkAsync(call.SessionId ?? string.Empty, title, url, kind, cancellationToken),
						"Project link saved.", "Agent action: project link saved");
				}),
			new AgentControlTool(
				"pact_list_project_links",
				"List saved resources in this session's project, newest first. ROOT has no project links.",
				() => AgentControlSchema.Object(new()),
				async (call, cancellationToken) => await CompleteAsync(
					await dispatcher.ListProjectLinksAsync(call.SessionId ?? string.Empty, cancellationToken),
					"[]", statusText: null)),
			new AgentControlTool(
				"pact_request_review",
				"Ask another agent to review your work. A reviewer tab opens in your project and turns "
					+ "pass through Pact: when Pact types that your turn is ready, call pact_get_review_turn. "
					+ "Returns the review id.",
				() => AgentControlSchema.Object(
					new()
					{
						["scenarioId"] = AgentControlSchema.Enumerated(
							"Review scenario.",
							scenarios().Select(value => value.Id)),
						["reviewProfileId"] = AgentControlSchema.Enumerated(
							"Reviewer profile.",
							profiles().Select(value => value.Id)),
						["target"] = AgentControlSchema.String("Path, branch, diff, or text under review."),
						["maxIterations"] = new JsonObject
						{
							["type"] = "integer",
							["minimum"] = 1
						}
					},
					"scenarioId",
					"reviewProfileId",
					"target"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "scenarioId", out var scenarioId, out var error)
						|| !TryGetRequiredString(call.Arguments, "reviewProfileId", out var reviewProfileId, out error)
						|| !TryGetRequiredString(call.Arguments, "target", out var target, out error)
						|| !TryGetOptionalInt32(call.Arguments, "maxIterations", out var maxIterations, out error))
					{
						return error;
					}

					var result = await relay.RequestAsync(
						call.SessionId ?? string.Empty,
						new RequestReviewRequest(scenarioId, reviewProfileId, target, maxIterations),
						cancellationToken);
					return await CompleteAsync(result, "Review started.", "Agent action: review requested");
				}),
			new AgentControlTool(
				"pact_get_review_turn",
				"Your review turn: the message to act on, the other side's last answer as previousTurn and "
					+ "the turnId to answer with. After the review has ended, how it ended and the last answer (finalText) with the side that gave it (finalFrom).",
				() => AgentControlSchema.Object(new()),
				async (call, _) => await CompleteAsync(
					relay.GetTurn(call.SessionId ?? string.Empty),
					string.Empty,
					statusText: null)),
			new AgentControlTool(
				"pact_submit_review_turn",
				"Hand your review turn back with your complete answer as text and the turnId from "
					+ "pact_get_review_turn. A reviewer approves with approved: true instead of writing a completion marker.",
				() => AgentControlSchema.Object(
					new()
					{
						["turnId"] = AgentControlSchema.String("The turnId returned by pact_get_review_turn."),
						["text"] = AgentControlSchema.String("Your complete answer."),
						["approved"] = new JsonObject
						{
							["type"] = "boolean",
							["description"] = "Reviewer only: approve the work and end the review."
						}
					},
					"turnId",
					"text"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "turnId", out var turnId, out var error)
						|| !TryGetRequiredString(call.Arguments, "text", out var text, out error)
						|| !TryGetOptionalBoolean(call.Arguments, "approved", out var approved, out error))
					{
						return error;
					}

					var result = await relay.SubmitAsync(
						call.SessionId ?? string.Empty,
						turnId,
						text,
						approved == true,
						cancellationToken);
					return await CompleteAsync(result, "Turn submitted.", "Agent action: review turn submitted");
				}),
			new AgentControlTool(
				"pact_append_note",
				"Append text to this session's project notes.",
				() => AgentControlSchema.Object(
					new() { ["text"] = AgentControlSchema.String("Markdown to append.") },
					"text"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "text", out var text, out var error))
					{
						return error;
					}

					var result = await dispatcher.AppendNoteAsync(
						call.SessionId ?? string.Empty,
						new AppendNoteRequest(text),
						cancellationToken);
					return await CompleteAsync(result, "Notes updated.", "Agent action: notes updated");
				}),
			new AgentControlTool(
				"pact_open_web_tab",
				"Open an HTTP(S) address under this session's owner.",
				() => AgentControlSchema.Object(
					new()
					{
						["url"] = AgentControlSchema.String("Absolute HTTP or HTTPS address."),
						["title"] = AgentControlSchema.String("Optional tab label.")
					},
					"url"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "url", out var url, out var error)
						|| !TryGetOptionalString(call.Arguments, "title", out var title, out error))
					{
						return error;
					}

					var result = await dispatcher.OpenWebTabAsync(
						call.SessionId ?? string.Empty,
						new OpenWebTabRequest(url, title),
						cancellationToken);
					return await CompleteAsync(result, "Browser tab opened.", "Agent action: browser tab opened");
				}),
			new AgentControlTool(
				"pact_open_document",
				"Open a Markdown file of this session's project in the Docs pane and show it to the user.",
				() => AgentControlSchema.Object(
					new()
					{
						["path"] = AgentControlSchema.String(
							"Path relative to the project root, or an absolute path inside it.")
					},
					"path"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "path", out var path, out var error))
					{
						return error;
					}

					var result = await dispatcher.OpenDocumentAsync(
						call.SessionId ?? string.Empty,
						new OpenDocumentRequest(path),
						cancellationToken);
					return await CompleteAsync(result, "Document opened.", "Agent action: document opened");
				}),
			new AgentControlTool(
				"pact_add_file_link",
				"Add a link to a file to this session's list in Pact's right column, so the user can open it "
					+ "read-only over the terminal. Use it for a file you wrote outside the project (scratchpad, "
					+ "Temp) or one the user should read. The file must exist. Does not open it.",
				() => AgentControlSchema.Object(
					new()
					{
						["path"] = AgentControlSchema.String("Absolute path, or relative to your working directory."),
						["title"] = AgentControlSchema.String("Optional short label.")
					},
					"path"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "path", out var path, out var error)
						|| !TryGetOptionalString(call.Arguments, "title", out var title, out error))
					{
						return error;
					}

					var result = await dispatcher.AddFileLinkAsync(
						call.SessionId ?? string.Empty,
						new AddFileLinkRequest(path, title),
						cancellationToken);
					return await CompleteAsync(result, "File linked.", "Agent action: file linked");
				}),
			new AgentControlTool(
				"pact_get_notes",
				"Read this session's current project Notes text and revision.",
				() => AgentControlSchema.Object(new()),
				async (call, cancellationToken) =>
				{
					var result = await dispatcher.GetNotesAsync(
						call.SessionId ?? string.Empty,
						cancellationToken);
					return await CompleteAsync(result, "Notes read.", "Agent action: notes read");
				}),
			new AgentControlTool(
				"pact_replace_notes",
				"Replace this session's project Notes when the supplied revision is current.",
				() => AgentControlSchema.Object(
					new()
					{
						["text"] = AgentControlSchema.String("Complete replacement Markdown; may be empty."),
						["expectedRevision"] = AgentControlSchema.String("Revision returned by pact_get_notes.")
					},
					"text",
					"expectedRevision"),
				async (call, cancellationToken) =>
				{
					if (!TryGetRequiredString(call.Arguments, "text", out var text, out var error)
						|| !TryGetRequiredString(call.Arguments, "expectedRevision", out var expectedRevision, out error))
					{
						return error;
					}

					var result = await dispatcher.ReplaceNotesAsync(
						call.SessionId ?? string.Empty,
						new ReplaceNoteRequest(text, expectedRevision),
						cancellationToken);
					return await CompleteAsync(result, "Notes replaced.", "Agent action: notes replaced");
				}),
			new AgentControlTool(
				"pact_get_subscription_usage",
				"Subscription usage per agent profile, as the text Pact shows for the 5-hour and weekly "
					+ "limits (used percent and when each resets). Use it to plan work or to schedule a "
					+ "wake-up after a reset.",
				() => AgentControlSchema.Object(new()),
				(_, _) => CompleteAsync(subscriptionUsage(), "[]", statusText: null)),
			.. remarks.Create()
		]);
	}
}
