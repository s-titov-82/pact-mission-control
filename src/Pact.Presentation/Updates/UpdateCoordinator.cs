using Pact.Core.Updates;
using Pact.Infrastructure.Updates;

namespace Pact.Presentation.Updates;

/// <summary>
/// Serializes manual and automatic release checks and publishes their state transitions.
/// </summary>
public sealed class UpdateCoordinator : IAsyncDisposable
{
	private static readonly TimeSpan InitialCheckDelay = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan NormalCheckCadence = TimeSpan.FromHours(1);
	private const string CheckFailureMessage = "The update check could not be completed.";
	private const string DownloadFailureMessage =
		"The update could not be downloaded and verified.";
	private readonly IGitHubReleaseClient _releaseClient;
	private readonly TimeProvider _timeProvider;
	private readonly SemaphoreSlim _checkGate = new(1, 1);
	private readonly SemaphoreSlim _prepareGate = new(1, 1);
	private readonly CancellationTokenSource _disposeSource = new();
	private readonly IUpdatePackageStore? _packageStore;
	private readonly Lock _stateSync = new();
	private UpdateStatus _status;
	private DateTimeOffset? _automaticChecksSuppressedUntil;
	private string? _rateLimitReason;
	private StableReleaseVersion? _deferredAutomaticVersion;
	private int _consecutiveRateLimits;
	private CancellationTokenSource? _lifetimeSource;
	private Task? _scheduleTask;
	private bool _disposed;

	/// <summary>
	/// Creates the process-wide update state machine.
	/// </summary>
	/// <param name="releaseClient">The fixed-repository release client.</param>
	/// <param name="timeProvider">The clock used for cadence and backoff.</param>
	/// <param name="runningVersion">The stable version of this Pact process.</param>
	/// <param name="packageStore">Optional verified package store used by download phases.</param>
	public UpdateCoordinator(
		IGitHubReleaseClient releaseClient,
		TimeProvider timeProvider,
		StableReleaseVersion runningVersion,
		IUpdatePackageStore? packageStore = null)
	{
		ArgumentNullException.ThrowIfNull(releaseClient);
		ArgumentNullException.ThrowIfNull(timeProvider);
		_releaseClient = releaseClient;
		_timeProvider = timeProvider;
		_packageStore = packageStore;
		_status = new UpdateStatus(
			UpdateState.Idle,
			runningVersion,
			AvailableRelease: null,
			PreparedPackage: null,
			Error: null);
	}

	/// <summary>Gets the most recently published immutable status.</summary>
	public UpdateStatus Status
	{
		get
		{
			lock (_stateSync)
			{
				return _status;
			}
		}
	}

	/// <summary>
	/// Gets the earliest time at which either automatic or manual discovery may call GitHub.
	/// </summary>
	public DateTimeOffset? AutomaticChecksSuppressedUntil
	{
		get
		{
			lock (_stateSync)
			{
				return _automaticChecksSuppressedUntil;
			}
		}
	}

	/// <summary>Occurs after the coordinator publishes a distinct status snapshot.</summary>
	public event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;

	/// <summary>
	/// Starts the single background schedule. Repeated calls do not create another loop.
	/// </summary>
	/// <param name="lifetimeToken">Cancels checks when the application lifetime ends.</param>
	/// <returns>A task that completes after the schedule has been installed.</returns>
	public Task StartAsync(CancellationToken lifetimeToken)
	{
		lock (_stateSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (_scheduleTask is not null)
			{
				return Task.CompletedTask;
			}

			_lifetimeSource = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
			_scheduleTask = RunScheduleAsync(_lifetimeSource.Token);
		}

		return Task.CompletedTask;
	}

