using Pact.Core.AgentControl;
using Pact.Core.Sessions;
using Pact.Core.Projects;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>Why a project's single review slot could not be taken.</summary>
/// <param name="ActiveRunId">
/// Active run id, or <see langword="null"/> while another request holds a pre-run reservation.
/// </param>
public sealed record ProjectSlotConflict(string? ActiveRunId);

/// <summary>Narrow shell operations deliberately exposed to agent-requested actions.</summary>
public interface IAgentControlHost
{
	/// <summary>Persists a resource under the resolved project owner.</summary>
	Task<ProjectLink> AddProjectLinkAsync(string projectId, string title, string url, string? kind, CancellationToken cancellationToken);

	/// <summary>Reads resources of the resolved project; refuses a stale owner.</summary>
	Task<IReadOnlyList<ProjectLink>> ListProjectLinksAsync(string projectId, CancellationToken cancellationToken);

	/// <summary>Resolves the owner of a live session.</summary>
	bool TryGetOwner(string sessionId, out AgentControlOwner owner);

	/// <summary>Reads the current live project Notes buffer and revision.</summary>
	Task<ProjectNotesSnapshot> ReadProjectNotesAsync(
		string projectId,
		CancellationToken cancellationToken);

	/// <summary>Replaces project Notes when the supplied revision is still current.</summary>
	Task<ProjectNotesMutationResult> ReplaceProjectNotesAsync(
		string projectId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken);

	/// <summary>Appends text without replacing existing project notes.</summary>
	Task<ProjectNotesMutationResult> AppendToProjectNotesAsync(
		string projectId,
		string text,
		CancellationToken cancellationToken);

	/// <summary>Creates a saved browser tab under a project or ROOT owner.</summary>
	Task CreateWebTabAsync(
		AgentControlOwner owner,
		string url,
		string? title,
		CancellationToken cancellationToken);

	/// <summary>Reads the root directory of a project.</summary>
	Task<string> GetProjectRootAsync(string projectId, CancellationToken cancellationToken);

	/// <summary>Shows a project Markdown file in its Docs pane and switches the user to it.</summary>
	/// <returns><see langword="false"/> when the Docs pane does not list the file.</returns>
	Task<bool> OpenProjectDocumentAsync(
		string projectId,
		string relativePath,
		CancellationToken cancellationToken);

	/// <summary>Reads the working directory of a live session; <see langword="null"/> for an unknown one.</summary>
	Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Adds a file link to a session's list and saves it with the session.</summary>
	Task AddSessionFileLinkAsync(string sessionId, AgentFileLink link, CancellationToken cancellationToken);
}
