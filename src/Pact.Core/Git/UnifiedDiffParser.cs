using System.Globalization;
using System.Text.RegularExpressions;

namespace Pact.Core.Git;

/// <summary>Parses the output of <c>git diff</c> for one file into hunks and numbered lines.</summary>
public static partial class UnifiedDiffParser
{
	/// <summary>Parses one file's diff; empty output yields no hunks.</summary>
	public static FileDiff Parse(string gitDiffOutput)
	{
		ArgumentNullException.ThrowIfNull(gitDiffOutput);

		List<DiffHunk> hunks = [];
		List<DiffLine>? lines = null;
		Match? header = null;
		var oldNumber = 0;
		var newNumber = 0;

		void CloseHunk()
		{
			if (header is not null && lines is not null)
			{
				hunks.Add(new DiffHunk(
					Number(header.Groups["oldStart"]),
					Count(header.Groups["oldCount"]),
					Number(header.Groups["newStart"]),
					Count(header.Groups["newCount"]),
					header.Groups["header"].Value.Trim(),
					lines));
			}
		}

		foreach (var rawLine in gitDiffOutput.Split('\n'))
		{
			var line = rawLine.TrimEnd('\r');
			if (header is null && line.StartsWith("Binary files ", StringComparison.Ordinal))
			{
				return FileDiff.Binary;
			}

			var match = HunkHeader().Match(line);
			if (match.Success)
			{
				CloseHunk();
				header = match;
				lines = [];
				oldNumber = Number(match.Groups["oldStart"]);
				newNumber = Number(match.Groups["newStart"]);
				continue;
			}

			if (lines is null || line.Length == 0)
			{
				continue;
			}

			switch (line[0])
			{
				case ' ':
					lines.Add(new DiffLine(DiffLineKind.Context, oldNumber++, newNumber++, line[1..]));
					break;
				case '-':
					lines.Add(new DiffLine(DiffLineKind.Removed, oldNumber++, null, line[1..]));
					break;
				case '+':
					lines.Add(new DiffLine(DiffLineKind.Added, null, newNumber++, line[1..]));
					break;
				default:
					// "\ No newline at end of file" and anything else is not a line.
					break;
			}
		}

		CloseHunk();
		return new FileDiff(null, hunks);
	}

	private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

	private static int Count(Group group) => group.Success ? Number(group) : 1;

	[GeneratedRegex(@"^@@ -(?<oldStart>\d+)(,(?<oldCount>\d+))? \+(?<newStart>\d+)(,(?<newCount>\d+))? @@(?<header>.*)$")]
	private static partial Regex HunkHeader();
}
