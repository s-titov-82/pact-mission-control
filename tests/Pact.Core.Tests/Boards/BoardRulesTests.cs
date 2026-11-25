using Pact.Core.Boards;

namespace Pact.Core.Tests.Boards;

public sealed class BoardRulesTests
{
	private static readonly BoardActor Actor = new("session-a", "Agent A");
	private static readonly DateTimeOffset Now = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

	[Test]
	public void Pure_move_ignores_description_revision_but_combined_edit_does_not()
	{
		var initial = ReadyPair();
		var token = BoardRevisions.ForCard(initial.Cards[1]);
		var changed = Apply(initial, new UpdateCard("b", token, new() { Description = new("New brief") })).State;
		var moved = Apply(changed, new UpdateCard("b", token, new(), "a", BoardRevisions.ForOrder(initial), true));
		moved.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		moved.State.Cards[0].Description.ShouldBe("New brief");
		Apply(changed, new UpdateCard("b", token, new() { Title = new("Changed") }, "a", BoardRevisions.ForOrder(initial), true))
			.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
	}

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
	public void Deleting_a_ready_card_removes_its_discussion_and_invalidates_the_queue_token()
	{
		var state = Apply(ReadyPair(), new AddComment("a", "Result")).State;
		var revision = BoardRevisions.ForDeletion(state.Cards[0]);
		var deleted = Apply(state, new DeleteCard("a", revision));
		deleted.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		deleted.State.Cards.ShouldBe([state.Cards[1]]);
		Apply(deleted.State, new TakeNext(BoardRevisions.ForOrder(state))).Outcome.ShouldBe(BoardMutationOutcome.Conflict);
		Apply(deleted.State, new DeleteCard("a", revision)).Outcome.ShouldBe(BoardMutationOutcome.NotFound);
		var created = Apply(deleted.State, new CreateCard("New task", "", null));
		created.State.Cards.Single(card => card.Id == created.CardId).Number.ShouldBe(state.NextCardNumber);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void Delete_rejects_a_revision_before_a_description_or_comment_change(bool changeDescription)
	{
		var state = ReadyPair();
		var card = state.Cards[0];
		var command = new DeleteCard(card.Id, BoardRevisions.ForDeletion(card));
		var changed = Apply(state, changeDescription
			? new UpdateCard(card.Id, BoardRevisions.ForCard(card), new() { Description = new("New brief") })
			: new AddComment(card.Id, "New comment")).State;
		var rejected = Apply(changed, command);
		rejected.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
		rejected.State.ShouldBeSameAs(changed);
	}

	[Test]
	public void Deleting_one_archived_card_preserves_the_queue_and_other_archived_cards()
	{
		var archived = Card("archive", BoardCardStatus.Done) with { Number = 3, IsArchived = true };
		var other = archived with { Id = "other", Number = 4 };
		var active = ReadyPair();
		var state = active with { Cards = [.. active.Cards, archived, other], NextCardNumber = 5 };
		var result = Apply(state, new DeleteCard(archived.Id, BoardRevisions.ForDeletion(archived)));
		result.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		result.State.Cards.ShouldBe([.. active.Cards, other]);
		BoardRevisions.ForOrder(result.State).ShouldBe(BoardRevisions.ForOrder(state));
	}

	[Test]
	public void Archiving_all_done_preserves_order_and_rejects_stale_or_empty_batches()
	{
		var active = ReadyPair();
		var existingArchive = Card("archive", BoardCardStatus.Done) with { Number = 3, IsArchived = true };
		var secondDone = Card("second-done", BoardCardStatus.Done) with { Number = 4 };
		var firstDone = Card("first-done", BoardCardStatus.Done) with { Number = 5, FieldsVersion = 7, UpdatedAt = DateTimeOffset.UnixEpoch };
		var state = active with
		{
			Cards = [active.Cards[0], existingArchive, firstDone, active.Cards[1], secondDone],
			NextCardNumber = 6,
			OrderVersion = 10
		};
		var command = new ArchiveAllDone(BoardRevisions.ForOrder(state));

		var result = Apply(state, command);

		result.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		result.State.Cards.ShouldBe([
			active.Cards[0], existingArchive, active.Cards[1],
			firstDone with { IsArchived = true, UpdatedAt = Now, FieldsVersion = 8 },
			secondDone with { IsArchived = true, UpdatedAt = Now, FieldsVersion = 1 }
		]);
		result.State.OrderVersion.ShouldBe(11);
		result.State.NextCardNumber.ShouldBe(6);
		Apply(result.State, new SetArchived(firstDone.Id, false, BoardRevisions.ForCard(firstDone))).Outcome
			.ShouldBe(BoardMutationOutcome.Conflict);

		var stale = Apply(result.State, command);
		stale.Outcome.ShouldBe(BoardMutationOutcome.Conflict);
		stale.State.ShouldBeSameAs(result.State);
		var empty = Apply(result.State, new ArchiveAllDone(BoardRevisions.ForOrder(result.State)));
		empty.Outcome.ShouldBe(BoardMutationOutcome.Empty);
		empty.State.ShouldBeSameAs(result.State);
	}

	[Test]
	public void Cleaning_archive_rejects_new_archived_cards_and_preserves_active_order_and_numbering()
	{
		var active = ReadyPair();
		var archived = Card("archive", BoardCardStatus.Done) with { Number = 3, IsArchived = true };
		var done = Card("done", BoardCardStatus.Done) with { Number = 4 };
		var state = active with { Cards = [active.Cards[0], archived, active.Cards[1], done], NextCardNumber = 5 };
		var command = new CleanArchive(BoardRevisions.ForArchive(state));
		var changedArchive = Apply(state, new SetArchived(done.Id, true, BoardRevisions.ForCard(done))).State;
		Apply(changedArchive, command).Outcome.ShouldBe(BoardMutationOutcome.Conflict);

		var changedActive = Apply(state, new AddComment(done.Id, "Keep me")).State;
		var cleaned = Apply(changedActive, command);
		cleaned.Outcome.ShouldBe(BoardMutationOutcome.Applied);
		cleaned.State.Cards.ShouldBe(changedActive.Cards.Where(card => !card.IsArchived));
		cleaned.State.NextCardNumber.ShouldBe(state.NextCardNumber);
		BoardRevisions.ForOrder(cleaned.State).ShouldBe(BoardRevisions.ForOrder(changedActive));
		var empty = Apply(cleaned.State, new CleanArchive(BoardRevisions.ForArchive(cleaned.State)));
		empty.Outcome.ShouldBe(BoardMutationOutcome.Empty);
		empty.State.ShouldBeSameAs(cleaned.State);
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
