using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

/// <summary>An immutable endpoint comparison; null base means the empty tree.</summary>
/// <param name="BaseCommit">Full base id.</param>
/// <param name="TargetCommit">Full target id.</param>
/// <param name="Files">Changed repository-relative paths.</param>
/// <param name="AddedLines">Added text lines.</param>
/// <param name="RemovedLines">Removed text lines.</param>
public sealed record GitRevisionChanges(string? BaseCommit, string TargetCommit, IReadOnlyList<GitFileEntry> Files, int AddedLines, int RemovedLines);

/// <summary>The current branch's committed difference against its configured tracking upstream.</summary>
/// <param name="Branch">Observed local branch.</param>
/// <param name="Upstream">Configured tracking ref for display.</param>
/// <param name="Comparison">Resolved endpoints, absent when unavailable.</param>
/// <param name="UnavailableReason">Explanatory status, never represented as a clean comparison.</param>
public sealed record GitBranchChanges(string Branch, string? Upstream, GitRevisionChanges? Comparison, string? UnavailableReason);

/// <summary>Reads arbitrary immutable comparisons and the current tracking-upstream comparison.</summary>
public interface IGitComparisonReader
{
	/// <summary>Resolves HEAD and tracking upstream without fetching or changing repository state.</summary>
	Task<GitBranchChanges> ReadBranchChangesAsync(string root, CancellationToken cancellationToken);
	/// <summary>Reads changed paths and totals for exact full ids, or an empty-tree base.</summary>
	Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken);
	/// <summary>Reads one file's bounded diff between the captured endpoints.</summary>
	Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken);
}
