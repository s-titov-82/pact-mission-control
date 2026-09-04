using System.Text.RegularExpressions;

namespace Pact.Presentation.Services;

/// <summary>
/// Removes the few escape sequences that would destroy the terminal view — full reset and
/// clear-scrollback — while passing everything else through untouched.
/// </summary>
/// <remarks>
/// Deliberately minimal. Mouse-tracking sequences (<c>?1000/1002/1003/1006</c>) in particular
/// must reach xterm, because agent TUIs repaint a fixed grid and own their own scrolling: strip
/// those and the mouse wheel stops working inside the agent.
/// </remarks>
public sealed partial class TerminalDisplayOutputFilter
{
	private string _carry = string.Empty;

	/// <summary>
	/// Filters the next chunk of a session's output stream.
	/// </summary>
	/// <returns>
	/// The text to display. A trailing partial escape sequence is held back and prepended to the
	/// next call, so one instance must follow one stream for its whole lifetime. This carry is
	/// also what lets downstream scanners assume sequences are never split across chunks.
	/// </returns>
	public string Filter(string text)
	{
		text = _carry + text;
		_carry = string.Empty;

		var incompleteStart = FindTrailingIncompleteCandidate(text);
		if (incompleteStart >= 0)
		{
			_carry = text[incompleteStart..];
			text = text[..incompleteStart];
		}

		text = RisPattern().Replace(text, string.Empty);
		return ClearScrollbackPattern().Replace(text, string.Empty);
	}

	private static int FindTrailingIncompleteCandidate(string text)
	{
		var escapeIndex = text.LastIndexOf("\u001b[", StringComparison.Ordinal);
		if (escapeIndex < 0)
		{
			return -1;
		}

		for (var index = escapeIndex + 2; index < text.Length; index++)
		{
			var value = text[index];
			if (value is >= '@' and <= '~')
			{
				return -1;
			}

			if (value is >= '0' and <= '?' or >= ' ' and <= '/')
			{
				continue;
			}

			return -1;
		}

		return escapeIndex;
	}

	[GeneratedRegex("\\x1B\\[3J")]
	private static partial Regex ClearScrollbackPattern();

	[GeneratedRegex("\\x1Bc")]
	private static partial Regex RisPattern();
}