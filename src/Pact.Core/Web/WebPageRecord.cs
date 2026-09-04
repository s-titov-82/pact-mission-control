namespace Pact.Core.Web;

/// <summary>
/// One saved browser tab nested under a project in <c>projects.json</c>.
/// </summary>
/// <param name="Id">Stable key; may be the project's active item.</param>
/// <param name="Title">Tab label, refreshed from the page title as the user navigates.</param>
/// <param name="StartUrl">Address the tab was created with; kept so the tab can be reset.</param>
/// <param name="ResumeUrl">
/// Address to reopen at, tracking the user's navigation. Equals <paramref name="StartUrl"/>
/// until the user navigates away.
/// </param>
/// <param name="CreatedAt">When the tab was opened.</param>
/// <param name="LastActiveAt">Last interaction, used for ordering.</param>
public sealed record WebPageRecord(
	string Id,
	string Title,
	string StartUrl,
	string ResumeUrl,
	DateTimeOffset CreatedAt,
	DateTimeOffset LastActiveAt);