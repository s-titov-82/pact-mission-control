namespace Pact.Core.Git;

/// <summary>What one line of a diff hunk is.</summary>
public enum DiffLineKind
{
	/// <summary>Unchanged line shown for context.</summary>
	Context,

	/// <summary>Line present only in the new version.</summary>
	Added,

	/// <summary>Line present only in the old version.</summary>
	Removed
}

/// <summary>One line of a diff hunk.</summary>
/// <param name="Kind">Whether the line is context, added or removed.</param>
/// <param name="OldNumber">Line number in the old version; <see langword="null"/> for an added line.</param>
/// <param name="NewNumber">Line number in the new version; <see langword="null"/> for a removed line.</param>
/// <param name="Text">Line text without the diff prefix and without a line ending.</param>
public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text);

/// <summary>One <c>@@</c> block of a unified diff.</summary>
/// <param name="OldStart">First old line number covered.</param>
/// <param name="OldCount">Number of old lines covered.</param>
/// <param name="NewStart">First new line number covered.</param>
/// <param name="NewCount">Number of new lines covered.</param>
/// <param name="Header">Text after the closing <c>@@</c>, usually the enclosing declaration.</param>
/// <param name="Lines">The hunk's lines in order.</param>
public sealed record DiffHunk(
	int OldStart,
	int OldCount,
	int NewStart,
	int NewCount,
	string Header,
	IReadOnlyList<DiffLine> Lines);

/// <summary>The diff of one file, or a placeholder when it cannot be shown as lines.</summary>
/// <param name="Placeholder">Text shown instead of lines, or <see langword="null"/> for a line diff.</param>
/// <param name="Hunks">The hunks; empty when there is a placeholder or no change.</param>
public sealed record FileDiff(string? Placeholder, IReadOnlyList<DiffHunk> Hunks)
{
	/// <summary>A file whose content is binary.</summary>
	public static FileDiff Binary { get; } = new("Binary file, not shown", []);

	/// <summary>A file too large to read for display.</summary>
	public static FileDiff TooLarge { get; } = new("File is too large to show", []);

	/// <summary>Number of lines across all hunks.</summary>
	public int LineCount => Hunks.Sum(hunk => hunk.Lines.Count);

	/// <summary>Shows a new file's whole text as added lines numbered from one.</summary>
	public static FileDiff AllAdded(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (text.Length == 0)
		{
			return new FileDiff(null, []);
		}

		var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
		var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
		var diffLines = Enumerable.Range(0, count)
			.Select(index => new DiffLine(DiffLineKind.Added, null, index + 1, lines[index]))
			.ToArray();
		return new FileDiff(null, [new DiffHunk(0, 0, 1, count, string.Empty, diffLines)]);
	}
}
