using System.Text.Json;
using Pact.Core.AgentControl;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>Outcome of one agent-requested action.</summary>
/// <param name="Succeeded">Whether the action was performed.</param>
/// <param name="Payload">Optional result text.</param>
/// <param name="Failure">Refusal when the action was not performed.</param>
public sealed record AgentControlResult(
	bool Succeeded,
	string? Payload,
	AgentControlFailure? Failure)
{
	/// <summary>Creates a successful result.</summary>
	public static AgentControlResult Ok(string? payload = null) => new(true, payload, null);

	/// <summary>Creates a refusal with a new failure.</summary>
	public static AgentControlResult Refused(string code, string message) =>
		new(false, null, new AgentControlFailure(code, message));

	/// <summary>Creates a refusal from a validated failure.</summary>
	public static AgentControlResult Refused(AgentControlFailure failure) =>
		new(false, null, failure);
}

/// <summary>Validates agent requests and delegates accepted actions to their shell owner.</summary>
public sealed class AgentControlDispatcher
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase
	};

	private readonly IAgentControlHost _host;

	/// <summary>Creates a dispatcher over <paramref name="host"/>.</summary>
	public AgentControlDispatcher(IAgentControlHost host)
	{
		ArgumentNullException.ThrowIfNull(host);
		_host = host;
	}

	/// <summary>Reads the calling session's current project Notes buffer and revision.</summary>
	public async Task<AgentControlResult> GetNotesAsync(
		string sessionId,
		CancellationToken cancellationToken)
	{
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}

		var failure = AgentControlRequestValidator.ValidateProjectNotesOwner(owner);
		if (failure is not null)
		{
			return AgentControlResult.Refused(failure);
		}

		var snapshot = await _host.ReadProjectNotesAsync(
			owner.ProjectId!,
			cancellationToken).ConfigureAwait(false);
		return AgentControlResult.Ok(Serialize(snapshot));
	}

	/// <summary>Revision-safely replaces the calling session's project Notes.</summary>
	public async Task<AgentControlResult> ReplaceNotesAsync(
		string sessionId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}

		var failure = AgentControlRequestValidator.Validate(owner, request);
		if (failure is not null)
		{
			return AgentControlResult.Refused(failure);
		}

		var result = await _host.ReplaceProjectNotesAsync(
			owner.ProjectId!,
			request,
			cancellationToken).ConfigureAwait(false);
		return MapNotesMutation(result);
	}

	/// <summary>Appends text to the calling session's project notes.</summary>
	public async Task<AgentControlResult> AppendNoteAsync(
		string sessionId,
		AppendNoteRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}

		var failure = AgentControlRequestValidator.Validate(owner, request);
		if (failure is not null)
		{
			return AgentControlResult.Refused(failure);
		}

		var result = await _host.AppendToProjectNotesAsync(
			owner.ProjectId!,
			request.Text,
			cancellationToken).ConfigureAwait(false);
		return MapNotesMutation(result);
	}

	/// <summary>Creates a saved browser tab under the caller's project or ROOT owner.</summary>
	public async Task<AgentControlResult> OpenWebTabAsync(
		string sessionId,
		OpenWebTabRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}

		var failure = AgentControlRequestValidator.Validate(owner, request);
		if (failure is not null)
		{
			return AgentControlResult.Refused(failure);
		}

		await _host.CreateWebTabAsync(
			owner,
			request.Url,
			request.Title,
			cancellationToken).ConfigureAwait(false);
		return AgentControlResult.Ok();
	}

	/// <summary>Atomically starts a review for the calling session's project.</summary>
	public async Task<AgentControlResult> RequestReviewAsync(
		string sessionId,
		RequestReviewRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return UnknownSession();
		}

		var failure = AgentControlRequestValidator.Validate(owner, request);
		if (failure is not null)
		{
			return AgentControlResult.Refused(failure);
		}

		var outcome = await _host.StartReviewIfIdleAsync(
			owner.ProjectId!,
			sessionId,
			request,
			cancellationToken).ConfigureAwait(false);
		if (outcome.RunId is { } runId)
		{
			return AgentControlResult.Ok(runId);
		}

		if (outcome.Conflict is { } conflict)
		{
			return conflict.ActiveRunId is { } activeRunId
				? AgentControlResult.Refused(
					"run-already-active",
					$"This project already has an active scenario run '{activeRunId}'; wait for it to finish.")
				: AgentControlResult.Refused(
					"review-already-starting",
					"A review is already starting for this project; wait for it to begin, then try again.");
		}

		return AgentControlResult.Refused(
			"review-start-failed",
			outcome.FailureMessage ?? "The review could not be started.");
	}

	private static AgentControlResult UnknownSession() =>
		AgentControlResult.Refused(
			"unknown-session",
			"The calling session is no longer live.");

	private static AgentControlResult MapNotesMutation(ProjectNotesMutationResult result) =>
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

	private static string Serialize<T>(T value) =>
		JsonSerializer.Serialize(value, JsonOptions);
}
