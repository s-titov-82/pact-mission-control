using System.ComponentModel;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Prompting;
using Pact.Core.Scenarios;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Scenarios;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Owns scenario setup, run lifecycle, session locking, and scenario-owned artifacts
/// while the shell remains responsible for terminal transport and view selection.
/// </summary>
internal sealed class AvaloniaScenarioCoordinator
{
	private readonly MainWindowViewModel _viewModel;
	private readonly AppPaths _appPaths;
	private readonly ScenarioDefinitionStore _definitionStore;
	private readonly ScenarioRunService _runService;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly Func<ScenarioRunViewModel, Task> _selectRunAsync;
	private readonly Func<string, Task> _reportStatusAsync;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly Lock _slotSync = new();
	private readonly HashSet<string> _reservedProjectIds = new(StringComparer.Ordinal);

	public AvaloniaScenarioCoordinator(
		MainWindowViewModel viewModel,
		AppPaths appPaths,
		ScenarioDefinitionStore definitionStore,
		Func<string, string, bool, CancellationToken, Task<PromptDeliveryResult>> sendPromptAndSubmitAsync,
		Func<string, SessionViewModel?> findSession,
		Func<string, Task> sendEscapeAsync,
		Func<string, bool> isSessionActive,
		IUiTaskDispatcher uiTaskDispatcher,
		Func<ScenarioRunViewModel, Task> selectRunAsync,
		Func<string, Task> reportStatusAsync,
		ObservedTaskGroup eventTasks)
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
		_definitionStore = definitionStore ?? throw new ArgumentNullException(nameof(definitionStore));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_selectRunAsync = selectRunAsync ?? throw new ArgumentNullException(nameof(selectRunAsync));
		_reportStatusAsync = reportStatusAsync ?? throw new ArgumentNullException(nameof(reportStatusAsync));
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

