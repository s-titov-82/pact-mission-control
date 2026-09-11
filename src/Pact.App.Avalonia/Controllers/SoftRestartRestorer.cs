using Pact.Core.Updates;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed class SoftRestartRestorer
{
	private readonly Func<string, SessionViewModel?> _findSession;
	private readonly Func<SessionViewModel, CancellationToken, Task<SessionStartPlan>>
		_startSessionAsync;
	private readonly Func<string, WebPageViewModel?> _findWebPage;
	private readonly Func<WebPageViewModel, CancellationToken, Task> _loadWebPageAsync;
	private readonly Func<string, DateTimeOffset, bool> _restoreUnread;
	private readonly Func<CancellationToken, Task<bool>> _restoreOrchestratorAsync;
	private readonly Func<SoftRestartSelection, CancellationToken, Task<bool>>
		_restoreSelectionAsync;
	private readonly Func<string, bool> _isOrchestratorSession;
	private readonly TimeProvider _timeProvider;

	public SoftRestartRestorer(
		Func<string, SessionViewModel?> findSession,
		Func<SessionViewModel, CancellationToken, Task<SessionStartPlan>> startSessionAsync,
		Func<string, WebPageViewModel?> findWebPage,
		Func<WebPageViewModel, CancellationToken, Task> loadWebPageAsync,
		Func<string, DateTimeOffset, bool> restoreUnread,
		Func<CancellationToken, Task<bool>> restoreOrchestratorAsync,
		Func<SoftRestartSelection, CancellationToken, Task<bool>> restoreSelectionAsync,
		Func<string, bool> isOrchestratorSession,
		TimeProvider timeProvider)
	{
		_findSession = findSession ?? throw new ArgumentNullException(nameof(findSession));
		_startSessionAsync = startSessionAsync
			?? throw new ArgumentNullException(nameof(startSessionAsync));
		_findWebPage = findWebPage ?? throw new ArgumentNullException(nameof(findWebPage));
		_loadWebPageAsync = loadWebPageAsync
			?? throw new ArgumentNullException(nameof(loadWebPageAsync));
		_restoreUnread = restoreUnread ?? throw new ArgumentNullException(nameof(restoreUnread));
		_restoreOrchestratorAsync = restoreOrchestratorAsync
			?? throw new ArgumentNullException(nameof(restoreOrchestratorAsync));
		_restoreSelectionAsync = restoreSelectionAsync
			?? throw new ArgumentNullException(nameof(restoreSelectionAsync));
		_isOrchestratorSession = isOrchestratorSession
			?? throw new ArgumentNullException(nameof(isOrchestratorSession));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	}

	public async Task<RestorationSummary> RestoreAsync(
		SoftRestartTicket ticket,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(ticket);
		List<string> restoredTerminals = [];
		List<string> coldFallbacks = [];
		List<string> restoredPages = [];
		List<string> failures = [];

		foreach (var sessionId in ticket.LiveTerminalIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (_isOrchestratorSession(sessionId))
			{
				continue;
			}

			var session = _findSession(sessionId);
			if (session is null)
			{
				failures.Add($"terminal:{sessionId}:not-found-or-paused");
				continue;
			}
			try
			{
				var plan = await _startSessionAsync(session, cancellationToken)
					.ConfigureAwait(false);
				restoredTerminals.Add(sessionId);
				if (plan.FellBackToColdStart)
				{
					coldFallbacks.Add($"{sessionId}:{plan.FallbackReason}");
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception)
			{
				failures.Add($"terminal:{sessionId}:start-failed");
			}
		}

		var occurredAt = _timeProvider.GetUtcNow();
		foreach (var sessionId in ticket.UnreadTerminalIds)
		{
			if (_isOrchestratorSession(sessionId))
			{
				continue;
			}

			if (!_restoreUnread(sessionId, occurredAt))
			{
				failures.Add($"terminal:{sessionId}:unread-not-restored");
			}
		}

		foreach (var pageId in ticket.LoadedWebPageIds)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var page = _findWebPage(pageId);
			if (page is null)
			{
				failures.Add($"web:{pageId}:not-found-or-paused");
				continue;
			}
			try
			{
				await _loadWebPageAsync(page, cancellationToken).ConfigureAwait(false);
				restoredPages.Add(pageId);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception)
			{
				failures.Add($"web:{pageId}:load-failed");
			}
		}

		var orchestratorRestored = false;
		if (ticket.OrchestratorWasRunning)
		{
			try
			{
				orchestratorRestored = await _restoreOrchestratorAsync(cancellationToken)
					.ConfigureAwait(false);
				if (!orchestratorRestored)
				{
					failures.Add("orchestrator:not-enabled-or-provisioned");
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception)
			{
				failures.Add("orchestrator:start-failed");
			}
		}
		foreach (var sessionId in ticket.UnreadTerminalIds.Where(_isOrchestratorSession))
		{
			if (!_restoreUnread(sessionId, occurredAt))
			{
				failures.Add($"terminal:{sessionId}:unread-not-restored");
			}
		}

		var selectionRestored = false;
		if (ticket.Selection is { } selection)
		{
			try
			{
				selectionRestored = await _restoreSelectionAsync(selection, cancellationToken)
					.ConfigureAwait(false);
				if (!selectionRestored)
				{
					failures.Add("selection:not-found-or-paused");
				}
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception)
			{
				failures.Add("selection:restore-failed");
			}
		}

		return new RestorationSummary(
			restoredTerminals,
			coldFallbacks,
			restoredPages,
			failures,
			orchestratorRestored,
			selectionRestored,
			ticket.Outcome.Kind == SoftRestartOutcomeKind.UpdateNotApplied
				? ticket.Outcome.ErrorCategory
				: null);
	}
}
