using System.Text.RegularExpressions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>
/// Recognizes Codex's interrupt hint and completion summaries in stable screens. The start
/// banner counts as a completion, so a fresh or cleared session reads as idle.
/// </summary>
public sealed partial class CodexScreenProfile : AgentScreenProfileBase
{
	/// <summary>
	/// Gets the stateless Codex screen classifier.
	/// </summary>
	public static readonly CodexScreenProfile Instance = new();

	private CodexScreenProfile()
	{
	}

	/// <inheritdoc />
	protected override string ComposerPromptCharacters => "›";

	/// <inheritdoc />
	protected override string[] ScrollMarkers => ["Jump to bottom (ctrl+End)"];

	/// <inheritdoc />
	protected override string[] ResumeSessionMarkers => ["Resume a previous session"];

	/// <inheritdoc />
	protected override string TrustRequestMarker => "Do you trust the contents of this directory?";

	/// <inheritdoc />
	private protected override string? DescribeTrustRequest(string screen)
	{
		var menu = FolderAccessMenuRx().Match(screen);
		if (!menu.Success)
		{
			return base.DescribeTrustRequest(screen);
		}

		// Codex also shows this menu when changing folders. Enter is safe for the
		// startup readiness flow only with its affirmative option still selected.
		return menu.Groups["trust"].Success && menu.Groups["exit"].Value == "Quit"
			? TrustPromptDescription
			: "Folder access selection";
	}

	[GeneratedRegex(
		@"^[ \t]*Folder access[ \t]*\r?$.*?^[ \t]*Trust this folder\?.*?^[ \t]*(?<trust>›)?[ \t]*1\. Trust and continue[ \t]*\r?\n"
		+ @"[ \t]*›?[ \t]*2\. (?<exit>Quit|Keep current directory|Back to Agent Command Center)[ \t]*\r?\n"
		+ @"\s*enter continue(?: and create sandbox)? · esc (?:quit|cancel|back)\s*\z",
		RegexOptions.Multiline | RegexOptions.Singleline)]
	private static partial Regex FolderAccessMenuRx();

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

	[GeneratedRegex(@"(?<descr>Working)\s\(\s?\d{1,2}[hms]", RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
	private static partial Regex WorkingRx();

	[GeneratedRegex(@"(?<descr>Worked\sfor\s\d{1,2}[hms])|[\s\r\n]─{25,}[\s\r\n]|(?<descr>Run /usage to use one)|>_\sOpenAI\sCodex\s\(v", RegexOptions.RightToLeft)]
	private static partial Regex WorkedForRx();

	[GeneratedRegex(@"■\sConversation\sinterrupted|■ request timed out|To continue this session, run", RegexOptions.RightToLeft)]
	private static partial Regex InterruptedRx();

	[GeneratedRegex(@"^•\s+(?<message>.+?)(?=^[\s─]*?(?:Worked\s+for\s+\d|─{25,}))", RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.RightToLeft)]
	private static partial Regex LastMessageRx();

	/// <inheritdoc />
	protected override (int Index, string Description) FindInputRequest(string scope)
	{
		ArgumentNullException.ThrowIfNull(scope);
		var (index, description) = LastMatchIndex(scope, InputRequestedRx());

		// A structured question counts once any prompt glyph follows it. Checking the glyph
		// separately keeps the search anchored on the rare header: a right-to-left pattern that
		// ends in a glyph is retried from every '>' on screen, which is quadratic on code output.
		Match? question = null;
		for (var match = QuestionHeaderRx().Match(scope); match.Success; match = match.NextMatch())
		{
			if (scope.AsSpan(match.Index + match.Length).IndexOfAny(PromptCharacters) >= 0)
			{
				question = match;
			}
		}

		return question is not null && question.Index > index
			? (question.Index, question.Groups["descr"].Value.TrimEnd())
			: (index, description);
	}

	[GeneratedRegex(@"•\s+(?<descr>[^\r\n]*?).*?\?[\s\r\n]*?[>›❯]|Question\s+\d\/\d[^\r\n]*?[\r\n\s]+(?<descr>[^\r\n]*?).*?enter to submit answer", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.RightToLeft)]
	private static partial Regex InputRequestedRx();

	[GeneratedRegex(@"Question\s+\d\/\d[^\r\n]*[\r\n]\s*(?<descr>[^\r\n]*)", RegexOptions.IgnoreCase)]
	private static partial Regex QuestionHeaderRx();

}
