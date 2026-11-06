using Pact.Core.Git;

namespace Pact.Infrastructure.Git;

/// <summary>Immutable commit identity and the metadata displayed by the history picker.</summary>
/// <param name="Id">Full resolved object id.</param>
/// <param name="Parents">Full parent ids in Git's order; empty for a root commit.</param>
/// <param name="Author">Author's display name.</param>
/// <param name="AuthoredAt">Authored timestamp with its recorded offset.</param>
/// <param name="Subject">Commit subject.</param>
public sealed record GitCommitInfo(string Id, IReadOnlyList<string> Parents, string Author, DateTimeOffset AuthoredAt, string Subject)
{
	/// <summary>Comparison base, or null for the empty tree.</summary>
	public string? BaseCommit => Parents.Count == 0 ? null : Parents[0];
	/// <summary>Compact display identity; never used to read Git objects.</summary>
	public string ShortId => Id[..Math.Min(8, Id.Length)];
	/// <summary>Whether the comparison deliberately uses the first of several parents.</summary>
	public bool IsMerge => Parents.Count > 1;
}

/// <summary>Changes introduced by a commit relative to its first parent or the empty tree.</summary>
/// <param name="Commit">Immutable target and comparison base.</param>
/// <param name="Files">Changed repository-relative paths.</param>
/// <param name="AddedLines">Added text lines, excluding binary files.</param>
/// <param name="RemovedLines">Removed text lines, excluding binary files.</param>
public sealed record CommitChanges(GitCommitInfo Commit, IReadOnlyList<GitFileEntry> Files, int AddedLines, int RemovedLines);

/// <summary>Reads history and immutable committed content without changing HEAD or the index.</summary>
public interface IGitCommitReader
{
	/// <summary>Reads at most fifty history entries from HEAD, skipping preceding entries.</summary>
	Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken);
	/// <summary>Resolves a ref or id to an immutable commit; null when it cannot resolve to a commit.</summary>
	Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken);
	/// <summary>Reads the paths and line totals introduced by an already resolved commit.</summary>
	Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken);
	/// <summary>Reads one committed file's first-parent diff with the existing display limits.</summary>
	Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken);
	/// <summary>Reads bounded text at a full commit id; null for missing, binary or oversized blobs.</summary>
	Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken);
}