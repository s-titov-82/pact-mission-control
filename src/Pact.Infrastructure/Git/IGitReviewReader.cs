using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

/// <summary>The checkpoint from which local review changes are measured.</summary>
public enum GitReviewScope
{
	/// <summary>Changes since HEAD, including local files.</summary>
	Uncommitted,
	/// <summary>Changes since the common ancestor with the chosen merge target.</summary>
	Unmerged,
	/// <summary>The exact difference from tracking, falling back to the merge target's base.</summary>
	Unpushed
}

/// <summary>A local comparison with separate immutable base and observed actual HEAD.</summary>
/// <param name="BaseCommit">Full comparison-base id, or empty tree.</param>
/// <param name="HeadCommit">Actual HEAD, or null when unborn.</param>
/// <param name="Files">Unique net paths, including nonignored untracked text.</param>
/// <param name="AddedLines">Added text lines, including untracked files.</param>
/// <param name="RemovedLines">Removed text lines.</param>
public sealed record GitLocalChanges(string? BaseCommit, string? HeadCommit, IReadOnlyList<GitFileEntry> Files, int AddedLines, int RemovedLines);

/// <summary>A scope's resolved checkpoint and net local result, or an explicit unavailable state.</summary>
/// <param name="Scope">Requested review intent.</param>
/// <param name="Branch">Observed local branch.</param>
/// <param name="BaseRef">Ref supplying the checkpoint or merge target.</param>
/// <param name="Changes">Net local comparison; null if unavailable.</param>
/// <param name="AheadCount">Local-only commits relative to tracking.</param>
/// <param name="BehindCount">Remote-only commits relative to tracking.</param>
/// <param name="IsFallback">Unpushed is using the Unmerged base because tracking is not configured.</param>
/// <param name="UnavailableReason">Explanation when no comparison is available.</param>
public sealed record GitReviewChanges(GitReviewScope Scope, string Branch, string? BaseRef, GitLocalChanges? Changes,
	int AheadCount, int BehindCount, bool IsFallback, string? UnavailableReason)
{
	/// <summary>Preflight refusal from the observed repository status; execution must check again.</summary>
	public string? PullRebaseBlockedReason { get; init; }
	/// <summary>The immutable base, independent of actual HEAD.</summary>
	public string? BaseCommit => Changes?.BaseCommit;
	/// <summary>Actual HEAD observed while reading local content.</summary>
	public string? HeadCommit => Changes?.HeadCommit;
	/// <summary>The aggregate paths, independent of any selected commit filter.</summary>
	public IReadOnlyList<GitFileEntry> Files => Changes?.Files ?? [];
	/// <summary>Added lines in the net comparison.</summary>
	public int AddedLines => Changes?.AddedLines ?? 0;
	/// <summary>Removed lines in the net comparison.</summary>
	public int RemovedLines => Changes?.RemovedLines ?? 0;
}

/// <summary>Reads review checkpoints and local content without changing Git refs or the index.</summary>
public interface IGitReviewReader
{
	/// <summary>Creates a short-lived sequential observation reader that may reuse same-base local snapshots within one tick.</summary>
	/// <remarks>Discard after that tick; never retain it for subsequent polling or explicit user selections.</remarks>
	IGitReviewReader CreateReadBatch() => this;
	/// <summary>Resolves a scope against an explicit branch or automatic default; does not fetch.</summary>
	Task<GitReviewChanges> ReadReviewChangesAsync(string root, GitReviewScope scope, string? targetBranch, CancellationToken cancellationToken);
	/// <summary>Reads the current local net result against a full captured base, or the empty tree.</summary>
	Task<GitLocalChanges> ReadLocalChangesAsync(string root, string? baseCommit, CancellationToken cancellationToken);
	/// <summary>Reads a bounded file diff against its captured local comparison base.</summary>
	Task<FileDiff> ReadLocalDiffAsync(string root, GitFileEntry entry, string? baseCommit, CancellationToken cancellationToken);
	/// <summary>Lists branch ref names suitable for explicit target selection.</summary>
	Task<IReadOnlyList<string>> ReadBranchesAsync(string root, CancellationToken cancellationToken);
	/// <summary>Reads up to fifty local-side commits after a scope base, with immutable endpoints.</summary>
	Task<IReadOnlyList<GitCommitInfo>> ReadScopeCommitsAsync(string root, string? baseCommit, string headCommit, int skip, int count, CancellationToken cancellationToken);
}
