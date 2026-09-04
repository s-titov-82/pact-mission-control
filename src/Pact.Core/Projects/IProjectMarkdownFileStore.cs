namespace Pact.Core.Projects;

/// <summary>
/// Reads and conditionally writes Markdown files that belong to a project checkout.
/// </summary>
public interface IProjectMarkdownFileStore
{
	/// <summary>Reads the current text and a revision token used for conflict detection.</summary>
	Task<ProjectMarkdownFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken);

	/// <summary>
	/// Writes only when the on-disk revision still matches <paramref name="expectedRevision"/>.
	/// </summary>
	Task<ProjectMarkdownSaveResult> TrySaveAsync(
		string path,
		string text,
		string expectedRevision,
		CancellationToken cancellationToken);

	/// <summary>Replaces the current file regardless of its revision.</summary>
	Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
		string path,
		string text,
		CancellationToken cancellationToken);
}

/// <summary>Captures the file contents and the revision from which they were read.</summary>
public sealed record ProjectMarkdownFileSnapshot(bool Exists, string Text, string Revision);

/// <summary>Reports whether a conditional save succeeded and the latest disk snapshot.</summary>
public sealed record ProjectMarkdownSaveResult(bool Saved, ProjectMarkdownFileSnapshot Snapshot);