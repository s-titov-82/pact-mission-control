using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Platform;
using Pact.Core.Updates;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Updates;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Adapts the UI-neutral update coordinator to owned Avalonia dialogs and shell launching.
/// </summary>
internal sealed class AvaloniaUpdateController
{
	private readonly IExternalLauncher _externalLauncher;
	private readonly UpdatePathPolicy _pathPolicy;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly Func<string, Exception?, Task> _logAsync;
	private Func<UpdateRelease, Task<UpdateAvailableDialogResult>>? _showDialogAsync;
	private Func<string, Task>? _showInformationAsync;
	private Func<SoftRestartSafetyResult>? _getRestartSafety;
	private Func<PreparedUpdatePackage, CancellationToken, Task<SoftRestartRequestResult>>?
		_requestApplyUpdateAsync;
	private readonly Lock _restartSync = new();
	private StableReleaseVersion? _automaticallyOfferedRestartVersion;
	private int _dialogOpen;
	private int _manualChecks;
	private int _preparing;
	private bool _started;
	private CancellationToken _lifetimeToken;

	public AvaloniaUpdateController(
		UpdateCoordinator coordinator,
		IExternalLauncher externalLauncher,
		UpdatePathPolicy pathPolicy,
		IUiTaskDispatcher uiTaskDispatcher,
		ObservedTaskGroup eventTasks,
		Func<string, Exception?, Task> logAsync)
	{
		Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
		_externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
		_pathPolicy = pathPolicy ?? throw new ArgumentNullException(nameof(pathPolicy));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_logAsync = logAsync ?? throw new ArgumentNullException(nameof(logAsync));
	}

	public UpdateCoordinator Coordinator { get; }

	public bool IsRestartActionVisible =>
		Coordinator.Status is { State: UpdateState.ReadyToRestart, PreparedPackage: not null };

	public string RestartActionText => Coordinator.Status.PreparedPackage is { } package
		? $"Version {package.Release.Version} ready - restart"
		: string.Empty;

	public event EventHandler? RestartAndUpdateRequested;

	public void ConfigureHost(
		Func<UpdateRelease, Task<UpdateAvailableDialogResult>> showDialogAsync,
		Func<string, Task> showInformationAsync)
	{
		_showDialogAsync = showDialogAsync ?? throw new ArgumentNullException(nameof(showDialogAsync));
		_showInformationAsync = showInformationAsync ?? throw new ArgumentNullException(nameof(showInformationAsync));
	}

	public void ConfigureRestart(Func<SoftRestartSafetyResult> getRestartSafety)
	{
		_getRestartSafety = getRestartSafety
			?? throw new ArgumentNullException(nameof(getRestartSafety));
	}

	public void ConfigureRestart(
		Func<SoftRestartSafetyResult> getRestartSafety,
		Func<PreparedUpdatePackage, CancellationToken, Task<SoftRestartRequestResult>>
			requestApplyUpdateAsync)
	{
		ConfigureRestart(getRestartSafety);
		_requestApplyUpdateAsync = requestApplyUpdateAsync
			?? throw new ArgumentNullException(nameof(requestApplyUpdateAsync));
	}

	public async Task<SoftRestartRequestResult> RequestRestartAndUpdateAsync(
		CancellationToken cancellationToken)
	{
		if (Coordinator.Status is not
			{ State: UpdateState.ReadyToRestart, PreparedPackage: { } package })
		{
			RefreshRestartSafety();
			return new SoftRestartRequestResult(false, [], "update-not-ready");
		}
		if (_requestApplyUpdateAsync is not { } requestApplyUpdateAsync)
		{
			return new SoftRestartRequestResult(false, [], "update-restart-unavailable");
		}

		var result = await requestApplyUpdateAsync(package, cancellationToken);
		if (result.Started)
		{
			Coordinator.MarkApplying(package);
		}
		else
		{
			RefreshRestartSafety();
		}
		return result;
	}

	public Task StartAsync(CancellationToken lifetimeToken)
	{
		if (_started)
		{
			return Task.CompletedTask;
		}

		if (_showDialogAsync is null || _showInformationAsync is null)
		{
			throw new InvalidOperationException("The update UI host must be configured before startup.");
		}

		_started = true;
		_lifetimeToken = lifetimeToken;
		Coordinator.StatusChanged += OnStatusChanged;
		return Coordinator.StartAsync(lifetimeToken);
	}

	public async Task CheckNowAsync(CancellationToken cancellationToken)
	{
		GitHubReleaseResponse response;
		try
		{
			Interlocked.Increment(ref _manualChecks);
			response = await Coordinator.CheckNowAsync(
				UpdateCheckOrigin.Manual,
				cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			await _logAsync("Manual update check failed", exception);
			await ShowInformationAsync("Pact could not check for updates. Check the network connection and try again.");
			return;
		}
		finally
		{
			Interlocked.Decrement(ref _manualChecks);
		}

		switch (response)
		{
			case GitHubReleaseResponse.NoUpdate:
				await ShowInformationAsync("Pact is up to date.");
				break;
			case GitHubReleaseResponse.RateLimited limited:
				await ShowInformationAsync(
					$"GitHub temporarily limited update checks. Please try again after {limited.RetryAt.ToLocalTime():g}.");
				break;
			case GitHubReleaseResponse.Invalid invalid:
				await ShowInformationAsync($"Pact could not check for updates. {invalid.Reason}");
				break;
			case GitHubReleaseResponse.Available:
				break;
		}
	}

	public async Task OpenCurrentReleaseNotesAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (Coordinator.Status.AvailableRelease is not { } release)
		{
			return;
		}

		await OpenReleaseNotesAsync(release);
	}

