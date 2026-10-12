using System.Collections.Immutable;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class TerminalScreenSnapshotTests
{
	[Test]
	public void Explicit_trailing_spaces_survive_the_cell_translated_projection()
	{
		var snapshot = TerminalScreenSnapshot.FromRows([new("output ", false, []), new("› ", false, [])], 80, true);
		snapshot.Text.ShouldBe("output \n› ");
	}

	[Test]
	public void Wrapped_rows_preserve_padding_and_join_into_logical_text()
	{
		var snapshot = TerminalScreenSnapshot.FromRows(
			[new("output  ", false, null), new("PS D:\\Personal\\", false, []), new("Pact>  ", true, []), new("", false, [])], 80, true);
		snapshot.Text.ShouldBe("output  \nPS D:\\Personal\\Pact>  ");
		snapshot.Rows[2].IsWrapped.ShouldBeTrue();
	}

	[Test]
	public void Captured_plain_row_is_distinct_from_unknown_legacy_styling()
	{
		var snapshot = TerminalScreenSnapshot.FromRows([new("› draft", false, [])], 80, true);
		snapshot.Rows[0].DimRanges.ShouldNotBeNull();
		TerminalScreenSnapshot.FromText("› draft").Rows[0].DimRanges.ShouldBeNull();
	}

	[TestCase(-1, 1)]
	[TestCase(0, 0)]
	[TestCase(2, 2)]
	[TestCase(1, int.MaxValue)]
	public void Invalid_dim_ranges_are_rejected(int start, int length)
	{
		Should.Throw<ArgumentException>(() => TerminalScreenSnapshot.FromRows(
			[new("abc", false, [new(start, length)])], 80, true));
	}

	[Test]
	public void Overlapping_dim_ranges_are_rejected()
	{
		Should.Throw<ArgumentException>(() => TerminalScreenSnapshot.FromRows(
			[new("abcd", false, [new(0, 3), new(2, 1)])], 80, true));
	}

	[Test]
	public void Snapshot_is_immutable_and_keeps_utf16_ranges()
	{
		var builder = ImmutableArray.CreateBuilder<TerminalScreenRow>();
		builder.Add(new("› 😀e\u0301", false, [new(2, 4)]));
		var snapshot = TerminalScreenSnapshot.FromRows(builder.ToImmutable(), 80, false);
		builder[0] = new("changed", false, []);
		snapshot.Text.ShouldBe("› 😀e\u0301");
		snapshot.Stable.ShouldBeFalse();
		snapshot.Rows[0].DimRanges!.Value[0].ShouldBe(new TerminalTextRange(2, 4));
	}
}
