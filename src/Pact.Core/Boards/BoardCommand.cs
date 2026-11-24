namespace Pact.Core.Boards;

/// <summary>Distinguishes an absent patch field from an explicit null.</summary>
public sealed record BoardField<T>(T Value);

/// <summary>Only specified values replace card fields.</summary>
public sealed record BoardCardPatch
{
	/// <summary>Replacement title, when specified.</summary>
	public BoardField<string>? Title { get; init; }
	/// <summary>Replacement Markdown, when specified.</summary>
	public BoardField<string>? Description { get; init; }
	/// <summary>Replacement URL or null to clear.</summary>
	public BoardField<string?>? JiraUrl { get; init; }
	/// <summary>Any explicit workflow state.</summary>
	public BoardField<BoardCardStatus>? Status { get; init; }
	/// <summary>Session reference or null to clear; host validates ownership.</summary>
	public BoardField<string?>? AssignedSessionId { get; init; }
	/// <summary>Host-resolved label, supplied together with the session id.</summary>
	public BoardField<string?>? AssignedSessionName { get; init; }
}

/// <summary>A board mutation validated and saved as one transaction.</summary>
public abstract record BoardCommand;
/// <summary>Creates at the end of Backlog.</summary>
public sealed record CreateCard(string Title, string Description, string? JiraUrl) : BoardCommand;
/// <summary>Replaces specified fields and optionally moves before an active card in the target column.</summary>
public sealed record UpdateCard(string CardId, string ExpectedCardRevision, BoardCardPatch Patch, string? BeforeCardId = null, string? ExpectedOrderRevision = null, bool Move = false) : BoardCommand;
/// <summary>Appends an explicit comment from the trusted actor.</summary>
public sealed record AddComment(string CardId, string Text) : BoardCommand;
/// <summary>Removes a comment without reusing its number.</summary>
public sealed record DeleteComment(string CardId, string CommentId) : BoardCommand;
/// <summary>Archives Done or restores to the end of Done.</summary>
public sealed record SetArchived(string CardId, bool Archived, string ExpectedCardRevision) : BoardCommand;
/// <summary>Permanently removes one active or archived card with its comments.</summary>
public sealed record DeleteCard(string CardId, string ExpectedDeletionRevision) : BoardCommand;
/// <summary>Permanently removes the archive contents covered by the confirmation.</summary>
public sealed record CleanArchive(string ExpectedArchiveRevision) : BoardCommand;
/// <summary>Claims the first Ready card using the observed order token.</summary>
public sealed record TakeNext(string ExpectedOrderRevision) : BoardCommand;

/// <summary>Observable outcomes of an atomic mutation.</summary>
public enum BoardMutationOutcome
{
	/// <summary>The request was applied, possibly as a no-op.</summary>
	Applied,
	/// <summary>The requested collection has no cards.</summary>
	Empty,
	/// <summary>A supplied revision is stale.</summary>
	Conflict,
	/// <summary>The requested entity no longer exists.</summary>
	NotFound,
	/// <summary>The request violates a board constraint.</summary>
	InvalidArgument,
	/// <summary>Restore the card before editing it.</summary>
	Archived
}

/// <summary>Pure transition result; failed transitions retain the original state.</summary>
public sealed record BoardTransition(BoardMutationOutcome Outcome, BoardState State, string? CardId = null, string? CommentId = null);
/// <summary>A transition published only after persistence succeeds.</summary>
public sealed record BoardMutationResult(BoardMutationOutcome Outcome, BoardSnapshot Snapshot, string? CardId = null, string? CommentId = null);
