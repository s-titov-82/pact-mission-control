using Pact.Core.Git;

namespace Pact.Infrastructure.Tests.Git;

public sealed partial class GitCommitReaderTests
{
	[Test]
	public async Task Observation_batch_reuses_same_base_local_snapshot_but_the_next_batch_reads_disk_again()
	{
		await Write("a", "base\n");
		await Commit("base");
		await Git("checkout", "-b", "feature");
		await Write("untracked", "one\n");
		var runner = new CountingLocalDiffRunner(_runner);
		GitWorkingTreeReader reader = new(runner);
		var batch = reader.CreateReadBatch();
		var uncommitted = await batch.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, "refs/heads/main", Token);
		var unmerged = await batch.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, "refs/heads/main", Token);
		var unpushed = await batch.ReadReviewChangesAsync(Root, GitReviewScope.Unpushed, "refs/heads/main", Token);
		unmerged.Changes.ShouldBeSameAs(uncommitted.Changes);
		unpushed.Changes.ShouldBeSameAs(uncommitted.Changes);
		runner.NumstatReads.ShouldBe(1);
		await Write("untracked", "one\ntwo\n");
		var next = await reader.CreateReadBatch().ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token);
		next.AddedLines.ShouldBe(2);
		runner.NumstatReads.ShouldBe(2);
	}

	private sealed class CountingLocalDiffRunner(IGitCliRunner inner) : IGitCliRunner
	{
		public int NumstatReads { get; private set; }
		public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, IProgress<string>? outputLine, CancellationToken cancellationToken)
		{
			if (arguments.Contains("--numstat"))
			{ NumstatReads++; }
			return inner.RunAsync(workingDirectory, arguments, outputLine, cancellationToken);
		}
	}
	[Test]
	public async Task Pull_eligibility_uses_index_and_operation_state_even_when_net_diff_is_empty()
	{
		await Write("a", "base\n");
		await Commit("base");
		await Git("branch", "tracking");
		await Git("branch", "--set-upstream-to=tracking", "main");
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token)).PullRebaseBlockedReason.ShouldBeNull();
		await Write("a", "staged\n");
		await Git("add", "a");
		await Write("a", "base\n");
		var review = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token);
		review.Files.ShouldBeEmpty();
		review.PullRebaseBlockedReason.ShouldNotBeNull().ShouldContain("local changes");
		await Git("reset", "--mixed", "HEAD");
		await Write("untracked", "new\n");
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token)).PullRebaseBlockedReason.ShouldNotBeNull();
		File.Delete(Path.Combine(Root, "untracked"));
		Directory.CreateDirectory(Path.Combine(Root, ".git", "rebase-merge"));
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token)).PullRebaseBlockedReason.ShouldNotBeNull().ShouldContain("active merge/rebase");
	}
	[Test]
	public async Task Moving_head_during_a_scope_read_is_rejected_instead_of_publishing_mixed_paths()
	{
		await Write("a", "base\n");
		await Commit("base");
		var baseline = (await Git("rev-parse", "HEAD")).Trim();
		await Git("checkout", "-b", "feature");
		await Write("a", "work\n");
		await Commit("work");
		GitWorkingTreeReader moving = new(new MovingReviewRunner(_runner, () => Git("update-ref", "HEAD", baseline)));
		await Should.ThrowAsync<IOException>(() => moving.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, "refs/heads/main", Token));
	}

	[Test]
	public async Task Scope_commit_pages_exclude_the_base_and_keep_their_immutable_target()
	{
		await Write("a", "base\n");
		await Commit("base");
		var baseline = (await Git("rev-parse", "HEAD")).Trim();
		await Write("a", "one\n");
		await Commit("one");
		await Write("a", "two\n");
		await Commit("two");
		var head = (await Git("rev-parse", "HEAD")).Trim();
		(await Reader.ReadScopeCommitsAsync(Root, baseline, head, 0, 1, Token)).ShouldHaveSingleItem().Subject.ShouldBe("two");
		await Git("reset", "--hard", baseline);
		(await Reader.ReadScopeCommitsAsync(Root, baseline, head, 1, 1, Token)).ShouldHaveSingleItem().Subject.ShouldBe("one");
		(await Reader.ReadScopeCommitsAsync(Root, baseline, head, 2, 1, Token)).ShouldBeEmpty();
	}

	private sealed class MovingReviewRunner(IGitCliRunner inner, Func<Task<string>> move) : IGitCliRunner
	{
		private bool _moved;
		public async Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, IProgress<string>? outputLine, CancellationToken cancellationToken)
		{
			if (!_moved && arguments.Contains("--numstat"))
			{
				_moved = true;
				await move();
			}
			return await inner.RunAsync(workingDirectory, arguments, outputLine, cancellationToken);
		}
	}

	[Test]
	public async Task Restored_rename_source_is_not_counted_as_removed_and_the_new_name_is_added()
	{
		await Write("a", "base\n");
		await Commit("base");
		var baseline = (await Git("rev-parse", "HEAD")).Trim();
		await Git("mv", "a", "b");
		await Write("a", "base\n");
		var index = await File.ReadAllBytesAsync(Path.Combine(Root, ".git", "index"), Token);
		var changes = await Reader.ReadLocalChangesAsync(Root, baseline, Token);
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("b");
		changes.AddedLines.ShouldBe(1);
		changes.RemovedLines.ShouldBe(0);
		(await File.ReadAllBytesAsync(Path.Combine(Root, ".git", "index"), Token)).ShouldBe(index);
	}

	[Test]
	public async Task Recreated_base_file_preserves_line_ending_changes_in_net_totals()
	{
		await Write("a", "base\n");
		await Commit("base");
		var baseline = (await Git("rev-parse", "HEAD")).Trim();
		await Git("rm", "--cached", "a");
		await Write("a", "base\r\n");
		var changes = await Reader.ReadLocalChangesAsync(Root, baseline, Token);
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("a");
		changes.AddedLines.ShouldBe(1);
		changes.RemovedLines.ShouldBe(1);
	}

	[Test]
	public async Task Local_scope_keeps_binary_and_oversized_paths_with_bounded_placeholders()
	{
		await Write("a", "base\n");
		await Commit("base");
		await File.WriteAllBytesAsync(Path.Combine(Root, "binary"), [1, 0, 2], Token);
		await Write("large", new string('x', (int)GitWorkingTreeReader.MaxFileBytes + 1));
		var changes = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token);
		changes.Files.Count.ShouldBe(2);
		foreach (var entry in changes.Files)
		{
			(await Reader.ReadLocalDiffAsync(Root, entry, changes.BaseCommit, Token))
				.ShouldBe(entry.Path == "large" ? FileDiff.TooLarge : FileDiff.Binary);
		}
	}

	[Test]
	public async Task Unmerged_without_a_common_ancestor_is_explained_and_not_reported_clean()
	{
		await Write("a", "base\n");
		await Commit("base");
		await Git("checkout", "--orphan", "isolated");
		await Git("rm", "-rf", ".");
		await Write("b", "other root\n");
		await Commit("other root");
		var review = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, "refs/heads/main", Token);
		review.Changes.ShouldBeNull();
		review.UnavailableReason.ShouldNotBeNull().ShouldContain("common ancestor");
	}

	[Test]
	public async Task Unmerged_reads_net_local_contents_from_the_target_merge_base()
	{
		await Write("a", "base\n");
		await Commit("base");
		var start = (await Git("rev-parse", "HEAD")).Trim();
		await Git("checkout", "-b", "feature");
		await Write("a", "committed\n");
		await Commit("work");
		await Write("a", "base\n"); // Cancels committed work in the aggregate.
		await Write("new", "one\ntwo\n");
		await Write(".gitignore", "ignored\n");
		await Write("ignored", "ignore me\n");
		var review = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, "refs/heads/main", Token);
		review.BaseCommit.ShouldBe(start);
		review.HeadCommit.ShouldNotBe(start);
		review.Files.Select(file => file.Path).Order().ShouldBe([".gitignore", "new"]);
		review.AddedLines.ShouldBe(3);
		review.RemovedLines.ShouldBe(0);
		(await Reader.ReadScopeCommitsAsync(Root, start, review.HeadCommit!, 0, 50, Token)).ShouldHaveSingleItem().Subject.ShouldBe("work");
	}

	[Test]
	public async Task Unpushed_uses_tracking_endpoint_and_includes_dirty_files_and_remote_reversions()
	{
		await Write("base", "base\n");
		await Commit("base");
		await Git("checkout", "-b", "tracking");
		await Write("remote", "remote only\n");
		await Commit("remote work");
		var remote = (await Git("rev-parse", "HEAD")).Trim();
		await Git("checkout", "main");
		await Git("branch", "--set-upstream-to=tracking", "main");
		await Write("local", "committed\n");
		await Commit("local work");
		await Write("local", "edited\n");
		await Write("new", "new\n");
		var review = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unpushed, null, Token);
		review.BaseCommit.ShouldBe(remote);
		review.IsFallback.ShouldBeFalse();
		review.AheadCount.ShouldBe(1);
		review.BehindCount.ShouldBe(1);
		review.Files.Select(file => file.Path).Order().ShouldBe(["local", "new", "remote"]);
		review.AddedLines.ShouldBe(2);
		review.RemovedLines.ShouldBe(1);
		var local = review.Files.Single(file => file.Path == "local");
		(await Reader.ReadLocalDiffAsync(Root, local, review.BaseCommit, Token)).Hunks.SelectMany(h => h.Lines)
			.ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "edited");
		(await Reader.ReadScopeCommitsAsync(Root, review.BaseCommit, review.HeadCommit!, 0, 50, Token))
			.ShouldHaveSingleItem().Subject.ShouldBe("local work");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Untracked_recreated_base_paths_are_unique_net_diffs(bool changed)
	{
		await Write("a", "base\n");
		await Commit("base");
		var baseline = (await Git("rev-parse", "HEAD")).Trim();
		await Git("rm", "--cached", "a");
		if (changed)
		{ await Write("a", "changed\n"); }
		var review = await Reader.ReadLocalChangesAsync(Root, baseline, Token);
		review.BaseCommit.ShouldBe(baseline);
		review.HeadCommit.ShouldBe(baseline);
		review.Files.Count.ShouldBe(changed ? 1 : 0);
		review.AddedLines.ShouldBe(changed ? 1 : 0);
		review.RemovedLines.ShouldBe(changed ? 1 : 0);
		if (changed)
		{
			var diff = await Reader.ReadLocalDiffAsync(Root, review.Files[0], baseline, Token);
			diff.Hunks.SelectMany(h => h.Lines).ShouldContain(line => line.Kind == DiffLineKind.Removed && line.Text == "base");
			diff.Hunks.SelectMany(h => h.Lines).ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "changed");
		}
	}

	[Test]
	public async Task Unpushed_without_tracking_uses_target_base_but_a_gone_configured_ref_is_unavailable()
	{
		await Write("base", "base\n");
		await Commit("base");
		var start = (await Git("rev-parse", "HEAD")).Trim();
		await Git("checkout", "-b", "feature");
		await Write("a", "work\n");
		await Commit("work");
		var fallback = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unpushed, "refs/heads/main", Token);
		fallback.IsFallback.ShouldBeTrue();
		fallback.BaseCommit.ShouldBe(start);
		fallback.UnavailableReason.ShouldBeNull();
		await Git("branch", "tracking");
		await Git("branch", "--set-upstream-to=tracking");
		await Git("branch", "-D", "tracking");
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unpushed, "refs/heads/main", Token))
			.UnavailableReason.ShouldNotBeNull();
	}

	[Test]
	public async Task Uncommitted_unborn_staged_and_untracked_contents_use_the_empty_tree()
	{
		await Write("a", "staged\n");
		await Git("add", "a");
		await Write("a", "disk\n");
		await Write("new", "new\n");
		var review = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Uncommitted, null, Token);
		review.BaseCommit.ShouldBeNull();
		review.HeadCommit.ShouldBeNull();
		review.UnavailableReason.ShouldBeNull();
		review.Files.Select(file => file.Path).Order().ShouldBe(["a", "new"]);
		review.AddedLines.ShouldBe(2);
		(await Reader.ReadLocalDiffAsync(Root, review.Files.Single(file => file.Path == "a"), null, Token))
			.Hunks.SelectMany(h => h.Lines).ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "disk");
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, null, Token)).UnavailableReason.ShouldNotBeNull();
	}

	[Test]
	public async Task Automatic_target_uses_remote_default_and_manual_target_can_differ()
	{
		await Write("base", "base\n");
		await Commit("base");
		var start = (await Git("rev-parse", "HEAD")).Trim();
		await Git("update-ref", "refs/remotes/origin/main", start);
		await Git("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
		await Git("checkout", "-b", "feature");
		await Write("a", "work\n");
		await Commit("work");
		var auto = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, null, Token);
		auto.BaseRef.ShouldBe("refs/remotes/origin/main");
		auto.BaseCommit.ShouldBe(start);
		(await Reader.ReadBranchesAsync(Root, Token)).ShouldContain("refs/heads/feature");
		var self = await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, "refs/heads/feature", Token);
		self.Files.ShouldBeEmpty();
		await Git("checkout", "--detach");
		(await Reader.ReadReviewChangesAsync(Root, GitReviewScope.Unmerged, null, Token)).UnavailableReason.ShouldNotBeNull();
	}
}