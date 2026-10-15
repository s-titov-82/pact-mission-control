using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>Interprets bounded dim metadata only inside a structurally recognized current composer.</summary>
public static class AgentComposerInspector
{
	/// <summary>Returns content-free composer evidence, or null when its current frame is incomplete.</summary>
	public static TerminalPromptEvidence? Inspect(TerminalScreenSnapshot screen, string promptCharacters, bool bordered, IReadOnlyList<string> chromeLabels)
	{
		ArgumentNullException.ThrowIfNull(screen);
		ArgumentNullException.ThrowIfNull(promptCharacters);
		ArgumentNullException.ThrowIfNull(chromeLabels);
		var (prompt, end) = FindFrame(screen, promptCharacters, bordered);
		if (prompt < 0 || end < 0)
		{
			return null;
		}
		var count = 0;
		var draftCount = 0;
		var reliable = true;
		for (var index = prompt; index < end; index++)
		{
			var row = screen.Rows[index];
			reliable &= row.DimRanges is not null;
			var start = index == prompt ? row.Text.IndexOfAny(promptCharacters.ToCharArray()) + 1 : 0;
			if (chromeLabels.Contains(row.Text[start..].Trim(), StringComparer.Ordinal))
			{
				continue;
			}
			var rangeIndex = 0;
			for (var offset = start; offset < row.Text.Length; offset++)
			{
				if (char.IsWhiteSpace(row.Text[offset]))
				{
					continue;
				}
				count++;
				if (row.DimRanges is not { } ranges)
				{
					reliable = false;
					continue;
				}
				while (rangeIndex < ranges.Length && ranges[rangeIndex].Start + ranges[rangeIndex].Length <= offset)
				{
					rangeIndex++;
				}
				if (rangeIndex >= ranges.Length || ranges[rangeIndex].Start > offset)
				{
					draftCount++;
				}
			}
		}
		return new(true, true, count, false, reliable, draftCount);
	}

	/// <summary>
	/// The cell after the last draft character in the current composer, or after the prompt
	/// when the composer holds only a dim placeholder; null without a complete composer frame.
	/// </summary>
	public static TerminalCellPosition? FindDraftEnd(TerminalScreenSnapshot screen, string promptCharacters, bool bordered, IReadOnlyList<string> chromeLabels)
	{
		ArgumentNullException.ThrowIfNull(screen);
		ArgumentNullException.ThrowIfNull(promptCharacters);
		ArgumentNullException.ThrowIfNull(chromeLabels);
		var (prompt, end) = FindFrame(screen, promptCharacters, bordered);
		if (prompt < 0 || end < 0)
		{
			return null;
		}
		var glyph = screen.Rows[prompt].Text.IndexOfAny(promptCharacters.ToCharArray());
		TerminalCellPosition position = new(glyph + 3, prompt + 1);
		for (var index = prompt; index < end; index++)
		{
			var row = screen.Rows[index];
			var start = index == prompt ? glyph + 1 : 0;
			if (chromeLabels.Contains(row.Text[start..].Trim(), StringComparer.Ordinal))
			{
				continue;
			}
			for (var offset = row.Text.Length - 1; offset >= start; offset--)
			{
				if (!char.IsWhiteSpace(row.Text[offset]) && !IsDim(row, offset))
				{
					position = new(offset + 2, index + 1);
					break;
				}
			}
		}
		return position with { Column = Math.Min(position.Column, screen.Columns) };
	}

	private static bool IsDim(TerminalScreenRow row, int offset) =>
		row.DimRanges is { } ranges && ranges.Any(range => offset >= range.Start && offset < range.Start + range.Length);

	internal static (int Prompt, int End) FindFrame(TerminalScreenSnapshot screen, string markers, bool bordered)
	{
		for (var index = screen.Rows.Length - 1; index >= 0; index--)
		{
			var row = screen.Rows[index];
			var trimmed = row.Text.TrimStart();
			if (row.IsWrapped || !StartsWithMarker(trimmed, markers))
			{
				continue;
			}
			if (IsNumberedOption(trimmed[1..]))
			{
				return (-1, -1);
			}
			if (bordered)
			{
				var upper = index - 1;
				while (upper >= 0 && string.IsNullOrWhiteSpace(screen.Rows[upper].Text))
				{
					upper--;
				}
				if (upper < 0 || screen.Rows[upper].IsWrapped || !IsRule(screen.Rows[upper].Text))
				{
					return (-1, -1);
				}
				for (var end = index + 1; end < screen.Rows.Length; end++)
				{
					if (!screen.Rows[end].IsWrapped && IsRule(screen.Rows[end].Text))
					{
						return (index, end);
					}
				}
				return (-1, -1);
			}
			// The composer ends at Codex's status footer; hint rows such as
			// "? for shortcuts" may follow it.
			for (var footer = index + 1; footer < screen.Rows.Length; footer++)
			{
				if (!screen.Rows[footer].IsWrapped && IsCodexFooter(screen.Rows[footer].Text))
				{
					return (index, footer);
				}
			}
			return (-1, -1);
		}
		return (-1, -1);
	}

	private static readonly string[] CodexReasoningEfforts = ["default", "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

	/// <summary>
	/// Codex's status footer always names the model with its reasoning effort,
	/// e.g. "GPT-6.1-Sol medium · D:\repo · main · Context 30% used".
	/// </summary>
	private static bool IsCodexFooter(string text)
	{
		foreach (var segment in text.Split(" · "))
		{
			var words = segment.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (words.Length >= 2 && CodexReasoningEfforts.Contains(words[^1], StringComparer.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// A composer border: mostly '─', so a rule carrying a short label (and therefore a
	/// different width than its pair) still counts.
	/// </summary>
	internal static bool IsRule(string text)
	{
		var value = text.Trim();
		var lines = value.Count(character => character == '─');
		return lines >= 16 && lines * 2 >= value.Length;
	}

	private static bool StartsWithMarker(string text, string markers) =>
		text.Length > 0 && markers.Contains(text[0], StringComparison.Ordinal);

	internal static bool HasCurrentSelection(TerminalScreenSnapshot screen, string markers)
	{
		for (var index = screen.Rows.Length - 1; index >= 0; index--)
		{
			var text = screen.Rows[index].Text.TrimStart();
			if (screen.Rows[index].IsWrapped || !StartsWithMarker(text, markers))
			{
				continue;
			}
			if (!IsNumberedOption(text[1..]))
			{
				return false;
			}
			var sibling = screen.Rows.Where((_, rowIndex) => rowIndex != index)
				.Any(row => IsNumberedOption(row.Text));
			var hint = screen.Rows.Skip(index + 1).Any(row =>
				row.Text.Contains("enter to confirm", StringComparison.OrdinalIgnoreCase)
				|| row.Text.Contains("enter to select", StringComparison.OrdinalIgnoreCase)
				|| row.Text.Contains("esc to cancel", StringComparison.OrdinalIgnoreCase)
				|| row.Text.Contains("esc to go back", StringComparison.OrdinalIgnoreCase));
			return sibling && hint;
		}
		return false;
	}

	internal static bool IsNumberedOption(string text)
	{
		var value = text.TrimStart();
		var dot = value.IndexOf('.', StringComparison.Ordinal);
		return dot > 0 && !value.AsSpan(0, dot).ContainsAnyExceptInRange('0', '9');
	}
}
