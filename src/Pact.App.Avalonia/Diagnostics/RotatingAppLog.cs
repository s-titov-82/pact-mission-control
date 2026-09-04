using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Pact.App.Avalonia.Diagnostics;

/// <summary>Writes daily JSON log segments with a bounded size and age.</summary>
internal sealed class RotatingAppLog
{
	private const long DefaultMaxSegmentBytes = 5 * 1024 * 1024;
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates =
		new(StringComparer.OrdinalIgnoreCase);

	private readonly string _directory;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly long _maxSegmentBytes;
	private readonly TimeSpan _retention;

	/// <summary>
	/// Creates a log writer. The clock and limits are injectable so rotation and retention remain
	/// deterministic in tests.
	/// </summary>
	public RotatingAppLog(
		string directory,
		Func<DateTimeOffset>? utcNow = null,
		long maxSegmentBytes = DefaultMaxSegmentBytes,
		TimeSpan? retention = null)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSegmentBytes);

		_directory = Path.GetFullPath(directory);
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_maxSegmentBytes = maxSegmentBytes;
		_retention = retention ?? TimeSpan.FromDays(3);
		if (_retention <= TimeSpan.Zero)
		{
			throw new ArgumentOutOfRangeException(nameof(retention));
		}
	}

	/// <summary>
	/// Appends one JSON event. Logging failures are deliberately swallowed so diagnostics cannot
	/// replace the application failure they were meant to record.
	/// </summary>
	public async Task AppendAsync(string phase, Exception? exception = null)
	{
		try
		{
			var gate = DirectoryGates.GetOrAdd(
				_directory,
				static _ => new SemaphoreSlim(1, 1));
			await gate.WaitAsync().ConfigureAwait(false);
			try
			{
				var now = _utcNow().ToUniversalTime();
				Directory.CreateDirectory(_directory);
				ApplyRetentionCore(now);

				var path = SelectSegmentPath(now);
				await using FileStream stream = new(
					path,
					FileMode.Append,
					FileAccess.Write,
					FileShare.ReadWrite | FileShare.Delete,
					bufferSize: 4096,
					useAsync: true);
				await using StreamWriter writer = new(
					stream,
					new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
				var line = JsonSerializer.Serialize(new
				{
					timestamp = now,
					phase,
					exceptionType = exception?.GetType().FullName,
					message = exception?.Message,
					stackTrace = exception?.StackTrace
				});
				await writer.WriteLineAsync(line).ConfigureAwait(false);
			}
			finally
			{
				gate.Release();
			}
		}
		catch
		{
			// Diagnostics are best effort by contract.
		}
	}

	/// <summary>Deletes only Pact log segments whose last write is older than the retention window.</summary>
	public void ApplyRetention()
	{
		try
		{
			Directory.CreateDirectory(_directory);
			ApplyRetentionCore(_utcNow().ToUniversalTime());
		}
		catch
		{
			// Cleanup is best effort and must not break startup.
		}
	}

	private string SelectSegmentPath(DateTimeOffset now)
	{
		var prefix = $"pact-{now:yyyy-MM-dd}.";
		for (var segment = 0; ; segment++)
		{
			var path = Path.Combine(_directory, $"{prefix}{segment}.log");
			if (!File.Exists(path) || new FileInfo(path).Length < _maxSegmentBytes)
			{
				return path;
			}
		}
	}

	private void ApplyRetentionCore(DateTimeOffset now)
	{
		var cutoff = now.UtcDateTime - _retention;
		foreach (var path in Directory.EnumerateFiles(_directory, "pact-*.log", SearchOption.TopDirectoryOnly))
		{
			try
			{
				if (File.GetLastWriteTimeUtc(path) < cutoff)
				{
					File.Delete(path);
				}
			}
			catch (IOException)
			{
				// Continue with other segments when one is concurrently open.
			}
			catch (UnauthorizedAccessException)
			{
				// Continue with other segments when permissions changed externally.
			}
		}
	}
}