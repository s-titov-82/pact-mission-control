namespace Pact.Core.Boards;

/// <summary>Pure board transitions, independent of UI, sessions and persistence.</summary>
public static class BoardRules
{
	/// <summary>Applies an atomic command or returns the unchanged state with its rejection.</summary>
	public static BoardTransition Apply(BoardState state, BoardCommand command, BoardActor actor, DateTimeOffset now)
	{
		ArgumentNullException.ThrowIfNull(state);
		ArgumentNullException.ThrowIfNull(command);
		ArgumentNullException.ThrowIfNull(actor);
		if (command is CreateCard create)
		{
			if (state.Cards.Count(c => !c.IsArchived) >= BoardLimits.ActiveCards)
			{
				return Reject(BoardMutationOutcome.InvalidArgument);
			}

			var created = new BoardCard
			{
				Id = Guid.NewGuid().ToString("N"),
				Number = state.NextCardNumber,
				Title = create.Title.Trim(),
				Description = create.Description,
				JiraUrl = NormalizeUrl(create.JiraUrl),
				CreatedAt = now,
				UpdatedAt = now
			};
			if (!ValidFields(created))
			{
				return Reject(BoardMutationOutcome.InvalidArgument);
			}

			var createdCards = state.Cards.ToList();
			InsertAtEnd(createdCards, created);
			return new(BoardMutationOutcome.Applied, state with
			{
				NextCardNumber = checked(state.NextCardNumber + 1),
				OrderVersion = checked(state.OrderVersion + 1),
				Cards = createdCards.AsReadOnly()
			}, created.Id);
		}

		if (command is TakeNext take)
		{
			if (take.ExpectedOrderRevision != BoardRevisions.ForOrder(state))
			{
				return Reject(BoardMutationOutcome.Conflict);
			}

			if (string.IsNullOrWhiteSpace(actor.SessionId))
			{
				return Reject(BoardMutationOutcome.InvalidArgument);
			}

			var ready = state.Cards.FirstOrDefault(c => !c.IsArchived && c.Status == BoardCardStatus.Ready);
			if (ready is null)
			{
				return Reject(BoardMutationOutcome.Empty);
			}

			return Apply(state, new UpdateCard(ready.Id, BoardRevisions.ForCard(ready), new()
			{
				Status = new(BoardCardStatus.InProgress),
				AssignedSessionId = new(actor.SessionId),
				AssignedSessionName = new(actor.DisplayName)
			}), actor, now);
		}

		var cardId = command switch
		{
			UpdateCard update => update.CardId,
			AddComment add => add.CardId,
			DeleteComment delete => delete.CardId,
			SetArchived archive => archive.CardId,
			_ => null
		};
		var card = state.Cards.FirstOrDefault(c => c.Id == cardId);
		if (card is null)
		{
			return Reject(BoardMutationOutcome.NotFound);
		}

		if (card.IsArchived && command is not SetArchived)
		{
			return Reject(BoardMutationOutcome.Archived);
		}

		var cards = state.Cards.ToList();
		var changed = card;
		string? commentId = null;
		bool reposition = false;
		string? before = null;
		switch (command)
		{
			case UpdateCard update:
				if (update.ExpectedCardRevision != BoardRevisions.ForCard(card))
				{
					return Reject(BoardMutationOutcome.Conflict);
				}

				if (update.Move && update.ExpectedOrderRevision != BoardRevisions.ForOrder(state))
				{
					return Reject(BoardMutationOutcome.Conflict);
				}

				changed = card with
				{
					Title = update.Patch.Title?.Value.Trim() ?? card.Title,
					Description = update.Patch.Description?.Value ?? card.Description,
					JiraUrl = update.Patch.JiraUrl is { } url ? NormalizeUrl(url.Value) : card.JiraUrl,
					Status = update.Patch.Status?.Value ?? card.Status,
					AssignedSessionId = update.Patch.AssignedSessionId is { } assigned ? assigned.Value : card.AssignedSessionId,
					AssignedSessionName = update.Patch.AssignedSessionName is { } name ? name.Value : card.AssignedSessionName
				};
				if (!ValidFields(changed))
				{
					return Reject(BoardMutationOutcome.InvalidArgument);
				}

				if (update.Move && update.BeforeCardId is { } anchor)
				{
					if (anchor == card.Id || !cards.Any(c => c.Id == anchor && !c.IsArchived && c.Status == changed.Status))
					{
						return Reject(BoardMutationOutcome.InvalidArgument);
					}

					before = anchor;
				}
				reposition = update.Move || card.Status != changed.Status;
				break;
			case SetArchived archive:
				if (archive.ExpectedCardRevision != BoardRevisions.ForCard(card))
				{
					return Reject(BoardMutationOutcome.Conflict);
				}

				if (card.Status != BoardCardStatus.Done)
				{
					return Reject(BoardMutationOutcome.InvalidArgument);
				}

				if (!archive.Archived && card.IsArchived && cards.Count(c => !c.IsArchived) >= BoardLimits.ActiveCards)
				{
					return Reject(BoardMutationOutcome.InvalidArgument);
				}

				changed = card with { IsArchived = archive.Archived };
				reposition = card.IsArchived != archive.Archived;
				break;
			case AddComment add:
				if (string.IsNullOrWhiteSpace(add.Text) || add.Text.Length > BoardLimits.Comment)
				{
					return Reject(BoardMutationOutcome.InvalidArgument);
				}

				commentId = Guid.NewGuid().ToString("N");
				var comments = card.Comments.ToList();
				comments.Add(new(commentId, card.NextCommentNumber, actor.SessionId, actor.DisplayName, now, add.Text));
				changed = card with { Comments = comments.AsReadOnly(), NextCommentNumber = checked(card.NextCommentNumber + 1) };
				break;
			case DeleteComment delete:
				if (!card.Comments.Any(c => c.Id == delete.CommentId))
				{
					return Reject(BoardMutationOutcome.NotFound);
				}

				commentId = delete.CommentId;
				changed = card with { Comments = card.Comments.Where(c => c.Id != delete.CommentId).ToList().AsReadOnly() };
				break;
		}

		if (BoardRevisions.ForCard(changed) != BoardRevisions.ForCard(card))
		{
			changed = changed with { FieldsVersion = checked(card.FieldsVersion + 1) };
		}

		if (changed != card)
		{
			changed = changed with { UpdatedAt = now };
		}

		if (reposition)
		{
			cards.Remove(card);
			if (before is not null)
			{
				cards.Insert(cards.FindIndex(c => c.Id == before), changed);
			}
			else
			{
				InsertAtEnd(cards, changed);
			}
		}
		else
		{
			cards[cards.IndexOf(card)] = changed;
		}

		var result = state with { Cards = cards.AsReadOnly() };
		if (BoardRevisions.ForOrder(result) != BoardRevisions.ForOrder(state))
		{
			result = result with { OrderVersion = checked(state.OrderVersion + 1) };
		}

		return new(BoardMutationOutcome.Applied, result, card.Id, commentId);

		BoardTransition Reject(BoardMutationOutcome outcome)
		{
			return new(outcome, state);
		}
	}

	private static void InsertAtEnd(List<BoardCard> cards, BoardCard card)
	{
		var last = cards.FindLastIndex(c => c.Status == card.Status && c.IsArchived == card.IsArchived);
		cards.Insert(last < 0 ? cards.Count : last + 1, card);
	}

	private static string? NormalizeUrl(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

	/// <summary>Validates bounded user-editable fields, including persisted input.</summary>
	public static bool ValidFields(BoardCard card)
	{
		ArgumentNullException.ThrowIfNull(card);
		return !string.IsNullOrWhiteSpace(card.Title) && card.Title.Length <= BoardLimits.Title
			&& card.Description is not null && card.Description.Length <= BoardLimits.Description
			&& Enum.IsDefined(card.Status)
			&& (card.JiraUrl is null || (card.JiraUrl.Length <= BoardLimits.JiraUrl
				&& Uri.TryCreate(card.JiraUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"));
	}
}