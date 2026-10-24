using Pact.Core.Reviews;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.Tests.Services.Reviews;

public sealed class ReviewMessageRendererTests
{
	[Test]
	public void Reviewer_start_message_renders_target_and_protocol()
	{
		ReviewMessageRenderer renderer = new(Definition(start: "Review {target}."), "Strict policy.", "docs/plan.md");

		var message = renderer.Render(ReviewRole.Reviewer, iteration: 1, previousTurn: null);

		message.ShouldStartWith("Review docs/plan.md.");
		message.ShouldContain("pact_submit_review_turn");
		message.ShouldContain("`previousTurn`");
		message.ShouldContain("Completion marker policy:\nStrict policy.");
	}

	[Test]
	public void User_template_with_file_placeholders_still_carries_previous_turn()
	{
		ReviewMessageRenderer renderer = new(
			Definition(firstFeedback: "Read {reviewResultFile}."),
			"Strict policy.",
			"docs/plan.md");

		var message = renderer.Render(ReviewRole.Author, iteration: 1, previousTurn: "1. finding");

		message.ShouldStartWith($"Read {ReviewMessageRenderer.PreviousTurnPhrase}.");
		message.ShouldNotContain(".pact-reviews");
		message.ShouldEndWith(
			"When done, call `pact_submit_review_turn` with this message's `turnId` and your complete "
			+ "answer as `text`; keep your terminal reply to one short line.");
	}

	[Test]
	public void Templates_follow_the_role_and_iteration()
	{
		ReviewMessageRenderer renderer = new(
			Definition(
				start: "start",
				firstFeedback: "first {reviewerOutput}",
				authorReturn: "return {authorOutput}",
				feedback: "later {reviewerOutput}"),
			"policy",
			"target");

		renderer.Render(ReviewRole.Reviewer, 1, null).ShouldStartWith("start");
		renderer.Render(ReviewRole.Author, 1, "findings").ShouldStartWith("first findings");
		renderer.Render(ReviewRole.Reviewer, 2, "answer").ShouldStartWith("return answer");
		renderer.Render(ReviewRole.Author, 2, "more").ShouldStartWith("later more");
	}

	[Test]
	public void Reviewer_message_replaces_the_marker_with_approved()
	{
		ReviewMessageRenderer renderer = new(Definition(), "policy", "target");

		var message = renderer.Render(ReviewRole.Reviewer, iteration: 2, previousTurn: "answer");

		message.ShouldContain("call `pact_submit_review_turn` with `approved: true` instead and do not write the marker.");
		message.ShouldContain("final run summary");
	}

	[Test]
	public void The_policy_block_is_left_out_when_the_start_template_carries_the_instruction()
	{
		ReviewMessageRenderer renderer = new(Definition(start: "Review. {reviewerInstruction}"), "policy text", "target");

		var message = renderer.Render(ReviewRole.Reviewer, iteration: 1, previousTurn: null);

		message.ShouldNotContain("Completion marker policy:");
		message.ShouldStartWith("Review. policy text");
	}

	[Test]
	public void Author_messages_say_nothing_about_approval()
	{
		ReviewMessageRenderer renderer = new(Definition(), "policy", "target");

		renderer.Render(ReviewRole.Author, iteration: 1, previousTurn: "findings").ShouldNotContain("approved");
	}

	[Test]
	public void Shipped_defaults_mention_no_response_files()
	{
		foreach (var definition in ScenarioDefinitionStore.LoadDefaultDefinitions())
		{
			foreach (var text in new[]
			{
				definition.StartPromptTemplate,
				definition.FirstFeedbackTemplate,
				definition.AuthorReturnTemplate,
				definition.FeedbackTemplate
			})
			{
				text.ShouldNotContain("response file");
				text.ShouldNotContain("review file");
				text.ShouldNotContain("read that file");
				text.ShouldNotContain("{reviewResultFile}");
				text.ShouldNotContain("{authorFeedbackFile}");
			}
		}
	}

	private static ScenarioDefinition Definition(
		string start = "Review {target}.",
		string firstFeedback = "Findings: {reviewerOutput}",
		string authorReturn = "Answer: {authorOutput}",
		string feedback = "More: {reviewerOutput}") => new(
		"code-review",
		ScenarioKind.ReviewLoop,
		"Code review",
		MaxIterations: 3,
		StopMarker: "AGENT_TERMINAL_DONE",
		DefaultTarget: string.Empty,
		StartPromptTemplate: start,
		FirstFeedbackTemplate: firstFeedback,
		AuthorReturnTemplate: authorReturn,
		FeedbackTemplate: feedback,
		ReviewerInstructions: [],
		DefaultReviewerInstructionId: string.Empty);
}
