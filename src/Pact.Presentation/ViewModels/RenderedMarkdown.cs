using System.Globalization;
using Pact.Core.Markdown;

namespace Pact.Presentation.ViewModels;

/// <summary>How a rendered block changed against the commit.</summary>
public enum RenderedChange
{
	/// <summary>Unchanged.</summary>
	None,

	/// <summary>Every line of it is new.</summary>
	Added,

	/// <summary>Some of its lines are new, or lines were removed inside it.</summary>
	Changed
}

/// <summary>One item of a rendered Markdown file, in document order.</summary>
public abstract record RenderedItem;

/// <summary>A block of the new version.</summary>
/// <param name="FirstLine">Its first source line, 1-based; a remark on the block anchors there.</param>
/// <param name="LastLine">Its last source line.</param>
/// <param name="Markdown">Its source.</param>
/// <param name="Change">How it changed.</param>
/// <param name="Row">The row of <see cref="ShownDiff.Rows"/> showing <paramref name="FirstLine"/>.</param>
public sealed record RenderedBlock(int FirstLine, int LastLine, string Markdown, RenderedChange Change, int Row) : RenderedItem;

/// <summary>Lines removed where no block of the new version holds them.</summary>
/// <param name="OldStart">The first removed line in the old version.</param>
/// <param name="Markdown">The removed source.</param>
/// <param name="LineCount">How many lines were removed.</param>
public sealed record RenderedRemoved(int OldStart, string Markdown, int LineCount) : RenderedItem;

/// <summary>A Markdown file's whole-file view turned into blocks to render, with their changes marked.</summary>
public static class RenderedMarkdown
{
	/// <summary>The blocks of <paramref name="shown"/>'s new text and its removed runs, in document order.</summary>
	public static IReadOnlyList<RenderedItem> Build(ShownDiff shown)
	{
		ArgumentNullException.ThrowIfNull(shown);
		var lines = shown.NewLines ?? [];
		var rows = RowsByLine(shown);
		var added = shown.Rows
			.Where(row => row.Kind == DiffRowKind.Added)
			.Select(row => int.Parse(row.NewNumber, CultureInfo.InvariantCulture))
			.ToHashSet();
		var runs = shown.RemovedRuns.OrderBy(run => run.BeforeNewLine).ToArray();
		List<RenderedItem> items = [];
		var next = 0;
		foreach (var block in MarkdownBlocks.Split(lines))
		{
			// Runs above the block, blank separators included, sit before it.
			for (; next < runs.Length && runs[next].BeforeNewLine <= block.FirstLine; next++)
			{
				items.Add(Removed(runs[next]));
			}

			var removedInside = false;
			for (; next < runs.Length && runs[next].BeforeNewLine <= block.LastLine; next++)
			{
				removedInside = true;
			}

			var addedLines = Enumerable.Range(block.FirstLine, block.LastLine - block.FirstLine + 1).Count(added.Contains);
			var change = addedLines == block.LastLine - block.FirstLine + 1 && !removedInside
				? RenderedChange.Added
				: addedLines > 0 || removedInside ? RenderedChange.Changed : RenderedChange.None;
			items.Add(new RenderedBlock(
				block.FirstLine,
				block.LastLine,
				string.Join('\n', lines.Skip(block.FirstLine - 1).Take(block.LastLine - block.FirstLine + 1)),
				change,
				rows.GetValueOrDefault(block.FirstLine, -1)));
		}

		for (; next < runs.Length; next++)
		{
			items.Add(Removed(runs[next]));
		}

		return items;
	}

	/// <summary>
	/// Threads placed by row, gathered under the block holding their line and keyed by its first
	/// line. Removed lines belong to no block.
	/// </summary>
	public static IReadOnlyDictionary<int, IReadOnlyList<T>> ThreadsByBlock<T>(
		IReadOnlyList<RenderedItem> items,
		ShownDiff shown,
		IReadOnlyDictionary<int, IReadOnlyList<T>> byRow)
	{
		ArgumentNullException.ThrowIfNull(items);
		ArgumentNullException.ThrowIfNull(shown);
		ArgumentNullException.ThrowIfNull(byRow);
		var blocks = items.OfType<RenderedBlock>().ToArray();
		Dictionary<int, List<T>> gathered = [];
		foreach (var (row, threads) in byRow.OrderBy(pair => pair.Key))
		{
			if (row < 0 || row >= shown.Rows.Count
				|| !int.TryParse(shown.Rows[row].NewNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var line)
				|| Array.Find(blocks, block => block.FirstLine <= line && line <= block.LastLine) is not { } holder)
			{
				continue;
			}

			if (!gathered.TryGetValue(holder.FirstLine, out var list))
			{
				gathered[holder.FirstLine] = list = [];
			}

			list.AddRange(threads);
		}

		return gathered.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<T>)pair.Value);
	}

	private static Dictionary<int, int> RowsByLine(ShownDiff shown)
	{
		Dictionary<int, int> rows = [];
		for (var index = 0; index < shown.Rows.Count; index++)
		{
			var row = shown.Rows[index];
			if (row.Kind is DiffRowKind.Added or DiffRowKind.Context
				&& int.TryParse(row.NewNumber, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
			{
				rows[line] = index;
			}
		}

		return rows;
	}

	private static RenderedRemoved Removed(RemovedRun run) =>
		new(run.OldStart, string.Join('\n', run.Lines), run.Lines.Count);
}
