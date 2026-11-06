using Pact.Core.Git;

namespace Pact.Infrastructure.Tests.Git;

[NonParallelizable]
public sealed class GitCommitReaderTests
{
	private readonly GitCliRunner _runner = new();
	private TemporaryDirectory _folder = null!;
	private string Root => _folder.Path;
	private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;
	private GitWorkingTreeReader Reader => new(_runner);

	[SetUp]
	public async Task SetUp()
	{
		_folder = TemporaryDirectory.Create();
		await Git("init", "--initial-branch=main");
		await Git("config", "user.name", "PACT Test");
		await Git("config", "user.email", "pact@example.test");
		await Git("config", "commit.gpgsign", "false");
		await Git("config", "core.autocrlf", "false");
	}
	[TearDown]
	public void TearDown() => _folder.Dispose();

	[Test]
	public async Task Branch_comparison_excludes_dirty_files_and_keeps_resolved_endpoints_after_upstream_moves()
	{
		await Write("a.txt", "base\n");
		await Commit("base");
		await Git("branch", "tracking");
		await Git("branch", "--set-upstream-to=tracking", "main");
		(await Reader.ReadBranchChangesAsync(Root, Token)).Comparison.ShouldNotBeNull().Files.ShouldBeEmpty();
		await Write("a.txt", "committed\n");
		await Commit("local");
		await Write("a.txt", "dirty\n");
		await Write("untracked", "not committed");
		var branch = await Reader.ReadBranchChangesAsync(Root, Token);
		branch.Upstream.ShouldBe("tracking");
		var comparison = branch.Comparison.ShouldNotBeNull();
		comparison.Files.ShouldHaveSingleItem().Path.ShouldBe("a.txt");
		comparison.AddedLines.ShouldBe(1);
		(await Reader.ReadCommitTextAsync(Root, "a.txt", comparison.TargetCommit, Token)).ShouldBe("committed\n");
		await Git("branch", "-f", "tracking", "HEAD");
		(await Reader.ReadBranchChangesAsync(Root, Token)).Comparison.ShouldNotBeNull().Files.ShouldBeEmpty();
		var frozen = await Reader.ReadComparisonDiffAsync(Root, comparison.Files[0], comparison.BaseCommit, comparison.TargetCommit, Token);
		frozen.Hunks.SelectMany(hunk => hunk.Lines).ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "committed");
	}

	[Test]
	public async Task Branch_comparison_reports_unborn_missing_gone_upstream_and_detached_states()
	{
		(await Reader.ReadBranchChangesAsync(Root, Token)).UnavailableReason.ShouldNotBeNull().ShouldContain("unborn");
		await Write("a", "base");
		await Commit("base");
		(await Reader.ReadBranchChangesAsync(Root, Token)).UnavailableReason.ShouldNotBeNull().ShouldContain("upstream");
		await Git("branch", "tracking");
		await Git("branch", "--set-upstream-to=tracking", "main");
		await Git("branch", "-D", "tracking");
		(await Reader.ReadBranchChangesAsync(Root, Token)).Comparison.ShouldBeNull();
		await Git("checkout", "--detach");
		(await Reader.ReadBranchChangesAsync(Root, Token)).UnavailableReason.ShouldNotBeNull().ShouldContain("Detached");
	}

	[Test]
	public async Task Diverged_branch_compares_upstream_endpoint_not_merge_base()
	{
		await Write("base", "base");
		await Commit("base");
		await Git("checkout", "-b", "tracking");
		await Write("upstream-only", "upstream");
		await Commit("upstream");
		await Git("checkout", "main");
		await Git("branch", "--set-upstream-to=tracking", "main");
		await Write("local-only", "local");
		await Commit("local");
		var comparison = (await Reader.ReadBranchChangesAsync(Root, Token)).Comparison.ShouldNotBeNull();
		comparison.Files.ShouldContain(file => file.Path == "upstream-only" && file.Kind == GitChangeKind.Deleted);
		comparison.Files.ShouldContain(file => file.Path == "local-only" && file.Kind == GitChangeKind.Added);
	}

	[Test]
	public async Task Historical_file_selection_treats_git_metacharacters_literally()
	{
		await Write("a[1].txt", "literal-file\n");
		await Write("a1.txt", "other-file\n");
		await Commit("root");
		var commit = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		var changes = await Reader.ReadCommitChangesAsync(Root, commit, Token);
		var file = changes.Files.Single(file => file.Path == "a[1].txt");
		var diff = await Reader.ReadCommitDiffAsync(Root, file, commit, Token);
		var added = diff.Hunks.SelectMany(hunk => hunk.Lines).Where(line => line.Kind == DiffLineKind.Added).Select(line => line.Text).ToArray();
		added.ShouldBe(["literal-file"]);
	}

	[Test]
	public async Task Ordinary_commit_reads_modified_and_deleted_files_from_its_own_revision()
	{
		await Write("a.txt", "old\nkeep\n");
		await Write("gone.txt", "removed\n");
		await Commit("base");
		await Write("a.txt", "new\nkeep\n");
		File.Delete(Path.Combine(Root, "gone.txt"));
		await Commit("change and delete");
		var commit = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		await Write("a.txt", "uncommitted\n");
		var changes = await Reader.ReadCommitChangesAsync(Root, commit, Token);
		var modified = changes.Files.Single(file => file.Path == "a.txt");
		var removed = changes.Files.Single(file => file.Path == "gone.txt");
		removed.Kind.ShouldBe(GitChangeKind.Deleted);
		var diff = await Reader.ReadCommitDiffAsync(Root, modified, commit, Token);
		diff.Hunks.SelectMany(hunk => hunk.Lines).ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "new");
		diff.Hunks.SelectMany(hunk => hunk.Lines).ShouldContain(line => line.Kind == DiffLineKind.Removed && line.Text == "old");
		(await Reader.ReadCommitDiffAsync(Root, removed, commit, Token)).Hunks
			.SelectMany(hunk => hunk.Lines).ShouldContain(line => line.Kind == DiffLineKind.Removed && line.Text == "removed");
		(await Reader.ReadCommitTextAsync(Root, "a.txt", commit.Id, Token)).ShouldBe("new\nkeep\n");
		(await Reader.ReadCommitTextAsync(Root, "gone.txt", commit.Id, Token)).ShouldBeNull();
	}

	[Test]
	public async Task Unborn_history_is_empty()
	{
		(await Reader.ReadCommitsAsync(Root, 0, 50, Token)).ShouldBeEmpty();
		(await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldBeNull();
	}

	[Test]
	public async Task Nul_path_records_preserve_newline_and_rename_names()
	{
		GitWorkingTreeReader reader = new(new NulRunner());
		GitCommitInfo commit = new(new string('a', 64), [], "author", DateTimeOffset.MinValue, "subject");
		var changes = await reader.ReadCommitChangesAsync(Root, commit, Token);
		var entry = changes.Files.ShouldHaveSingleItem();
		entry.OriginalPath.ShouldBe("old\nname");
		entry.Path.ShouldBe("new\nname");
	}

	[Test]
	public async Task Runner_preserves_raw_blob_line_endings_and_missing_final_newline()
	{
		await Write("a.txt", "one\r\ntwo");
		await Commit("root");
		(await Git("show", "HEAD:a.txt")).ShouldBe("one\r\ntwo");
	}

	[Test]
	public async Task Root_commit_reads_added_files_and_immutable_text()
	{
		await Write("папка/план работ.md", "# История\ntext");
		await Commit("root subject");
		var commit = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		commit.Parents.ShouldBeEmpty();
		commit.Subject.ShouldBe("root subject");
		commit.Author.ShouldBe("PACT Test");
		await Write("папка/план работ.md", "different working text");
		var changes = await Reader.ReadCommitChangesAsync(Root, commit, Token);
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("папка/план работ.md");
		changes.AddedLines.ShouldBe(2);
		changes.RemovedLines.ShouldBe(0);
		var diff = await Reader.ReadCommitDiffAsync(Root, changes.Files[0], commit, Token);
		diff.Hunks.SelectMany(hunk => hunk.Lines).ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "# История");
		(await Reader.ReadCommitTextAsync(Root, changes.Files[0].Path, commit.Id, Token)).ShouldBe("# История\ntext");
		File.Delete(Path.Combine(Root, "папка", "план работ.md"));
		(await Reader.ReadCommitTextAsync(Root, changes.Files[0].Path, commit.Id, Token)).ShouldBe("# История\ntext");
	}

	[Test]
	public async Task Merge_uses_first_parent_and_preserves_renamed_paths()
	{
		await Write("old name.txt", "one\ntwo\nthree\n");
		await Commit("root");
		await Git("checkout", "-b", "side");
		await Git("mv", "old name.txt", "new name.txt");
		await Commit("rename");
		await Git("checkout", "main");
		await Write("main.txt", "main\n");
		await Commit("main");
		var parent = (await Git("rev-parse", "HEAD")).Trim();
		await Git("merge", "--no-ff", "side", "-m", "merge");
		var commit = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		commit.IsMerge.ShouldBeTrue();
		commit.BaseCommit.ShouldBe(parent);
		var changes = await Reader.ReadCommitChangesAsync(Root, commit, Token);
		var rename = changes.Files.ShouldHaveSingleItem();
		rename.Path.ShouldBe("new name.txt");
		rename.OriginalPath.ShouldBe("old name.txt");
		(await Reader.ReadCommitDiffAsync(Root, rename, commit, Token)).Placeholder.ShouldBeNull();
	}

	[Test]
	public async Task History_pages_and_resolved_refs_keep_full_commit_identity()
	{
		await Write("a", "one");
		await Commit("first");
		var first = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		await Write("a", "two");
		await Commit("second");
		var page = await Reader.ReadCommitsAsync(Root, 0, 1, Token);
		page.ShouldHaveSingleItem().Subject.ShouldBe("second");
		(await Reader.ReadCommitsAsync(Root, 1, 1, Token)).ShouldHaveSingleItem().Id.ShouldBe(first.Id);
		(await Reader.ReadCommitTextAsync(Root, "a", first.Id, Token)).ShouldBe("one");
		(await Reader.ResolveCommitAsync(Root, "--output=bad", Token)).ShouldBeNull();
		await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Reader.ReadCommitsAsync(Root, 0, 51, Token));
	}

	[Test]
	public async Task Commit_blobs_are_bounded_and_binary_files_have_placeholders()
	{
		await File.WriteAllBytesAsync(Path.Combine(Root, "binary"), [1, 0, 2], Token);
		await Write("large", new string('x', (int)GitWorkingTreeReader.MaxFileBytes + 1));
		await Commit("large and binary");
		var commit = (await Reader.ResolveCommitAsync(Root, "HEAD", Token)).ShouldNotBeNull();
		var changes = await Reader.ReadCommitChangesAsync(Root, commit, Token);
		foreach (var entry in changes.Files)
		{
			(await Reader.ReadCommitTextAsync(Root, entry.Path, commit.Id, Token)).ShouldBeNull();
			(await Reader.ReadCommitDiffAsync(Root, entry, commit, Token)).ShouldBe(entry.Path == "large" ? FileDiff.TooLarge : FileDiff.Binary);
		}
	}

	private async Task Write(string path, string text)
	{
		var full = Path.Combine(Root, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		await File.WriteAllTextAsync(full, text, Token);
	}
	private async Task Commit(string subject)
	{
		await Git("add", "-A");
		await Git("commit", "-m", subject);
	}
	private async Task<string> Git(params string[] args)
	{
		var result = await _runner.RunAsync(Root, args, null, Token);
		result.Succeeded.ShouldBeTrue(result.StandardError);
		return result.StandardOutput;
	}

	private sealed class NulRunner : IGitCliRunner
	{
		public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
			IProgress<string>? outputLine, CancellationToken cancellationToken) => Task.FromResult(new GitCommandResult(0,
			arguments.Contains("--name-status") ? "R100\0old\nname\0new\nname\0" : "0\t0\trenamed\n", string.Empty));
	}
}