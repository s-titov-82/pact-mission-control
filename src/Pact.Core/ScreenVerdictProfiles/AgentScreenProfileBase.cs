using System.Text.RegularExpressions;
using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles
{
	/// <summary>
	/// Shared screen-classification algorithm. Subclasses supply only the agent-specific
	/// markers; the anchoring and precedence rules that decide busy versus done live here so
	/// every agent is judged the same way.
	/// </summary>
	public abstract partial class AgentScreenProfileBase : IAgentScreenProfile
	{
		/// <summary>
		/// Characters that mark this agent's idle composer/prompt line. Override
		/// when an agent uses a different glyph (Codex shows no '>' at all).
		/// </summary>
		protected virtual char[] PromptCharacters { get; } = ['>', '›', '❯'];

		/// <summary>
		/// Finds the prompt glyph that anchors the current composer, or -1 when none is visible.
		/// </summary>
		protected virtual int FindPrompt(string window)
		{
			ArgumentNullException.ThrowIfNull(window);
			return window.LastIndexOfAny(PromptCharacters);
		}

		/// <summary>
		/// Markers proving the view is scrolled back. Their presence forces
		/// <see cref="TerminalScreenVerdictState.Unknown"/>, because scrollback shows history rather
		/// than the agent's current state.
		/// </summary>
		protected abstract string[] ScrollMarkers { get; }

		/// <summary>
		/// Markers for the session-picker or resume screen, where the agent waits on the user
		/// and is therefore treated as done rather than working.
		/// </summary>
		protected abstract string[] ResumeSessionMarkers { get; }

		/// <summary>
		/// The text the agent shows when it asks the user to trust the folder.
		/// </summary>
		protected abstract string TrustRequestMarker { get; }

		/// <summary>Matches text shown while the agent is actively working.</summary>
		protected abstract Regex WorkingRegex { get; }

		/// <summary>Matches the completion summary shown after the agent finishes a turn.</summary>
		protected abstract Regex WorkedForRegex { get; }

		/// <summary>Recognizes a screen on which the agent waits for a human answer.</summary>
		protected abstract Regex InputRequestedRegex { get; }

		/// <summary>
		/// Inspects the prompt tail without exposing its text. Profiles without a readable
		/// composer return null.
		/// </summary>
		protected virtual TerminalPromptEvidence? InspectPrompt(string window, int promptAt) => null;

		/// <summary>Matches the notice shown when the user interrupted a turn, which also ends it.</summary>
		protected abstract Regex InterruptedRegex { get; }

		/// <summary>
		/// Matches the agent's most recent message, capturing it in a group named
		/// <c>message</c>. Profiles without a reliable pattern return no message.
		/// </summary>
		protected virtual Regex? LastMessageRegex => null;

		/// <summary>
		/// Description carried by every agent's folder-trust dialog, so callers key off one
		/// constant instead of per-agent wording. Only this description authorizes Pact to
		/// answer a question on the user's behalf.
		/// </summary>
		public const string TrustPromptDescription = "Folder trust request";

		/// <inheritdoc />
		public TerminalScreenVerdict Classify(string screen)
		{
			ArgumentNullException.ThrowIfNull(screen);

			if (ScrollMarkers.Length > 0 && ScrollMarkers.Any(marker => screen.Contains(marker, StringComparison.Ordinal)))
			{
				return new(TerminalScreenVerdictState.Unknown, "Scrolled");
			}

			if (!string.IsNullOrWhiteSpace(TrustRequestMarker) && screen.Contains(TrustRequestMarker, StringComparison.Ordinal))
			{
				return new(
					TerminalScreenVerdictState.InputRequested, TrustPromptDescription);
			}

			if (ResumeSessionMarkers.Length > 0 && ResumeSessionMarkers.Any(marker => screen.Contains(marker, StringComparison.Ordinal)))
			{
				return new(TerminalScreenVerdictState.InputRequested, "Resuming selector");
			}

			var window = screen.Length <= 3000 ? screen : screen[^3000..];
			var promptAt = FindPrompt(window);

			// When the idle composer is visible, only the text right above it
			// counts: the transcript can quote stale busy/done markers from
			// earlier turns, and the marker closest to the prompt wins. When no
			// composer is visible at all (Codex hides it while working), there
			// is no anchor to narrow against, so the whole captured window is
			// searched instead - this is also what a bare marker string with no
			// surrounding chrome (as used by plumbing-level tests) needs.
			var scope = promptAt >= 0
				? window[Math.Max(0, promptAt - 1000)..(promptAt + 1)]
				: window;

			var promptEvidence = promptAt >= 0 ? InspectPrompt(window, promptAt) : null;
			var promptIsEmpty = promptEvidence?.IsEmpty;

			if (string.IsNullOrWhiteSpace(scope))
			{
				return new(
					TerminalScreenVerdictState.Unknown,
					string.Empty,
					string.Empty,
					promptIsEmpty,
					promptEvidence);
			}

			var lastMessage = ExtractLastMessage(scope);
			var (busyAt, busyDescr) = LastMatchIndex(scope, WorkingRegex);
			var (inputRequestAt, inputRequestDescr) = LastMatchIndex(scope, InputRequestedRegex);
			var (workedForAt, workedForDescr) = LastMatchIndex(scope, WorkedForRegex);
			var (interruptedAt, interruptedDescr) = LastMatchIndex(scope, InterruptedRegex);
			var (doneAt, doneDescr) = workedForAt > interruptedAt ? (workedForAt, workedForDescr) : (interruptedAt, interruptedDescr);

			if (busyAt < 0 && doneAt < 0 && inputRequestAt < 0)
			{
				return new(
					TerminalScreenVerdictState.Unknown,
					string.Empty,
					lastMessage,
					promptIsEmpty,
					promptEvidence);
			}

			return busyAt > doneAt && busyAt > inputRequestAt
				? new(TerminalScreenVerdictState.Busy, busyDescr, lastMessage, promptIsEmpty, promptEvidence)
				: inputRequestAt > doneAt
				? new(TerminalScreenVerdictState.InputRequested, inputRequestDescr, lastMessage, promptIsEmpty, promptEvidence)
				: new(
					TerminalScreenVerdictState.Done,
					doneDescr,
					lastMessage,
					promptIsEmpty ?? (promptEvidence is null ? true : null),
					promptEvidence);
		}

		private string ExtractLastMessage(string scope)
		{
			if (LastMessageRegex is not { } regex)
			{
				return string.Empty;
			}

			var match = regex.Match(scope);
			return match.Success ? match.Groups["message"].Value.Trim() : string.Empty;
		}

		private static (int pos, string descr) LastMatchIndex(string text, Regex regex)
		{
			var match = regex.Match(text);
			return match.Success ? (match.Index, match.Groups["descr"].Value) : (-1, string.Empty);
		}
	}
}
