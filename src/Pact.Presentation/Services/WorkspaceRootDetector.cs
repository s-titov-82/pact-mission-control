namespace Pact.Presentation.Services;

/// <summary>
/// Normalizes project root paths so the same directory always yields one identity.
/// </summary>
public static class WorkspaceRootDetector
{
	/// <summary>
	/// Converts a path to its canonical absolute form with no trailing separator, so paths that
	/// differ only in form are recognized as the same project.
	/// </summary>
	/// <exception cref="ArgumentException"><paramref name="path"/> is null or blank.</exception>
	public static string NormalizeRoot(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);

		return Path.GetFullPath(path)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
	}

	/// <summary>
	/// Derives the default project name — the leaf directory name — from a root path.
	/// </summary>
	public static string GetWorkspaceName(string rootPath)
	{
		var normalized = NormalizeRoot(rootPath);
		return new DirectoryInfo(normalized).Name;
	}
}