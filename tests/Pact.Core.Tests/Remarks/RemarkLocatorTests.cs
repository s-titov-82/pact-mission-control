using Pact.Core.Remarks;

namespace Pact.Core.Tests.Remarks;

public sealed class RemarkLocatorTests
{
	[Test]
	public void An_unchanged_line_follows_lines_inserted_above() =>
		RemarkLocator.LocateNew(Anchor(2, "b", ["a"], ["c"]), ["a", "b", "c"], ["x", "a", "b", "c"]).ShouldBe(3);

	[Test]
	public void A_changed_line_is_outdated() =>
		RemarkLocator.LocateNew(Anchor(2, "b", ["a"], ["c"]), ["a", "b", "c"], ["a", "B", "c"]).ShouldBeNull();

	[Test]
	public void A_deleted_line_is_outdated() =>
		RemarkLocator.LocateNew(Anchor(2, "b", ["a"], ["c"]), ["a", "b", "c"], ["a", "c"]).ShouldBeNull();

	// The spec's case: the surviving copy now holds the anchor's line number and is the only match
	// of its text and context, yet a line diff cannot tell which copy went.
	[Test]
	public void Deleting_the_first_of_two_identical_blocks_outdates_a_remark_on_it()
	{
		string[] captured = ["x", "p", "q", "r", "p", "q", "r", "y"];

		RemarkLocator.LocateNew(Anchor(3, "q", ["p"], ["r"]), captured, ["x", "p", "q", "r", "y"]).ShouldBeNull();
	}

	[Test]
	public void Deleting_an_unrelated_line_keeps_a_remark_in_a_repeated_block()
	{
		string[] captured = ["x", "p", "q", "r", "p", "q", "r", "y"];

		RemarkLocator.LocateNew(Anchor(3, "q", ["p"], ["r"]), captured, ["p", "q", "r", "p", "q", "r", "y"]).ShouldBe(2);
	}

	[Test]
	public void Crlf_and_lf_versions_of_the_same_text_keep_the_anchor()
	{
		var crlf = RemarkLocator.SplitLines("a\r\nb\r\n");
		var lf = RemarkLocator.SplitLines("a\nb\n");

		crlf.ShouldBe(["a", "b"]);
		RemarkLocator.LocateNew(Anchor(2, "b", ["a"], []), crlf, lf).ShouldBe(2);
	}

	[Test]
	public void An_edit_beyond_the_budget_outdates_instead_of_scanning()
	{
		string[] before = ["a", "b", "c", "d"];
		string[] after = ["A", "B", "C", "d"];

		LineDiff.MapUnchanged(before, after, maxEdits: 2).ShouldBeNull();
		LineDiff.MapUnchanged(before, after, maxEdits: 6)!.ShouldBe([null, null, null, 3]);
	}

	[Test]
	public void The_map_keeps_a_longest_common_subsequence_of_equal_lines()
	{
		Random random = new(20261027);
		for (var round = 0; round < 300; round++)
		{
			var before = RandomLines(random);
			var after = RandomLines(random);

			var map = LineDiff.MapUnchanged(before, after)!;

			var previous = -1;
			for (var index = 0; index < before.Length; index++)
			{
				if (map[index] is { } target)
				{
					after[target].ShouldBe(before[index]);
					target.ShouldBeGreaterThan(previous);
					previous = target;
				}
			}

			map.Count(target => target is not null).ShouldBe(LongestCommonSubsequence(before, after));
		}
	}

	[Test]
	public void An_old_side_remark_is_valid_only_on_its_commit()
	{
		RemarkLocator.IsOldValid(Old("abc"), "abc").ShouldBeTrue();
		RemarkLocator.IsOldValid(Old("abc"), "def").ShouldBeFalse();
		RemarkLocator.IsOldValid(Old(null), null).ShouldBeTrue();
		RemarkLocator.IsOldValid(Old(null), "abc").ShouldBeFalse();
	}

	private static RemarkAnchor Anchor(int line, string text, string[] before, string[] after) =>
		new("src/X.cs", RemarkSide.New, line, text, before, after, "abc", "snap");

	private static string[] RandomLines(Random random) =>
		Enumerable.Range(0, random.Next(0, 12)).Select(_ => ((char)('a' + random.Next(0, 3))).ToString()).ToArray();

	private static int LongestCommonSubsequence(string[] a, string[] b)
	{
		var table = new int[a.Length + 1, b.Length + 1];
		for (var i = 1; i <= a.Length; i++)
		{
			for (var j = 1; j <= b.Length; j++)
			{
				table[i, j] = a[i - 1] == b[j - 1]
					? table[i - 1, j - 1] + 1
					: Math.Max(table[i - 1, j], table[i, j - 1]);
			}
		}

		return table[a.Length, b.Length];
	}

	private static RemarkAnchor Old(string? commit) =>
		new("src/X.cs", RemarkSide.Old, 4, "gone();", [], [], commit, null);
}
