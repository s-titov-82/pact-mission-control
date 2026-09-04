using System.Collections.Concurrent;
using Pact.Core.Projects;

namespace Pact.Infrastructure.Storage;

/// <summary>
/// Stores each project's notes as a Markdown file under <c>Settings/Notes</c>, named by
/// <see cref="ProjectNoteFileKey"/>. Access per file is serialized so an append cannot
/// interleave with a save and lose text.
/// </summary>
public sealed class ProjectNotesStore : IProjectNotesStore
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
	private readonly AppPaths _paths;

	/// <summary>
	/// Creates a notes store over the given path layout.
	/// </summary>
	public ProjectNotesStore(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);
		_paths = paths;
	}

	/// <inheritdoc />
	public async Task<string> LoadAsync(string projectRootPath, CancellationToken cancellationToken)
	{
		var path = NotePath(projectRootPath);
		var gate = GetLock(path);
		await gate.WaitAsync(cancellationToken);
		try
		{ return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty; }
		finally { gate.Release(); }
	}

	/// <inheritdoc />
	public async Task SaveAsync(string projectRootPath, string text, CancellationToken cancellationToken)
	{
		var path = NotePath(projectRootPath);
		var gate = GetLock(path);
		await gate.WaitAsync(cancellationToken);
		try
		{ await AtomicFileWriter.WriteTextAsync(path, text, _paths.AtomicTempDirectory, cancellationToken); }
		finally { gate.Release(); }
	}

	/// <inheritdoc />
	/// <remarks>Blank or whitespace-only text is ignored rather than appended as an empty block.</remarks>
	public async Task AppendAsync(string projectRootPath, string text, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}

		var path = NotePath(projectRootPath);
		var gate = GetLock(path);
		await gate.WaitAsync(cancellationToken);
		try
		{
			var existing = File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : string.Empty;
			await AtomicFileWriter.WriteTextAsync(
				path,
				AppendWithSeparation(existing, text),
				_paths.AtomicTempDirectory,
				cancellationToken);
		}
		finally { gate.Release(); }
	}

	/// <summary>
	/// Joins appended text onto existing notes with exactly one blank line between them and a
	/// single trailing newline, so repeated appends cannot accumulate blank lines.
	/// </summary>
	public static string AppendWithSeparation(string existing, string text)
	{
		ArgumentNullException.ThrowIfNull(existing);
		ArgumentNullException.ThrowIfNull(text);

		var trimmedText = text.Trim('\r', '\n');
		return existing.Length == 0 ? trimmedText + "\n" : existing.TrimEnd('\r', '\n') + "\n\n" + trimmedText + "\n";
	}

	private string NotePath(string projectRootPath) =>
		Path.Combine(_paths.NotesDirectory, ProjectNoteFileKey.FromRootPath(projectRootPath) + ".md");

	private static SemaphoreSlim GetLock(string path) => Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
}