using Pact.Core.Reviews;

namespace Pact.Core.Tests.Reviews;

public sealed class ReviewConversationTests
{
	private const string Author = "author-session";
	private const string Reviewer = "reviewer-session";

	[Test]
	public void First_turn_is_the_reviewers()
	{
		var conversation = Create();

		var turn = conversation.Start();

		turn.ShouldBe(new ReviewTurn("r1-1", ReviewRole.Reviewer, 1));
		conversation.CurrentTurn.ShouldBe(turn);
		conversation.State.ShouldBe(ReviewState.Running);
	}

	[Test]
	public void Reviewer_findings_hand_the_turn_to_the_author()
	{
		var conversation = Create();
		var first = conversation.Start();

		var outcome = conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		outcome.Accepted.ShouldBeTrue();
		outcome.Repeated.ShouldBeFalse();
		outcome.State.ShouldBe(ReviewState.Running);
		conversation.CurrentTurn.ShouldBe(new ReviewTurn("r1-2", ReviewRole.Author, 1));
		conversation.LastText(ReviewRole.Reviewer).ShouldBe("findings");
	}

	[Test]
	public void Author_answer_hands_the_next_iteration_to_the_reviewer()
	{
		var conversation = Create(maxIterations: 3);
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		conversation.Submit(Author, "r1-2", "fixed", approved: false).Accepted.ShouldBeTrue();

		conversation.CurrentTurn.ShouldBe(new ReviewTurn("r1-3", ReviewRole.Reviewer, 2));
		conversation.LastText(ReviewRole.Author).ShouldBe("fixed");
	}

	[Test]
	public void The_last_answer_is_remembered_with_its_side_and_iteration()
	{
		var conversation = Create(maxIterations: 1);
		conversation.LastAnswer.ShouldBeNull();
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		conversation.Submit(Author, "r1-2", "fixed", approved: false);

		conversation.State.ShouldBe(ReviewState.MaxIterationsReached);
		conversation.LastAnswer.ShouldBe(new ReviewAnswer(ReviewRole.Author, 1, "fixed"));
	}

	[Test]
	public void Reviewer_approval_completes_the_review()
	{
		var conversation = Create();
		var first = conversation.Start();

		var outcome = conversation.Submit(Reviewer, first.TurnId, "looks good", approved: true);

		outcome.Accepted.ShouldBeTrue();
		outcome.State.ShouldBe(ReviewState.Completed);
		conversation.State.ShouldBe(ReviewState.Completed);
		conversation.CurrentTurn.ShouldBeNull();
		conversation.LastText(ReviewRole.Reviewer).ShouldBe("looks good");
	}

	[Test]
	public void Author_answer_at_the_limit_ends_at_max_iterations()
	{
		var conversation = Create(maxIterations: 1);
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		var outcome = conversation.Submit(Author, "r1-2", "last answer", approved: false);

		outcome.Accepted.ShouldBeTrue();
		outcome.State.ShouldBe(ReviewState.MaxIterationsReached);
		conversation.State.ShouldBe(ReviewState.MaxIterationsReached);
		conversation.CurrentTurn.ShouldBeNull();
		conversation.LastText(ReviewRole.Author).ShouldBe("last answer");
	}

	[Test]
	public void Author_cannot_approve()
	{
		var conversation = Create();
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		var outcome = conversation.Submit(Author, "r1-2", "done", approved: true);

		outcome.Accepted.ShouldBeFalse();
		outcome.ErrorCode.ShouldBe("invalid-argument");
		conversation.CurrentTurn!.TurnId.ShouldBe("r1-2");
	}

	[Test]
	public void Submitting_out_of_turn_is_refused()
	{
		var conversation = Create();
		var first = conversation.Start();

		var outcome = conversation.Submit(Author, first.TurnId, "too early", approved: false);

		outcome.Accepted.ShouldBeFalse();
		outcome.ErrorCode.ShouldBe("not-your-turn");
		outcome.ErrorMessage.ShouldNotBeNull().ShouldContain("Running");
		conversation.CurrentTurn.ShouldBe(first);
	}

	[Test]
	public void A_blank_answer_is_refused()
	{
		var conversation = Create();
		var first = conversation.Start();

		var outcome = conversation.Submit(Reviewer, first.TurnId, "  ", approved: false);

		outcome.ErrorCode.ShouldBe("invalid-argument");
		conversation.CurrentTurn.ShouldBe(first);
	}

	[Test]
	public void A_stranger_cannot_submit()
	{
		var conversation = Create();
		var first = conversation.Start();

		var outcome = conversation.Submit("someone-else", first.TurnId, "hi", approved: false);

		outcome.ErrorCode.ShouldBe("not-a-participant");
		conversation.IsParticipant("someone-else").ShouldBeFalse();
	}

	[Test]
	public void A_retried_accepted_turn_is_repeated_not_relayed()
	{
		var conversation = Create();
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		var outcome = conversation.Submit(Reviewer, first.TurnId, "findings", approved: false);

		outcome.Accepted.ShouldBeTrue();
		outcome.Repeated.ShouldBeTrue();
		conversation.CurrentTurn.ShouldBe(new ReviewTurn("r1-2", ReviewRole.Author, 1));
	}

	[Test]
	public void Repeating_the_final_approval_returns_its_stored_result()
	{
		var conversation = Create();
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "looks good", approved: true);

		var outcome = conversation.Submit(Reviewer, first.TurnId, "looks good", approved: true);

		outcome.Accepted.ShouldBeTrue();
		outcome.Repeated.ShouldBeTrue();
		outcome.State.ShouldBe(ReviewState.Completed);
	}

	[Test]
	public void A_new_answer_after_the_end_is_refused()
	{
		var conversation = Create();
		var first = conversation.Start();
		conversation.Submit(Reviewer, first.TurnId, "looks good", approved: true);

		var outcome = conversation.Submit(Author, "r1-2", "more", approved: false);

		outcome.Accepted.ShouldBeFalse();
		outcome.ErrorCode.ShouldBe("review-ended");
		outcome.ErrorMessage.ShouldNotBeNull().ShouldContain("Completed");
	}

	[Test]
	public void Ending_from_outside_is_final()
	{
		var conversation = Create();
		conversation.Start();

		conversation.End(ReviewState.Aborted, "author tab closed");
		conversation.End(ReviewState.Failed, "late failure");

		conversation.State.ShouldBe(ReviewState.Aborted);
		conversation.EndReason.ShouldBe("author tab closed");
		conversation.CurrentTurn.ShouldBeNull();
	}

	[Test]
	public void Roles_follow_the_sessions()
	{
		var conversation = Create();

		conversation.RoleOf(Author).ShouldBe(ReviewRole.Author);
		conversation.RoleOf(Reviewer).ShouldBe(ReviewRole.Reviewer);
		conversation.IsParticipant(Author).ShouldBeTrue();
	}

	private static ReviewConversation Create(int maxIterations = 3) =>
		new("r1", Author, Reviewer, maxIterations);
}
