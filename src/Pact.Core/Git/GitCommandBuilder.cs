namespace Pact.Core.Git;

/// <summary>
/// Builds git argument lists as separate elements, never as a joined command line, so paths
/// and messages containing spaces or quotes cannot alter the command's structure.
/// </summary>
public static class GitCommandBuilder
{
	// --no-rebase forces merge reconciliation so pull does not abort with
	// "Not possible to fast-forward" when local and remote branches diverge.

	/// <summary>
	/// Builds a pull that reconciles by merging, so a diverged branch does not abort the pull.
	/// </summary>
	public static IReadOnlyList<string> BuildPullArguments() => ["pull", "--no-rebase"];

	/// <summary>
	/// Builds a push.
	/// </summary>
	/// <param name="remote">Target remote.</param>
	/// <param name="branch">Branch to push.</param>
	/// <param name="mode">Whether and how to force; see <see cref="GitPushMode"/>.</param>
	/// <param name="setUpstream">Whether to record the remote branch as upstream.</param>
	/// <param name="extraArguments">Configured extra arguments, inserted before the generated flags.</param>
	public static IReadOnlyList<string> BuildPushArguments(
		string remote,
		string branch,
		GitPushMode mode,
		bool setUpstream,
		IReadOnlyList<string>? extraArguments = null)
	{
		List<string> arguments = ["push"];
		arguments.AddRange(extraArguments ?? []);

		if (mode == GitPushMode.ForceWithLease)
		{
			arguments.Add("--force-with-lease");
		}
		else if (mode == GitPushMode.Force)
		{
			arguments.Add("--force");
		}

		if (setUpstream)
		{
			arguments.Add("-u");
		}

		arguments.Add(remote);
		arguments.Add(branch);
		return arguments;
	}

	/// <summary>
	/// Builds a stage command for explicit paths. The <c>--</c> separator is always present so
	/// a path resembling an option is still treated as a path.
	/// </summary>
	public static IReadOnlyList<string> BuildStageArguments(IEnumerable<string> paths)
	{
		List<string> arguments = ["add", "-A", "--"];
		arguments.AddRange(paths);
		return arguments;
	}

	/// <summary>
	/// Builds a stage command for status entries, including each rename's original path so the
	/// rename is staged as a whole.
	/// </summary>
	public static IReadOnlyList<string> BuildStageArguments(IEnumerable<GitFileEntry> files)
	{
		ArgumentNullException.ThrowIfNull(files);

		return BuildStageArguments(ExpandFileEntryPaths(files));
	}

	/// <summary>
	/// Builds a commit limited to the given paths.
	/// </summary>
	/// <param name="message">Commit message, passed as its own argument so newlines survive.</param>
	/// <param name="paths">Paths to commit.</param>
	/// <param name="extraArguments">Configured extra arguments, inserted before the message.</param>
	public static IReadOnlyList<string> BuildCommitArguments(
		string message,
		IEnumerable<string> paths,
		IReadOnlyList<string>? extraArguments = null)
	{
		List<string> arguments = ["commit"];
		arguments.AddRange(extraArguments ?? []);
		arguments.Add("-m");
		arguments.Add(message);
		arguments.Add("--");
		arguments.AddRange(paths);
		return arguments;
	}

	/// <summary>
	/// Builds a commit for status entries, including each rename's original path.
	/// </summary>
	public static IReadOnlyList<string> BuildCommitArguments(
		string message,
		IEnumerable<GitFileEntry> files,
		IReadOnlyList<string>? extraArguments = null)
	{
		ArgumentNullException.ThrowIfNull(files);

		return BuildCommitArguments(message, ExpandFileEntryPaths(files), extraArguments);
	}

	/// <summary>
	/// Builds a branch switch.
	/// </summary>
	/// <param name="branch">Branch to switch to, or to create.</param>
	/// <param name="create">Whether to create the branch.</param>
	/// <param name="track">
	/// Whether to set up tracking. Ignored when <paramref name="create"/> is set, since a newly
	/// created branch has no upstream to track yet.
	/// </param>
	/// <param name="extraArguments">Configured extra arguments, inserted before the generated flags.</param>
	public static IReadOnlyList<string> BuildSwitchArguments(
		string branch,
		bool create,
		bool track = false,
		IReadOnlyList<string>? extraArguments = null)
	{
		List<string> arguments = ["switch"];
		arguments.AddRange(extraArguments ?? []);

		if (create)
		{
			arguments.Add("-c");
		}
		else if (track)
		{
			arguments.Add("--track");
		}

		arguments.Add(branch);
		return arguments;
	}

	/// <summary>
	/// Builds a rebase of the current branch onto <paramref name="branch"/>.
	/// </summary>
	public static IReadOnlyList<string> BuildRebaseArguments(
		string branch,
		IReadOnlyList<string>? extraArguments = null)
	{
		List<string> arguments = ["rebase"];
		arguments.AddRange(extraArguments ?? []);
		arguments.Add(branch);
		return arguments;
	}

	/// <summary>
	/// Builds a merge of <paramref name="branch"/> into the current branch.
	/// </summary>
	public static IReadOnlyList<string> BuildMergeArguments(
		string branch,
		IReadOnlyList<string>? extraArguments = null)
	{
		List<string> arguments = ["merge"];
		arguments.AddRange(extraArguments ?? []);
		arguments.Add(branch);
		return arguments;
	}

	/// <summary>Builds a stash push, saving the working tree so a branch switch can proceed.</summary>
	public static IReadOnlyList<string> BuildStashPushArguments() => ["stash", "push"];

	/// <summary>Builds a stash pop, restoring the most recently stashed changes.</summary>
	public static IReadOnlyList<string> BuildStashPopArguments() => ["stash", "pop"];

	/// <summary>
	/// Builds a merge-tool launch that skips the per-file prompt, since the user already chose
	/// to resolve conflicts.
	/// </summary>
	public static IReadOnlyList<string> BuildMergetoolArguments() => ["mergetool", "-y"];

	private static List<string> ExpandFileEntryPaths(IEnumerable<GitFileEntry> files)
	{
		List<string> paths = [];

		foreach (var file in files)
		{
			if (!string.IsNullOrEmpty(file.OriginalPath))
			{
				paths.Add(file.OriginalPath);
			}

			paths.Add(file.Path);
		}

		return paths;
	}
}

/// <summary>
/// How hard a push may overwrite the remote branch.
/// </summary>
public enum GitPushMode
{
	/// <summary>Fails rather than overwriting remote commits.</summary>
	Normal,

	/// <summary>
	/// Overwrites only if the remote still matches what was last fetched, so a teammate's
	/// commits pushed in the meantime are not silently discarded. Preferred over
	/// <see cref="Force"/>.
	/// </summary>
	ForceWithLease,

	/// <summary>Overwrites unconditionally, discarding any remote commits not held locally.</summary>
	Force
}