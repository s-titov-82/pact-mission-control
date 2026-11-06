using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

/// <summary>An immutable endpoint comparison; null base means the empty tree.</summary>
/// <param name="BaseCommit">Full base id.</param>
/// <param name="TargetCommit">Full target id.</param>
/// <param name="Files">Changed repository-relative paths.</param>
/// <param name="AddedLines">Added text lines.</param>
/// <param name="RemovedLines">Removed text lines.</param>
public sealed record GitRevisionChanges(string? BaseCommit, string TargetCommit, IReadOnlyList<GitFileEntry> Files, int AddedLines, int RemovedLines);

/// <summary>Reads arbitrary immutable comparisons; live local scopes belong to IGitReviewReader.</summary>
public interface IGitComparisonReader
{
	/// <summary>Reads changed paths and totals for exact full ids, or an empty-tree base.</summary>
	Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken);
	/// <summary>Reads one file's bounded diff between the captured endpoints.</summary>
	Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken);
}
