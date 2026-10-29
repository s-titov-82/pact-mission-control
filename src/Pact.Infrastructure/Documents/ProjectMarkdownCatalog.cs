namespace Pact.Infrastructure.Documents;

/// <summary>
/// The project's Markdown files, the <c>docs</c> tree and everything outside it alike. The review
/// logs in <c>.pact/reviews</c> are left out: the project screen lists them.
/// </summary>
public sealed record ProjectMarkdownCatalog(IReadOnlyList<ProjectMarkdownFileEntry> All)
{
	private static readonly HashSet<string> IgnoredDirectoryNames = new(
		[".git", ".worktrees", ".pact-reviews", "bin", "obj", "node_modules"],
		StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Scans the project root once, skipping generated directories, reparse points and review logs.
	/// </summary>
	public static ProjectMarkdownCatalog Scan(string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		var root = Path.GetFullPath(projectRootPath);
		if (!Directory.Exists(root))
		{
			return new ProjectMarkdownCatalog([]);
		}

		// The supplied project root is trusted even when it is a junction; only
		// descendants are rejected for being reparse points.
		List<string> markdownPaths = [];
		AddMarkdownTree(root, markdownPaths);
		var reviewsRoot = Path.Combine(root, ".pact", "reviews");
		bool IsReviewLog(string path)
		{
			return string.Equals(Path.GetDirectoryName(path), reviewsRoot, StringComparison.OrdinalIgnoreCase);
		}

		return new ProjectMarkdownCatalog(ToEntries(root, markdownPaths.Where(path => !IsReviewLog(path))));
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
				// A file link may point outside the project.
				.Where(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
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
}

/// <summary>Identifies a project Markdown file by full and project-relative path.</summary>
public sealed record ProjectMarkdownFileEntry(string FullPath, string RelativePath);
