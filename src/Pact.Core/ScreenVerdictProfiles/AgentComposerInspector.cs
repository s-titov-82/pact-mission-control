using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>Interprets bounded dim metadata only inside a structurally recognized current composer.</summary>
public static class AgentComposerInspector
{
	/// <summary>Returns content-free composer evidence, or null when its current frame is incomplete.</summary>
	public static TerminalPromptEvidence? Inspect(TerminalScreenSnapshot screen, char promptCharacter, bool bordered, IReadOnlyList<string> chromeLabels)
	{
		ArgumentNullException.ThrowIfNull(screen);
		ArgumentNullException.ThrowIfNull(chromeLabels);
		var (prompt, end) = FindFrame(screen, promptCharacter, bordered);
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
			var start = index == prompt ? row.Text.IndexOf(promptCharacter, StringComparison.Ordinal) + 1 : 0;
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

	internal static (int Prompt, int End) FindFrame(TerminalScreenSnapshot screen, char marker, bool bordered)
	{
		for (var index = screen.Rows.Length - 1; index >= 0; index--)
		{
			var row = screen.Rows[index];
			var trimmed = row.Text.TrimStart();
			if (row.IsWrapped || !trimmed.StartsWith(marker))
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
					if (!screen.Rows[end].IsWrapped && IsRule(screen.Rows[end].Text) && screen.Rows[end].Text.Trim().Length == screen.Rows[upper].Text.Trim().Length)
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

	internal static bool IsRule(string text)
	{
		var value = text.Trim();
		return value.Length >= 16 && value.All(character => character == '─');
	}

	internal static bool HasCurrentSelection(TerminalScreenSnapshot screen, char marker)
	{
		for (var index = screen.Rows.Length - 1; index >= 0; index--)
		{
			var text = screen.Rows[index].Text.TrimStart();
			if (screen.Rows[index].IsWrapped || !text.StartsWith(marker))
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
