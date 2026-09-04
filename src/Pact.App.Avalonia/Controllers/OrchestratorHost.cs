using Pact.App.Avalonia.Lifecycle;
using Pact.Core.AgentControl;
using Pact.Core.Presentation;
using Pact.Core.Scenarios;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.Services.Orchestrator;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed class OrchestratorHost : IOrchestratorHost
{
	private readonly MainWindowViewModel _viewModel;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<string, bool> _isSessionAlive;
	private readonly Func<SessionViewModel, string, CancellationToken, Task> _sendMessageAsync;
	private readonly Func<WebPageViewModel, CancellationToken, Task> _resumeWebTabAsync;
	private readonly Func<
		string,
		WebPageDocumentRange,
		CancellationToken,
		Task<WebPageDocumentFragment?>> _readWebTabHtmlAsync;
	private readonly Func<string?> _orchestratorSessionId;

	public OrchestratorHost(
		MainWindowViewModel viewModel,
		IUiTaskDispatcher uiTaskDispatcher,
		Func<string, bool> isSessionAlive,
		Func<SessionViewModel, string, CancellationToken, Task> sendMessageAsync,
		Func<WebPageViewModel, CancellationToken, Task> resumeWebTabAsync,
		Func<
			string,
			WebPageDocumentRange,
			CancellationToken,
			Task<WebPageDocumentFragment?>> readWebTabHtmlAsync,
		Func<string?> orchestratorSessionId)
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_uiTaskDispatcher = uiTaskDispatcher
			?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_isSessionAlive = isSessionAlive
			?? throw new ArgumentNullException(nameof(isSessionAlive));
		_sendMessageAsync = sendMessageAsync
			?? throw new ArgumentNullException(nameof(sendMessageAsync));
		_resumeWebTabAsync = resumeWebTabAsync
			?? throw new ArgumentNullException(nameof(resumeWebTabAsync));
		_readWebTabHtmlAsync = readWebTabHtmlAsync
			?? throw new ArgumentNullException(nameof(readWebTabHtmlAsync));
		_orchestratorSessionId = orchestratorSessionId
			?? throw new ArgumentNullException(nameof(orchestratorSessionId));
	}

	public string? OrchestratorSessionId => _orchestratorSessionId();

	public IReadOnlyList<WorkspaceSummary> ListWorkspaces()
	{
		IReadOnlyList<WorkspaceSummary> result = [];
		_uiTaskDispatcher.Post(() =>
		{
			var workspaces = _viewModel.Workspaces
				.Select(workspace => new WorkspaceSummary(
					workspace.Id,
					workspace.Name,
					IsRoot: false,
					workspace.Sessions.Select(ProjectSession).ToArray()))
				.ToList();
			workspaces.Add(new WorkspaceSummary(
				"ROOT",
				"ROOT",
				IsRoot: true,
				_viewModel.RootTabs.Sessions.Select(ProjectSession).ToArray()));
			result = workspaces;
		});
		return result;
	}

	public bool TryGetSession(string sessionId, out SessionSummary summary)
	{
		SessionSummary? found = null;
		_uiTaskDispatcher.Post(() =>
		{
			var session = FindSession(sessionId);
			if (session is not null)
			{
				found = ProjectSession(session);
			}
		});
		summary = found!;
		return found is not null;
	}

	public bool TryGetScreen(string sessionId, out SessionScreenState state)
	{
		SessionScreenState? found = null;
		_uiTaskDispatcher.Post(() =>
		{
			if (_viewModel.TerminalTabStatuses.TryGetScreenState(sessionId, out var current))
			{
				found = current;
			}
		});
		state = found!;
		return found is not null;
	}

	public IReadOnlyList<ActiveRunSummary> ListActiveRuns()
	{
		IReadOnlyList<ActiveRunSummary> result = [];
		_uiTaskDispatcher.Post(() =>
		{
			result = _viewModel.ScenarioRuns
				.Where(run => !run.IsTerminal)
				.Select(ProjectRun)
				.ToArray();
		});
		return result;
	}

	public bool TryGetActiveRun(string runId, out ReviewRunDetails details)
	{
		ReviewRunDetails? found = null;
		_uiTaskDispatcher.Post(() =>
		{
			var run = FindActiveRun(runId);
			if (run is not null)
			{
				found = new ReviewRunDetails(
					ProjectRun(run),
					run.Journal.Select(entry => new ReviewJournalSummary(
						entry.Timestamp,
						entry.Level.ToString().ToLowerInvariant(),
						entry.StepId,
						entry.Message)).ToArray());
			}
		});
		details = found!;
		return found is not null;
	}

	public ReviewControlOutcome RequestReviewPause(string runId)
	{
		ReviewControlOutcome? result = null;
		_uiTaskDispatcher.Post(() =>
		{
			var run = FindActiveRun(runId);
			if (run is null)
			{
				result = new(ReviewControlStatus.UnknownRun, null);
				return;
			}

			var status = run.RequestManualPause() switch
			{
				ScenarioPauseRequestStatus.Requested
					or ScenarioPauseRequestStatus.Escalated => ReviewControlStatus.Applied,
				ScenarioPauseRequestStatus.Unchanged => ReviewControlStatus.Unchanged,
				_ => ReviewControlStatus.NotPausable
			};
			result = new(status, ProjectRun(run));
		});
		return result!;
	}

	public ReviewControlOutcome ResumeReview(string runId)
	{
		ReviewControlOutcome? result = null;
		_uiTaskDispatcher.Post(() =>
		{
			var run = FindActiveRun(runId);
			if (run is null)
			{
				result = new(ReviewControlStatus.UnknownRun, null);
				return;
			}

			if (run.State == ScenarioRunState.StoppingAfterStep)
			{
				result = new(ReviewControlStatus.NotPausable, ProjectRun(run));
				return;
			}

			var status = run.TryResume()
				? ReviewControlStatus.Applied
				: ReviewControlStatus.Unchanged;
			result = new(status, ProjectRun(run));
		});
		return result!;
	}

	public IReadOnlyList<UsageSummary> ListUsage()
	{
		IReadOnlyList<UsageSummary> result = [];
		_uiTaskDispatcher.Post(() =>
		{
			result = _viewModel.SubscriptionUsages
				.Select(row => new UsageSummary(
					row.ProfileId,
					row.ProfileName,
					row.State.ToString(),
					row.FiveHourText,
					row.WeeklyText))
				.ToArray();
		});
		return result;
	}

	public bool IsRunningWorkspace(string workspaceId)
	{
		var found = false;
		_uiTaskDispatcher.Post(() => found = FindRunningWorkspace(workspaceId) is not null);
		return found;
	}

	public async Task<ProjectNotesSnapshot?> ReadProjectNotesAsync(
		string workspaceId,
		CancellationToken cancellationToken)
	{
		ProjectNotesSnapshot? result = null;
		await _uiTaskDispatcher.InvokeAsync(async () =>
		{
			if (FindRunningWorkspace(workspaceId) is not null)
			{
				result = await _viewModel.ReadProjectNotesAsync(
					workspaceId,
					cancellationToken);
			}
		});
		return result;
	}

	public async Task<ProjectNotesMutationResult?> ReplaceProjectNotesAsync(
		string workspaceId,
		ReplaceNoteRequest request,
		CancellationToken cancellationToken)
	{
		ProjectNotesMutationResult? result = null;
		await _uiTaskDispatcher.InvokeAsync(async () =>
		{
			if (FindRunningWorkspace(workspaceId) is not null)
			{
				result = await _viewModel.ReplaceProjectNotesAsync(
					workspaceId,
					request.Text,
					request.ExpectedRevision,
					cancellationToken);
			}
		});
		return result;
	}

	public async Task<ProjectNotesMutationResult?> AppendProjectNoteAsync(
		string workspaceId,
		string text,
		CancellationToken cancellationToken)
	{
		ProjectNotesMutationResult? result = null;
		await _uiTaskDispatcher.InvokeAsync(async () =>
		{
			if (FindRunningWorkspace(workspaceId) is not null)
			{
				result = await _viewModel.AppendToProjectNotesAsync(
					workspaceId,
					text,
					cancellationToken);
			}
		});
		return result;
	}

	public IReadOnlyList<WebTabSummary> ListWebTabs()
	{
		IReadOnlyList<WebTabSummary> result = [];
		_uiTaskDispatcher.Post(() =>
		{
			result = _viewModel.Workspaces
				.SelectMany(workspace => workspace.WebPages.Select(page =>
					ProjectWebTab(workspace, page)))
				.Concat(_viewModel.RootTabs.WebPages.Select(ProjectRootWebTab))
				.ToArray();
		});
		return result;
	}

	public bool TryGetWebTab(string pageId, out WebTabSummary summary)
	{
		WebTabSummary? found = null;
		_uiTaskDispatcher.Post(() =>
		{
			var page = FindExposedWebPage(pageId);
			if (page is not null)
			{
				found = ProjectWebTab(page);
			}
		});
		summary = found!;
		return found is not null;
	}

	public async Task<bool> ResumeWebTabAsync(
		string pageId,
		CancellationToken cancellationToken)
	{
		var resumed = false;
		await _uiTaskDispatcher.InvokeAsync(async () =>
		{
			var page = FindExposedWebPage(pageId);
			if (page is null)
			{
				return;
			}

			if (page.IsRootItem && page.IsManuallyPaused)
			{
				await _viewModel.SetRootItemPausedAsync(
					pageId,
					paused: false,
					cancellationToken);
				page = FindExposedWebPage(pageId);
				if (page is null)
				{
					return;
				}
			}

			await _resumeWebTabAsync(page, cancellationToken);
			resumed = true;
		});
		return resumed;
	}

	public async Task<WebPageDocumentFragment?> ReadWebTabHtmlAsync(
		string pageId,
		WebPageDocumentRange range,
		CancellationToken cancellationToken)
	{
		WebPageDocumentFragment? result = null;
		await _uiTaskDispatcher.InvokeAsync(async () =>
		{
			var page = FindExposedWebPage(pageId);
			if (page?.IsBrowserLoaded == true)
			{
				result = await _readWebTabHtmlAsync(
					pageId,
					range,
					cancellationToken);
			}
		});
		return result;
	}

	public async Task SendMessageAsync(
		string sessionId,
		string text,
		CancellationToken cancellationToken)
	{
		SessionViewModel? session = null;
		_uiTaskDispatcher.Post(() => session = FindSession(sessionId));
		if (session is null)
		{
			throw new InvalidOperationException($"Session '{sessionId}' is not registered.");
		}

		await _uiTaskDispatcher.InvokeAsync(
			() => _sendMessageAsync(session, text, cancellationToken));
	}

	public bool IsScenarioLocked(string sessionId, out string runId)
	{
		string? found = null;
		_uiTaskDispatcher.Post(() => found = FindSession(sessionId)?.LockedByScenarioRunId);
		runId = found ?? string.Empty;
		return found is not null;
	}

	public bool IsSessionAlive(string sessionId) => _isSessionAlive(sessionId);

	private WorkspaceViewModel? FindRunningWorkspace(string workspaceId) =>
		_viewModel.Workspaces.FirstOrDefault(workspace => string.Equals(
			workspace.Id,
			workspaceId,
			StringComparison.Ordinal));

	private WebPageViewModel? FindExposedWebPage(string pageId) =>
		_viewModel.Workspaces
			.SelectMany(workspace => workspace.WebPages)
			.Concat(_viewModel.RootTabs.WebPages)
			.FirstOrDefault(page => string.Equals(
				page.Record.Id,
				pageId,
				StringComparison.Ordinal));

	private WebTabSummary ProjectWebTab(WebPageViewModel page)
	{
		if (page.IsRootItem)
		{
			return ProjectRootWebTab(page);
		}

		var workspace = _viewModel.Workspaces.First(candidate =>
			candidate.WebPages.Contains(page));
		return ProjectWebTab(workspace, page);
	}

	private WebTabSummary ProjectWebTab(
		WorkspaceViewModel workspace,
		WebPageViewModel page) => new(
		workspace.Id,
		workspace.Name,
		IsRoot: false,
		page.Record.Id,
		page.Title,
		page.ResumeUrl,
		page.IsBrowserLoaded ? "active" : "paused",
		ReferenceEquals(page, _viewModel.SelectedWebPage));

	private WebTabSummary ProjectRootWebTab(WebPageViewModel page) => new(
		"ROOT",
		"ROOT",
		IsRoot: true,
		page.Record.Id,
		page.Title,
		page.ResumeUrl,
		page.IsBrowserLoaded ? "active" : "paused",
		ReferenceEquals(page, _viewModel.SelectedWebPage));

	private SessionViewModel? FindSession(string sessionId) =>
		_viewModel.Workspaces.SelectMany(workspace => workspace.Sessions)
			.Concat(_viewModel.RootTabs.Sessions)
			.FirstOrDefault(session => string.Equals(
				session.Record.Id,
				sessionId,
				StringComparison.Ordinal));

	private static SessionSummary ProjectSession(SessionViewModel session) => new(
		session.Record.Id,
		session.Title,
		session.Record.Kind.ToString(),
		session.Record.Status.ToString(),
		session.Indicator.ToString(),
		session.StatusDescription,
		session.BusySince == default ? null : session.BusySince);

	private string FindWorkspaceId(ScenarioRunViewModel run) =>
		_viewModel.Workspaces.FirstOrDefault(workspace => workspace.ScenarioRuns.Contains(run))?.Id
		?? "unknown";

	private ScenarioRunViewModel? FindActiveRun(string runId) =>
		_viewModel.ScenarioRuns.FirstOrDefault(run =>
			!run.IsTerminal
			&& string.Equals(run.RunId, runId, StringComparison.Ordinal));

	private ActiveRunSummary ProjectRun(ScenarioRunViewModel run)
	{
		var expected = run.ExpectedResponse;
		var state = run.PauseRequested
			? "pause-requested"
			: run.State switch
			{
				ScenarioRunState.Running => "running",
				ScenarioRunState.Paused => "paused",
				ScenarioRunState.StoppingAfterStep => "stopping",
				_ => run.State.ToString().ToLowerInvariant()
			};
		var pauseKind = run.State == ScenarioRunState.Paused
			? run.UnlockAllSessionsWhilePaused ? "manual" : "attention"
			: null;
		var currentStepName = run.Blueprint.Steps
			.FirstOrDefault(step => string.Equals(
				step.Id,
				run.CurrentStepId,
				StringComparison.Ordinal))?.Description;

		return new ActiveRunSummary(
			run.RunId,
			FindWorkspaceId(run),
			FindRole(run, "author"),
			FindRole(run, "reviewer"),
			run.CurrentIteration,
			run.StartedAt,
			state,
			pauseKind,
			run.CurrentStepId,
			currentStepName,
			run.PauseRequested,
			expected?.Role,
			expected?.SessionId,
			expected?.TaskPath,
			expected?.ResponsePath);
	}

	private static string FindRole(ScenarioRunViewModel run, string role) =>
		run.RoleBindings.FirstOrDefault(pair => string.Equals(
			pair.Key,
			role,
			StringComparison.OrdinalIgnoreCase)).Value
		?? string.Empty;
}
