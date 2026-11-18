namespace Pact.Core.Boards;

/// <summary>A project's complete durable board; array order determines order inside each status.</summary>
public sealed record BoardState
{
	/// <summary>The supported storage schema.</summary>
	public int SchemaVersion { get; init; } = 1;
	/// <summary>Next project-local card number, never reused.</summary>
	public long NextCardNumber { get; init; } = 1;
	/// <summary>Changes whenever the active queue changes, including an ABA change.</summary>
	public long OrderVersion { get; init; }
	/// <summary>Cards in durable order, including the archive.</summary>
	public IReadOnlyList<BoardCard> Cards { get; init; } = [];
	/// <summary>An absent file is an empty board.</summary>
	public static BoardState Empty { get; } = new();
}

/// <summary>Immutable disk snapshot with separate persistence and ordering tokens.</summary>
public sealed record BoardSnapshot(BoardState State, string DiskRevision, string OrderRevision);

/// <summary>Identity supplied by the application, never by MCP arguments.</summary>
public sealed record BoardActor(string? SessionId, string DisplayName);
