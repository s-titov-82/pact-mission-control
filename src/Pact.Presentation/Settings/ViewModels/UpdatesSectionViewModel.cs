using Pact.Core.Updates;
using Pact.Presentation.Updates;

namespace Pact.Presentation.Settings.ViewModels;

/// <summary>
/// Projects the process-wide update coordinator into the non-persisting Updates settings page.
/// </summary>
public sealed class UpdatesSectionViewModel : SettingsSectionViewModelBase, IDisposable
{
	private readonly UpdateCoordinator _coordinator;
	private readonly Action<Action> _post;
	private UpdateRelease? _availableRelease;
	private bool _disposed;

	/// <summary>Creates a live projection of the supplied update coordinator.</summary>
	/// <param name="coordinator">The process-wide update coordinator.</param>
	/// <param name="post">
	/// Optional UI dispatcher boundary. Tests may omit it when all transitions are synchronous.
	/// </param>
	public UpdatesSectionViewModel(UpdateCoordinator coordinator, Action<Action>? post = null)
		: base(
			SettingsSection.Updates,
			"Updates",
			"Stable Pact releases from GitHub. Checks run hourly and can also be requested here.",
			string.Empty,
			string.Empty)
	{
		_coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
		_post = post ?? (static action => action());
		_coordinator.StatusChanged += OnStatusChanged;
		ApplyStatus(_coordinator.Status);
	}

	/// <inheritdoc />
	public override bool SupportsFileOperations => false;

	/// <summary>Gets the stable version of the currently running Pact process.</summary>
	public string RunningVersion { get; private set => SetField(ref field, value); } = string.Empty;

	/// <summary>Gets a user-facing summary of the current update state.</summary>
	public string StateText { get; private set => SetField(ref field, value); } = string.Empty;

	/// <summary>Gets whether a manual GitHub check may be requested.</summary>
	public bool CanCheck { get; private set => SetField(ref field, value); }

	/// <summary>Gets whether a validated HTTPS release-notes link is currently available.</summary>
	public bool CanOpenReleaseNotes { get; private set => SetField(ref field, value); }

	/// <summary>Gets whether a verified staged package directory can be opened.</summary>
	public bool CanOpenContainingFolder { get; private set => SetField(ref field, value); }

	/// <summary>Occurs when the user explicitly requests a release check.</summary>
	public event EventHandler? CheckRequested;

	/// <summary>Occurs when the user requests the current release notes.</summary>
	public event EventHandler? OpenReleaseNotesRequested;

	/// <summary>Occurs when the user requests the verified package's containing folder.</summary>
	public event EventHandler? OpenContainingFolderRequested;

	/// <summary>Raises the manual-check action when the coordinator is not busy.</summary>
	public void RequestCheck()
	{
		if (CanCheck)
		{
			CheckRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Raises the release-notes action only for a validated HTTPS address.</summary>
	public void RequestOpenReleaseNotes()
	{
		if (CanOpenReleaseNotes)
		{
			OpenReleaseNotesRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Raises the containing-folder action only after a package was verified.</summary>
	public void RequestOpenContainingFolder()
	{
		if (CanOpenContainingFolder)
		{
			OpenContainingFolderRequested?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <inheritdoc />
	public override Task LoadAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		ApplyStatus(_coordinator.Status);
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public override Task<bool> SaveAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return Task.FromResult(false);
	}

	/// <summary>Stops observing coordinator changes.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		_coordinator.StatusChanged -= OnStatusChanged;
	}

	private void OnStatusChanged(object? sender, UpdateStatusChangedEventArgs args) =>
		_post(() => ApplyStatus(args.Status));

	private void ApplyStatus(UpdateStatus status)
	{
		RunningVersion = status.RunningVersion.ToString();
		_availableRelease = status.AvailableRelease;
		StateText = status.State switch
		{
			UpdateState.Idle => "Ready to check for updates.",
			UpdateState.Checking => "Checking for updates...",
			UpdateState.Available => $"Version {status.AvailableRelease!.Version} is available.",
			UpdateState.Downloading => $"Downloading version {status.AvailableRelease?.Version}...",
			UpdateState.ReadyWaitingForSafeState => $"Version {status.PreparedPackage?.Release.Version} is ready; waiting for active work.",
			UpdateState.ReadyToRestart => $"Version {status.PreparedPackage?.Release.Version} is ready to install.",
			UpdateState.Applying => "Restarting to apply the update...",
			UpdateState.Failed => status.Error ?? "The update operation failed.",
			_ => throw new ArgumentOutOfRangeException(nameof(status), status.State, null)
		};
		CanCheck = status.State is UpdateState.Idle or UpdateState.Available or UpdateState.Failed;
		CanOpenReleaseNotes = _availableRelease?.ReleaseNotesUri is { IsAbsoluteUri: true } uri
			&& uri.Scheme == Uri.UriSchemeHttps;
		CanOpenContainingFolder = status.PreparedPackage is not null;
	}
}
