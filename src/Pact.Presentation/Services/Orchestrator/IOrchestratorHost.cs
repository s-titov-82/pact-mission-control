using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Core.Sessions;

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

/// <summary>An active review run, its control state, and the durable exchange it awaits.</summary>
/// <param name="RunId">Stable run id.</param>
/// <param name="WorkspaceId">Owning project id.</param>
/// <param name="AuthorSessionId">Author session controlled by the run.</param>
/// <param name="ReviewerSessionId">Reviewer session controlled by the run.</param>
/// <param name="Iteration">Current review iteration.</param>
/// <param name="StartedAt">When the run started.</param>
/// <param name="State">Running, pause-requested, paused, or stopping.</param>
/// <param name="PauseKind">Manual or attention for a paused run; otherwise null.</param>
/// <param name="CurrentStepId">Current stable step id, when known.</param>
/// <param name="CurrentStepName">Human-readable current step, when known.</param>
/// <param name="PauseRequested">Whether a manual pause is pending at the next boundary.</param>
/// <param name="ExpectedRole">Role expected to write the current response, when any.</param>
/// <param name="ExpectedSessionId">Session bound to the expected role, when any.</param>
/// <param name="ExpectedTaskPath">Published immutable task file, when any.</param>
/// <param name="ExpectedResponsePath">Response file whose completion advances the run, when any.</param>
public sealed record ActiveRunSummary(
	string RunId,
	string WorkspaceId,
	string AuthorSessionId,
	string ReviewerSessionId,
	int Iteration,
	DateTimeOffset StartedAt,
	string State,
	string? PauseKind,
	string? CurrentStepId,
	string? CurrentStepName,
	bool PauseRequested,
	string? ExpectedRole,
	string? ExpectedSessionId,
	string? ExpectedTaskPath,
	string? ExpectedResponsePath);

/// <summary>One in-memory review journal entry exposed to the orchestrator.</summary>
/// <param name="Timestamp">When the entry was recorded.</param>
/// <param name="Level">Lowercase journal severity.</param>
/// <param name="StepId">Stable step id.</param>
/// <param name="Message">Journal message, including file paths when recorded by the run.</param>
public sealed record ReviewJournalSummary(
	DateTimeOffset Timestamp,
	string Level,
	string StepId,
	string Message);

/// <summary>Detailed review state together with its in-memory journal snapshot.</summary>
/// <param name="Run">Current active-run projection.</param>
/// <param name="Journal">Journal entries recorded so far.</param>
public sealed record ReviewRunDetails(
	ActiveRunSummary Run,
	IReadOnlyList<ReviewJournalSummary> Journal);

/// <summary>Outcome of an orchestrator review-control request.</summary>
public enum ReviewControlStatus
{
	/// <summary>The request changed the run.</summary>
	Applied,

	/// <summary>The run was already in the requested effective state.</summary>
	Unchanged,

	/// <summary>The run is stopping and cannot accept this control request.</summary>
	NotPausable,

	/// <summary>No active run has the supplied id.</summary>
	UnknownRun
}

/// <summary>Typed result of a review Pause or Resume request.</summary>
/// <param name="Status">Whether the request applied or why it did not.</param>
/// <param name="Run">Latest run projection when the run remains active.</param>
public sealed record ReviewControlOutcome(
	ReviewControlStatus Status,
	ActiveRunSummary? Run);

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

	/// <summary>Lists review runs that currently control terminal input.</summary>
	IReadOnlyList<ActiveRunSummary> ListActiveRuns();

	/// <summary>Tries to read one active review run and its in-memory journal.</summary>
	bool TryGetActiveRun(string runId, out ReviewRunDetails details);

	/// <summary>Requests or escalates a manual pause for an active review run.</summary>
	ReviewControlOutcome RequestReviewPause(string runId);

	/// <summary>Resumes an established pause without canceling a pending pause request.</summary>
	ReviewControlOutcome ResumeReview(string runId);

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

	/// <summary>Submits text to a live terminal through the normal human prompt path.</summary>
	Task SendMessageAsync(
		string sessionId,
		string text,
		CancellationToken cancellationToken);

	/// <summary>Reports whether a scenario run controls the session and returns its run id.</summary>
	bool IsScenarioLocked(string sessionId, out string runId);

	/// <summary>Reports whether the session has a live terminal controller.</summary>
	bool IsSessionAlive(string sessionId);

	/// <summary>Gets the orchestrator's own session id, when it is running.</summary>
	string? OrchestratorSessionId { get; }
}
