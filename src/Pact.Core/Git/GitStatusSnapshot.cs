namespace Pact.Core.Git;

/// <summary>
/// Immutable view of a working tree parsed from <c>git status --porcelain=v2</c>.
/// </summary>
/// <param name="Branch">Current branch name, or the commit id when <paramref name="IsDetached"/>.</param>
/// <param name="Upstream">
/// Tracking branch, or <see langword="null"/> when the branch has none. When null,
/// <paramref name="Ahead"/> and <paramref name="Behind"/> are both zero and carry no meaning.
/// </param>
/// <param name="Ahead">Commits present locally but not upstream.</param>
/// <param name="Behind">Commits present upstream but not locally.</param>
/// <param name="IsDetached">Whether HEAD is detached rather than on a branch.</param>
/// <param name="Files">Changed entries; empty for a clean tree.</param>
public sealed record GitStatusSnapshot(
	string Branch,
	string? Upstream,
	int Ahead,
	int Behind,
	bool IsDetached,
	IReadOnlyList<GitFileEntry> Files)
{
	/// <summary>Number of added entries.</summary>
	public int Added => Files.Count(file => file.Kind == GitChangeKind.Added);

	/// <summary>Number of modified entries.</summary>
	public int Modified => Files.Count(file => file.Kind == GitChangeKind.Modified);

	/// <summary>Number of deleted entries.</summary>
	public int Deleted => Files.Count(file => file.Kind == GitChangeKind.Deleted);

	/// <summary>Number of untracked entries.</summary>
	public int Untracked => Files.Count(file => file.Kind == GitChangeKind.Untracked);

	/// <summary>Number of entries with unresolved merge conflicts.</summary>
	public int Conflicted => Files.Count(file => file.Kind == GitChangeKind.Conflicted);

	/// <summary>
	/// Whether the tree has any change at all, untracked files included — so a tree holding
	/// only untracked files still counts as dirty.
	/// </summary>
	public bool IsDirty => Files.Count > 0;

	/// <summary>
	/// Whether any entry is conflicted, which blocks git actions that require a settled tree.
	/// </summary>
	public bool HasConflicts => Conflicted > 0;
}

/// <summary>
/// One changed path in a <see cref="GitStatusSnapshot"/>.
/// </summary>
/// <param name="Path">Repository-relative path, using forward slashes as git reports them.</param>
/// <param name="OriginalPath">
/// Previous path for a rename or copy; <see langword="null"/> otherwise.
/// </param>
/// <param name="Kind">How the path changed.</param>
public sealed record GitFileEntry(string Path, string? OriginalPath, GitChangeKind Kind);

/// <summary>
/// How one path changed, collapsing git's staged and unstaged states into the single
/// distinction the git panel presents.
/// </summary>
public enum GitChangeKind
{
	/// <summary>Newly tracked.</summary>
	Added,

	/// <summary>Contents changed, including renames and copies.</summary>
	Modified,

	/// <summary>Removed from the tree.</summary>
	Deleted,

	/// <summary>Present on disk but not tracked by git.</summary>
	Untracked,

	/// <summary>Left with unresolved conflict markers by a merge or rebase.</summary>
	Conflicted
}