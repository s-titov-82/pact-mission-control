using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Services.Orchestrator;

/// <summary>Validates orchestrator requests and returns compact JSON projections.</summary>
public sealed class OrchestratorDispatcher
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	static OrchestratorDispatcher()
	{
		JsonOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
	}

	private readonly IOrchestratorHost _host;

	/// <summary>Creates a dispatcher over the deliberately bounded shell host.</summary>
	public OrchestratorDispatcher(IOrchestratorHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>Returns every project and ROOT workspace with its sessions.</summary>
	public AgentControlResult ListWorkspaces() =>
		AgentControlResult.Ok(Serialize(_host.ListWorkspaces()));

	/// <summary>Returns one session's extracted message or complete stable screen.</summary>
	public AgentControlResult GetSession(string sessionId, string content)
	{
		if (content is not ("message" or "screen"))
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				"'content' must be either 'message' or 'screen'.");
		}

		if (!_host.TryGetSession(sessionId, out var summary))
		{
			return AgentControlResult.Refused(
				"unknown-session",
				$"Session '{sessionId}' is not registered.");
		}

		if (!_host.TryGetScreen(sessionId, out var state))
		{
			return AgentControlResult.Refused(
				"screen-unavailable",
				$"Session '{sessionId}' has no retained stable screen.");
		}

		return content == "message"
			? AgentControlResult.Ok(Serialize(new
			{
				Session = summary,
				Content = "message",
				Message = state.LastMessage,
				state.LastMessageIsCurrent
			}))
			: AgentControlResult.Ok(Serialize(new
			{
				Session = summary,
				Content = "screen",
				state.Screen
			}));
	}

	/// <summary>Returns subscription usage for every configured agent profile.</summary>
	public AgentControlResult GetSubscriptionUsage() =>
		AgentControlResult.Ok(Serialize(_host.ListUsage()));

	/// <summary>Returns review runs that currently control session input.</summary>
	public AgentControlResult ListActiveRuns() =>
		AgentControlResult.Ok(Serialize(_host.ListActiveRuns()));

	/// <summary>Returns one active review run with its current in-memory journal.</summary>
	public AgentControlResult GetReviewRun(string runId)
	{
		if (!_host.TryGetActiveRun(runId, out var details))
		{
			return AgentControlResult.Refused(
				"unknown-review-run",
				$"Review run '{runId}' is not active.");
		}

		return AgentControlResult.Ok(Serialize(details));
	}

	/// <summary>Requests or escalates a manual pause for one active review run.</summary>
	public AgentControlResult PauseReview(string runId) =>
		ProjectReviewControl(runId, _host.RequestReviewPause(runId));

	/// <summary>Resumes an established manual or attention pause.</summary>
	public AgentControlResult ResumeReview(string runId) =>
		ProjectReviewControl(runId, _host.ResumeReview(runId));

	/// <summary>Reads the current Notes buffer for a running project workspace.</summary>
	public async Task<AgentControlResult> GetProjectNotesAsync(
		string workspaceId,
		CancellationToken cancellationToken)
	{
		var ownerFailure = ValidateProjectWorkspace(workspaceId);
		if (ownerFailure is not null)
		{
			return ownerFailure;
		}

		var snapshot = await _host.ReadProjectNotesAsync(
			workspaceId,
			cancellationToken).ConfigureAwait(false);
		return snapshot is null
			? UnknownWorkspace(workspaceId)
			: AgentControlResult.Ok(Serialize(snapshot));
	}

	/// <summary>Revision-safely replaces Notes for a running project workspace.</summary>
	public async Task<AgentControlResult> ReplaceProjectNotesAsync(
		string workspaceId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		var ownerFailure = ValidateProjectWorkspace(workspaceId);
		if (ownerFailure is not null)
		{
			return ownerFailure;
		}

		if (string.IsNullOrWhiteSpace(request.ExpectedRevision))
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				"'expectedRevision' is required and must be non-blank.");
		}

		var result = await _host.ReplaceProjectNotesAsync(
			workspaceId,
			request,
			cancellationToken).ConfigureAwait(false);
		return result is null
			? UnknownWorkspace(workspaceId)
			: MapNotesMutation(result);
	}

	/// <summary>Appends non-blank text to Notes for a running project workspace.</summary>
	public async Task<AgentControlResult> AppendProjectNoteAsync(
		string workspaceId,
		string text,
		CancellationToken cancellationToken)
	{
		var ownerFailure = ValidateProjectWorkspace(workspaceId);
		if (ownerFailure is not null)
		{
			return ownerFailure;
		}

		if (string.IsNullOrWhiteSpace(text))
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				"'text' must not be blank.");
		}

		var result = await _host.AppendProjectNoteAsync(
			workspaceId,
			text,
			cancellationToken).ConfigureAwait(false);
		return result is null
			? UnknownWorkspace(workspaceId)
			: MapNotesMutation(result);
	}

	/// <summary>Lists saved web tabs under running projects and ROOT.</summary>
	public AgentControlResult ListWebTabs() =>
		AgentControlResult.Ok(Serialize(_host.ListWebTabs()));

	/// <summary>Loads a known saved web tab in the background.</summary>
	public async Task<AgentControlResult> ResumeWebTabAsync(
		string pageId,
		CancellationToken cancellationToken)
	{
		if (!_host.TryGetWebTab(pageId, out _))
		{
			return UnknownWebTab(pageId);
		}

		return await _host.ResumeWebTabAsync(pageId, cancellationToken)
			.ConfigureAwait(false)
			? AgentControlResult.Ok()
			: UnknownWebTab(pageId);
	}

	/// <summary>Reads a bounded UTF-16 slice of a known active web tab's live document.</summary>
	public async Task<AgentControlResult> GetWebTabHtmlAsync(
		string pageId,
		int offset,
		int maxChars,
		CancellationToken cancellationToken)
	{
		WebPageDocumentRange range;
		try
		{
			range = new WebPageDocumentRange(offset, maxChars);
		}
		catch (ArgumentOutOfRangeException exception)
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				exception.Message);
		}

		if (!_host.TryGetWebTab(pageId, out var summary))
		{
			return UnknownWebTab(pageId);
		}

		if (!string.Equals(summary.State, "active", StringComparison.Ordinal))
		{
			return AgentControlResult.Refused(
				"web-tab-paused",
				$"Web tab '{pageId}' is paused; resume it before reading its document.");
		}

		try
		{
			var fragment = await _host.ReadWebTabHtmlAsync(
				pageId,
				range,
				cancellationToken).ConfigureAwait(false);
			return fragment is null
				? AgentControlResult.Refused(
					"web-content-unavailable",
					$"Web tab '{pageId}' has no readable live document.")
				: AgentControlResult.Ok(Serialize(new
				{
					summary.PageId,
					summary.Url,
					fragment.Html,
					fragment.TotalLength,
					fragment.NextOffset
				}));
		}
		catch (OperationCanceledException)
			when (!cancellationToken.IsCancellationRequested)
		{
			return AgentControlResult.Refused(
				"web-content-unavailable",
				$"Web tab '{pageId}' did not return document HTML in time.");
		}
		catch (Exception exception)
			when (exception is not OperationCanceledException)
		{
			return AgentControlResult.Refused(
				"web-content-unavailable",
				$"Web tab '{pageId}' document could not be read.");
		}
	}

	/// <summary>
	/// Sends a prompt through the normal terminal input path after enforcing orchestrator isolation.
	/// </summary>
	public async Task<AgentControlResult> SendMessageAsync(
		string sessionId,
		string text,
		CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				"'text' must not be blank.");
		}

		if (string.Equals(
			sessionId,
			_host.OrchestratorSessionId,
			StringComparison.Ordinal))
		{
			return AgentControlResult.Refused(
				"self-target",
				"The orchestrator cannot send a message to its own session.");
		}

		if (!_host.IsSessionAlive(sessionId))
		{
			return AgentControlResult.Refused(
				"session-not-alive",
				$"Session '{sessionId}' has no live terminal.");
		}

		if (_host.IsScenarioLocked(sessionId, out var runId))
		{
			return AgentControlResult.Refused(
				"session-scenario-locked",
				$"Session '{sessionId}' is controlled by review run '{runId}'.");
		}

		if (_host.TryGetScreen(sessionId, out var screen) && screen.InputRequested)
		{
			return AgentControlResult.Refused(
				"input-requested",
				$"Session '{sessionId}' is waiting for a human answer ({screen.StatusLine}); "
					+ "nothing was sent.");
		}

		await _host.SendMessageAsync(sessionId, text, cancellationToken)
			.ConfigureAwait(false);
		return AgentControlResult.Ok();
	}

	private static string Serialize<T>(T value) =>
		JsonSerializer.Serialize(value, JsonOptions);

	private static AgentControlResult ProjectReviewControl(
		string runId,
		ReviewControlOutcome outcome) =>
		outcome.Status switch
		{
			ReviewControlStatus.UnknownRun => AgentControlResult.Refused(
				"unknown-review-run",
				$"Review run '{runId}' is not active."),
			ReviewControlStatus.NotPausable => AgentControlResult.Refused(
				"review-not-pausable",
				$"Review run '{runId}' is stopping and cannot accept that control request."),
			_ => AgentControlResult.Ok(Serialize(outcome))
		};

	private AgentControlResult? ValidateProjectWorkspace(string workspaceId)
	{
		if (_host.ListWorkspaces().Any(workspace =>
				workspace.IsRoot
				&& string.Equals(
					workspace.WorkspaceId,
					workspaceId,
					StringComparison.Ordinal)))
		{
			return AgentControlResult.Refused(
				"owner-not-a-project",
				"ROOT has no project Notes.");
		}

		return _host.IsRunningWorkspace(workspaceId)
			? null
			: UnknownWorkspace(workspaceId);
	}

	private static AgentControlResult MapNotesMutation(
		ProjectNotesMutationResult result) =>
		result.Status switch
		{
			ProjectNotesMutationStatus.Applied =>
				AgentControlResult.Ok(Serialize(new { result.Snapshot.Revision })),
			ProjectNotesMutationStatus.Conflict =>
				AgentControlResult.Refused(
					"notes-conflict",
					"Project Notes changed after the supplied revision; read them again before retrying."),
			ProjectNotesMutationStatus.AppliedButNotPersisted =>
				AgentControlResult.Refused(
					"notes-save-failed",
					"The local Notes buffer changed but could not be persisted; Pact will retain it for retry."),
			_ => throw new ArgumentOutOfRangeException(nameof(result))
		};

	private static AgentControlResult UnknownWorkspace(string workspaceId) =>
		AgentControlResult.Refused(
			"unknown-workspace",
			$"Workspace '{workspaceId}' is not running.");

	private static AgentControlResult UnknownWebTab(string pageId) =>
		AgentControlResult.Refused(
			"unknown-web-tab",
			$"Web tab '{pageId}' is not exposed by a running workspace or ROOT.");
}
