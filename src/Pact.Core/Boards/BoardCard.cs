namespace Pact.Core.Boards;

/// <summary>Explicit workflow state; terminal activity never changes it.</summary>
public enum BoardCardStatus
{
	/// <summary>Awaiting analysis.</summary>
	Backlog,
	/// <summary>Eligible for the next-card operation.</summary>
	Ready,
	/// <summary>Being implemented.</summary>
	InProgress,
	/// <summary>Awaiting verification or release.</summary>
	Verification,
	/// <summary>All closing conditions have been met.</summary>
	Done
}

/// <summary>One independent task, retaining its identity when moved or archived.</summary>
public sealed record BoardCard
{
	/// <summary>Stable opaque identity.</summary>
	public string Id { get; init; } = string.Empty;
	/// <summary>Stable project-local display number.</summary>
	public long Number { get; init; }
	/// <summary>Short task label.</summary>
	public string Title { get; init; } = string.Empty;
	/// <summary>Markdown brief including the closing condition.</summary>
	public string Description { get; init; } = string.Empty;
	/// <summary>Optional HTTP(S) issue address, never fetched automatically.</summary>
	public string? JiraUrl { get; init; }
	/// <summary>Explicit workflow state.</summary>
	public BoardCardStatus Status { get; init; }
	/// <summary>Optional session reference, not a reservation.</summary>
	public string? AssignedSessionId { get; init; }
	/// <summary>Retained label when the assigned session no longer exists.</summary>
	public string? AssignedSessionName { get; init; }
	/// <summary>Archived cards are excluded from the execution queue.</summary>
	public bool IsArchived { get; init; }
	/// <summary>Monotonic version of editable fields, excluding comments and order.</summary>
	public long FieldsVersion { get; init; }
	/// <summary>Creation time in UTC.</summary>
	public DateTimeOffset CreatedAt { get; init; }
	/// <summary>Last successful card or comment modification.</summary>
	public DateTimeOffset UpdatedAt { get; init; }
	/// <summary>Next comment number, not reused after deletion.</summary>
	public long NextCommentNumber { get; init; } = 1;
	/// <summary>Chronological discussion.</summary>
	public IReadOnlyList<BoardComment> Comments { get; init; } = [];
}
