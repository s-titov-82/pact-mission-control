namespace Pact.Core.Git;

/// <summary>
/// Builds the ordered git invocations for "rebase onto base branch". Planning is separated
/// from execution so the exact command sequence can be shown to the user and unit tested
/// without running git.
/// </summary>
public static class GitRebaseOntoBasePlanner
{
	/// <summary>
	/// Plans the steps needed to bring the current branch onto an updated base branch.
	/// </summary>
	/// <param name="isDirty">
	/// Whether the working tree has changes. When true the plan brackets the rebase with a
	/// stash push and pop so local work is preserved across the branch switches.
	/// </param>
	/// <param name="baseBranch">Branch to rebase onto.</param>
	/// <param name="currentBranch">Branch being rebased.</param>
	/// <param name="commands">
	/// Configured button commands supplying the pull and rebase argument overrides, or
	/// <see langword="null"/> to use the defaults.
	/// </param>
	/// <returns>
	/// Steps to run in order. When the current branch already is the base branch, the plan
	/// collapses to a single pull rather than rebasing a branch onto itself.
	/// </returns>
	public static IReadOnlyList<GitPlannedStep> Plan(
		bool isDirty,
		string baseBranch,
		string currentBranch,
		GitButtonCommandSet? commands = null)
	{
		commands ??= GitButtonCommandSet.Create(null);
		var pullArguments = commands.Arguments(GitButtonCommandSet.PullId);

		if (string.Equals(baseBranch, currentBranch, StringComparison.Ordinal))
		{
			return [new GitPlannedStep($"Pull {baseBranch}", pullArguments)];
		}

		List<GitPlannedStep> steps = [];

		if (isDirty)
		{
			steps.Add(new GitPlannedStep("Stash changes", GitCommandBuilder.BuildStashPushArguments()));
		}

		steps.Add(new GitPlannedStep($"Switch to {baseBranch}", GitCommandBuilder.BuildSwitchArguments(baseBranch, create: false)));
		steps.Add(new GitPlannedStep($"Pull {baseBranch}", pullArguments));
		steps.Add(new GitPlannedStep($"Switch to {currentBranch}", GitCommandBuilder.BuildSwitchArguments(currentBranch, create: false)));
		steps.Add(new GitPlannedStep(
			$"Rebase onto {baseBranch}",
			GitCommandBuilder.BuildRebaseArguments(baseBranch, commands.ExtraArguments(GitButtonCommandSet.RebaseId))));

		if (isDirty)
		{
			steps.Add(new GitPlannedStep("Restore stashed changes", GitCommandBuilder.BuildStashPopArguments()));
		}

		return steps;
	}
}

/// <summary>
/// One git invocation in a planned sequence.
/// </summary>
/// <param name="Title">Progress text shown while the step runs.</param>
/// <param name="Arguments">Arguments passed to git, already fully substituted.</param>
public sealed record GitPlannedStep(string Title, IReadOnlyList<string> Arguments);