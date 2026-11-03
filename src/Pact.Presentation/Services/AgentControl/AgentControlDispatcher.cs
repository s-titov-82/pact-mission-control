using System.Text.Json;
using Pact.Core.AgentControl;
using Pact.Core.Sessions;
using Pact.Core.Projects;

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
	/// <summary>Adds a durable resource under the calling session's project.</summary>
	public async Task<AgentControlResult> AddProjectLinkAsync(string sessionId, string title, string url, string? kind, CancellationToken cancellationToken)
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

		try
		{
			var link = ProjectLinkRules.Create(title, url, kind, DateTimeOffset.UtcNow);
			var saved = await _host.AddProjectLinkAsync(owner.ProjectId!, link.Title, link.Url, link.Kind, cancellationToken).ConfigureAwait(false);
			return AgentControlResult.Ok(Serialize(saved));
		}
		catch (ArgumentException exception)
		{
			return AgentControlResult.Refused("invalid-argument", exception.Message);
		}
		catch (KeyNotFoundException exception)
		{
			return AgentControlResult.Refused("unknown-project", exception.Message);
		}
		catch (InvalidOperationException exception)
		{
			return AgentControlResult.Refused("link-limit", exception.Message);
		}
	}

	/// <summary>Lists durable resources of the calling session's project.</summary>
	public async Task<AgentControlResult> ListProjectLinksAsync(string sessionId, CancellationToken cancellationToken)
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

		try
		{
			var links = await _host.ListProjectLinksAsync(owner.ProjectId!, cancellationToken).ConfigureAwait(false);
			return AgentControlResult.Ok(Serialize(links));
		}
		catch (KeyNotFoundException exception)
		{
			return AgentControlResult.Refused("unknown-project", exception.Message);
		}
	}

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

	/// <summary>Shows a Markdown file of the calling session's project in its Docs pane.</summary>
	public async Task<AgentControlResult> OpenDocumentAsync(
		string sessionId,
		OpenDocumentRequest request,
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

		var root = await _host.GetProjectRootAsync(owner.ProjectId!, cancellationToken).ConfigureAwait(false);
		if (!ProjectDocumentPath.TryResolve(root, request.Path, out var relativePath, out failure))
		{
			return AgentControlResult.Refused(failure!);
		}

		return await _host.OpenProjectDocumentAsync(owner.ProjectId!, relativePath, cancellationToken).ConfigureAwait(false)
			? AgentControlResult.Ok()
			: AgentControlResult.Refused(
				"not-listed",
				$"The Docs pane does not list '{relativePath}'; files in generated folders such as .git, bin or .pact-reviews are not shown.");
	}

	/// <summary>
	/// Adds an existing file to the calling session's linked files; a relative path is taken from
	/// the session's working directory.
	/// </summary>
	public async Task<AgentControlResult> AddFileLinkAsync(
		string sessionId,
		AddFileLinkRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		var workingDirectory = await _host.GetSessionWorkingDirectoryAsync(sessionId, cancellationToken).ConfigureAwait(false);
		if (workingDirectory is null)
		{
			return UnknownSession();
		}

		var path = request.Path.Trim().Trim('"', '\'').Trim();
		if (path.Length == 0)
		{
			return AgentControlResult.Refused("invalid-argument", "Argument 'path' is required.");
		}

		string full;
		try
		{
			if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
			{
				path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
			}

			full = Path.GetFullPath(Path.Combine(workingDirectory, path));
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return AgentControlResult.Refused("invalid-argument", $"'{request.Path}' is not a valid path.");
		}

		if (Directory.Exists(full))
		{
			return AgentControlResult.Refused("not-a-file", $"'{full}' is a folder; link a file.");
		}

		if (!File.Exists(full))
		{
			return AgentControlResult.Refused("file-not-found", $"No file at '{full}'. Write the file before linking it.");
		}

		var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
		await _host.AddSessionFileLinkAsync(sessionId, new AgentFileLink(full, title, DateTimeOffset.UtcNow), cancellationToken)
			.ConfigureAwait(false);
		return AgentControlResult.Ok(Serialize(new { path = full }));
	}

	/// <summary>Resolves the root of the calling session's project, or the refusal for ROOT and unknown sessions.</summary>
	public async Task<(string? Root, AgentControlResult? Refusal)> ResolveProjectRootAsync(
		string sessionId,
		CancellationToken cancellationToken)
	{
		if (!_host.TryGetOwner(sessionId, out var owner))
		{
			return (null, UnknownSession());
		}

		var failure = AgentControlRequestValidator.ValidateProjectNotesOwner(owner);
		if (failure is not null)
		{
			return (null, AgentControlResult.Refused(failure));
		}

		return (await _host.GetProjectRootAsync(owner.ProjectId!, cancellationToken).ConfigureAwait(false), null);
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
