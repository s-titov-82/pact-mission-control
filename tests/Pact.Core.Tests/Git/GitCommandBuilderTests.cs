using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class GitCommandBuilderTests
{
	[Test]
	public void BuildPullArguments_returns_pull_with_no_rebase() => GitCommandBuilder.BuildPullArguments().ShouldBe(["pull", "--no-rebase"]);

	[Test]
	public void BuildPushArguments_returns_normal_push_without_upstream_flag()
	{
		var arguments = GitCommandBuilder.BuildPushArguments(
			remote: "origin",
			branch: "feat/x",
			mode: GitPushMode.Normal,
			setUpstream: false);

		arguments.ShouldBe(["push", "origin", "feat/x"]);
	}

	[Test]
	public void BuildPushArguments_returns_force_with_lease_before_upstream_flag()
	{
		var arguments = GitCommandBuilder.BuildPushArguments(
			remote: "origin",
			branch: "feat/x",
			mode: GitPushMode.ForceWithLease,
			setUpstream: true);

		arguments.ShouldBe(["push", "--force-with-lease", "-u", "origin", "feat/x"]);
	}

	[Test]
	public void BuildPushArguments_returns_force_without_force_with_lease()
	{
		var arguments = GitCommandBuilder.BuildPushArguments(
			remote: "origin",
			branch: "feat/x",
			mode: GitPushMode.Force,
			setUpstream: false);

		arguments.ShouldContain("--force");
		arguments.ShouldNotContain("--force-with-lease");
		arguments.ShouldBe(["push", "--force", "origin", "feat/x"]);
	}

	[Test]
	public void BuildStageArguments_uses_pathspec_separator()
	{
		var arguments = GitCommandBuilder.BuildStageArguments(new[] { "a.txt", "src/b.txt" });

		arguments.ShouldBe(["add", "-A", "--", "a.txt", "src/b.txt"]);
	}

	[Test]
	public void BuildCommitArguments_uses_message_and_pathspec_separator()
	{
		var arguments = GitCommandBuilder.BuildCommitArguments(
			"commit message",
			new[] { "a.txt", "src/b.txt" });

		arguments.ShouldBe(["commit", "-m", "commit message", "--", "a.txt", "src/b.txt"]);
	}

	[Test]
	public void BuildCommitArguments_from_file_entries_expands_renames_to_old_and_new_paths()
	{
		GitFileEntry[] files =
		[
			new("new-name.txt", "old-name.txt", GitChangeKind.Modified),
			new("added.txt", null, GitChangeKind.Added)
		];

		var arguments = GitCommandBuilder.BuildCommitArguments("rename", files);

		arguments.ShouldBe(["commit", "-m", "rename", "--", "old-name.txt", "new-name.txt", "added.txt"]);
	}

	[Test]
	public void BuildSwitchArguments_supports_existing_and_new_branches()
	{
		GitCommandBuilder.BuildSwitchArguments("dev", create: false).ShouldBe(["switch", "dev"]);
		GitCommandBuilder.BuildSwitchArguments("dev", create: true).ShouldBe(["switch", "-c", "dev"]);
	}

	[Test]
	public void BuildSwitchArguments_supports_remote_tracking_branch() => GitCommandBuilder.BuildSwitchArguments("origin/dev", create: false, track: true).ShouldBe(["switch", "--track", "origin/dev"]);

	[Test]
	public void BuildRebaseMergeStashAndMergetoolArguments_return_simple_argument_lists()
	{
		GitCommandBuilder.BuildRebaseArguments("master").ShouldBe(["rebase", "master"]);
		GitCommandBuilder.BuildMergeArguments("dev").ShouldBe(["merge", "dev"]);
		GitCommandBuilder.BuildStashPushArguments().ShouldBe(["stash", "push"]);
		GitCommandBuilder.BuildStashPopArguments().ShouldBe(["stash", "pop"]);
		GitCommandBuilder.BuildMergetoolArguments().ShouldBe(["mergetool", "-y"]);
	}

	[Test]
	public void Dialog_builders_insert_extra_arguments_right_after_the_subcommand()
	{
		GitCommandBuilder.BuildPushArguments("origin", "feat/x", GitPushMode.ForceWithLease, setUpstream: true, extraArguments: ["--follow-tags"]).ShouldBe(["push", "--follow-tags", "--force-with-lease", "-u", "origin", "feat/x"]);

		GitCommandBuilder.BuildCommitArguments("msg", new[] { "a.txt" }, extraArguments: ["--signoff"]).ShouldBe(["commit", "--signoff", "-m", "msg", "--", "a.txt"]);

		GitCommandBuilder.BuildSwitchArguments("dev", create: true, track: false, extraArguments: ["--quiet"]).ShouldBe(["switch", "--quiet", "-c", "dev"]);

		GitCommandBuilder.BuildRebaseArguments("master", ["--autostash"]).ShouldBe(["rebase", "--autostash", "master"]);

		GitCommandBuilder.BuildMergeArguments("dev", ["--no-ff"]).ShouldBe(["merge", "--no-ff", "dev"]);
	}
}