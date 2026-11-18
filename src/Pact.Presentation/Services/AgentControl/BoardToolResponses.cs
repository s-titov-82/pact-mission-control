using Pact.Core.Boards;

namespace Pact.Presentation.Services.AgentControl;

internal static class BoardToolResponses
{
	public static object Summary(BoardCard card) => new
	{
		card.Id,
		card.Number,
		card.Title,
		Status = card.Status.ToString(),
		card.AssignedSessionId,
		card.IsArchived,
		CommentCount = card.Comments.Count,
		CardRevision = BoardRevisions.ForCard(card)
	};
	public static object Mutation(BoardMutationResult result) => new
	{
		Outcome = result.Outcome.ToString(),
		result.CardId,
		result.CommentId,
		result.Snapshot.OrderRevision,
		Card = result.CardId is { } id && result.Snapshot.State.Cards.FirstOrDefault(card => card.Id == id) is { } card ? Summary(card) : null
	};
	public static object Read(BoardSnapshot snapshot, BoardToolRequest request)
	{
		if (request.CardId is { } id)
		{
			var card = snapshot.State.Cards.FirstOrDefault(card => card.Id == id)
				?? throw new KeyNotFoundException("Card not found.");
			var comments = card.Comments.Where(comment => request.BeforeNumber is null || comment.Number < request.BeforeNumber)
				.OrderByDescending(comment => comment.Number).Take(request.Limit + 1).ToArray();
			var page = comments.Take(request.Limit).Reverse().ToArray();
			return new
			{
				Card = Summary(card),
				card.Description,
				card.JiraUrl,
				AssignedSessionName = Label(card.AssignedSessionName),
				card.CreatedAt,
				card.UpdatedAt,
				snapshot.OrderRevision,
				Comments = page.Select(comment => new
				{
					comment.Id,
					comment.Number,
					comment.AuthorSessionId,
					AuthorName = Label(comment.AuthorName),
					comment.CreatedAt,
					comment.Text
				}),
				NextBeforeNumber = comments.Length > request.Limit ? page[0].Number : (long?)null
			};
		}
		var cards = snapshot.State.Cards.Where(card => card.IsArchived == request.Archived
			&& (request.Status is null || card.Status == request.Status));
		if (request.Archived)
		{
			var page = cards.Where(card => request.AfterNumber is null || card.Number > request.AfterNumber)
				.OrderBy(card => card.Number).Take(request.Limit + 1).ToArray();
			return new
			{
				Cards = page.Take(request.Limit).Select(Summary),
				snapshot.OrderRevision,
				NextAfterNumber = page.Length > request.Limit ? page[request.Limit - 1].Number : (long?)null
			};
		}
		return new { Cards = cards.Select(Summary), snapshot.OrderRevision };
	}
	private static string? Label(string? text) => text is { Length: > 128 } ? text[..128] : text;
}