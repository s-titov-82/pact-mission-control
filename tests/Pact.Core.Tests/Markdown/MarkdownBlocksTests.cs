using Pact.Core.Markdown;

namespace Pact.Core.Tests.Markdown;

public sealed class MarkdownBlocksTests
{
	[Test]
	public void Paragraph_lines_form_one_block_and_blank_lines_separate_blocks() =>
		MarkdownBlocks.Split(["a", "b", "", "c"]).ShouldBe([new(1, 2), new(4, 4)]);

	[Test]
	public void A_heading_is_its_own_block_even_without_a_blank_line() =>
		MarkdownBlocks.Split(["# T", "text"]).ShouldBe([new(1, 1), new(2, 2)]);

	[Test]
	public void Each_list_item_is_a_block_with_its_indented_continuation() =>
		MarkdownBlocks.Split(["- one", "  more", "- two", "1. three"]).ShouldBe([new(1, 2), new(3, 3), new(4, 4)]);

	[Test]
	public void A_fenced_code_block_is_one_block_whatever_it_holds() =>
		MarkdownBlocks.Split(["```", "# x", "", "- y", "```", "after"]).ShouldBe([new(1, 5), new(6, 6)]);

	[Test]
	public void Consecutive_quote_lines_and_table_lines_are_one_block_each() =>
		MarkdownBlocks.Split(["> a", "> b", "| x |", "|---|", "| 1 |"]).ShouldBe([new(1, 2), new(3, 5)]);

	[Test]
	public void An_unclosed_fence_runs_to_the_end() =>
		MarkdownBlocks.Split(["```", "x"]).ShouldBe([new(1, 2)]);

	[Test]
	public void A_longer_fence_is_not_closed_by_a_shorter_one_inside_it() =>
		MarkdownBlocks.Split(["````", "```", "x", "```", "````", "after"]).ShouldBe([new(1, 5), new(6, 6)]);

	[Test]
	public void A_nested_list_stays_inside_its_parent_item() =>
		MarkdownBlocks.Split(["- one", "  - sub", "  - sub2", "- two"]).ShouldBe([new(1, 3), new(4, 4)]);

	[Test]
	public void An_item_with_an_indented_paragraph_after_a_blank_line_keeps_it() =>
		MarkdownBlocks.Split(["- one", "", "  more", "", "next"]).ShouldBe([new(1, 3), new(5, 5)]);

	[Test]
	public void A_paragraph_line_right_after_a_list_item_ends_the_item() =>
		MarkdownBlocks.Split(["- one", "text"]).ShouldBe([new(1, 1), new(2, 2)]);
}
