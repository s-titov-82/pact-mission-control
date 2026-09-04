namespace Pact.Infrastructure.Documents;

/// <summary>
/// Folder or file node of a project Markdown hierarchy. Folder nodes carry no
/// <see cref="FullPath"/>; file nodes carry no children.
/// </summary>
public sealed record MarkdownTreeNode(
	string Name,
	string RelativePath,
	string? FullPath,
	IReadOnlyList<MarkdownTreeNode> Children)
{
	/// <summary>Whether this node groups other nodes instead of addressing a file.</summary>
	public bool IsFolder => FullPath is null;

	/// <summary>
	/// Builds the hierarchy of a flat entry group. Folders appear only where the
	/// subtree contains a Markdown file, siblings order folders before files, and
	/// every node keeps its project-relative path even when <paramref name="trimPrefix"/>
	/// removes a leading group directory from the displayed names.
	/// </summary>
	public static IReadOnlyList<MarkdownTreeNode> Build(
		IReadOnlyList<ProjectMarkdownFileEntry> entries,
		string trimPrefix = "")
	{
		ArgumentNullException.ThrowIfNull(entries);
		ArgumentNullException.ThrowIfNull(trimPrefix);
		MutableFolder root = new(string.Empty, string.Empty);
		foreach (var entry in entries)
		{
			var displayPath = entry.RelativePath.StartsWith(
				trimPrefix,
				StringComparison.OrdinalIgnoreCase)
				? entry.RelativePath[trimPrefix.Length..]
				: entry.RelativePath;
			var segments = displayPath.Split('/');
			var folder = root;
			for (var index = 0; index < segments.Length - 1; index++)
			{
				folder = folder.GetOrAddFolder(segments[index]);
			}

			folder.Files.Add(
				new MarkdownTreeNode(segments[^1], entry.RelativePath, entry.FullPath, []));
		}

		return root.ToNodes(trimPrefix);
	}

	private sealed class MutableFolder(string name, string displayPath)
	{
		private string Name { get; } = name;

		private string DisplayPath { get; } = displayPath;

		private Dictionary<string, MutableFolder> Folders { get; } =
			new(StringComparer.OrdinalIgnoreCase);

		public List<MarkdownTreeNode> Files { get; } = [];

		public MutableFolder GetOrAddFolder(string segment)
		{
			if (Folders.TryGetValue(segment, out var existing))
			{
				return existing;
			}

			MutableFolder created = new(
				segment,
				DisplayPath.Length == 0 ? segment : $"{DisplayPath}/{segment}");
			Folders[segment] = created;
			return created;
		}

		public IReadOnlyList<MarkdownTreeNode> ToNodes(string trimPrefix) =>
			[
				.. Folders.Values
					.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
					.Select(folder => new MarkdownTreeNode(
						folder.Name,
						trimPrefix + folder.DisplayPath,
						null,
						folder.ToNodes(trimPrefix))),
				.. Files.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
			];
	}
}
