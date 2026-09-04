using System.Text.RegularExpressions;
using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>
/// Recognizes Claude's interrupt hint and idle composer markers in stable screens.
/// </summary>
public sealed partial class ClaudeScreenProfile : AgentScreenProfileBase
{
	/// <summary>
	/// Gets the stateless Claude screen classifier.
	/// </summary>
	public static readonly ClaudeScreenProfile Instance = new();

	private ClaudeScreenProfile()
	{
	}

	/// <inheritdoc />
	protected override string[] ScrollMarkers => ["Jump to bottom (ctrl+End)"];

	/// <inheritdoc />
	protected override string[] ResumeSessionMarkers => ["Ctrl-A to show all projects"];

	/// <inheritdoc />
	protected override string TrustRequestMarker => "❯ 1. Yes, I trust this folder";

	/// <inheritdoc />
	protected override Regex InterruptedRegex => InterruptedRx();

	/// <inheritdoc />
	protected override Regex WorkingRegex => WorkingRx();

	/// <inheritdoc />
	protected override Regex WorkedForRegex => WorkedForRx();

	/// <inheritdoc />
	protected override Regex LastMessageRegex => LastMessageRx();

	/// <inheritdoc />
	protected override Regex InputRequestedRegex => InputRequestedRx();

	/// <inheritdoc />
	protected override int FindPrompt(string window)
	{
		ArgumentNullException.ThrowIfNull(window);
		for (var index = window.Length - 1; index >= 0; index--)
		{
			if (!PromptCharacters.Contains(window[index]))
			{
				continue;
			}

			var lineStart = index == 0 ? 0 : window.LastIndexOf('\n', index - 1) + 1;
			if (window.AsSpan(lineStart, index - lineStart).IsWhiteSpace())
			{
				return index;
			}
		}

		return -1;
	}

	/// <inheritdoc />
	protected override TerminalPromptEvidence InspectPrompt(string window, int promptAt)
	{
		return new(
			PromptFound: true,
			BoundaryFound: true,
			NonWhitespaceCharacterCount: 0,
			SeparatorSharesLogicalLine: false);
	}

	// The interrupt-hint keybinding text is constant regardless of which
	// whimsical verb ("Cogitating", "Working", "Baking"...) animates next
	// to it, so matching it directly is far more robust than parsing the
	// ever-changing verb and its surrounding punctuation.
	[GeneratedRegex(@"(?<descr>(?<=[*✢✽✻]\s*).*?(?=…|\.{3})|[A-Z][a-z\-é]{2,15}in[g']([\sA-za-z\(\)\/])*?(?=…|\.{3})|Waiting for \d+ background agents?|\d \w{4,15} still running)", RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
	private static partial Regex WorkingRx();

	// "Worked for 2m 30s", "Cooked for 12s", "Sautéed for 3s" - a
	// capitalized past-tense verb followed by a duration.
	[GeneratedRegex(@"(?<descr>[A-Z][a-z\-é]{2,15}ed\sfor\s\d{1,2}[hms])", RegexOptions.RightToLeft)]
	private static partial Regex WorkedForRx();

	[GeneratedRegex(@"[⎿✻]\s*(?<descr>Interrupted|(529 Overloaded)|(API Error)|(Unable to connect to API))", RegexOptions.RightToLeft)]
	private static partial Regex InterruptedRx();

	[GeneratedRegex(@"^●\s+(?<message>.+?)(?=^.*?(?:[A-Z][a-z\-é]{2,15}ed\s+for\s+\d{1,2}[hms]))", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.RightToLeft)]
	private static partial Regex LastMessageRx();

	[GeneratedRegex(@"(?<descr>Enter to select)|(?<descr>√ Submit)|(?<descr>(?<=─{20,}[\s\r\n]+)\[\s*?\]\s*?[\w\d\-]*?(?=[\s\r\n]))", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.RightToLeft)]
	private static partial Regex InputRequestedRx();
}
