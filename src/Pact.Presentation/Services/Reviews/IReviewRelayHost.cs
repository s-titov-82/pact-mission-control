using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Sessions;

namespace Pact.Presentation.Services.Reviews;

/// <summary>A session's project, as a review needs it.</summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="RootPath">Project root, where the review log is kept.</param>
/// <param name="SessionTitle">The session's tab title.</param>
/// <param name="Kind">The session's agent kind.</param>
public sealed record ReviewProject(string ProjectId, string RootPath, string SessionTitle, AgentKind Kind);

/// <summary>Whether a started reviewer can take its first turn.</summary>
/// <param name="Ready">The reviewer is ready for a prompt.</param>
/// <param name="StatusLine">What blocked it when not ready.</param>
public sealed record ReviewerStart(bool Ready, string StatusLine);

/// <summary>The shell operations a review relay needs.</summary>
public interface IReviewRelayHost
{
	/// <summary>Resolves a session's project; <see langword="false"/> for ROOT and unknown sessions.</summary>
	bool TryGetProject(string sessionId, out ReviewProject project);

	/// <summary>Whether sessions of this agent kind get Pact's MCP tools.</summary>
	bool HasAgentControl(AgentKind kind);

	/// <summary>Creates the reviewer as a visible tab of the project without selecting it.</summary>
	/// <returns>The reviewer's session id.</returns>
	Task<string> CreateReviewerAsync(string projectId, ReviewProfile profile, CancellationToken cancellationToken);

	/// <summary>Starts the reviewer and waits until it can take a prompt.</summary>
	Task<ReviewerStart> StartReviewerAsync(string reviewerSessionId, CancellationToken cancellationToken);

	/// <summary>Types a trigger into a session and submits it, as an outsider.</summary>
	Task<PromptDeliveryResult> DeliverTriggerAsync(string sessionId, string text, CancellationToken cancellationToken);

	/// <summary>
	/// Whether the session's settled screen shows it can take a prompt: a Done verdict or an empty
	/// composer, not busy, no question.
	/// </summary>
	bool IsReadyForTrigger(string sessionId);

	/// <summary>Closes a session's tab the way the user would.</summary>
	Task CloseSessionAsync(string sessionId);

	/// <summary>Shows a project document and switches the user to it.</summary>
	Task OpenProjectDocumentAsync(string projectId, string relativePath);

	/// <summary>Writes a fact to the application log; never message text.</summary>
	void Trace(string line);
}
