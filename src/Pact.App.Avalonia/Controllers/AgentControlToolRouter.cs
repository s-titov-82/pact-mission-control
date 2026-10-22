using System.Text.Json.Nodes;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Core.Scenarios;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Orchestrator;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Parses MCP tool arguments and routes each call to the session or orchestrator dispatcher.
/// </summary>
internal sealed class AgentControlToolRouter(
	AgentControlDispatcher agentDispatcher,
	OrchestratorDispatcher orchestratorDispatcher,
	MainWindowViewModel viewModel,
	ReviewProfileProvider reviewProfiles,
	IUiTaskDispatcher uiTaskDispatcher,
	Func<string, Task> reportStatusAsync)
{
	private readonly AgentControlDispatcher _agentDispatcher = agentDispatcher ?? throw new ArgumentNullException(nameof(agentDispatcher));
	private readonly OrchestratorDispatcher _orchestratorDispatcher = orchestratorDispatcher ?? throw new ArgumentNullException(nameof(orchestratorDispatcher));
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly ReviewProfileProvider _reviewProfileProvider = reviewProfiles ?? throw new ArgumentNullException(nameof(reviewProfiles));
	private readonly IUiTaskDispatcher _uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
	private readonly Func<string, Task> _reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));

	/// <summary>Lists the tools the caller may use.</summary>
	public JsonNode BuildToolsList(bool orchestrator) =>
		orchestrator
			? OrchestratorToolCatalog.BuildToolsListResult()
			: BuildAgentControlToolsList();

	private JsonNode BuildAgentControlToolsList()
	{
		ScenarioDefinition[] scenarios = [];
		_uiTaskDispatcher.Post(() => scenarios = _viewModel.ScenarioDefinitions.ToArray());
		return AgentControlToolCatalog.BuildToolsListResult(
			scenarios,
			_reviewProfileProvider.Current);
	}

	/// <summary>Validates a tool call's arguments and runs it.</summary>
	public async Task<AgentControlResultData> InvokeAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		if (call.IsOrchestrator)
		{
			return await InvokeOrchestratorToolAsync(call, cancellationToken);
		}

		AgentControlResult result;
		string successText;
		string statusText;
		var sessionId = call.SessionId ?? string.Empty;
		switch (call.ToolName)
		{
			case "pact_get_notes":
				result = await _agentDispatcher.GetNotesAsync(
					sessionId,
					cancellationToken);
				successText = "Notes read.";
				statusText = "Agent action: notes read";
				break;

			case "pact_replace_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var replacementText,
						out var replacementError))
				{
					return replacementError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"expectedRevision",
						out var expectedRevision,
						out var revisionError))
				{
					return revisionError;
				}

				result = await _agentDispatcher.ReplaceNotesAsync(
					sessionId,
					new ReplaceNoteRequest(replacementText, expectedRevision),
					cancellationToken);
				successText = "Notes replaced.";
				statusText = "Agent action: notes replaced";
				break;

			case "pact_append_note":
				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var noteText,
						out var noteError))
				{
					return noteError;
				}

				result = await _agentDispatcher.AppendNoteAsync(
					sessionId,
					new AppendNoteRequest(noteText),
					cancellationToken);
				successText = "Notes updated.";
				statusText = "Agent action: notes updated";
				break;

			case "pact_open_web_tab":
				if (!TryGetRequiredString(
						call.Arguments,
						"url",
						out var url,
						out var urlError))
				{
					return urlError;
				}

				if (!TryGetOptionalString(
						call.Arguments,
						"title",
						out var title,
						out var titleError))
				{
					return titleError;
				}

				result = await _agentDispatcher.OpenWebTabAsync(
					sessionId,
					new OpenWebTabRequest(url, title),
					cancellationToken);
				successText = "Browser tab opened.";
				statusText = "Agent action: browser tab opened";
				break;

			case "pact_request_review":
				if (!TryGetRequiredString(
						call.Arguments,
						"scenarioId",
						out var scenarioId,
						out var scenarioError))
				{
					return scenarioError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"reviewProfileId",
						out var reviewProfileId,
						out var profileError))
				{
					return profileError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"target",
						out var target,
						out var targetError))
				{
					return targetError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"maxIterations",
						out var maxIterations,
						out var iterationsError))
				{
					return iterationsError;
				}

				result = await _agentDispatcher.RequestReviewAsync(
					sessionId,
					new RequestReviewRequest(
						scenarioId,
						reviewProfileId,
						target,
						maxIterations),
					cancellationToken);
				successText = result.Payload ?? "Review run started.";
				statusText = "Agent action: review run started";
				break;

			default:
				return new AgentControlResultData(
					$"Unknown tool '{call.ToolName}'.",
					IsError: true);
		}

		if (!result.Succeeded)
		{
			var failure = result.Failure;
			return new AgentControlResultData(
				failure is null
					? "The agent action was refused."
					: $"{failure.Code}: {failure.Message}",
				IsError: true);
		}

		await _uiTaskDispatcher.InvokeAsync(() => _reportStatusAsync(statusText));
		return new AgentControlResultData(
			result.Payload ?? successText,
			IsError: false);
	}

	private async Task<AgentControlResultData> InvokeOrchestratorToolAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		AgentControlResult result;
		switch (call.ToolName)
		{
			case "pact_list_workspaces":
				result = _orchestratorDispatcher.ListWorkspaces();
				break;
			case "pact_get_session":
				if (!TryGetRequiredString(
						call.Arguments,
						"sessionId",
						out var sessionId,
						out var sessionError))
				{
					return sessionError;
				}

				if (!TryGetOptionalString(
						call.Arguments,
						"content",
						out var content,
						out var contentError))
				{
					return contentError;
				}

				result = _orchestratorDispatcher.GetSession(
					sessionId,
					content ?? "message");
				break;
			case "pact_send_message":
				if (!TryGetRequiredString(
						call.Arguments,
						"sessionId",
						out var targetSessionId,
						out var targetError))
				{
					return targetError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"message",
						out var text,
						out var textError))
				{
					return textError;
				}

				result = await _orchestratorDispatcher.SendMessageAsync(
					targetSessionId,
					text,
					cancellationToken);
				break;
			case "pact_start_session":
				if (!TryGetRequiredString(
						call.Arguments,
						"sessionId",
						out var startSessionId,
						out var startError))
				{
					return startError;
				}

				result = await _orchestratorDispatcher.StartSessionAsync(
					startSessionId,
					cancellationToken);
				break;
			case "pact_get_subscription_usage":
				result = _orchestratorDispatcher.GetSubscriptionUsage();
				break;
			case "pact_list_active_runs":
				result = _orchestratorDispatcher.ListActiveRuns();
				break;
			case "pact_get_review_run":
			case "pact_pause_review":
			case "pact_resume_review":
				if (!TryGetRequiredString(
						call.Arguments,
						"runId",
						out var reviewRunId,
						out var reviewRunError))
				{
					return reviewRunError;
				}

				result = call.ToolName switch
				{
					"pact_get_review_run" => _orchestratorDispatcher.GetReviewRun(reviewRunId),
					"pact_pause_review" => _orchestratorDispatcher.PauseReview(reviewRunId),
					_ => _orchestratorDispatcher.ResumeReview(reviewRunId)
				};
				break;
			case "pact_get_project_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var notesWorkspaceId,
						out var notesWorkspaceError))
				{
					return notesWorkspaceError;
				}

				result = await _orchestratorDispatcher.GetProjectNotesAsync(
					notesWorkspaceId,
					cancellationToken);
				break;
			case "pact_replace_project_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var replaceWorkspaceId,
						out var replaceWorkspaceError))
				{
					return replaceWorkspaceError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var replacementText,
						out var replacementTextError))
				{
					return replacementTextError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"expectedRevision",
						out var expectedRevision,
						out var expectedRevisionError))
				{
					return expectedRevisionError;
				}

				result = await _orchestratorDispatcher.ReplaceProjectNotesAsync(
					replaceWorkspaceId,
					new ReplaceNoteRequest(replacementText, expectedRevision),
					cancellationToken);
				break;
			case "pact_append_project_note":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var appendWorkspaceId,
						out var appendWorkspaceError))
				{
					return appendWorkspaceError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var appendText,
						out var appendTextError))
				{
					return appendTextError;
				}

				result = await _orchestratorDispatcher.AppendProjectNoteAsync(
					appendWorkspaceId,
					appendText,
					cancellationToken);
				break;
			case "pact_list_web_tabs":
				result = _orchestratorDispatcher.ListWebTabs();
				break;
			case "pact_resume_web_tab":
				if (!TryGetRequiredString(
						call.Arguments,
						"pageId",
						out var resumePageId,
						out var resumePageError))
				{
					return resumePageError;
				}

				result = await _orchestratorDispatcher.ResumeWebTabAsync(
					resumePageId,
					cancellationToken);
				break;
			case "pact_get_web_tab_html":
				if (!TryGetRequiredString(
						call.Arguments,
						"pageId",
						out var htmlPageId,
						out var htmlPageError))
				{
					return htmlPageError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"offset",
						out var offset,
						out var offsetError))
				{
					return offsetError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"maxChars",
						out var maxChars,
						out var maxCharsError))
				{
					return maxCharsError;
				}

				result = await _orchestratorDispatcher.GetWebTabHtmlAsync(
					htmlPageId,
					offset ?? 0,
					maxChars ?? WebPageDocumentRange.DefaultMaxChars,
					cancellationToken);
				break;
			default:
				return new AgentControlResultData(
					$"Unknown tool '{call.ToolName}'.",
					IsError: true);
		}

		return result.Succeeded
			? new AgentControlResultData(result.Payload ?? "Done.", IsError: false)
			: new AgentControlResultData(
				result.Failure is { } failure
					? $"{failure.Code}: {failure.Message}"
					: "The orchestrator action was refused.",
				IsError: true);
	}

	private static bool TryGetRequiredString(
		JsonNode arguments,
		string name,
		out string value,
		out AgentControlResultData error)
	{
		value = string.Empty;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = ArgumentError(name, "is required");
			return false;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value!))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	private static bool TryGetOptionalString(
		JsonNode arguments,
		string name,
		out string? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	private static bool TryGetOptionalInt32(
		JsonNode arguments,
		string name,
		out int? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<int>(out var parsed))
		{
			error = ArgumentError(name, "must be an integer");
			return false;
		}

		value = parsed;
		error = null!;
		return true;
	}

	private static AgentControlResultData ArgumentError(string name, string reason) =>
		new($"Argument '{name}' {reason}.", IsError: true);
}
