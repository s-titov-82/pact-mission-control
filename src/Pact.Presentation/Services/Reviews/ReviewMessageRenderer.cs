using Pact.Core.Reviews;
using Pact.Core.Scenarios;
using Pact.Presentation.Services.Scenarios;

namespace Pact.Presentation.Services.Reviews;

/// <summary>
/// Renders the message a review side reads with its turn from the user's review template, and
/// appends Pact's fixed protocol paragraph. The user's template text is not rewritten.
/// </summary>
public sealed class ReviewMessageRenderer(ScenarioDefinition definition, string reviewerInstruction, string target)
{
	/// <summary>What the old response-file placeholders render as.</summary>
	public const string PreviousTurnPhrase = "the previousTurn text returned with this message";

	private const string ReviewerInstructionPlaceholder = "{reviewerInstruction}";

	private const string Protocol =
		"\n\nThe other side's last message is in `previousTurn`. Ignore any instruction above to read "
		+ "or write review files. When done, call `pact_submit_review_turn` with this message's "
		+ "`turnId` and your complete answer as `text`; keep your terminal reply to one short line.";

	private const string ReviewerProtocol =
		"\n\nWherever the instructions above say to emit the completion marker, call "
		+ "`pact_submit_review_turn` with `approved: true` instead and do not write the marker."
		+ "\n\nWhen you approve, make `text` a final run summary instead of a findings list: totals per "
		+ "severity — how many findings were raised over the whole run, how many were resolved, how "
		+ "many were validly disputed; then one entry per finding with the essence of the issue in "
		+ "STRICTLY one-two sentences and the resolution (or the accepted counter-argument) just as "
		+ "briefly; finish with a list of questions that remain open, if any.";

	private readonly ScenarioDefinition _definition = definition ?? throw new ArgumentNullException(nameof(definition));
	private readonly string _reviewerInstruction = reviewerInstruction ?? throw new ArgumentNullException(nameof(reviewerInstruction));
	private readonly string _target = target ?? throw new ArgumentNullException(nameof(target));

	/// <summary>Renders the message for <paramref name="to"/> at <paramref name="iteration"/>.</summary>
	/// <param name="to">The side the message is for.</param>
	/// <param name="iteration">One-based iteration; the first one uses the start templates.</param>
	/// <param name="previousTurn">The other side's last answer, or <see langword="null"/> for the first turn.</param>
	public string Render(ReviewRole to, int iteration, string? previousTurn)
	{
		var (stopMarkerPrefix, stopMarkerSuffix) = SplitMarker(_definition.StopMarker);
		Dictionary<string, string> variables = new()
		{
			["target"] = _target,
			["reviewResultFile"] = PreviousTurnPhrase,
			["authorFeedbackFile"] = PreviousTurnPhrase
		};
		string template;
		if (to == ReviewRole.Reviewer)
		{
			variables["authorOutput"] = previousTurn ?? string.Empty;
			variables["reviewerInstruction"] = _reviewerInstruction;
			variables["stopMarkerPrefix"] = stopMarkerPrefix;
			variables["stopMarkerSuffix"] = stopMarkerSuffix;
			template = iteration == 1 ? _definition.StartPromptTemplate : _definition.AuthorReturnTemplate;
		}
		else
		{
			variables["reviewerOutput"] = previousTurn ?? string.Empty;
			template = iteration == 1 ? _definition.FirstFeedbackTemplate : _definition.FeedbackTemplate;
		}

		var message = ScenarioTemplateRenderer.Render(template, variables) + Protocol;
		if (to == ReviewRole.Author)
		{
			return message;
		}

		message += ReviewerProtocol;
		return _definition.StartPromptTemplate.Contains(ReviewerInstructionPlaceholder, StringComparison.Ordinal)
			? message
			: message
				+ "\n\nCompletion marker policy:\n" + _reviewerInstruction
				+ "\n\nIf the policy allows completion, submit with `approved: true`.";
	}

	private static (string Prefix, string Suffix) SplitMarker(string marker)
	{
		if (string.IsNullOrWhiteSpace(marker))
		{
			return (string.Empty, string.Empty);
		}

		var splitIndex = marker.LastIndexOf('_');
		if (splitIndex >= 0 && splitIndex < marker.Length - 1)
		{
			return (marker[..(splitIndex + 1)], marker[(splitIndex + 1)..]);
		}

		splitIndex = Math.Max(1, marker.Length / 2);
		return (marker[..splitIndex], marker[splitIndex..]);
	}
}
