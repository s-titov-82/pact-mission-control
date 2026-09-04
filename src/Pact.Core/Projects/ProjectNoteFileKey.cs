using System.Security.Cryptography;
using System.Text;

namespace Pact.Core.Projects;

/// <summary>
/// Derives the note file name for a project root.
/// </summary>
public static class ProjectNoteFileKey
{
	/// <summary>
	/// Builds a stable, filesystem-safe key for <paramref name="rootPath"/>.
	/// </summary>
	/// <returns>
	/// A short path hash, suffixed with a readable form of the leaf directory when one can be
	/// derived. The hash — not the readable part — provides uniqueness, so two roots sharing a
	/// leaf name never collide.
	/// </returns>
	/// <remarks>
	/// The path is normalized (trimmed, forward slashes converted, trailing separator dropped,
	/// lowercased) before hashing, so the same directory written differently maps to the same
	/// key. Moving a project directory therefore yields a new key and its notes must be moved
	/// with it.
	/// </remarks>
	/// <exception cref="ArgumentException"><paramref name="rootPath"/> is null or blank.</exception>
	public static string FromRootPath(string rootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
		var normalized = rootPath.Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
		var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
		var leaf = normalized[(normalized.LastIndexOf('\\') + 1)..];
		StringBuilder suffix = new(leaf.Length);
		foreach (var ch in leaf)
		{
			if (char.IsAsciiLetterOrDigit(ch))
			{
				suffix.Append(ch);
			}
			else if (suffix.Length > 0 && suffix[^1] != '-')
			{
				suffix.Append('-');
			}
		}
		var readable = suffix.ToString().Trim('-');
		return readable.Length == 0 ? hash : hash + "-" + readable;
	}
}