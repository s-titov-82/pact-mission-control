namespace Pact.Core.Sessions;

/// <summary>The short place name shown next to a linked file.</summary>
public static class AgentFileLocation
{
	/// <summary>
	/// <c>scratchpad</c> for a file under a folder of that name, <c>project</c> inside
	/// <paramref name="projectRoot"/>, <c>Temp</c> inside one of <paramref name="tempRoots"/>, and
	/// otherwise the name of the folder holding the file.
	/// </summary>
	public static string Describe(string path, string? projectRoot, IReadOnlyList<string> tempRoots)
	{
		ArgumentNullException.ThrowIfNull(path);
		ArgumentNullException.ThrowIfNull(tempRoots);
		var full = Path.GetFullPath(path);
		var folder = Path.GetDirectoryName(full) ?? full;
		if (folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			.Any(segment => string.Equals(segment, "scratchpad", StringComparison.OrdinalIgnoreCase)))
		{
			return "scratchpad";
		}

		if (projectRoot is not null && IsInside(full, projectRoot))
		{
			return "project";
		}

		if (tempRoots.Any(root => IsInside(full, root)))
		{
			return "Temp";
		}

		var name = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		return name.Length > 0 ? name : folder;
	}

	private static bool IsInside(string full, string root)
	{
		var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
			+ Path.DirectorySeparatorChar;
		return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
	}
}
