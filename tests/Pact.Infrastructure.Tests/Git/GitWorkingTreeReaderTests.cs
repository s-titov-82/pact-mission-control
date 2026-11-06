using Pact.Core.Git;

namespace Pact.Infrastructure.Tests.Git;

[NonParallelizable]
public sealed class GitWorkingTreeReaderTests
{
	private readonly GitCliRunner _runner = new();
	private TemporaryDirectory _root = null!;

	private static CancellationToken Token => TestContext.CurrentContext.CancellationToken;

	private string Root => _root.Path;

	[SetUp]
	public void SetUp() => _root = TemporaryDirectory.Create();

	[TearDown]
	public void TearDown() => _root.Dispose();

	[Test]
	public async Task Modified_staged_and_untracked_files_are_read_with_line_totals()
	{
		await InitAsync();
		await WriteAsync("a.txt", "1\n2\n");
		await CommitAllAsync("initial");
		await WriteAsync("a.txt", "1\nX\n");
		await WriteAsync("b.txt", "b\n");
		await GitAsync("add", "b.txt");
		await WriteAsync("c.txt", "c\n");

		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		changes.Branch.ShouldBe("pact-test");
		changes.HeadCommit.ShouldBe(await HeadAsync());
		changes.Files.Single(file => file.Path == "a.txt").ShouldSatisfyAllConditions(
			file => file.Kind.ShouldBe(GitChangeKind.Modified),
			file => file.IsStaged.ShouldBeFalse());
		changes.Files.Single(file => file.Path == "b.txt").ShouldSatisfyAllConditions(
			file => file.Kind.ShouldBe(GitChangeKind.Added),
			file => file.IsStaged.ShouldBeTrue());
		changes.Files.Single(file => file.Path == "c.txt").Kind.ShouldBe(GitChangeKind.Untracked);
		changes.AddedLines.ShouldBe(2);
		changes.RemovedLines.ShouldBe(1);
	}

	[Test]
	public async Task Files_inside_an_untracked_folder_are_listed_one_by_one()
	{
		await InitAsync();
		await WriteAsync("keep.txt", "k\n");
		await CommitAllAsync("initial");
		await WriteAsync("n/x.txt", "x\n");
		await WriteAsync("n/y.txt", "y\n");

		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		changes.Files.Select(file => file.Path).ShouldBe(["n/x.txt", "n/y.txt"], ignoreOrder: true);
	}

