namespace Pact.Core.Sessions;

/// <summary>A file an agent linked for the user to read, kept with the agent's session.</summary>
/// <param name="Path">Absolute path, as the link was added.</param>
/// <param name="Title">Optional short label the agent gave.</param>
/// <param name="AddedAt">When the link was added.</param>
public sealed record AgentFileLink(string Path, string? Title, DateTimeOffset AddedAt);

/// <summary>The rules of a session's link list: newest first, one link per file, a fixed cap.</summary>
public static class AgentFileLinks
{
	/// <summary>Links a session keeps; adding past it drops the oldest.</summary>
	public const int MaxPerSession = 20;

	/// <summary>
	/// The list with <paramref name="link"/> first; a link to the same file is replaced rather
	/// than kept twice.
	/// </summary>
	public static IReadOnlyList<AgentFileLink> Add(IReadOnlyList<AgentFileLink> links, AgentFileLink link)
	{
		ArgumentNullException.ThrowIfNull(links);
		ArgumentNullException.ThrowIfNull(link);
		return [link, .. links.Where(existing => !SamePath(existing.Path, link.Path)).Take(MaxPerSession - 1)];
	}

	/// <summary>The list without the link to <paramref name="path"/>; the same list when there is none.</summary>
	public static IReadOnlyList<AgentFileLink> Remove(IReadOnlyList<AgentFileLink> links, string path)
	{
		ArgumentNullException.ThrowIfNull(links);
		ArgumentNullException.ThrowIfNull(path);
		return links.Any(link => SamePath(link.Path, path))
			? [.. links.Where(link => !SamePath(link.Path, path))]
			: links;
	}

	/// <summary>Whether two spellings name the same file: slashes, case and trailing separators aside.</summary>
	public static bool SamePath(string left, string right) =>
		string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);

	private static string Normalize(string path) =>
		Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
