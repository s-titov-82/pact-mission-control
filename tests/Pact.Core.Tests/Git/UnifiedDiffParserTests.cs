using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class UnifiedDiffParserTests
{
	[Test]
	public void Lines_are_numbered_from_the_hunk_header()
	{
		var diff = UnifiedDiffParser.Parse(
			"diff --git a/x.cs b/x.cs\nindex 1..2 100644\n--- a/x.cs\n+++ b/x.cs\n" +
			"@@ -10,3 +10,3 @@ class X\n a\n-b\n+B\n c\n");

		diff.Placeholder.ShouldBeNull();
		var hunk = diff.Hunks.ShouldHaveSingleItem();
		hunk.Header.ShouldBe("class X");
		hunk.Lines.Select(line => (line.Kind, line.OldNumber, line.NewNumber, line.Text)).ShouldBe(
		[
			(DiffLineKind.Context, 10, 10, "a"),
			(DiffLineKind.Removed, 11, null, "b"),
			(DiffLineKind.Added, null, 11, "B"),
			(DiffLineKind.Context, 12, 12, "c")
		]);
	}

	[Test]
	public void A_hunk_header_without_counts_means_one_line()
	{
		var hunk = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n+b\n").Hunks.ShouldHaveSingleItem();

		hunk.OldCount.ShouldBe(1);
		hunk.NewCount.ShouldBe(1);
		hunk.Header.ShouldBe(string.Empty);
	}

	[Test]
	public void Binary_output_becomes_the_binary_placeholder()
	{
		var diff = UnifiedDiffParser.Parse(
			"diff --git a/p.png b/p.png\nindex 1..2 100644\nBinary files a/p.png and b/p.png differ\n");

		diff.Placeholder.ShouldBe("Binary file, not shown");
		diff.Hunks.ShouldBeEmpty();
	}

	[Test]
	public void The_no_newline_marker_is_not_a_line()
	{
		var hunk = UnifiedDiffParser.Parse("@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+a\n")
			.Hunks.ShouldHaveSingleItem();

		hunk.Lines.Count.ShouldBe(2);
	}

	[Test]
	public void Crlf_output_keeps_no_carriage_returns()
	{
		var hunk = UnifiedDiffParser.Parse("@@ -1 +1 @@\r\n-a\r\n+b\r\n").Hunks.ShouldHaveSingleItem();

		hunk.Lines.Select(line => line.Text).ShouldBe(["a", "b"]);
	}

	[Test]
	public void Several_hunks_are_kept_in_order()
	{
		var diff = UnifiedDiffParser.Parse("@@ -1,2 +1,2 @@\n-a\n+A\n b\n@@ -20,1 +20,2 @@\n x\n+y\n");

		diff.Hunks.Count.ShouldBe(2);
		diff.Hunks[1].Lines.Select(line => (line.OldNumber, line.NewNumber)).ShouldBe([(20, 20), (null, 21)]);
		diff.LineCount.ShouldBe(5);
	}

	[Test]
	public void Empty_output_has_no_hunks_and_no_placeholder()
	{
		var diff = UnifiedDiffParser.Parse(string.Empty);

		diff.Placeholder.ShouldBeNull();
		diff.Hunks.ShouldBeEmpty();
	}

	[Test]
	public void All_added_numbers_every_line_from_one()
	{
		var hunk = FileDiff.AllAdded("x\ny\n").Hunks.ShouldHaveSingleItem();

		hunk.Lines.Select(line => (line.Kind, line.OldNumber, line.NewNumber, line.Text)).ShouldBe(
		[
			(DiffLineKind.Added, null, 1, "x"),
			(DiffLineKind.Added, null, 2, "y")
		]);
		FileDiff.AllAdded(string.Empty).Hunks.ShouldBeEmpty();
	}

	[Test]
	public void Placeholders_carry_the_ui_copy()
	{
		FileDiff.Binary.Placeholder.ShouldBe("Binary file, not shown");
		FileDiff.TooLarge.Placeholder.ShouldBe("File is too large to show");
	}
}
