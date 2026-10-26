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
}
