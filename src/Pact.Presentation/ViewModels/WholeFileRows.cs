using System.Globalization;
using Pact.Core.Git;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// Lines removed together, folded into one row of the whole-file view.
/// </summary>
/// <param name="BeforeNewLine">The new line the run sits above; one past the last line at the end.</param>
/// <param name="OldStart">The first removed line in the old version; it names the run across refreshes.</param>
/// <param name="Lines">The removed lines.</param>
public sealed record RemovedRun(int BeforeNewLine, int OldStart, IReadOnlyList<string> Lines);

/// <summary>Rows of a file shown whole: every new line, with its removed runs folded where they were.</summary>
internal static class WholeFileRows
{
	/// <summary>
	/// Builds the rows. A deleted file has no new text: its old lines are the rows, all removed.
	/// </summary>
	public static (IReadOnlyList<DiffRowViewModel> Rows, IReadOnlyList<RemovedRun> Runs) Build(
		FileDiff diff,
		IReadOnlyList<string>? newLines,
		bool deleted,
		IReadOnlySet<int> expandedOldStarts)
	{
		ArgumentNullException.ThrowIfNull(diff);
		ArgumentNullException.ThrowIfNull(expandedOldStarts);
		if (deleted || newLines is null)
		{
			var removed = diff.Hunks.SelectMany(hunk => hunk.Lines)
				.Select(line => new DiffRowViewModel(DiffRowKind.Removed, Format(line.OldNumber), string.Empty, line.Text))
				.ToArray();
			return (removed, []);
		}

		var added = diff.Hunks.SelectMany(hunk => hunk.Lines)
			.Where(line => line.Kind == DiffLineKind.Added && line.NewNumber is not null)
			.Select(line => line.NewNumber!.Value)
			.ToHashSet();
		var runs = Runs(diff, newLines.Count);
		var runsBefore = runs.ToLookup(run => run.BeforeNewLine);
		List<DiffRowViewModel> rows = [];
		for (var line = 1; line <= newLines.Count + 1; line++)
		{
			foreach (var run in runsBefore[line])
			{
				var expanded = expandedOldStarts.Contains(run.OldStart);
				rows.Add(new DiffRowViewModel(DiffRowKind.Fold, string.Empty, string.Empty, FoldText(run.Lines.Count, expanded)));
				if (expanded)
				{
					rows.AddRange(run.Lines.Select((text, index) =>
						new DiffRowViewModel(DiffRowKind.Removed, Format(run.OldStart + index), string.Empty, text)));
				}
			}

			if (line <= newLines.Count)
			{
				rows.Add(new DiffRowViewModel(
					added.Contains(line) ? DiffRowKind.Added : DiffRowKind.Context,
					string.Empty,
					Format(line),
					newLines[line - 1]));
			}
		}

		return (rows, runs);
	}

	// Each hunk's consecutive removed lines sit above the next new line of the hunk, or above the
	// line that follows the hunk.
	private static RemovedRun[] Runs(FileDiff diff, int newLineCount)
	{
		List<RemovedRun> runs = [];
		foreach (var hunk in diff.Hunks)
		{
			var nextNew = hunk.NewStart == 0 ? 1 : hunk.NewStart;
			List<string>? pending = null;
			var pendingStart = 0;
			foreach (var line in hunk.Lines)
			{
				if (line.Kind == DiffLineKind.Removed)
				{
					if (pending is null)
					{
						pending = [];
						pendingStart = line.OldNumber ?? 0;
					}

					pending.Add(line.Text);
					continue;
				}

				if (pending is not null)
				{
					runs.Add(new RemovedRun(line.NewNumber ?? nextNew, pendingStart, pending));
					pending = null;
				}

				if (line.NewNumber is { } number)
				{
					nextNew = number + 1;
				}
			}

			if (pending is not null)
			{
				runs.Add(new RemovedRun(Math.Min(nextNew, newLineCount + 1), pendingStart, pending));
			}
		}

		return [.. runs];
	}

	private static string FoldText(int count, bool expanded) => string.Create(
		CultureInfo.InvariantCulture,
		$"{(expanded ? "▾" : "▸")} {count} {(count == 1 ? "line" : "lines")} removed");

	private static string Format(int? number) =>
		number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
}
