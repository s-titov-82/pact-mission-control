namespace Pact.Infrastructure.Git;

/// <summary>
/// Tests whether a project root is under git, deciding if the git panel is offered at all.
/// </summary>
public static class GitRepositoryDetector
{
	/// <summary>
	/// Whether <paramref name="rootPath"/> is the root of a git repository.
	/// </summary>
	/// <remarks>
	/// Accepts <c>.git</c> as either a directory or a file, because a worktree or submodule
	/// checkout stores a gitdir pointer file rather than a directory.
	/// </remarks>
	public static bool IsGitRepository(string? rootPath)
	{
		if (string.IsNullOrWhiteSpace(rootPath))
		{
			return false;
		}

		var gitPath = Path.Combine(rootPath, ".git");
		return Directory.Exists(gitPath) || File.Exists(gitPath);
	}
}
