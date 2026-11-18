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

	private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}
