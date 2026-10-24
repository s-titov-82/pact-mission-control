using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Core.Sessions;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.Services.Orchestrator;

/// <summary>A project or ROOT owner and its sessions as exposed to the orchestrator.</summary>
/// <param name="WorkspaceId">Stable project id, or the ROOT owner id.</param>
/// <param name="Title">User-visible owner title.</param>
/// <param name="IsRoot">Whether this is the project-independent ROOT owner.</param>
/// <param name="Sessions">Sessions currently owned by this workspace.</param>
public sealed record WorkspaceSummary(
	string WorkspaceId,
	string Title,
	bool IsRoot,
	IReadOnlyList<SessionSummary> Sessions);

/// <summary>A read-only session projection exposed to the orchestrator.</summary>
/// <param name="SessionId">Stable session id.</param>
/// <param name="Title">User-visible session title.</param>
/// <param name="AgentKind">Configured agent kind.</param>
/// <param name="ProcessStatus">Terminal process lifecycle status.</param>
/// <param name="Indicator">Derived terminal-tab indicator.</param>
/// <param name="Activity">Current classifier description.</param>
/// <param name="ActivitySince">When the current activity began, when known.</param>
public sealed record SessionSummary(
	string SessionId,
	string Title,
	string AgentKind,
	string ProcessStatus,
	string Indicator,
	string Activity,
	DateTimeOffset? ActivitySince);

/// <summary>One agent profile's current subscription-usage projection.</summary>
/// <param name="ProfileId">Stable profile id.</param>
/// <param name="ProfileName">User-visible profile name.</param>
/// <param name="State">Availability or refresh state.</param>
/// <param name="FiveHourText">Formatted five-hour budget.</param>
/// <param name="WeeklyText">Formatted weekly budget.</param>
public sealed record UsageSummary(
	string ProfileId,
	string ProfileName,
	string State,
	string FiveHourText,
	string WeeklyText);

/// <summary>A saved browser tab owned by a running project or ROOT.</summary>
/// <param name="WorkspaceId">Stable owner workspace id.</param>
/// <param name="WorkspaceTitle">User-visible owner title.</param>
/// <param name="IsRoot">Whether the page belongs to ROOT.</param>
/// <param name="PageId">Stable saved-page id.</param>
/// <param name="Title">User-visible page title.</param>
/// <param name="Url">Current persisted resume URL.</param>
/// <param name="State">Active when its browser host is loaded; paused otherwise.</param>
/// <param name="IsSelected">Whether the shell currently presents this page.</param>
public sealed record WebTabSummary(
	string WorkspaceId,
	string WorkspaceTitle,
	bool IsRoot,
	string PageId,
	string Title,
	string Url,
	string State,
	bool IsSelected);

/// <summary>How a request to start a stopped session ended.</summary>
public enum SessionStartStatus
{
	/// <summary>The session started and can take a prompt.</summary>
	Ready,

	/// <summary>The session was already running; nothing was started.</summary>
	AlreadyRunning,

	/// <summary>No session has that id.</summary>
	NotFound,

	/// <summary>The session started but did not become ready to take a prompt.</summary>
	NotReady
}

/// <summary>Result of starting a stopped session, with the reason it is not ready.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Reason">The pending question, or why readiness was not reached; empty otherwise.</param>
public readonly record struct SessionStartOutcome(SessionStartStatus Status, string Reason = "");

/// <summary>
/// Supplies deliberately bounded shell projections and actions to the orchestrator dispatcher.
/// Live view models never cross this boundary, keeping the dispatcher UI-independent and
/// preventing it from reaching state the shell did not deliberately expose.
/// </summary>
public interface IOrchestratorHost
{
	/// <summary>Lists every project and ROOT workspace with its sessions.</summary>
	IReadOnlyList<WorkspaceSummary> ListWorkspaces();

	/// <summary>Tries to read one session projection.</summary>
	bool TryGetSession(string sessionId, out SessionSummary summary);

	/// <summary>Tries to read one session's retained stable screen state.</summary>
	bool TryGetScreen(string sessionId, out SessionScreenState state);

	/// <summary>Lists reviews that are starting or running.</summary>
	IReadOnlyList<ReviewSummary> ListActiveReviews();

	/// <summary>Finds an active review, or returns <see langword="null"/>.</summary>
	ReviewSummary? FindReview(string reviewId);

	/// <summary>Lists the current subscription-usage projections.</summary>
	IReadOnlyList<UsageSummary> ListUsage();

	/// <summary>Reports whether a project workspace is currently running.</summary>
	bool IsRunningWorkspace(string workspaceId);

	/// <summary>Reads the current Notes buffer for a running project.</summary>
	Task<ProjectNotesSnapshot?> ReadProjectNotesAsync(
		string workspaceId,
		CancellationToken cancellationToken);

	/// <summary>Revision-safely replaces Notes for a running project.</summary>
	Task<ProjectNotesMutationResult?> ReplaceProjectNotesAsync(
		string workspaceId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken);

	/// <summary>Appends text to Notes for a running project.</summary>
	Task<ProjectNotesMutationResult?> AppendProjectNoteAsync(
		string workspaceId,
		string text,
		CancellationToken cancellationToken);

	/// <summary>Lists saved web tabs owned by running projects and ROOT.</summary>
	IReadOnlyList<WebTabSummary> ListWebTabs();

	/// <summary>Tries to read one web-tab projection from the exposed set.</summary>
	bool TryGetWebTab(string pageId, out WebTabSummary summary);

	/// <summary>Loads a known web tab in the background without selecting it.</summary>
	Task<bool> ResumeWebTabAsync(
		string pageId,
		CancellationToken cancellationToken);

	/// <summary>Reads one bounded live-DOM fragment from an active web tab.</summary>
	Task<WebPageDocumentFragment?> ReadWebTabHtmlAsync(
		string pageId,
		WebPageDocumentRange range,
		CancellationToken cancellationToken);

	/// <summary>
	/// Submits text to a live terminal through the one prompt sender and reports how the delivery
	/// ended; a question or another run's lock refuses it there, in the write turn.
	/// </summary>
	Task<PromptDeliveryResult> SendMessageAsync(
		string sessionId,
		string text,
		CancellationToken cancellationToken);

	/// <summary>Reports whether the session has a live terminal controller.</summary>
	bool IsSessionAlive(string sessionId);

	/// <summary>
	/// Starts a stopped session in the background, the same way opening its tab would, and waits
	/// until it can take a prompt.
	/// </summary>
	Task<SessionStartOutcome> StartSessionAsync(string sessionId, CancellationToken cancellationToken);

	/// <summary>Gets the orchestrator's own session id, when it is running.</summary>
	string? OrchestratorSessionId { get; }
}
