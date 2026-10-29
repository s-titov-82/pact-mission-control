using Pact.Infrastructure.Files;

namespace Pact.Presentation.ViewModels;

/// <summary>A linked file as the panel over the terminal shows it, read when the panel opened.</summary>
/// <param name="FullPath">The file's full path.</param>
/// <param name="FileName">The file's name.</param>
/// <param name="Text">Its text, or <see langword="null"/> when <paramref name="Placeholder"/> shows instead.</param>
/// <param name="Placeholder">Why there is no text: missing, binary or too large.</param>
/// <param name="IsMarkdown">Whether the text is rendered as Markdown.</param>
/// <param name="ProjectRoot">The project the file lies in, so its links resolve as in Docs; otherwise <see langword="null"/>.</param>
/// <param name="ProjectRelativePath">The file's path in that project, with forward slashes.</param>
public sealed record AgentFileDocument(
	string FullPath,
	string FileName,
	string? Text,
	string? Placeholder,
	bool IsMarkdown,
	string? ProjectRoot,
	string? ProjectRelativePath)
{
	/// <summary>Reads <paramref name="path"/> off the calling thread.</summary>
	/// <param name="path">The linked file.</param>
	/// <param name="projectRoot">Root of the project of the session that linked it, if any.</param>
	/// <param name="cancellationToken">Cancels the read.</param>
	public static async Task<AgentFileDocument> LoadAsync(string path, string? projectRoot, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(path);
		var full = Path.GetFullPath(path);
		var content = await Task.Run(() => TextFileReader.ReadAsync(full, cancellationToken), cancellationToken).ConfigureAwait(false);
		var placeholder = content.State switch
		{
			TextFileState.Text => null,
			TextFileState.Binary => "Binary file, not shown",
			TextFileState.TooLarge => "File is too large to show",
			_ => "File not found"
		};
		string? relative = null;
		if (projectRoot is not null)
		{
			var candidate = Path.GetRelativePath(Path.GetFullPath(projectRoot), full);
			if (!Path.IsPathRooted(candidate) && candidate != ".." && !candidate.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
			{
				relative = candidate.Replace(Path.DirectorySeparatorChar, '/');
			}
		}

		return new AgentFileDocument(
			full,
			Path.GetFileName(full),
			content.Text,
			placeholder,
			content.Text is not null && string.Equals(Path.GetExtension(full), ".md", StringComparison.OrdinalIgnoreCase),
			relative is null ? null : projectRoot,
			relative);
	}
}
