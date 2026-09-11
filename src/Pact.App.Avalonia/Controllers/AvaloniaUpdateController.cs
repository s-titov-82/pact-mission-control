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
	private const string DownloadPlaceholder =
		"Downloading is available in the next delivery phase";
	private readonly IExternalLauncher _externalLauncher;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly Func<string, Exception?, Task> _logAsync;
	private Func<UpdateRelease, Task<UpdateAvailableDialogResult>>? _showDialogAsync;
	private Func<string, Task>? _showInformationAsync;
	private int _dialogOpen;
	private int _manualChecks;
	private bool _started;

	public AvaloniaUpdateController(
		UpdateCoordinator coordinator,
		IExternalLauncher externalLauncher,
		IUiTaskDispatcher uiTaskDispatcher,
		ObservedTaskGroup eventTasks,
		Func<string, Exception?, Task> logAsync)
	{
		Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
		_externalLauncher = externalLauncher ?? throw new ArgumentNullException(nameof(externalLauncher));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_logAsync = logAsync ?? throw new ArgumentNullException(nameof(logAsync));
	}

	public UpdateCoordinator Coordinator { get; }

	public void ConfigureHost(
		Func<UpdateRelease, Task<UpdateAvailableDialogResult>> showDialogAsync,
		Func<string, Task> showInformationAsync)
	{
		_showDialogAsync = showDialogAsync ?? throw new ArgumentNullException(nameof(showDialogAsync));
		_showInformationAsync = showInformationAsync ?? throw new ArgumentNullException(nameof(showInformationAsync));
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
		if (args.Status.State == UpdateState.Failed
			&& Volatile.Read(ref _manualChecks) == 0)
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
						await ShowInformationAsync(DownloadPlaceholder);
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
