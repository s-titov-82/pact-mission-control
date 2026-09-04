namespace Pact.Core.Projects;

/// <summary>
/// Persistence for <c>Settings/projects.json</c>.
/// </summary>
public interface IProjectStore
{
	/// <summary>
	/// Reads the document, returning <see cref="ProjectsDocument.CreateDefault"/> when the file
	/// is missing or cannot be parsed, so a corrupt file never blocks startup.
	/// </summary>
	Task<ProjectsDocument> LoadAsync(CancellationToken cancellationToken);

	/// <summary>
	/// Writes the document atomically, so a crash mid-write cannot leave a truncated file.
	/// </summary>
	Task SaveAsync(ProjectsDocument document, CancellationToken cancellationToken);

	/// <summary>
	/// Applies <paramref name="update"/> to the current document and persists the result as one
	/// serialized read-modify-write, which is the only safe way to mutate state from concurrent
	/// callers.
	/// </summary>
	/// <param name="update">
	/// Pure transform returning the new document. It may be invoked while the store is locked, so
	/// it must not perform I/O or call back into the store.
	/// </param>
	/// <param name="cancellationToken">Cancellation token.</param>
	/// <returns>The persisted document.</returns>
	Task<ProjectsDocument> UpdateAsync(
		Func<ProjectsDocument, ProjectsDocument> update,
		CancellationToken cancellationToken);
}