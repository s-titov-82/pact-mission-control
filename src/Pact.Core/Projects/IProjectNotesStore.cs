namespace Pact.Core.Projects;

/// <summary>
/// Reads and writes a project's notes document. Notes live in their own file under
/// <c>Settings/Notes</c>, keyed by <see cref="ProjectNoteFileKey"/>, rather than inside
/// <c>projects.json</c>.
/// </summary>
public interface IProjectNotesStore
{
	/// <summary>
	/// Reads the notes text, returning an empty string when the project has no notes file yet.
	/// </summary>
	Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken);

	/// <summary>
	/// Replaces the notes text, creating the file if needed. Writes are atomic, so an
	/// interrupted save cannot truncate existing notes.
	/// </summary>
	Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken);

	/// <summary>
	/// Appends <paramref name="text"/> to the end of the notes, preserving existing content.
	/// Used by "send selection to notes" so a capture never overwrites earlier notes.
	/// </summary>
	Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken);
}