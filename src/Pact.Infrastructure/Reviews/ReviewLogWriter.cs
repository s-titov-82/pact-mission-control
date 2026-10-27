using System.Globalization;
using System.Text;
using Pact.Core.Reviews;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Reviews;

/// <summary>What the head of a review log records.</summary>
public sealed record ReviewLogHeader(
	string TemplateName,
	string Target,
	string AuthorTitle,
	string AuthorKind,
	string ReviewerTitle,
	string ReviewerKind,
	int MaxIterations,
	DateTimeOffset StartedAt);

/// <summary>
/// Keeps the Markdown log of one review under the author's project, appending after every turn
/// so a crash keeps what happened.
/// </summary>
public sealed class ReviewLogWriter : IDisposable
{
	private const int KeptLogs = 10;

	// Two reviews starting in one project must not prune from the same listing.
	private static readonly Lock RetentionGate = new();
	private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

	// A turn and the end of the review can be written from different threads.
	private readonly SemaphoreSlim _gate = new(1, 1);

	/// <summary>
	/// Picks the log file for a review in <c>.pact/reviews</c>, removes the oldest logs not in
	/// <paramref name="logsInUse"/> so ten remain with the new one, and keeps the <c>.pact</c> folder out of Git by writing
	/// <c>.pact/.gitignore</c> when it is missing.
	/// </summary>
	public static string CreatePath(
		string projectRoot,
		DateTimeOffset startedAt,
		string templateId,
		string reviewId,
		IReadOnlyCollection<string> logsInUse)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(logsInUse);
		ArgumentException.ThrowIfNullOrWhiteSpace(templateId);
		ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);

		PactFolder.EnsureGitIgnored(projectRoot);
		var reviewsDirectory = Path.Combine(projectRoot, ".pact", "reviews");
		Directory.CreateDirectory(reviewsDirectory);

		PruneDirectory(reviewsDirectory, logsInUse, keep: KeptLogs - 1);

		var shortId = reviewId.Length > 8 ? reviewId[..8] : reviewId;
		var stamp = startedAt.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture);
		return Path.Combine(reviewsDirectory, $"{stamp}-{templateId}-{shortId}.md");
	}

	/// <summary>Removes the oldest logs not in <paramref name="logsInUse"/> so ten remain.</summary>
	public static void Prune(string projectRoot, IReadOnlyCollection<string> logsInUse)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentNullException.ThrowIfNull(logsInUse);
		var reviewsDirectory = Path.Combine(projectRoot, ".pact", "reviews");
		if (Directory.Exists(reviewsDirectory))
		{
			PruneDirectory(reviewsDirectory, logsInUse, keep: KeptLogs);
		}
	}

	// Oldest first by creation time (names carry only the minute); a log a running review still
	// writes to is never removed, so the folder can briefly hold more and shrinks when it ends.
	private static void PruneDirectory(string reviewsDirectory, IReadOnlyCollection<string> logsInUse, int keep)
	{
		lock (RetentionGate)
		{
			var logs = Directory.GetFiles(reviewsDirectory, "*.md")
				.OrderBy(File.GetCreationTimeUtc)
				.ThenBy(path => path, StringComparer.Ordinal)
				.ToArray();
			var excess = logs.Length - keep;
			foreach (var old in logs
				.Where(path => !logsInUse.Contains(path, StringComparer.OrdinalIgnoreCase))
				.Take(Math.Max(0, excess)))
			{
				File.Delete(old);
			}
		}
	}

	/// <summary>Starts the log with what was reviewed and by whom.</summary>
	public Task WriteHeaderAsync(string path, ReviewLogHeader header, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(header);
		StringBuilder text = new();
		text.Append("# ").Append(header.TemplateName).Append("\n\n");
		text.Append("- Author: ").Append(header.AuthorTitle).Append(" (").Append(header.AuthorKind).Append(")\n");
		text.Append("- Reviewer: ").Append(header.ReviewerTitle).Append(" (").Append(header.ReviewerKind).Append(")\n");
		text.Append("- Iteration limit: ").Append(header.MaxIterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
		text.Append("- Started: ").Append(Stamp(header.StartedAt)).Append("\n\n");
		text.Append("## Target\n\n").Append(header.Target.TrimEnd()).Append("\n\n");
		return WriteAsync(path, text.ToString(), append: false, cancellationToken);
	}

	/// <summary>Appends one submitted turn, as the agent wrote it.</summary>
	public Task AppendTurnAsync(
		string path,
		ReviewRole role,
		int iteration,
		DateTimeOffset at,
		string text,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		return WriteAsync(
			path,
			$"## {role}, iteration {iteration.ToString(CultureInfo.InvariantCulture)} — {Stamp(at)}\n\n{text.TrimEnd()}\n\n",
			append: true,
			cancellationToken);
	}

	/// <summary>Closes the log with how the review ended.</summary>
	public Task WriteFooterAsync(
		string path,
		ReviewState state,
		string? reason,
		DateTimeOffset at,
		CancellationToken cancellationToken) =>
		WriteAsync(
			path,
			$"## Ended: {state} — {Stamp(at)}\n\n{(string.IsNullOrWhiteSpace(reason) ? string.Empty : reason.TrimEnd() + "\n")}",
			append: true,
			cancellationToken);

	private async Task WriteAsync(string path, string text, bool append, CancellationToken cancellationToken)
	{
		await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			// The folder may have been cleaned away during the review.
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			if (append)
			{
				await File.AppendAllTextAsync(path, text, Utf8, cancellationToken).ConfigureAwait(false);
			}
			else
			{
				await File.WriteAllTextAsync(path, text, Utf8, cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			_gate.Release();
		}
	}

	/// <inheritdoc />
	public void Dispose() => _gate.Dispose();

	private static string Stamp(DateTimeOffset at) =>
		at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