	[Test]
	public async Task Cyrillic_and_spaced_paths_come_back_verbatim()
	{
		await InitAsync();
		await WriteAsync("папка/файл.txt", "один\nдва\n");
		await CommitAllAsync("initial");
		await WriteAsync("папка/файл.txt", "один\nтри\n");
		await WriteAsync("док/план работ.md", "# План\n");

		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		var untracked = changes.Files.Single(file => file.Path == "док/план работ.md");
		var tracked = changes.Files.Single(file => file.Path == "папка/файл.txt");
		var untrackedDiff = await Reader.ReadDiffAsync(Root, untracked, changes.HeadCommit, Token);
		untrackedDiff.Hunks.ShouldHaveSingleItem().Lines.ShouldHaveSingleItem().Text.ShouldBe("# План");
		var trackedDiff = await Reader.ReadDiffAsync(Root, tracked, changes.HeadCommit, Token);
		trackedDiff.Hunks.ShouldNotBeEmpty();
		trackedDiff.Hunks[0].Lines.ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "три");
	}

	[Test]
	public async Task A_soft_reset_turns_a_clean_tree_into_staged_changes()
	{
		await InitAsync();
		await WriteAsync("a.txt", "1\n");
		await CommitAllAsync("first");
		var first = await HeadAsync();
		await WriteAsync("a.txt", "2\n");
		await CommitAllAsync("second");
		(await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull().Files.ShouldBeEmpty();

		await GitAsync("reset", "--soft", "HEAD~1");

		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();
		changes.HeadCommit.ShouldBe(first);
		changes.Files.ShouldHaveSingleItem().ShouldSatisfyAllConditions(
			file => file.Path.ShouldBe("a.txt"),
			file => file.IsStaged.ShouldBeTrue());
	}

	[Test]
	public async Task A_repository_without_commits_is_compared_with_the_empty_tree()
	{
		await InitAsync();
		await WriteAsync("a.txt", "x\ny\n");
		await GitAsync("add", "a.txt");

		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		changes.HeadCommit.ShouldBeNull();
		var entry = changes.Files.ShouldHaveSingleItem();
		entry.Kind.ShouldBe(GitChangeKind.Added);
		changes.AddedLines.ShouldBe(2);
		var diff = await Reader.ReadDiffAsync(Root, entry, changes.HeadCommit, Token);
		diff.Hunks.ShouldHaveSingleItem().Lines.Select(line => (line.Kind, line.Text))
			.ShouldBe([(DiffLineKind.Added, "x"), (DiffLineKind.Added, "y")]);
	}

	[Test]
	public async Task A_modified_file_diff_is_parsed_into_hunks()
	{
		await InitAsync();
		await WriteAsync("a.txt", "1\n2\n3\n");
		await CommitAllAsync("initial");
		await WriteAsync("a.txt", "1\nTWO\n3\n");
		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		var diff = await Reader.ReadDiffAsync(Root, changes.Files.Single(), changes.HeadCommit, Token);

		var lines = diff.Hunks.ShouldHaveSingleItem().Lines;
		lines.ShouldContain(line => line.Kind == DiffLineKind.Removed && line.Text == "2");
		lines.ShouldContain(line => line.Kind == DiffLineKind.Added && line.Text == "TWO");
	}

	[Test]
	public async Task A_renamed_file_is_diffed_against_its_old_path()
	{
		await InitAsync();
		await WriteAsync("old.txt", "a\nb\nc\nd\ne\nf\n");
		await CommitAllAsync("initial");
		await GitAsync("mv", "old.txt", "new.txt");
		await WriteAsync("new.txt", "a\nb\nc\nd\ne\nF\n");
		await GitAsync("add", "new.txt");
		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		var entry = changes.Files.ShouldHaveSingleItem();
		entry.Kind.ShouldBe(GitChangeKind.Modified);
		entry.Path.ShouldBe("new.txt");
		entry.OriginalPath.ShouldBe("old.txt");
		var diff = await Reader.ReadDiffAsync(Root, entry, changes.HeadCommit, Token);
		diff.Hunks.SelectMany(hunk => hunk.Lines).Where(line => line.Kind != DiffLineKind.Context)
			.Select(line => (line.Kind, line.Text))
			.ShouldBe([(DiffLineKind.Removed, "f"), (DiffLineKind.Added, "F")]);
	}

	[Test]
	public async Task Untracked_binary_and_oversized_files_get_placeholders()
	{
		await InitAsync();
		await File.WriteAllBytesAsync(Path.Combine(Root, "bin.dat"), [1, 0, 2], Token);
		await File.WriteAllBytesAsync(
			Path.Combine(Root, "big.txt"),
			Enumerable.Repeat((byte)'a', (int)GitWorkingTreeReader.MaxFileBytes + 1).ToArray(),
			Token);
		var changes = (await Reader.ReadChangesAsync(Root, Token)).ShouldNotBeNull();

		(await Reader.ReadDiffAsync(Root, changes.Files.Single(file => file.Path == "bin.dat"), null, Token))
			.ShouldBe(FileDiff.Binary);
		(await Reader.ReadDiffAsync(Root, changes.Files.Single(file => file.Path == "big.txt"), null, Token))
			.ShouldBe(FileDiff.TooLarge);
	}

	[Test]
	public async Task Working_text_is_read_and_binary_or_missing_gives_null()
	{
		await WriteAsync("src/кириллица.cs", "один\r\nдва\n");
		await File.WriteAllBytesAsync(Path.Combine(Root, "blob.bin"), [1, 0, 2], Token);

		(await Reader.ReadWorkingTextAsync(Root, "src/кириллица.cs", Token)).ShouldBe("один\r\nдва\n");
		(await Reader.ReadWorkingTextAsync(Root, "blob.bin", Token)).ShouldBeNull();
		(await Reader.ReadWorkingTextAsync(Root, "missing.txt", Token)).ShouldBeNull();
	}

	[Test]
	public async Task A_folder_that_is_not_a_repository_reads_as_null()
	{
		(await Reader.ReadChangesAsync(Root, Token)).ShouldBeNull();
	}

	private GitWorkingTreeReader Reader => new(_runner);

	[Test]
	public async Task Summary_counts_unique_paths_including_staged_unstaged_and_nested_untracked()
	{
		await InitAsync();
		await WriteAsync("a.txt", "one\n");
		await CommitAllAsync("initial");
		(await Reader.ReadChangedFileCountAsync(Root, Token)).ShouldBe(0);
		await WriteAsync("a.txt", "two\n");
		await GitAsync("add", "a.txt");
		await WriteAsync("a.txt", "three\n");
		await WriteAsync("nested/b.txt", "b\n");
		await WriteAsync("nested/c.txt", "c\n");
		(await Reader.ReadChangedFileCountAsync(Root, Token)).ShouldBe(3);
	}

	[Test]
	public async Task Summary_supports_unborn_head_and_counts_rename_once()
	{
		await InitAsync();
		await WriteAsync("a.txt", "one\n");
		await GitAsync("add", "a.txt");
		(await Reader.ReadChangedFileCountAsync(Root, Token)).ShouldBe(1);
		await CommitAllAsync("initial");
		await GitAsync("mv", "a.txt", "renamed.txt");
		(await Reader.ReadChangedFileCountAsync(Root, Token)).ShouldBe(1);
	}

	[Test]
	public async Task Summary_is_unknown_outside_a_repository() =>
		(await Reader.ReadChangedFileCountAsync(Root, Token)).ShouldBeNull();

	private Task InitAsync() => GitAsync("init", "--initial-branch=pact-test");

	private Task CommitAllAsync(string message) => CommitAllCoreAsync(message);

	private async Task CommitAllCoreAsync(string message)
	{
		await GitAsync("add", "-A");
		await GitAsync(
			"-c", "user.name=PACT Test",
			"-c", "user.email=pact@example.com",
			"-c", "commit.gpgsign=false",
			"commit", "-m", message);
	}

	private async Task<string> HeadAsync()
	{
		var result = await _runner.RunAsync(Root, ["rev-parse", "HEAD"], outputLine: null, Token);
		return result.StandardOutput.Trim();
	}

	private async Task WriteAsync(string relativePath, string text)
	{
		var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		await File.WriteAllTextAsync(path, text, Token);
	}

	private async Task GitAsync(params string[] arguments)
	{
		var result = await _runner.RunAsync(Root, arguments, outputLine: null, Token);
		result.Succeeded.ShouldBeTrue($"git {string.Join(' ', arguments)} failed: {result.StandardError}");
	}
}