	public Task OpenContainingFolderAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		if (Coordinator.Status.PreparedPackage is not { } package)
		{
			return Task.CompletedTask;
		}

		try
		{
			var setupPath = _pathPolicy.EnsureOwnedPath(package.SetupPath);
			var directory = Path.GetDirectoryName(setupPath)
				?? throw new InvalidDataException("The staged Setup has no containing directory.");
			_pathPolicy.EnsureOwnedPath(directory);
			return _externalLauncher.OpenFileAsync(directory);
		}
		catch (Exception exception)
		{
			return _logAsync("Rejected invalid staged update path", exception);
		}
	}

	public void Stop()
	{
		if (!_started)
		{
			return;
		}

		_started = false;
		Coordinator.StatusChanged -= OnStatusChanged;
	}

	private void OnStatusChanged(object? sender, UpdateStatusChangedEventArgs args)
	{
		if (args.Status.State == UpdateState.ReadyWaitingForSafeState
			&& Volatile.Read(ref _preparing) == 0)
		{
			ScheduleRestartSafetyRefresh();
		}
		else if (args.Status is { State: UpdateState.ReadyToRestart, PreparedPackage: { } package }
			&& TryReserveAutomaticRestartOffer(package.Release.Version))
		{
			_eventTasks.TryRun(
				"offer-restart-and-update",
				() => _uiTaskDispatcher.InvokeAsync(() =>
				{
					RestartAndUpdateRequested?.Invoke(this, EventArgs.Empty);
					return Task.CompletedTask;
				}));
		}

		if (args.Status.State == UpdateState.Failed
			&& Volatile.Read(ref _manualChecks) == 0
			&& Volatile.Read(ref _preparing) == 0)
		{
			_eventTasks.TryRun(
				"automatic-update-check-failure",
				() => _logAsync(
					args.Status.Error ?? "Automatic update check failed",
					null));
			return;
		}

		if (args.Status is { State: UpdateState.Available, AvailableRelease: { } release })
		{
			_eventTasks.TryRun(
				"show-update-available",
				() => _uiTaskDispatcher.InvokeAsync(() => ShowAvailableAsync(release)));
		}
	}

	public void RefreshRestartSafety()
	{
		if (_getRestartSafety is { } getSafety)
		{
			Coordinator.UpdateRestartSafety(getSafety().CanRestart);
		}
	}

	private bool TryReserveAutomaticRestartOffer(StableReleaseVersion version)
	{
		lock (_restartSync)
		{
			if (_automaticallyOfferedRestartVersion == version)
			{
				return false;
			}
			_automaticallyOfferedRestartVersion = version;
			return true;
		}
	}

	private void ScheduleRestartSafetyRefresh() =>
		_eventTasks.TryRun(
			"update-restart-safety",
			() => _uiTaskDispatcher.InvokeAsync(() =>
			{
				RefreshRestartSafety();
				return Task.CompletedTask;
			}));

	private async Task ShowAvailableAsync(UpdateRelease release)
	{
		if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
		{
			return;
		}

		try
		{
			while (_showDialogAsync is { } showDialog)
			{
				var action = await showDialog(release);
				switch (action)
				{
					case UpdateAvailableDialogResult.Download:
						await PrepareUpdateAsync(release);
						return;
					case UpdateAvailableDialogResult.Later:
						Coordinator.DeferAutomaticPrompt(release.Version);
						return;
					case UpdateAvailableDialogResult.OpenReleaseNotes:
						await OpenReleaseNotesAsync(release);
						break;
					default:
						throw new InvalidOperationException($"Unknown update action: {action}.");
				}
			}
		}
		finally
		{
			Interlocked.Exchange(ref _dialogOpen, 0);
		}
	}

	private async Task PrepareUpdateAsync(UpdateRelease release)
	{
		try
		{
			Interlocked.Increment(ref _preparing);
			var package = await Coordinator.PrepareUpdateAsync(
				release,
				progress: null,
				_lifetimeToken);
			await ShowInformationAsync(
				$"Pact {package.Release.Version} was downloaded and verified. Pact will offer a restart as soon as active work is safe.");
		}
		catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
		{
		}
		catch (Exception exception)
		{
			await _logAsync("Update download failed", exception);
			await ShowInformationAsync(
				"Pact could not download and verify the update. Try again later.");
		}
		finally
		{
			if (Interlocked.Decrement(ref _preparing) == 0
				&& Coordinator.Status.State == UpdateState.ReadyWaitingForSafeState)
			{
				ScheduleRestartSafetyRefresh();
			}
		}
	}

	private async Task OpenReleaseNotesAsync(UpdateRelease release)
	{
		var uri = release.ReleaseNotesUri;
		if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps)
		{
			await _logAsync("Rejected non-HTTPS update release-notes URL", null);
			return;
		}

		await _externalLauncher.OpenHttpUriAsync(uri);
	}

	private Task ShowInformationAsync(string message) =>
		_uiTaskDispatcher.InvokeAsync(
			() => _showInformationAsync?.Invoke(message) ?? Task.CompletedTask);
}
