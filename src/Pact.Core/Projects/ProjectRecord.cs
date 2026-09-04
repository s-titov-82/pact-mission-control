using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;

namespace Pact.Core.Projects;

/// <summary>
/// One project in <c>Settings/projects.json</c>, owning its terminal sessions, web pages, and
/// notes tab. The project is the unit of pause/restore, so nested items are stored here rather
/// than in a flat global list.
/// </summary>
/// <param name="Id">Stable key; referenced by <see cref="ActiveItemId"/> and by scenario runs.</param>
/// <param name="Name">Display name, defaulted from the root directory name.</param>
/// <param name="RootPath">Absolute project directory; the working directory for its sessions.</param>
/// <param name="CreatedAt">When the project was added.</param>
/// <param name="LastActiveAt">Last interaction, used to order the project list.</param>
/// <param name="Notes">Free-form notes text, or <see langword="null"/> when never written.</param>
public sealed record ProjectRecord(
	string Id,
	string Name,
	string RootPath,
	DateTimeOffset CreatedAt,
	DateTimeOffset LastActiveAt,
	string? Notes)
{
	/// <summary>Whether the project is open or parked.</summary>
	public WorkspaceStatus Status { get; init; } = WorkspaceStatus.Active;

	/// <summary>
	/// Id of the session, web page, or notes tab to reselect when this project is opened.
	/// <see langword="null"/> when the project has no items or the remembered item is gone;
	/// a stale id is tolerated and falls back to the first available item.
	/// </summary>
	public string? ActiveItemId { get; init; }

	/// <summary>Terminal sessions belonging to this project.</summary>
	public IReadOnlyList<SessionRecord> Sessions { get; init; } = [];

	/// <summary>Web pages belonging to this project.</summary>
	public IReadOnlyList<WebPageRecord> WebPages { get; init; } = [];

	/// <summary>Notes tab state, or <see langword="null"/> when the tab is hidden.</summary>
	public NotesTabRecord? NotesTab { get; init; }

	/// <summary>GitLab project id substituted into web link templates.</summary>
	public string? GitLabRepoId { get; init; }

	/// <summary>TeamCity project id substituted into web link templates.</summary>
	public string? TeamCityProjectId { get; init; }
}