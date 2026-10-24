using System.Text.Json.Nodes;
using Pact.Core.AgentControl;
using Pact.Core.Scenarios;
using Pact.Infrastructure.AgentControl;
using static Pact.Presentation.Services.AgentControl.AgentControlToolArguments;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>The tools an ordinary agent session may use, each described once.</summary>
public static class SessionTools
{
	/// <summary>Builds the session tool set.</summary>
	/// <param name="dispatcher">Runs each action against the calling session's owner.</param>
	/// <param name="scenarios">Current review scenarios; read on any thread when listing.</param>
	/// <param name="profiles">Current reviewer profiles; read on any thread when listing.</param>
	/// <param name="reportStatusAsync">Reports a successful action in the status bar.</param>
	public static AgentControlToolSet Create(
		AgentControlDispatcher dispatcher,
		Func<IReadOnlyList<ScenarioDefinition>> scenarios,
		Func<IReadOnlyList<ReviewProfile>> profiles,
		Func<string, Task> reportStatusAsync)
	{
		ArgumentNullException.ThrowIfNull(dispatcher);
		ArgumentNullException.ThrowIfNull(scenarios);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(reportStatusAsync);

		async Task<AgentControlResultData> CompleteAsync(
			AgentControlResult result,
			string successText,
			string statusText)
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

			await reportStatusAsync(statusText);
			return new AgentControlResultData(result.Payload ?? successText, IsError: false);
		}

		return new AgentControlToolSet(
		[
			new AgentControlTool(
				"pact_request_review",
				"Start an automated cross-agent review and return its run id.",
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

					var result = await dispatcher.RequestReviewAsync(
						call.SessionId ?? string.Empty,
						new RequestReviewRequest(scenarioId, reviewProfileId, target, maxIterations),
						cancellationToken);
					return await CompleteAsync(result, "Review run started.", "Agent action: review run started");
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
				})
		]);
	}
}
