using System.Globalization;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Presentation;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// The shell operations a review needs: the reviewer tab, its start, trigger delivery and the
/// review log in Docs. UI work goes through the UI dispatcher; status reads do not.
/// </summary>
internal sealed class ReviewRelayHost(
	MainWindowViewModel viewModel,
	SessionRuntimeCoordinator runtimeCoordinator,
	ITerminalWebViewHost terminalHost,
	SessionLauncher launcher,
	IUiTaskDispatcher uiDispatcher,
	bool agentControlEnabled,
	Func<SessionViewModel, Task> closeSessionAsync,
	Func<string, string, CancellationToken, Task<bool>> openProjectDocumentAsync,
	Action<string> trace) : IReviewRelayHost
{
	private readonly MainWindowViewModel _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
	private readonly SessionRuntimeCoordinator _runtimeCoordinator = runtimeCoordinator ?? throw new ArgumentNullException(nameof(runtimeCoordinator));
	private readonly ITerminalWebViewHost _terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
	private readonly SessionLauncher _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
	private readonly IUiTaskDispatcher _uiDispatcher = uiDispatcher ?? throw new ArgumentNullException(nameof(uiDispatcher));
	private readonly Func<SessionViewModel, Task> _closeSessionAsync = closeSessionAsync ?? throw new ArgumentNullException(nameof(closeSessionAsync));
	private readonly Func<string, string, CancellationToken, Task<bool>> _openProjectDocumentAsync = openProjectDocumentAsync ?? throw new ArgumentNullException(nameof(openProjectDocumentAsync));
	private readonly Action<string> _trace = trace ?? throw new ArgumentNullException(nameof(trace));

	public bool TryGetProject(string sessionId, out ReviewProject project)
	{
		ReviewProject? found = null;
		_uiDispatcher.Post(() =>
		{
			foreach (var workspace in _viewModel.Workspaces)
			{
				var session = workspace.Sessions.FirstOrDefault(candidate =>
					string.Equals(candidate.Record.Id, sessionId, StringComparison.Ordinal));
				if (session is not null)
				{
					found = new ReviewProject(workspace.Id, workspace.RootPath, session.Title, session.Record.Kind);
					return;
				}
			}
		});

		project = found!;
		return found is not null;
	}

	public bool HasAgentControl(AgentKind kind) =>
		agentControlEnabled && AgentControlArgumentTemplates.For(kind) is { Count: > 0 };

	public async Task<string> CreateReviewerAsync(
		string projectId,
		ReviewProfile profile,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var reviewerSessionId = Guid.NewGuid().ToString("N");
		await _uiDispatcher.InvokeAsync(async () =>
		{
			var workspace = _viewModel.Workspaces.Single(candidate =>
				string.Equals(candidate.Id, projectId, StringComparison.Ordinal));
			await _viewModel.CreateSessionAsync(
				sessionId: reviewerSessionId,
				projectId: projectId,
				kind: profile.Kind,
				title: $"Review · {profile.DisplayName}",
				workingDirectory: workspace.RootPath,
				launchCommand: profile.CommandTemplate,
				resumeCommand: null,
				cancellationToken: cancellationToken,
				workspaceId: workspace.Id,
				select: false);
			await _terminalHost.CreateTerminalAsync(reviewerSessionId);
		});
		_trace($"review-launch phase=session-created reviewer={reviewerSessionId} kind={profile.Kind}");
		return reviewerSessionId;
	}

	public async Task<ReviewerStart> StartReviewerAsync(string reviewerSessionId, CancellationToken cancellationToken)
	{
		ReviewerStart? start = null;
		await _uiDispatcher.InvokeAsync(async () =>
		{
			var reviewer = FindSession(reviewerSessionId)
				?? throw new InvalidOperationException("The reviewer tab no longer exists.");
			var plan = await _launcher.ActivateAsync(
				reviewer,
				startIfNeeded: true,
				preferResumeCommand: false,
				cancellationToken);
			_trace($"review-launch phase=activated reviewer={reviewerSessionId} plan={plan} "
				+ $"running={_runtimeCoordinator.TryGetActiveController(reviewerSessionId, out _, out _)}");
			var readiness = await _runtimeCoordinator.WaitForSessionReadyAsync(reviewerSessionId, cancellationToken);
			if (!readiness.IsReady)
			{
				TraceClassifierFacts(reviewerSessionId);
			}

			start = new ReviewerStart(readiness.IsReady, readiness.StatusLine);
		});
		return start!;
	}

	public Task<PromptDeliveryResult> DeliverTriggerAsync(
		string sessionId,
		string text,
		CancellationToken cancellationToken) =>
		_runtimeCoordinator.DeliverPromptAsync(
			sessionId,
			text,
			submit: true,
			PromptSender.Outsider,
			startIfNotRunning: false,
			cancellationToken);

	public bool IsReadyForTrigger(string sessionId) =>
		_viewModel.TerminalTabStatuses.TryGetScreenState(sessionId, out var state)
		&& SessionRuntimeCoordinator.IsReadyForPrompt(state);

	public Task CloseSessionAsync(string sessionId) =>
		_uiDispatcher.InvokeAsync(async () =>
		{
			if (FindSession(sessionId) is { } session)
			{
				await _closeSessionAsync(session);
			}
		});

	public Task OpenProjectDocumentAsync(string projectId, string relativePath) =>
		_uiDispatcher.InvokeAsync(() =>
			_openProjectDocumentAsync(projectId, relativePath, CancellationToken.None));

	public void Trace(string line) => _trace(line);

	private SessionViewModel? FindSession(string sessionId) =>
		_viewModel.Workspaces
			.SelectMany(workspace => workspace.Sessions)
			.FirstOrDefault(session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));

	// Facts only: sizes, times and the verdict name, never the screen text.
	private void TraceClassifierFacts(string sessionId)
	{
		var statuses = _viewModel.TerminalTabStatuses;
		if (!statuses.TryGetDiagnostics(sessionId, out var diagnostics)
			|| !statuses.TryGetScreenState(sessionId, out var screen))
		{
			_trace($"review-launch phase=not-ready reviewer={sessionId} registered=false");
			return;
		}

		_trace($"review-launch phase=not-ready reviewer={sessionId} registered=true "
			+ $"lifecycle={diagnostics.LifecycleStatus} indicator={diagnostics.Indicator} "
			+ $"verdict={diagnostics.VerdictState?.ToString() ?? "none"} "
			+ $"columns={diagnostics.Columns?.ToString(CultureInfo.InvariantCulture) ?? "none"} "
			+ $"rows={diagnostics.Rows?.ToString(CultureInfo.InvariantCulture) ?? "none"} "
			+ $"lastClassificationAt={diagnostics.LastClassificationAt?.ToString("O", CultureInfo.InvariantCulture) ?? "none"} "
			+ $"stableScreenChars={screen.Screen.Length} promptIsEmpty={diagnostics.PromptIsEmpty?.ToString() ?? "none"}");
	}
}
