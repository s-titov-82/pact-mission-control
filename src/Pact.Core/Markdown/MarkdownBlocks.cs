using System.Text.RegularExpressions;

namespace Pact.Core.Markdown;

/// <summary>One block of a Markdown text: the 1-based lines it spans, both inclusive.</summary>
public sealed record MarkdownBlock(int FirstLine, int LastLine);

/// <summary>
/// Splits Markdown into the blocks a reader sees one at a time: a heading, a paragraph, a list
/// item with what is nested in it, a quote, a table, a fenced code block. Blank lines between
/// blocks belong to none.
/// </summary>
public static partial class MarkdownBlocks
{
	/// <summary>The blocks of <paramref name="lines"/>, in order.</summary>
	public static IReadOnlyList<MarkdownBlock> Split(IReadOnlyList<string> lines)
	{
		ArgumentNullException.ThrowIfNull(lines);
		List<MarkdownBlock> blocks = [];
		var index = 0;
		while (index < lines.Count)
		{
			var line = lines[index];
			if (IsBlank(line))
			{
				index++;
				continue;
			}

			var start = index;
			var text = line.TrimStart();
			if (Fence(line) is { } fence)
			{
				index++;
				while (index < lines.Count && !Closes(lines[index], fence))
				{
					index++;
				}

				// An unclosed fence runs to the end of the text.
				index = Math.Min(index + 1, lines.Count);
			}
			else if (HeadingPattern().IsMatch(text))
			{
				index++;
			}
			else if (ItemPattern().IsMatch(text))
			{
				index = EndOfItem(lines, index);
			}
			else if (text.StartsWith('>') || text.StartsWith('|'))
			{
				var marker = text[0];
				while (index < lines.Count && lines[index].TrimStart().StartsWith(marker))
				{
					index++;
				}
			}
			else
			{
				index++;
				while (index < lines.Count && !IsBlank(lines[index]) && !StartsBlock(lines[index]))
				{
					index++;
				}
			}

			blocks.Add(new MarkdownBlock(start + 1, index));
		}

		return blocks;
	}

	// An item runs over every line indented deeper than its marker, nested items included, and over
	// a blank line only when deeper-indented text follows it.
	private static int EndOfItem(IReadOnlyList<string> lines, int start)
	{
		var indent = Indent(lines[start]);
		var index = start + 1;
		while (index < lines.Count)
		{
			if (!IsBlank(lines[index]))
			{
				if (Indent(lines[index]) <= indent)
				{
					break;
				}

				index++;
				continue;
			}

			var next = index;
			while (next < lines.Count && IsBlank(lines[next]))
			{
				next++;
			}

			if (next == lines.Count || Indent(lines[next]) <= indent)
			{
				break;
			}

			index = next;
		}

		return index;
	}

	private static bool StartsBlock(string line)
	{
		var text = line.TrimStart();
		return Fence(line) is not null || HeadingPattern().IsMatch(text) || ItemPattern().IsMatch(text)
			|| text.StartsWith('>') || text.StartsWith('|');
	}

	private readonly record struct FenceMark(char Character, int Length);

	// CommonMark: three or more backticks or tildes, indented at most three spaces.
	private static FenceMark? Fence(string line)
	{
		if (Indent(line) > 3)
		{
			return null;
		}

		var text = line.TrimStart();
		if (text.Length < 3 || (text[0] != '`' && text[0] != '~'))
		{
			return null;
		}

		var length = text.TakeWhile(character => character == text[0]).Count();
		return length >= 3 ? new FenceMark(text[0], length) : null;
	}

	// Only a run of the same character, at least as long, with nothing after it but spaces.
	private static bool Closes(string line, FenceMark fence)
	{
		if (Indent(line) > 3)
		{
			return false;
		}

		var text = line.Trim();
		return text.Length >= fence.Length && text.All(character => character == fence.Character);
	}

	private static bool IsBlank(string line) => string.IsNullOrWhiteSpace(line);

	private static int Indent(string line)
	{
		var width = 0;
		foreach (var character in line)
		{
			if (character == ' ')
			{
				width++;
			}
			else if (character == '\t')
			{
				width += 4;
			}
			else
			{
				break;
			}
		}

		return width;
	}

	[GeneratedRegex(@"^#{1,6}(\s|$)")]
	private static partial Regex HeadingPattern();

	[GeneratedRegex(@"^([-*+]|\d{1,9}[.)])(\s|$)")]
	private static partial Regex ItemPattern();
}
