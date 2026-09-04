namespace Pact.Core.Projects;

/// <summary>
/// Root of <c>Settings/projects.json</c>: the whole persisted cockpit state.
/// </summary>
/// <param name="SchemaVersion">
/// Format version, allowing future migrations to detect older files. The legacy
/// <c>registry.json</c> format is deliberately not migrated.
/// </param>
/// <param name="Projects">All projects, active and paused.</param>
public sealed record ProjectsDocument(
	int SchemaVersion,
	IReadOnlyList<ProjectRecord> Projects)
{
	/// <summary>
	/// Creates the empty document used on first run, or when the file is missing or unreadable.
	/// </summary>
	public static ProjectsDocument CreateDefault()
	{
		return new ProjectsDocument(
			SchemaVersion: 1,
			Projects: []);
	}
}