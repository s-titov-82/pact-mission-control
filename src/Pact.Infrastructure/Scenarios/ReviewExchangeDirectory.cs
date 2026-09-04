using Pact.Core.Scenarios;

namespace Pact.Infrastructure.Scenarios;

/// <summary>Immutable paths and completion footer for one pass/role exchange.</summary>
public sealed record ReviewExchangeStepPaths(
	string StepId,
	string TaskPath,
	string ResponsePath,
	string CompletionFooter);

/// <summary>
/// Owns the narrowly reserved .pact-reviews directory and never operates on generic .reviews data.
/// </summary>
public sealed class ReviewExchangeDirectory
{
	/// <summary>Directory name reserved for Pact review-loop exchange files.</summary>
	public const string RootName = ".pact-reviews";

	/// <summary>Allocates deterministic paths for one review pass and role.</summary>
	public static ReviewExchangeStepPaths CreateStep(
		string projectRoot,
		string runId,
		int iteration,
		string role)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iteration);
		ValidatePathSegment(role, nameof(role));
		var runDirectory = EnsureRunDirectory(projectRoot, runId);
		var shortRunId = Path.GetFileName(runDirectory);
		var stepId = $"pass-{iteration:D3}-{role}";
		return new ReviewExchangeStepPaths(
			stepId,
			Path.Combine(runDirectory, $"{stepId}-task.md"),
			Path.Combine(runDirectory, $"{stepId}-response.md"),
			$"<!-- PACT_RESPONSE_COMPLETE:{shortRunId}:{stepId} -->");
	}

	/// <summary>Atomically publishes a complete immutable task before terminal submission.</summary>
	public static async Task PublishTaskAsync(
		ReviewExchangeStepPaths step,
		string content,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentException.ThrowIfNullOrWhiteSpace(content);
		var directory = Path.GetDirectoryName(step.TaskPath)
			?? throw new InvalidOperationException("Task path has no parent directory.");
		Directory.CreateDirectory(directory);
		var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(step.TaskPath)}.{Guid.NewGuid():N}.tmp");
		try
		{
			await File.WriteAllTextAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
			File.Move(temporaryPath, step.TaskPath, overwrite: false);
		}
		finally
		{
			if (File.Exists(temporaryPath))
			{
				File.Delete(temporaryPath);
			}
		}
	}

	/// <summary>Waits until the unique response contains non-empty content and its exact final footer.</summary>
	public static async Task<string> WaitForCompletedResponseAsync(
		ReviewExchangeStepPaths step,
		TimeSpan watchdogTimeout,
		TimeSpan pollInterval,
		Action? incompleteResponseDetected,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(watchdogTimeout, TimeSpan.Zero);
		ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
		using var watchdog =
			CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		watchdog.CancelAfter(watchdogTimeout);
		var incompleteReported = false;

		try
		{
			while (true)
			{
				string? content = null;
				var exists = File.Exists(step.ResponsePath);
				if (exists)
				{
					try
					{
						content = await ReadFileToleratingWritersAsync(
							step.ResponsePath,
							watchdog.Token).ConfigureAwait(false);
					}
					catch (IOException)
					{
						// A writer that denies sharing is still publishing the file.
					}
				}

				var completed = TryExtractCompletedResponse(content, step.CompletionFooter);
				if (completed is not null)
				{
					return completed;
				}

				if (exists && !incompleteReported)
				{
					incompleteReported = true;
					incompleteResponseDetected?.Invoke();
				}

				await Task.Delay(pollInterval, watchdog.Token).ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			throw new ScenarioStepTimeoutException(
				$"Scenario step timed out waiting for completed response file '{step.ResponsePath}'.");
		}
	}

	/// <summary>Deletes only the directory computed for the supplied run identifier.</summary>
	public static void CleanupRun(string projectRoot, string runId)
	{
		ArgumentNullException.ThrowIfNull(runId);

		var root = GetOwnedRoot(projectRoot);
		var runDirectory = GetRunDirectory(projectRoot, runId);
		if (Directory.Exists(runDirectory))
		{
			Directory.Delete(runDirectory, recursive: true);
		}

		if (!Directory.Exists(root) || Directory.EnumerateDirectories(root).Any())
		{
			return;
		}

		var gitignorePath = Path.Combine(root, ".gitignore");
		if (File.Exists(gitignorePath))
		{
			File.Delete(gitignorePath);
		}

		if (!Directory.EnumerateFileSystemEntries(root).Any())
		{
			Directory.Delete(root);
		}
	}

	/// <summary>Removes the Pact-owned exchange root left by an interrupted prior process.</summary>
	public static void CleanupAbandoned(string projectRoot)
	{
		var root = GetOwnedRoot(projectRoot);
		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static string GetOwnedRoot(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var normalizedProjectRoot = Path.GetFullPath(projectRoot);
		return EnsureDescendant(normalizedProjectRoot, Path.Combine(normalizedProjectRoot, RootName));
	}

	private static string GetRunDirectory(string projectRoot, string runId)
	{
		ValidatePathSegment(runId, nameof(runId));

		var root = GetOwnedRoot(projectRoot);
		var shortRunId = runId[..Math.Min(8, runId.Length)];
		return EnsureDescendant(root, Path.Combine(root, shortRunId));
	}

	private static string EnsureRunDirectory(string projectRoot, string runId)
	{
		var root = GetOwnedRoot(projectRoot);
		var runDirectory = GetRunDirectory(projectRoot, runId);
		Directory.CreateDirectory(root);
		var gitignorePath = Path.Combine(root, ".gitignore");
		if (!File.Exists(gitignorePath))
		{
			File.WriteAllText(gitignorePath, "*\n");
		}

		Directory.CreateDirectory(runDirectory);
		return runDirectory;
	}

	private static void ValidatePathSegment(string value, string parameterName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
		if (value is "." or ".."
			|| value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
			|| value.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			|| value.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
		{
			throw new ArgumentException("Value must be a single valid path segment.", parameterName);
		}
	}

	private static string? TryExtractCompletedResponse(string? content, string expectedFooter)
	{
		if (string.IsNullOrEmpty(content))
		{
			return null;
		}

		var lines = content
			.Replace("\r\n", "\n", StringComparison.Ordinal)
			.Replace('\r', '\n')
			.Split('\n');
		var footerIndex = Array.FindLastIndex(lines, line => !string.IsNullOrWhiteSpace(line));
		if (footerIndex < 0
			|| !string.Equals(lines[footerIndex], expectedFooter, StringComparison.Ordinal)
			|| lines[..footerIndex].Any(IsTransportFooterLine))
		{
			return null;
		}

		var body = string.Join('\n', lines[..footerIndex]).TrimEnd();
		return body.Length == 0 ? null : body;
	}

	private static bool IsTransportFooterLine(string line)
	{
		var candidate = line.Trim();
		return candidate.StartsWith("<!-- PACT_RESPONSE_COMPLETE:", StringComparison.Ordinal)
			&& candidate.EndsWith("-->", StringComparison.Ordinal);
	}

	private static async Task<string> ReadFileToleratingWritersAsync(
		string path,
		CancellationToken cancellationToken)
	{
		await using FileStream stream = new(
			path,
			FileMode.Open,
			FileAccess.Read,
			FileShare.ReadWrite | FileShare.Delete);
		using StreamReader reader = new(stream);
		return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
	}

	private static string EnsureDescendant(string parent, string candidate)
	{
		var normalizedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
		var normalizedCandidate = Path.GetFullPath(candidate);
		var parentPrefix = normalizedParent + Path.DirectorySeparatorChar;
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		if (!normalizedCandidate.StartsWith(parentPrefix, comparison))
		{
			throw new InvalidOperationException("Computed review exchange path escaped its owned parent.");
		}

		return normalizedCandidate;
	}
}
