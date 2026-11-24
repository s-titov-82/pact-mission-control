using System.Security.Cryptography;
using System.Text.Json;

namespace Pact.Core.Boards;

/// <summary>Scope-specific hashes avoid conflicts from unrelated comments and cards.</summary>
public static class BoardRevisions
{
	/// <summary>Hashes fields, excluding comments, timestamps and display-only session labels.</summary>
	public static string ForCard(BoardCard card)
	{
		ArgumentNullException.ThrowIfNull(card);
		return Hash(new { card.Id, card.Title, card.Description, card.JiraUrl, card.Status, card.AssignedSessionId, card.IsArchived, card.FieldsVersion });
	}

	/// <summary>Hashes queue membership and order with an ABA-resistant version.</summary>
	public static string ForOrder(BoardState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return Hash(new { state.OrderVersion, Cards = state.Cards.Where(c => !c.IsArchived).Select(c => new { c.Id, c.Status }).ToArray() });
	}

	/// <summary>Protects the fields and discussion that will be removed together.</summary>
	public static string ForDeletion(BoardCard card)
	{
		ArgumentNullException.ThrowIfNull(card);
		return Hash(new { Fields = ForCard(card), card.NextCommentNumber, Comments = card.Comments.Select(comment => comment.Id) });
	}

	/// <summary>Protects the archive contents independently of active board changes.</summary>
	public static string ForArchive(BoardState state)
	{
		ArgumentNullException.ThrowIfNull(state);
		return Hash(state.Cards.Where(card => card.IsArchived).Select(ForDeletion));
	}

	private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