	/// <summary>
	/// Performs one serialized release check unless an active GitHub backoff forbids it.
	/// </summary>
	/// <param name="origin">Whether the schedule or the user requested the check.</param>
	/// <param name="cancellationToken">Cancels waiting or the network request.</param>
	/// <returns>The typed discovery result, including any effective backoff.</returns>
	public async Task<GitHubReleaseResponse> CheckNowAsync(
		UpdateCheckOrigin origin,
		CancellationToken cancellationToken)
	{
		if (origin is not UpdateCheckOrigin.Automatic and not UpdateCheckOrigin.Manual)
		{
			throw new ArgumentOutOfRangeException(nameof(origin));
		}

		ThrowIfDisposed();
		using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_disposeSource.Token);
		var operationToken = operationSource.Token;
		await _checkGate.WaitAsync(operationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			var now = _timeProvider.GetUtcNow();
			if (TryGetActiveRateLimit(now, out var activeRateLimit))
			{
				return activeRateLimit!;
			}

			var beforeCheck = Status;
			var preserveVisibleOffer = beforeCheck.State == UpdateState.Available;
			if (!preserveVisibleOffer)
			{
				Transition(beforeCheck with
				{
					State = UpdateState.Checking,
					Error = null
				});
			}

			GitHubReleaseResponse response;
			try
			{
				response = await _releaseClient.GetLatestStableAsync(
					beforeCheck.RunningVersion,
					operationToken).ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
			{
				Transition(ToIdle(beforeCheck.RunningVersion));
				throw;
			}
			catch (Exception) when (origin == UpdateCheckOrigin.Automatic)
			{
				PublishFailureThenIdle(beforeCheck.RunningVersion);
				return new GitHubReleaseResponse.Invalid(CheckFailureMessage);
			}
			catch (Exception)
			{
				Transition(new UpdateStatus(
					UpdateState.Failed,
					beforeCheck.RunningVersion,
					AvailableRelease: null,
					PreparedPackage: null,
					Error: CheckFailureMessage));
				throw;
			}

			return ApplyResponse(origin, beforeCheck.RunningVersion, response, now);
		}
		finally
		{
			_checkGate.Release();
		}
	}

	/// <summary>
	/// Suppresses only automatic offers for the supplied version until this process exits.
	/// </summary>
	/// <param name="version">The release for which the user selected Later.</param>
	public void DeferAutomaticPrompt(StableReleaseVersion version)
	{
		StableReleaseVersion runningVersion;
		var clearVisibleOffer = false;
		lock (_stateSync)
		{
			_deferredAutomaticVersion = version;
			runningVersion = _status.RunningVersion;
			clearVisibleOffer = _status.State == UpdateState.Available
				&& _status.AvailableRelease?.Version == version;
		}

		if (clearVisibleOffer)
		{
			Transition(ToIdle(runningVersion));
		}
	}

	/// <summary>
	/// Downloads and verifies a user-approved release without invoking its Setup program.
	/// </summary>
	/// <param name="release">The currently offered validated release.</param>
	/// <param name="progress">Optional cumulative Setup-byte progress observer.</param>
	/// <param name="cancellationToken">Cancels the download and returns the release to Available.</param>
	public async Task<PreparedUpdatePackage> PrepareUpdateAsync(
		UpdateRelease release,
		IProgress<long>? progress,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(release);
		if (_packageStore is null)
		{
			throw new InvalidOperationException("Update package storage is not configured.");
		}

		ThrowIfDisposed();
		using var operationSource = CancellationTokenSource.CreateLinkedTokenSource(
			cancellationToken,
			_disposeSource.Token);
		var operationToken = operationSource.Token;
		await _prepareGate.WaitAsync(operationToken).ConfigureAwait(false);
		try
		{
			var beforeDownload = Status;
			if (beforeDownload.AvailableRelease?.Version != release.Version)
			{
				throw new InvalidOperationException(
					"Only the currently available release can be downloaded.");
			}

			Transition(new UpdateStatus(
				UpdateState.Downloading,
				beforeDownload.RunningVersion,
				release,
				PreparedPackage: null,
				Error: null));
			try
			{
				var package = await _packageStore.DownloadAndVerifyAsync(
					release,
					progress,
					operationToken).ConfigureAwait(false);
				var current = Status;
				if (!HasNewerReleaseOrPackage(current, release.Version))
				{
					Transition(new UpdateStatus(
						UpdateState.ReadyWaitingForSafeState,
						current.RunningVersion,
						release,
						package,
						Error: null));
				}
				return package;
			}
			catch (OperationCanceledException) when (operationToken.IsCancellationRequested)
			{
				RestoreAvailableUnlessNewer(release);
				throw;
			}
			catch (Exception)
			{
				var current = Status;
				if (!HasNewerReleaseOrPackage(current, release.Version))
				{
					Transition(new UpdateStatus(
						UpdateState.Failed,
						current.RunningVersion,
						release,
						PreparedPackage: null,
						Error: DownloadFailureMessage));
				}
				throw;
			}
		}
		finally
		{
			_prepareGate.Release();
		}
	}

