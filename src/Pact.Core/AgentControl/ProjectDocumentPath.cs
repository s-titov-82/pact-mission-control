namespace Pact.Core.AgentControl;

/// <summary>
/// Turns a path an agent supplied into a Markdown file of its project, refusing anything that
/// leaves the project root, including through a junction or a file link.
/// </summary>
public static class ProjectDocumentPath
{
	/// <summary>Resolves <paramref name="path"/> against <paramref name="projectRoot"/>.</summary>
	/// <param name="projectRoot">Project root; trusted even when it is itself a junction.</param>
	/// <param name="path">Path relative to the root, or an absolute path inside it.</param>
	/// <param name="relativePath">The file relative to the root, with <c>/</c> separators.</param>
	/// <param name="failure">
	/// <c>invalid-argument</c> for a blank or non-Markdown path, <c>outside-project</c> for a path
	/// that leaves the root, <c>not-found</c> for a missing file.
	/// </param>
	public static bool TryResolve(
		string projectRoot,
		string path,
		out string relativePath,
		out AgentControlFailure? failure)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
		relativePath = string.Empty;
		if (string.IsNullOrWhiteSpace(path))
		{
			failure = new AgentControlFailure("invalid-argument", "path must be non-blank.");
			return false;
		}

		var root = Path.GetFullPath(projectRoot);
		var fullPath = Path.GetFullPath(Path.Combine(root, path));
		var relative = Path.GetRelativePath(root, fullPath);
		if (Path.IsPathRooted(relative)
			|| relative == ".."
			|| relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
		{
			failure = OutsideProject();
			return false;
		}

		if (!string.Equals(Path.GetExtension(fullPath), ".md", StringComparison.OrdinalIgnoreCase))
		{
			failure = new AgentControlFailure("invalid-argument", "path must name a Markdown (.md) file.");
			return false;
		}

		if (PassesThroughReparsePoint(root, relative))
		{
			failure = OutsideProject();
			return false;
		}

		if (!File.Exists(fullPath))
		{
			failure = new AgentControlFailure("not-found", $"No file at '{relative.Replace(Path.DirectorySeparatorChar, '/')}'.");
			return false;
		}

		relativePath = relative.Replace(Path.DirectorySeparatorChar, '/');
		failure = null;
		return true;
	}

	// Every directory between the root and the file, and the file itself, must be a plain entry;
	// a link could point anywhere on the machine.
	private static bool PassesThroughReparsePoint(string root, string relative)
	{
		var current = root;
		foreach (var part in relative.Split(Path.DirectorySeparatorChar))
		{
			current = Path.Combine(current, part);
			FileSystemInfo entry = Directory.Exists(current)
				? new DirectoryInfo(current)
				: new FileInfo(current);
			if (entry.Exists && entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				return true;
			}

			if (!entry.Exists && entry.LinkTarget is not null)
			{
				return true;
			}
		}

		return false;
	}

	private static AgentControlFailure OutsideProject() =>
		new("outside-project", "path must stay inside this session's project.");
}
