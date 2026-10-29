using System.Globalization;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class RenderedMarkdownTests
{
	[Test]
	public void A_block_whose_lines_are_all_added_is_added_and_one_with_some_is_changed()
	{
		var shown = Shown(["# T", "", "new a", "new b", "", "old", "added"], added: [3, 4, 7]);

		var blocks = RenderedMarkdown.Build(shown).OfType<RenderedBlock>().ToArray();

		blocks.Select(block => (block.FirstLine, block.LastLine, block.Change)).ShouldBe([
			(1, 1, RenderedChange.None), (3, 4, RenderedChange.Added), (6, 7, RenderedChange.Changed)]);
		blocks[1].Markdown.ShouldBe("new a\nnew b");
	}

	[Test]
	public void A_run_removed_inside_a_block_marks_the_block_changed()
	{
		var shown = Shown(["one", "two"], added: [], runs: [new RemovedRun(2, 2, ["gone"])]);

		var items = RenderedMarkdown.Build(shown);

		items.ShouldHaveSingleItem().ShouldBeOfType<RenderedBlock>().Change.ShouldBe(RenderedChange.Changed);
	}

	[Test]
	public void A_run_removed_between_blocks_becomes_a_removed_item_in_place()
	{
		var shown = Shown(["a", "", "b"], added: [], runs: [new RemovedRun(3, 3, ["old one", "old two"])]);

		var items = RenderedMarkdown.Build(shown);

		items.Count.ShouldBe(3);
		items[0].ShouldBeOfType<RenderedBlock>().FirstLine.ShouldBe(1);
		var removed = items[1].ShouldBeOfType<RenderedRemoved>();
		(removed.OldStart, removed.Markdown, removed.LineCount).ShouldBe((3, "old one\nold two", 2));
		items[2].ShouldBeOfType<RenderedBlock>().FirstLine.ShouldBe(3);
	}

	[Test]
	public void A_run_removed_before_a_blank_separator_sits_between_the_paragraphs()
	{
		var shown = Shown(["a", "", "b"], added: [], runs: [new RemovedRun(2, 2, ["gone"])]);

		var items = RenderedMarkdown.Build(shown);

		items.Select(item => item.GetType().Name).ShouldBe([nameof(RenderedBlock), nameof(RenderedRemoved), nameof(RenderedBlock)]);
	}

	[Test]
	public void A_run_after_the_last_block_goes_at_the_end()
	{
		var shown = Shown(["a"], added: [], runs: [new RemovedRun(2, 2, ["tail"])]);

		RenderedMarkdown.Build(shown)[^1].ShouldBeOfType<RenderedRemoved>();
	}

	[Test]
	public void Each_block_points_at_the_row_of_its_first_line()
	{
		var shown = Shown(["a", "", "b"], added: [], runs: [new RemovedRun(3, 3, ["old"])]);

		foreach (var block in RenderedMarkdown.Build(shown).OfType<RenderedBlock>())
		{
			shown.Rows[block.Row].NewNumber.ShouldBe(block.FirstLine.ToString(CultureInfo.InvariantCulture));
		}
	}

	[Test]
	public void A_thread_on_the_third_line_of_a_paragraph_lands_on_that_paragraph()
	{
		var shown = Shown(["# T", "", "p1", "p2", "p3"], added: []);
		var items = RenderedMarkdown.Build(shown);
		var rowOfLine5 = shown.Rows.ToList().FindIndex(row => row.NewNumber == "5");

		var byBlock = RenderedMarkdown.ThreadsByBlock(items, shown, new Dictionary<int, IReadOnlyList<string>> { [rowOfLine5] = ["C1"] });

		byBlock.Keys.ShouldBe([3]);
		byBlock[3].ShouldBe(["C1"]);
	}

	[Test]
	public void A_fenced_block_with_blank_lines_stays_one_block()
	{
		var shown = Shown(["```", "x", "", "y", "```"], added: [3, 4]);

		var block = RenderedMarkdown.Build(shown).ShouldHaveSingleItem().ShouldBeOfType<RenderedBlock>();

		(block.FirstLine, block.LastLine, block.Change).ShouldBe((1, 5, RenderedChange.Changed));
	}

	// Whole-file rows: every new line, with a fold row where each run sits.
	private static ShownDiff Shown(string[] lines, int[] added, RemovedRun[]? runs = null)
	{
		runs ??= [];
		List<DiffRowViewModel> rows = [];
		for (var line = 1; line <= lines.Length + 1; line++)
		{
			foreach (var run in runs.Where(run => run.BeforeNewLine == line))
			{
				rows.Add(new DiffRowViewModel(DiffRowKind.Fold, string.Empty, string.Empty, "▸ removed"));
			}

			if (line <= lines.Length)
			{
				rows.Add(new DiffRowViewModel(
					added.Contains(line) ? DiffRowKind.Added : DiffRowKind.Context,
					string.Empty,
					line.ToString(CultureInfo.InvariantCulture),
					lines[line - 1]));
			}
		}

		return new ShownDiff("docs/x.md", "head", rows, lines, runs);
	}
}
