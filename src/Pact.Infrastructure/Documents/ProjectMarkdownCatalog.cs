namespace Pact.Infrastructure.Documents;

/// <summary>
/// Splits project Markdown into the Common group (everything outside <c>docs</c>)
/// and the Docs group (the <c>docs</c> tree, including <c>docs/superpowers</c>).
/// </summary>
public sealed record ProjectMarkdownCatalog(
	IReadOnlyList<ProjectMarkdownFileEntry> Common,
	IReadOnlyList<ProjectMarkdownFileEntry> Docs)
{
	private static readonly HashSet<string> IgnoredDirectoryNames = new(
		[".git", ".worktrees", ".pact-reviews", "bin", "obj", "node_modules"],
		StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Scans the project root once, skipping generated directories and reparse
	/// points, and classifies each Markdown file by whether it lives under <c>docs</c>.
	/// </summary>
	public static ProjectMarkdownCatalog Scan(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		var root = Path.GetFullPath(projectRootPath);
		if (!Directory.Exists(root))
		{
			return new ProjectMarkdownCatalog([], []);
		}

		List<string> markdownPaths = [];
		AddMarkdownTree(root, markdownPaths);
		var docsRoot = Path.Combine(root, "docs");
		// The supplied project root is trusted even when it is a junction; only
		// descendants are rejected for being reparse points.

		return new ProjectMarkdownCatalog(
			ToEntries(root, markdownPaths.Where(path => !IsUnderDirectory(path, docsRoot))),
			ToEntries(root, markdownPaths.Where(path => IsUnderDirectory(path, docsRoot))));
	}

	private static void AddMarkdownTree(string directory, List<string> paths)
	{
		if (!Directory.Exists(directory))
		{
			return;
		}

		Stack<string> pending = new([directory]);
		while (pending.Count > 0)
		{
			var current = pending.Pop();
			foreach (var file in EnumerateMarkdownFiles(current))
			{
				paths.Add(file);
			}

			foreach (var child in EnumerateChildDirectories(current))
			{
				pending.Push(child);
			}
		}
	}

	private static string[] EnumerateMarkdownFiles(string directory)
	{
		try
		{
			return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
				.Where(path => string.Equals(
					Path.GetExtension(path),
					".md",
					StringComparison.OrdinalIgnoreCase))
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static string[] EnumerateChildDirectories(string directory)
	{
		try
		{
			return Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly)
				.Where(path => !IgnoredDirectoryNames.Contains(Path.GetFileName(path)))
				.Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
				.ToArray();
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			return [];
		}
	}

	private static ProjectMarkdownFileEntry[] ToEntries(
		string root,
		IEnumerable<string> paths) =>
		paths
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.Select(path => new ProjectMarkdownFileEntry(
				path,
				Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')))
			.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
			.ToArray();

	private static bool IsUnderDirectory(string path, string directory)
	{
		var relativePath = Path.GetRelativePath(directory, path);
		return !Path.IsPathRooted(relativePath)
			&& !relativePath.Equals("..", StringComparison.Ordinal)
			&& !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
	}
}

/// <summary>Identifies a project Markdown file by full and project-relative path.</summary>
public sealed record ProjectMarkdownFileEntry(string FullPath, string RelativePath);
