using System.Collections.Immutable;

namespace Pact.Core.Sessions;

/// <summary>A UTF-16 interval of dim text within one physical terminal row.</summary>
/// <param name="Start">Inclusive offset in the row's text.</param>
/// <param name="Length">Number of UTF-16 code units in the interval.</param>
public readonly record struct TerminalTextRange(int Start, int Length);

/// <summary>Rendered physical row with nullable, bounded dim metadata.</summary>
/// <param name="Text">Rendered characters, with terminal padding preserved.</param>
/// <param name="IsWrapped">Whether this row continues the preceding physical row.</param>
/// <param name="DimRanges">Null when uncaptured; empty when captured without dim text.</param>
public sealed record TerminalScreenRow(string Text, bool IsWrapped, ImmutableArray<TerminalTextRange>? DimRanges);

/// <summary>Immutable live viewport evidence; styling and work-state interpretation are independent.</summary>
public sealed class TerminalScreenSnapshot
{
	private TerminalScreenSnapshot(string text, ImmutableArray<TerminalScreenRow> rows, int columns, bool stable)
	{
		Text = text;
		Rows = rows;
		Columns = columns;
		Stable = stable;
	}

	/// <summary>Logical text, reconstructed without duplicating wrapped lines.</summary>
	public string Text { get; }
	/// <summary>Physical rows, retaining their capture coverage and wrapping.</summary>
	public ImmutableArray<TerminalScreenRow> Rows { get; }
	/// <summary>Viewport columns, or zero for a legacy text-only observation.</summary>
	public int Columns { get; }
	/// <summary>Number of physical rows in the captured viewport.</summary>
	public int RowCount => Rows.Length;
	/// <summary>Whether the snapshot was taken after the terminal's settle debounce.</summary>
	public bool Stable { get; }

	/// <summary>Preserves legacy text verbatim without claiming any style evidence.</summary>
	public static TerminalScreenSnapshot FromText(string text, bool stable = true)
	{
		ArgumentNullException.ThrowIfNull(text);
		return new(text, [.. text.Split('\n').Select(line => new TerminalScreenRow(line, false, null))], 0, stable);
	}

	/// <summary>Validates physical rows and projects the existing xterm logical-text contract.</summary>
	/// <exception cref="ArgumentException">Rows or dim intervals are incomplete, overlapping or out of bounds.</exception>
	public static TerminalScreenSnapshot FromRows(ImmutableArray<TerminalScreenRow> rows, int columns, bool stable)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);
		if (rows.IsDefault)
		{
			throw new ArgumentException("Rows must be initialized.", nameof(rows));
		}

		var logicalLines = new List<string>(rows.Length);
		for (var index = 0; index < rows.Length; index++)
		{
			var row = rows[index];
			ArgumentNullException.ThrowIfNull(row);
			ArgumentNullException.ThrowIfNull(row.Text);
			if (row.DimRanges is { } ranges)
			{
				if (ranges.IsDefault)
				{
					throw new ArgumentException("Dim ranges must be initialized or null.", nameof(rows));
				}
				var end = 0;
				foreach (var range in ranges)
				{
					if (range.Start < end || range.Length <= 0 || (long)range.Start + range.Length > row.Text.Length)
					{
						throw new ArgumentException("Dim intervals must be ordered, non-overlapping and within row text.", nameof(rows));
					}
					end = range.Start + range.Length;
				}
			}

			// xterm already removes empty cells; explicit spaces remain visible text.
			var text = row.Text;
			if (row.IsWrapped && logicalLines.Count > 0)
			{
				logicalLines[^1] += text;
			}
			else
			{
				logicalLines.Add(text);
			}
		}
		while (logicalLines.Count > 0 && string.IsNullOrWhiteSpace(logicalLines[^1]))
		{
			logicalLines.RemoveAt(logicalLines.Count - 1);
		}
		return new(string.Join('\n', logicalLines), rows, columns, stable);
	}
}
