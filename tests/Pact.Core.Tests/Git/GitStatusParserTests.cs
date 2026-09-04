using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class GitStatusParserTests
{
	[Test]
	public void Parse_reads_clean_branch_with_upstream()
	{
		var output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head master
            # branch.upstream origin/master
            # branch.ab +0 -0
            """;

		var snapshot = GitStatusParser.Parse(output);

		snapshot.Branch.ShouldBe("master");
		snapshot.Upstream.ShouldBe("origin/master");
		snapshot.Ahead.ShouldBe(0);
		snapshot.Behind.ShouldBe(0);
		snapshot.IsDetached.ShouldBeFalse();
		snapshot.IsDirty.ShouldBeFalse();
		snapshot.HasConflicts.ShouldBeFalse();
		snapshot.Files.ShouldBeEmpty();
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_reads_ahead_and_behind_counts()
	{
		var output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head feature/x
            # branch.upstream origin/feature/x
            # branch.ab +2 -1
            """;

		var snapshot = GitStatusParser.Parse(output);

		snapshot.Ahead.ShouldBe(2);
		snapshot.Behind.ShouldBe(1);
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_defaults_missing_upstream_and_ahead_behind()
	{
		var output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head local-only
            """;

		var snapshot = GitStatusParser.Parse(output);

		snapshot.Branch.ShouldBe("local-only");
		snapshot.Upstream.ShouldBeNull();
		snapshot.Ahead.ShouldBe(0);
		snapshot.Behind.ShouldBe(0);
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_marks_detached_head()
	{
		var output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head (detached)
            """;

		var snapshot = GitStatusParser.Parse(output);

		snapshot.Branch.ShouldBe("(detached)");
		snapshot.IsDetached.ShouldBeTrue();
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_classifies_mixed_entries_and_counts_each_file_once()
	{
		var output = """
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head feature/x
            # branch.upstream origin/feature/x
            # branch.ab +2 -1
            1 A. N... 000000 100644 100644 0000000000000000000000000000000000000000 2222222222222222222222222222222222222222 added.txt
            1 .M N... 100644 100644 100644 3333333333333333333333333333333333333333 3333333333333333333333333333333333333333 modified.txt
            1 MM N... 100644 100644 100644 4444444444444444444444444444444444444444 5555555555555555555555555555555555555555 both.txt
            1 .D N... 100644 100644 000000 6666666666666666666666666666666666666666 6666666666666666666666666666666666666666 deleted.txt
            2 R. N... 100644 100644 100644 7777777777777777777777777777777777777777 8888888888888888888888888888888888888888 R100 new-name.txt	old-name.txt
            ? untracked.txt
            u UU N... 100644 100644 100644 100644 9999999999999999999999999999999999999999 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb conflict.txt
            """;

		var snapshot = GitStatusParser.Parse(output);

		snapshot.Branch.ShouldBe("feature/x");
		snapshot.Upstream.ShouldBe("origin/feature/x");
		snapshot.Ahead.ShouldBe(2);
		snapshot.Behind.ShouldBe(1);
		snapshot.Added.ShouldBe(1);
		snapshot.Modified.ShouldBe(3);
		snapshot.Deleted.ShouldBe(1);
		snapshot.Untracked.ShouldBe(1);
		snapshot.Conflicted.ShouldBe(1);
		snapshot.IsDirty.ShouldBeTrue();
		snapshot.HasConflicts.ShouldBeTrue();
		snapshot.Files.Count.ShouldBe(7);
		AssertFile(snapshot.Files[0], "added.txt", null, GitChangeKind.Added);
		AssertFile(snapshot.Files[1], "modified.txt", null, GitChangeKind.Modified);
		AssertFile(snapshot.Files[2], "both.txt", null, GitChangeKind.Modified);
		AssertFile(snapshot.Files[3], "deleted.txt", null, GitChangeKind.Deleted);
		AssertFile(snapshot.Files[4], "new-name.txt", "old-name.txt", GitChangeKind.Modified);
		AssertFile(snapshot.Files[5], "untracked.txt", null, GitChangeKind.Untracked);
		AssertFile(snapshot.Files[6], "conflict.txt", null, GitChangeKind.Conflicted);
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_reads_real_unmerged_porcelain_v2_entry()
	{
		var output = """
            # branch.head feature
            u UU N... 100644 100644 100644 100644 9999999999999999999999999999999999999999 aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb conflict.txt
            """;

		var snapshot = GitStatusParser.Parse(output);

		var entry = snapshot.Files.ShouldHaveSingleItem();
		AssertFile(entry, "conflict.txt", null, GitChangeKind.Conflicted);
		snapshot.HasConflicts.ShouldBeTrue();
		snapshot.Conflicted.ShouldBe(1);
		AssertCountersMatchFiles(snapshot);
	}

	[Test]
	public void Parse_treats_typechange_as_modified_and_ignores_unknown_or_blank_lines()
	{
		var output = """
            # branch.head master

            ! ignored
            1 T. N... 100644 120000 120000 dddddddddddddddddddddddddddddddddddddddd eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee link.txt
            """;

		var snapshot = GitStatusParser.Parse(output);

		var entry = snapshot.Files.ShouldHaveSingleItem();
		AssertFile(entry, "link.txt", null, GitChangeKind.Modified);
		snapshot.Modified.ShouldBe(1);
		AssertCountersMatchFiles(snapshot);
	}

	private static void AssertFile(GitFileEntry entry, string path, string? originalPath, GitChangeKind kind)
	{
		entry.Path.ShouldBe(path);
		entry.OriginalPath.ShouldBe(originalPath);
		entry.Kind.ShouldBe(kind);
	}

	private static void AssertCountersMatchFiles(GitStatusSnapshot snapshot)
	{
		snapshot.Added.ShouldBe(snapshot.Files.Count(file => file.Kind == GitChangeKind.Added));
		snapshot.Modified.ShouldBe(snapshot.Files.Count(file => file.Kind == GitChangeKind.Modified));
		snapshot.Deleted.ShouldBe(snapshot.Files.Count(file => file.Kind == GitChangeKind.Deleted));
		snapshot.Untracked.ShouldBe(snapshot.Files.Count(file => file.Kind == GitChangeKind.Untracked));
		snapshot.Conflicted.ShouldBe(snapshot.Files.Count(file => file.Kind == GitChangeKind.Conflicted));
	}
}