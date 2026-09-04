using Pact.Core.AgentControl;

namespace Pact.Presentation.Services.AgentControl;

/// <summary>Why a project's single review slot could not be taken.</summary>
/// <param name="ActiveRunId">
/// Active run id, or <see langword="null"/> while another request holds a pre-run reservation.
/// </param>
public sealed record ProjectSlotConflict(string? ActiveRunId);

/// <summary>Result of atomically attempting to start a project review.</summary>
/// <param name="RunId">Started run id.</param>
/// <param name="Conflict">Slot conflict when no run started for that reason.</param>
/// <param name="FailureMessage">Other start failure.</param>
public sealed record ReviewStartOutcome(
	string? RunId,
	ProjectSlotConflict? Conflict,
	string? FailureMessage);

/// <summary>Narrow shell operations deliberately exposed to agent-requested actions.</summary>
public interface IAgentControlHost
{
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

	/// <summary>
	/// Atomically reserves the project review slot, creates its reviewer, and starts the run.
	/// </summary>
	Task<ReviewStartOutcome> StartReviewIfIdleAsync(
		string projectId,
		string authorSessionId,
		RequestReviewRequest request,
		CancellationToken cancellationToken);
}
