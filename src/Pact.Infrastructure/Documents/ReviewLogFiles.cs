namespace Pact.Infrastructure.Documents;

/// <summary>Lists the review logs Pact writes into a project's <c>.pact/reviews</c> folder.</summary>
public static class ReviewLogFiles
{
	/// <summary>Project-relative folder of the review logs.</summary>
	public const string Folder = ".pact/reviews";

	/// <summary>
	/// Project-relative paths of the Markdown files directly in the folder, newest first. Log
	/// names start with a sortable <c>yyyy-MM-dd-HHmm</c> timestamp, so name order is age order.
	/// </summary>
	public static IReadOnlyList<string> List(string projectRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		var folder = Path.Combine(projectRoot, ".pact", "reviews");
		if (!Directory.Exists(folder))
		{
			return [];
		}

		return Directory.EnumerateFiles(folder, "*.md", SearchOption.TopDirectoryOnly)
			.Select(Path.GetFileName)
			.OrderDescending(StringComparer.OrdinalIgnoreCase)
			.Select(name => $"{Folder}/{name}")
			.ToArray();
	}

	/// <summary>Deletes one log, named by its project-relative path as <see cref="List"/> gives it.</summary>
	/// <exception cref="ArgumentException">The path is not a Markdown file directly in the folder.</exception>
	public static void Delete(string projectRoot, string relativePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		var prefix = Folder + "/";
		var name = relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			? relativePath[prefix.Length..]
			: string.Empty;
		if (name.Length == 0
			|| name.IndexOfAny(['/', '\\']) >= 0
			|| name.Contains("..", StringComparison.Ordinal)
			|| !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"Not a review log: {relativePath}", nameof(relativePath));
		}

		File.Delete(Path.Combine(projectRoot, ".pact", "reviews", name));
	}
}