		MainWindowScenarioGateway gateway = new(
			sendPromptAndSubmitAsync,
			findSession,
			sendEscapeAsync,
			isSessionActive);
		_runService = new ScenarioRunService(
			gateway,
			exception => AppLog.AppendAsync(
				_appPaths.RootDirectory,
				"Scenario artifact cleanup failed",
				exception),
			reportDiagnosticAsync: (phase, exception) => AppLog.AppendAsync(
				_appPaths.RootDirectory,
				phase,
				exception));
	}

	public ScenarioSetupViewModel? CreateSetup(
		ScenarioDefinition definition,
		WorkspaceViewModel? workspace)
	{
		ArgumentNullException.ThrowIfNull(definition);
		if (!ScenarioCatalog.TryGet(definition.Kind, out var blueprint))
		{
			ReportStatus("Scenario has no blueprint");
			return null;
		}

		if (workspace is null)
		{
			ReportStatus("Select a project before running a scenario.");
			return null;
		}

		var candidates = workspace.Sessions
			.Where(session => PromptActionPolicy.CanTarget(PromptActionType.Prompt, session.Record.Kind))
			.ToArray();
		return new ScenarioSetupViewModel(blueprint, definition, candidates);
	}

	public async Task<ScenarioRunViewModel?> StartAsync(
		ScenarioDefinition definition,
		WorkspaceViewModel workspace,
		ScenarioSetupViewModel setup,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(setup);
		if (!ScenarioCatalog.TryGet(definition.Kind, out var blueprint))
		{
			await _reportStatusAsync("Scenario has no blueprint");
			return null;
		}

		IReadOnlyDictionary<string, string> roleBindings;
		try
		{
			roleBindings = setup.BuildRoleBindings();
		}
		catch (InvalidOperationException exception)
		{
			await _reportStatusAsync(exception.Message);
			return null;
		}

		if (setup.SaveTargetAsDefault
			&& !await SaveTargetDefaultAsync(definition, setup.Target, cancellationToken))
		{
			return null;
		}

		using var reservation = TryReserveProjectSlot(workspace.Id, out var conflict);
		if (reservation is null)
		{
			await _reportStatusAsync(conflict.ActiveRunId is { } activeRunId
				? $"Scenario '{activeRunId}' is already active for this project."
				: "A scenario is already starting for this project.");
			return null;
		}

		try
		{
			var run = StartReservedRun(
				reservation,
				definition,
				workspace,
				roleBindings,
				setup.Target,
				setup.MaxIterations,
				setup.ReviewerInstructionText);
			await _selectRunAsync(run);
			await _reportStatusAsync($"Scenario started: {definition.Name}.");
			return run;
		}
		catch (Exception exception)
		{
			await _reportStatusAsync($"Scenario start failed: {exception.Message}");
			return null;
		}
	}

	/// <summary>Atomically reserves the single active review slot for a project.</summary>
	/// <param name="projectId">Project whose scenario lifecycle owns the slot.</param>
	/// <param name="conflict">
	/// Existing run id, or a null id while another request is still creating its run.
	/// </param>
	/// <returns>A reservation released on dispose, or <see langword="null"/> on conflict.</returns>
	public IDisposable? TryReserveProjectSlot(
		string projectId,
		out ProjectSlotConflict conflict)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		lock (_slotSync)
		{
			var activeRunId = FindActiveRunId(projectId);
			if (activeRunId is not null)
			{
				conflict = new ProjectSlotConflict(activeRunId);
				return null;
			}

			if (!_reservedProjectIds.Add(projectId))
			{
				conflict = new ProjectSlotConflict(ActiveRunId: null);
				return null;
			}

			conflict = new ProjectSlotConflict(ActiveRunId: null);
			return new ProjectSlotReservation(this, projectId);
		}
	}

	/// <summary>
	/// Starts and registers a run while the caller still owns its project reservation.
	/// </summary>
	public ScenarioRunViewModel StartReservedRun(
		IDisposable reservation,
		ScenarioDefinition definition,
		WorkspaceViewModel workspace,
		IReadOnlyDictionary<string, string> roleBindings,
		string target,
		int maxIterations,
		string reviewerInstructionText)
	{
		ArgumentNullException.ThrowIfNull(reservation);
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(roleBindings);
		if (reservation is not ProjectSlotReservation projectReservation
			|| !ReferenceEquals(projectReservation.Owner, this)
			|| projectReservation.IsDisposed
			|| !string.Equals(
				projectReservation.ProjectId,
				workspace.Id,
				StringComparison.Ordinal))
		{
			throw new InvalidOperationException("The project review slot is not reserved by this caller.");
		}

		if (!ScenarioCatalog.TryGet(definition.Kind, out var blueprint))
		{
			throw new InvalidOperationException("Scenario has no blueprint.");
		}

		var handle = _runService.Start(
			blueprint,
			new ReviewLoopScenarioProgram(
				definition,
				reviewerInstructionText,
				workspace.RootPath),
			workspace.Id,
			roleBindings,
			target,
			maxIterations);
		ScenarioRunViewModel run = new(handle, _uiTaskDispatcher.Post);
		var sessionIds = roleBindings.Values.Distinct(StringComparer.Ordinal).ToArray();
		_viewModel.SetScenarioLocks(run.RunId, sessionIds, locked: true);
		AttachRunLifecycle(run, sessionIds);
		ApplyRunLockState(run, sessionIds);
		_viewModel.AddScenarioRun(workspace.Id, run, select: false);
		return run;
	}

	public void RequestSoftStop(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);
		run.RequestSoftStop();
		ReportStatus("Scenario will stop after the current step.");
	}

	public void Pause(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);
		run.RequestPause();
		ReportStatus("Scenario pause requested.");
	}

	public void Abort(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);
		run.Abort();
		ReportStatus("Scenario abort requested.");
	}

	public void Resume(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);
		try
		{
			run.Resume();
			ReportStatus("Scenario resumed.");
		}
		catch (InvalidOperationException exception)
		{
			ReportStatus(exception.Message);
		}
	}

	public async Task<bool> CloseRunAsync(
		ScenarioRunViewModel run,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(run);
		if (!run.IsTerminal)
		{
			run.Abort();
			try
			{
				await run.Completion.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
			}
			catch (TimeoutException)
			{
				await _reportStatusAsync("Scenario abort is still in progress.");
				return false;
			}
		}

		_viewModel.RemoveScenarioRun(run);
		return true;
	}

	public void NotifySessionDied(string sessionId) =>
		_runService.NotifySessionDied(sessionId);

	public static Task AbortWorkspaceRunsAsync(
		WorkspaceViewModel workspace,
		CancellationToken cancellationToken)
	{
		var sessionIds = workspace.Sessions
			.Select(session => session.Record.Id)
			.ToHashSet(StringComparer.Ordinal);
		var runs = workspace.ScenarioRuns
			.Where(run => !run.IsTerminal && run.RoleBindings.Values.Any(sessionIds.Contains))
			.ToArray();
		return AbortRunsAsync(runs, TimeSpan.FromSeconds(5), cancellationToken);
	}

	public Task AbortAllRunsAsync(CancellationToken cancellationToken) =>
		AbortRunsAsync(
			_viewModel.ScenarioRuns.Where(run => !run.IsTerminal).ToArray(),
			TimeSpan.FromSeconds(5),
			cancellationToken);

	public async Task CleanupAbandonedExchangesAsync(CancellationToken cancellationToken)
	{
		var projectRoots = _viewModel.Workspaces
			.Concat(_viewModel.PausedWorkspaces)
			.Select(workspace => workspace.RootPath)
			.Where(root => !string.IsNullOrWhiteSpace(root))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();

		foreach (var projectRoot in projectRoots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				ReviewExchangeDirectory.CleanupAbandoned(projectRoot);
			}
			catch (Exception exception)
			{
				await AppLog.AppendAsync(
					_appPaths.RootDirectory,
					$"Abandoned scenario artifact cleanup failed for {projectRoot}",
					exception);
			}
		}
	}

	private void AttachRunLifecycle(ScenarioRunViewModel run, IReadOnlyList<string> sessionIds)
	{
		void handler(object? _, PropertyChangedEventArgs args)
		{
			if (args.PropertyName is not (nameof(ScenarioRunViewModel.State)
				or nameof(ScenarioRunViewModel.StuckSessionId)))
			{
				return;
			}

			_uiTaskDispatcher.Post(() =>
			{
				ApplyRunLockState(run, sessionIds);
				if (run.IsTerminal)
				{
					run.PropertyChanged -= handler;
				}
			});
		}

		run.PropertyChanged += handler;
	}

	private void ApplyRunLockState(ScenarioRunViewModel run, IReadOnlyList<string> sessionIds)
	{
		if (run.IsTerminal)
		{
			_viewModel.SetScenarioLocks(run.RunId, sessionIds, locked: false);
			ReportStatus($"Scenario finished: {run.State}.");
			return;
		}

		if (run.State == ScenarioRunState.Paused)
		{
			if (run.UnlockAllSessionsWhilePaused)
			{
				_viewModel.SetScenarioLocks(run.RunId, sessionIds, locked: false);
				ReportStatus("Scenario paused by user.");
				return;
			}

			var stuckSessionId = run.StuckSessionId;
			var stillLocked = stuckSessionId is null
				? sessionIds.ToArray()
				: sessionIds.Where(id => !string.Equals(id, stuckSessionId, StringComparison.Ordinal)).ToArray();
			_viewModel.SetScenarioLocks(run.RunId, stillLocked, locked: true);
			if (stuckSessionId is not null)
			{
				_viewModel.SetScenarioLocks(run.RunId, [stuckSessionId], locked: false);
			}

			ReportStatus("Scenario paused - agent needs attention.");
			return;
		}

		_viewModel.SetScenarioLocks(run.RunId, sessionIds, locked: true);
	}

	private async Task<bool> SaveTargetDefaultAsync(
		ScenarioDefinition definition,
		string target,
		CancellationToken cancellationToken)
	{
		var definitions = _viewModel.ScenarioDefinitions.ToArray();
		var index = Array.FindIndex(
			definitions,
			candidate => string.Equals(candidate.Id, definition.Id, StringComparison.Ordinal));
		if (index < 0)
		{
			await _reportStatusAsync("Scenario definition is no longer available.");
			return false;
		}

		definitions[index] = definitions[index] with { DefaultTarget = target };
		try
		{
			await _definitionStore.SaveAsync(definitions, cancellationToken);
			_viewModel.ReplaceScenarioDefinitions(definitions);
			return true;
		}
		catch (Exception exception)
		{
			await _reportStatusAsync($"Scenario default save failed: {exception.Message}");
			return false;
		}
	}

	private static async Task AbortRunsAsync(
		ScenarioRunViewModel[] runs,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		if (runs.Length == 0)
		{
			return;
		}

		foreach (var run in runs)
		{
			run.Abort();
		}

		try
		{
			await Task.WhenAll(runs.Select(run => run.Completion)).WaitAsync(timeout, cancellationToken);
		}
		catch (TimeoutException)
		{
			// Continue close after the bounded wait. Each run will still release
			// its locks through the lifecycle handler when cancellation completes.
		}
	}

	private void ReportStatus(string message) =>
		_eventTasks.TryRun(
			"scenario-status",
			() => _reportStatusAsync(message));

	private string? FindActiveRunId(string projectId)
	{
		var workspace = _viewModel.Workspaces
			.Concat(_viewModel.PausedWorkspaces)
			.FirstOrDefault(candidate => string.Equals(
				candidate.Id,
				projectId,
				StringComparison.Ordinal));
		return workspace?.ScenarioRuns.FirstOrDefault(run => !run.IsTerminal)?.RunId;
	}

	private void ReleaseProjectSlot(string projectId)
	{
		lock (_slotSync)
		{
			_reservedProjectIds.Remove(projectId);
		}
	}

	private sealed class ProjectSlotReservation(
		AvaloniaScenarioCoordinator owner,
		string projectId) : IDisposable
	{
		private int _disposed;

		public AvaloniaScenarioCoordinator Owner { get; } = owner;
		public string ProjectId { get; } = projectId;
		public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

		public void Dispose()
		{
			if (Interlocked.Exchange(ref _disposed, 1) == 0)
			{
				Owner.ReleaseProjectSlot(ProjectId);
			}
		}
	}
}
