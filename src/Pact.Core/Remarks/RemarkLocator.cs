namespace Pact.Core.Remarks;

/// <summary>
/// Carries a remark's anchor from the file it was written on to the file as it is now. A remark
/// moves only while its line is provably the same line; otherwise it is outdated, never re-pointed.
/// </summary>
public static class RemarkLocator
{
	private const int RepeatContext = 1;

	/// <summary>Splits text into lines without line endings; a final newline adds no empty line.</summary>
	public static IReadOnlyList<string> SplitLines(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (text.Length == 0)
		{
			return [];
		}

		var lines = text.Split('\n');
		var count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
		var result = new string[count];
		for (var index = 0; index < count; index++)
		{
			result[index] = lines[index].TrimEnd('\r');
		}

		return result;
	}

	/// <summary>
	/// The 1-based line of a New-side anchor in <paramref name="current"/>, or <see langword="null"/>
	/// when it is outdated: the line was changed or deleted, the texts differ too much to tell, or the
	/// line with its context is one of several identical regions and any of them changed — a line diff
	/// cannot tell which copy of a repeated block went.
	/// </summary>
	/// <param name="anchor">The anchor; its context counts give the region to compare.</param>
	/// <param name="captured">The file as it was when the remark was written.</param>
	/// <param name="current">The file as it is now.</param>
	public static int? LocateNew(RemarkAnchor anchor, IReadOnlyList<string> captured, IReadOnlyList<string> current)
	{
		ArgumentNullException.ThrowIfNull(anchor);
		ArgumentNullException.ThrowIfNull(captured);
		ArgumentNullException.ThrowIfNull(current);
		var index = anchor.Line - 1;
		if (index < 0 || index >= captured.Count)
		{
			return null;
		}

		var map = LineDiff.MapUnchanged(captured, current);
		if (map is null)
		{
			return null;
		}

		// The repeat test looks at the line with one neighbour each side, however much context the
		// anchor keeps: a wider window would reach past a repeated block and look unique, while which
		// copy of the block went stays just as unknowable.
		var start = Math.Max(0, index - Math.Min(RepeatContext, anchor.Before.Count));
		var end = Math.Min(captured.Count - 1, index + Math.Min(RepeatContext, anchor.After.Count));
		var occurrences = Occurrences(captured, start, end - start + 1);
		if (occurrences.Count > 1)
		{
			var length = end - start + 1;
			foreach (var occurrence in occurrences)
			{
				for (var line = occurrence; line < occurrence + length; line++)
				{
					if (map[line] is null)
					{
						return null;
					}
				}
			}
		}

		return map[index] + 1;
	}

	/// <summary>Whether an Old-side anchor still holds: only while HEAD is the commit it was written against.</summary>
	public static bool IsOldValid(RemarkAnchor anchor, string? headCommit)
	{
		ArgumentNullException.ThrowIfNull(anchor);
		return string.Equals(anchor.BaseCommit, headCommit, StringComparison.Ordinal);
	}

	// Every start index where lines[start..start+length) occurs in lines.
	private static List<int> Occurrences(IReadOnlyList<string> lines, int start, int length)
	{
		List<int> found = [];
		for (var candidate = 0; candidate + length <= lines.Count; candidate++)
		{
			var matches = true;
			for (var offset = 0; offset < length && matches; offset++)
			{
				matches = string.Equals(lines[candidate + offset], lines[start + offset], StringComparison.Ordinal);
			}

			if (matches)
			{
				found.Add(candidate);
			}
		}

		return found;
	}
}