	/// <summary>Stops the schedule and waits for its current operation to observe cancellation.</summary>
	public async ValueTask DisposeAsync()
	{
		CancellationTokenSource? source;
		Task? schedule;
		lock (_stateSync)
		{
			if (_disposed)
			{
				return;
			}

			_disposed = true;
			source = _lifetimeSource;
			schedule = _scheduleTask;
		}

		if (source is not null)
		{
			await source.CancelAsync().ConfigureAwait(false);
		}
		await _disposeSource.CancelAsync().ConfigureAwait(false);

		if (schedule is not null)
		{
			try
			{
				await schedule.ConfigureAwait(false);
			}
			catch (OperationCanceledException) when (source?.IsCancellationRequested is true)
			{
			}
		}

		_lifetimeSource?.Dispose();
		_lifetimeSource = null;
		await _checkGate.WaitAsync().ConfigureAwait(false);
		_checkGate.Release();
		await _prepareGate.WaitAsync().ConfigureAwait(false);
		_prepareGate.Release();
		_checkGate.Dispose();
		_prepareGate.Dispose();
		_disposeSource.Dispose();
	}

	private async Task RunScheduleAsync(CancellationToken cancellationToken)
	{
		try
		{
			await Task.Delay(InitialCheckDelay, _timeProvider, cancellationToken)
				.ConfigureAwait(false);
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				await CheckNowAsync(UpdateCheckOrigin.Automatic, cancellationToken)
					.ConfigureAwait(false);
				var delay = GetNextAutomaticDelay(_timeProvider.GetUtcNow());
				await Task.Delay(delay, _timeProvider, cancellationToken)
					.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		catch (ObjectDisposedException) when (_disposeSource.IsCancellationRequested)
		{
		}
	}

	private GitHubReleaseResponse ApplyResponse(
		UpdateCheckOrigin origin,
		StableReleaseVersion runningVersion,
		GitHubReleaseResponse response,
		DateTimeOffset requestedAt)
	{
		if (response is GitHubReleaseResponse.RateLimited rateLimited)
		{
			var effective = RegisterRateLimit(rateLimited, requestedAt);
			Transition(ToIdle(runningVersion));
			return effective;
		}

		ResetRateLimit();
		switch (response)
		{
			case GitHubReleaseResponse.NoUpdate:
				Transition(ToIdle(runningVersion));
				break;
			case GitHubReleaseResponse.Available available:
				if (origin == UpdateCheckOrigin.Automatic
					&& IsDeferred(available.Release.Version))
				{
					Transition(ToIdle(runningVersion));
					break;
				}

				Transition(new UpdateStatus(
					UpdateState.Available,
					runningVersion,
					available.Release,
					PreparedPackage: null,
					Error: null));
				break;
			case GitHubReleaseResponse.Invalid invalid:
				Transition(new UpdateStatus(
					UpdateState.Failed,
					runningVersion,
					AvailableRelease: null,
					PreparedPackage: null,
					invalid.Reason));
				if (origin == UpdateCheckOrigin.Automatic)
				{
					Transition(ToIdle(runningVersion));
				}
				break;
		}

		return response;
	}

	private GitHubReleaseResponse.RateLimited RegisterRateLimit(
		GitHubReleaseResponse.RateLimited response,
		DateTimeOffset now)
	{
		lock (_stateSync)
		{
			var exponent = Math.Min(_consecutiveRateLimits, 6);
			var minutes = Math.Min(1 << exponent, 60);
			var localRetryAt = now.AddMinutes(minutes);
			var effectiveRetryAt = response.RetryAt > localRetryAt
				? response.RetryAt
				: localRetryAt;
			_consecutiveRateLimits = Math.Min(_consecutiveRateLimits + 1, 7);
			_automaticChecksSuppressedUntil = effectiveRetryAt;
			_rateLimitReason = response.Reason;
			return new GitHubReleaseResponse.RateLimited(
				effectiveRetryAt,
				response.Reason);
		}
	}

	private bool TryGetActiveRateLimit(
		DateTimeOffset now,
		out GitHubReleaseResponse.RateLimited? response)
	{
		lock (_stateSync)
		{
			if (_automaticChecksSuppressedUntil is { } retryAt && retryAt > now)
			{
				response = new GitHubReleaseResponse.RateLimited(
					retryAt,
					_rateLimitReason ?? "GitHub API rate limit is active.");
				return true;
			}

			_automaticChecksSuppressedUntil = null;
			_rateLimitReason = null;
			response = null;
			return false;
		}
	}

	private void ResetRateLimit()
	{
		lock (_stateSync)
		{
			_automaticChecksSuppressedUntil = null;
			_rateLimitReason = null;
			_consecutiveRateLimits = 0;
		}
	}

	private bool IsDeferred(StableReleaseVersion version)
	{
		lock (_stateSync)
		{
			return _deferredAutomaticVersion == version;
		}
	}

	private TimeSpan GetNextAutomaticDelay(DateTimeOffset now)
	{
		lock (_stateSync)
		{
			return _automaticChecksSuppressedUntil is { } retryAt && retryAt > now
				? retryAt - now
				: NormalCheckCadence;
		}
	}

	private void PublishFailureThenIdle(StableReleaseVersion runningVersion)
	{
		Transition(new UpdateStatus(
			UpdateState.Failed,
			runningVersion,
			AvailableRelease: null,
			PreparedPackage: null,
			Error: CheckFailureMessage));
		Transition(ToIdle(runningVersion));
	}

	private static UpdateStatus ToIdle(StableReleaseVersion runningVersion) => new(
		UpdateState.Idle,
		runningVersion,
		AvailableRelease: null,
		PreparedPackage: null,
		Error: null);

	private void Transition(UpdateStatus status)
	{
		EventHandler<UpdateStatusChangedEventArgs>? handler;
		lock (_stateSync)
		{
			if (_status == status)
			{
				return;
			}

			_status = status;
			handler = StatusChanged;
		}

		handler?.Invoke(this, new UpdateStatusChangedEventArgs(status));
	}

	private void ThrowIfDisposed()
	{
		lock (_stateSync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
		}
	}

	private void RestoreAvailableUnlessNewer(UpdateRelease release)
	{
		var current = Status;
		if (!HasNewerReleaseOrPackage(current, release.Version))
		{
			Transition(new UpdateStatus(
				UpdateState.Available,
				current.RunningVersion,
				release,
				PreparedPackage: null,
				Error: null));
		}
	}

	private static bool HasNewerReleaseOrPackage(
		UpdateStatus status,
		StableReleaseVersion version) =>
		status.AvailableRelease?.Version > version
		|| status.PreparedPackage?.Release.Version > version;
}
