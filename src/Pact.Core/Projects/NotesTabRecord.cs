namespace Pact.Core.Projects;

/// <summary>
/// Presence of a project's notes tab. The note text itself lives in a separate file under
/// <c>Settings/Notes</c>, so this record only records that the tab is open and where it sits
/// in the ordering.
/// </summary>
/// <param name="Id">Stable key; may be the project's active item.</param>
/// <param name="CreatedAt">When the tab was first shown.</param>
/// <param name="LastActiveAt">Last interaction, used for ordering.</param>
public sealed record NotesTabRecord(
	string Id,
	DateTimeOffset CreatedAt,
	DateTimeOffset LastActiveAt);