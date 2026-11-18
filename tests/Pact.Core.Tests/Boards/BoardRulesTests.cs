using Pact.Core.Boards;

namespace Pact.Core.Tests.Boards;

public sealed class BoardRulesTests
{
	private static readonly BoardActor Actor = new("session-a", "Agent A");
	private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public void TakeNext_claims_first_ready_and_reassigns_without_touching_backlog()
	{
		var state = ReadyPair();
		var result = Apply(state, new TakeNext(BoardRevisions.ForOrder(state)));
		result.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		result.CardId.ShouldBe("a");
		result.State.Cards.Single(c => c.Id == "a").AssignedSessionId.ShouldBe("session-a");
		result.State.Cards.Single(c => c.Id == "a").Status.ShouldBe(BoardCardStatus.InProgress);
		result.State.Cards.Single(c => c.Id == "b").Status.ShouldBe(BoardCardStatus.Ready);
	}

	[Test]
	public void TakeNext_empty_does_not_change_state()
	{
		var state = BoardState.Empty;
		var result = Apply(state, new TakeNext(BoardRevisions.ForOrder(state)));
		result.Outcome.ShouldBe(BoardMutationOutcome.Empty);
		result.State.ShouldBeSameAs(state);
	}

	[Test]
	public void TakeNext_stale_revision_does_not_claim_second_card()
	{
		var state = ReadyPair();
		var token = BoardRevisions.ForOrder(state);
		var first = Apply(state, new TakeNext(token));
		var second = Apply(first.State, new TakeNext(token));
		second.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
		second.State.Cards.Single(c => c.Id == "b").Status.ShouldBe(BoardCardStatus.Ready);
	}

	[Test]
	public void Comment_does_not_invalidate_card_or_order_and_can_be_deleted()
	{
		var state = ReadyPair();
		var cardToken = BoardRevisions.ForCard(state.Cards[0]);
		var orderToken = BoardRevisions.ForOrder(state);
		var added = Apply(state, new AddComment("a", "Review result"));
		added.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		added.State.Cards[0].Comments.Single().Text.ShouldBe("Review result");
		BoardRevisions.ForCard(added.State.Cards[0]).ShouldBe(cardToken);
		BoardRevisions.ForOrder(added.State).ShouldBe(orderToken);
		var deleted = Apply(added.State, new DeleteComment("a", added.CommentId!));
		deleted.State.Cards[0].Comments.ShouldBeEmpty();
		deleted.State.Cards[0].NextCommentNumber.ShouldBe(2);
	}

	[Test]
	public void Order_revision_detects_move_away_and_back()
	{
		var state = ReadyPair();
		var first = Apply(state, new UpdateCard("b", BoardRevisions.ForCard(state.Cards[1]), new(), "a", BoardRevisions.ForOrder(state), true));
		first.State.Cards.Select(c => c.Id).ShouldBe(["b", "a"]);
		var back = Apply(first.State, new UpdateCard("a", BoardRevisions.ForCard(first.State.Cards[1]), new(), "b", BoardRevisions.ForOrder(first.State), true));
		back.State.Cards.Select(c => c.Id).ShouldBe(["a", "b"]);
		BoardRevisions.ForOrder(back.State).ShouldNotBe(BoardRevisions.ForOrder(state));
	}

	[Test]
	public void Archive_only_done_and_restore_at_end()
	{
		var state = ReadyPair();
		Apply(state, new SetArchived("a", true, BoardRevisions.ForCard(state.Cards[0]))).Outcome.ShouldBe(BoardMutationOutcome.InvalidArgument);
		state = state with { Cards = [state.Cards[0] with { Status = BoardCardStatus.Done }, state.Cards[1] with { Status = BoardCardStatus.Done }] };
		var archived = Apply(state, new SetArchived("a", true, BoardRevisions.ForCard(state.Cards[0])));
		archived.State.Cards.Single(c => c.Id == "a").IsArchived.ShouldBeTrue();
		var restored = Apply(archived.State, new SetArchived("a", false, BoardRevisions.ForCard(archived.State.Cards.Single(c => c.Id == "a"))));
		restored.State.Cards.Select(c => c.Id).ShouldBe(["b", "a"]);
	}

	[Test]
	public void Move_invalid_anchor_leaves_whole_state_untouched()
	{
		var state = ReadyPair();
		var patch = new BoardCardPatch { Title = new("Changed") };
		var result = Apply(state, new UpdateCard("a", BoardRevisions.ForCard(state.Cards[0]), patch, "missing", BoardRevisions.ForOrder(state), true));
		result.Outcome.ShouldBe(BoardMutationOutcome.InvalidArgument);
		result.State.ShouldBeSameAs(state);
	}

	[Test]
	public void Limits_reject_growth_but_allow_archive()
	{
		var cards = Enumerable.Range(1, 200).Select(i => Card(i.ToString(System.Globalization.CultureInfo.InvariantCulture), BoardCardStatus.Done) with { Number = i }).ToArray();
		var state = new BoardState { Cards = cards, NextCardNumber = 201 };
		Apply(state, new CreateCard("Too many", "", null)).Outcome.ShouldBe(BoardMutationOutcome.InvalidArgument);
		var archived = Apply(state, new SetArchived(cards[0].Id, true, BoardRevisions.ForCard(cards[0])));
		Apply(archived.State, new CreateCard("Allowed", "", null)).Outcome.ShouldBe(BoardMutationOutcome.Applied);
	}

	[TestCase(0, false)]
	[TestCase(1, true)]
	[TestCase(256, true)]
	[TestCase(257, false)]
	public void Title_length_is_validated_before_creation(int length, bool accepted)
	{
		var result = Apply(BoardState.Empty, new CreateCard(new string('x', length), "", null));
		(result.Outcome == BoardMutationOutcome.Applied).ShouldBe(accepted);
	}

	[TestCase(BoardCardStatus.Backlog)]
	[TestCase(BoardCardStatus.Ready)]
	[TestCase(BoardCardStatus.InProgress)]
	[TestCase(BoardCardStatus.Verification)]
	[TestCase(BoardCardStatus.Done)]
	public void Every_status_can_be_explicitly_selected(BoardCardStatus status)
	{
		var state = ReadyPair();
		var result = Apply(state, new UpdateCard("a", BoardRevisions.ForCard(state.Cards[0]), new() { Status = new(status) }));
		result.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		result.State.Cards.Single(c => c.Id == "a").Status.ShouldBe(status);
	}

	private static BoardTransition Apply(BoardState state, BoardCommand command) => BoardRules.Apply(state, command, Actor, Now);
	private static BoardState ReadyPair() => new() { Cards = [Card("a", BoardCardStatus.Ready), Card("b", BoardCardStatus.Ready) with { Number = 2 }], NextCardNumber = 3 };
	private static BoardCard Card(string id, BoardCardStatus status) => new() { Id = id, Number = 1, Title = id, Status = status, AssignedSessionId = "old-session" };
}
