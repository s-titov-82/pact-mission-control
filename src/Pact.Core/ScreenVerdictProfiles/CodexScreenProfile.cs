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
			return base.DescribeTrustRequest(screen) is null ? null : "Folder access selection";
		}

		// Codex also shows this menu when changing folders. Enter is safe for the
		// startup readiness flow only on its Quit variant with the affirmative option selected.
		var startup = menu.Groups["trust"].Success && !menu.Groups["cancelSelected"].Success
			&& menu.Groups["cancel"].Value == "Quit"
			&& menu.Groups["footer"].Value.EndsWith("· esc quit", StringComparison.Ordinal)
			&& screen.AsSpan(0, menu.Index).TrimEnd().Contains(
				"Trust this folder? Codex can read, edit, and run files here,", StringComparison.Ordinal);
		return startup
			? TrustPromptDescription
			: "Folder access selection";
	}

	[GeneratedRegex(
		@"^[ \t]*(?<trust>›)?[ \t]*1\. Trust and continue[ \t]*\r?\n"
		+ @"[ \t]*(?<cancelSelected>›)?[ \t]*2\. (?<cancel>[^\r\n]*?)[ \t]*\r?\n"
		+ @"\s*(?<footer>enter continue[^\r\n]*?)[ \t]*\s*\z",
		RegexOptions.Multiline)]
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

	/// <inheritdoc />
	private protected override string? DescribeCurrentInputRequest(string screen)
	{
		// A long editable queue can place its header well above the transcript search window.
		const int maxQueueCharacters = 16_384;
		var queueScope = screen.Length <= maxQueueCharacters ? screen : screen[^maxQueueCharacters..];
		var queued = QueuedQuestionRx().Match(queueScope);
		return queued.Success ? queued.Groups["descr"].Value : null;
	}

	// Async questions can remain collapsed above the composer while Codex is working.
	// Match the live queue and its answer hint next to the composer. The working line can
	// appear after the queue while the answer is still pending.
	[GeneratedRegex(@"^[ \t]*(?:•[ \t]+)?Queued follow-up inputs[ \t]*\r?\n"
		+ @"(?:[ \t]*↳[^\r\n]*\r?\n)*"
		+ @"[ \t]*\?[ \t]+(?<descr>[1-9]\d* questions?)(?:[ \t]*·[ \t]*|[ \t]*\r?\n[ \t]*|[ \t]+)"
		+ @"[^\r\n]{1,60}?[ \t]+to answer[ \t)]*(?:\s+•[ \t]+Working[ \t]+\([^\r\n]*\))?\s*(?:›\s*)?\z",
		RegexOptions.IgnoreCase | RegexOptions.Multiline)]
	private static partial Regex QueuedQuestionRx();

}
