using System.Diagnostics;

namespace Pact.Updater;

internal sealed record FileReleaseResult(
	bool Released,
	IReadOnlyList<string> LockedPaths);

internal interface IInstalledFileReleaseGate
{
	Task<FileReleaseResult> WaitAsync(
		IReadOnlyList<string> paths,
		TimeSpan timeout,
		CancellationToken cancellationToken);
}

internal sealed class InstalledFileReleaseGate : IInstalledFileReleaseGate
{
	private static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMilliseconds(100);
	private readonly TimeSpan _retryInterval;
	private readonly Func<TimeSpan, CancellationToken, Task> _delay;
	private readonly Action<IReadOnlyList<string>>? _blockedObserved;

	public InstalledFileReleaseGate()
		: this(
			DefaultRetryInterval,
			static (delay, token) => Task.Delay(delay, token),
			blockedObserved: null)
	{
	}

	internal InstalledFileReleaseGate(
		TimeSpan retryInterval,
		Func<TimeSpan, CancellationToken, Task> delay,
		Action<IReadOnlyList<string>>? blockedObserved)
	{
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryInterval, TimeSpan.Zero);
		_retryInterval = retryInterval;
		_delay = delay ?? throw new ArgumentNullException(nameof(delay));
		_blockedObserved = blockedObserved;
	}

	public async Task<FileReleaseResult> WaitAsync(
		IReadOnlyList<string> paths,
		TimeSpan timeout,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(paths);
		ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
		if (paths.Count == 0)
		{
			throw new ArgumentException("At least one installed file is required.", nameof(paths));
		}
		var normalizedPaths = paths
			.Select(path =>
			{
				if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
				{
					throw new ArgumentException("Installed file paths must be absolute.", nameof(paths));
				}
				return Path.GetFullPath(path);
			})
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		var startedAt = Stopwatch.GetTimestamp();
		while (true)
		{
			cancellationToken.ThrowIfCancellationRequested();
			var lockedPaths = normalizedPaths.Where(IsLocked).ToArray();
			if (lockedPaths.Length == 0)
			{
				return new FileReleaseResult(true, []);
			}

			_blockedObserved?.Invoke(lockedPaths);
			var elapsed = Stopwatch.GetElapsedTime(startedAt);
			if (elapsed >= timeout)
			{
				return new FileReleaseResult(false, lockedPaths);
			}
			var delay = timeout - elapsed < _retryInterval
				? timeout - elapsed
				: _retryInterval;
			await _delay(delay, cancellationToken).ConfigureAwait(false);
		}
	}

	private static bool IsLocked(string path)
	{
		try
		{
			using FileStream stream = new(
				path,
				FileMode.Open,
				FileAccess.ReadWrite,
				FileShare.None);
			return false;
		}
		catch (IOException)
		{
			return true;
		}
		catch (UnauthorizedAccessException)
		{
			return true;
		}
	}
}
