using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class GitRebaseOntoBasePlannerTests
{
	[Test]
	public void Plan_for_dirty_feature_branch_stashes_updates_base_rebases_then_pops()
	{
		var steps = GitRebaseOntoBasePlanner.Plan(
			isDirty: true,
			baseBranch: "master",
			currentBranch: "feat/x");

		steps.Count.ShouldBe(6);
		AssertStep(steps[0], "Stash changes", ["stash", "push"]);
		AssertStep(steps[1], "Switch to master", ["switch", "master"]);
		AssertStep(steps[2], "Pull master", ["pull", "--no-rebase"]);
		AssertStep(steps[3], "Switch to feat/x", ["switch", "feat/x"]);
		AssertStep(steps[4], "Rebase onto master", ["rebase", "master"]);
		AssertStep(steps[5], "Restore stashed changes", ["stash", "pop"]);

		var rebaseIndex = IndexOfArguments(steps, ["rebase", "master"]);
		var popIndex = IndexOfArguments(steps, ["stash", "pop"]);
		(popIndex > rebaseIndex).ShouldBeTrue();
	}

	[Test]
	public void Plan_for_clean_feature_branch_omits_stash_steps()
	{
		var steps = GitRebaseOntoBasePlanner.Plan(
			isDirty: false,
			baseBranch: "master",
			currentBranch: "feat/x");

		steps.Count.ShouldBe(4);
		AssertStep(steps[0], "Switch to master", ["switch", "master"]);
		AssertStep(steps[1], "Pull master", ["pull", "--no-rebase"]);
		AssertStep(steps[2], "Switch to feat/x", ["switch", "feat/x"]);
		AssertStep(steps[3], "Rebase onto master", ["rebase", "master"]);
	}

	[Test]
	public void Plan_when_current_is_base_degrades_to_single_pull()
	{
		var step = GitRebaseOntoBasePlanner.Plan(
			isDirty: true,
			baseBranch: "master",
			currentBranch: "master").ShouldHaveSingleItem();

		AssertStep(step, "Pull master", ["pull", "--no-rebase"]);
	}

	[Test]
	public void Plan_uses_configured_pull_command_and_rebase_extra_flags()
	{
		var commands = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("pull", "Pull", Command: "pull --rebase"),
			new GitButtonCommandRecord("rebase", "Rebase", ExtraArgs: "--autostash")
		]);

		var steps = GitRebaseOntoBasePlanner.Plan(
			isDirty: false,
			baseBranch: "master",
			currentBranch: "feat/x",
			commands);

		AssertStep(steps[1], "Pull master", ["pull", "--rebase"]);
		AssertStep(steps[3], "Rebase onto master", ["rebase", "--autostash", "master"]);
	}

	private static void AssertStep(GitPlannedStep step, string title, IReadOnlyList<string> arguments)
	{
		step.Title.ShouldBe(title);
		step.Arguments.ShouldBe(arguments);
	}

	private static int IndexOfArguments(IReadOnlyList<GitPlannedStep> steps, IReadOnlyList<string> arguments)
	{
		for (var index = 0; index < steps.Count; index++)
		{
			if (steps[index].Arguments.SequenceEqual(arguments))
			{
				return index;
			}
		}

		return -1;
	}
}