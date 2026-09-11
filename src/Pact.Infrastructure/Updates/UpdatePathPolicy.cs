using Pact.Core.Updates;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Derives version and handoff children from the single update root and rejects path escapes.
/// </summary>
public sealed class UpdatePathPolicy
{
	private readonly AppPaths _paths;

	/// <summary>Creates a policy over the application's canonical path map.</summary>
	/// <param name="paths">The canonical data-root path map.</param>
	public UpdatePathPolicy(AppPaths paths)
	{
		_paths = paths ?? throw new ArgumentNullException(nameof(paths));
	}

	/// <summary>Gets the deterministic retained package directory for a stable version.</summary>
	public string GetPackageDirectory(StableReleaseVersion version) =>
		EnsureOwnedPath(Path.Combine(_paths.UpdatePackagesDirectory, version.ToString()));

	/// <summary>Gets the deterministic retained handoff directory for one opaque restart id.</summary>
	public string GetHandoffDirectory(string restartId)
	{
		if (!IsSafePathSegment(restartId))
		{
			throw new ArgumentException(
				"The restart id must be one nonempty file-name segment.",
				nameof(restartId));
		}

		return EnsureOwnedPath(Path.Combine(_paths.UpdateHandoffsDirectory, restartId));
	}

	/// <summary>
	/// Normalizes and returns a fully qualified strict descendant of the canonical update root.
	/// </summary>
	/// <exception cref="ArgumentException">
	/// The candidate is relative, is the update root itself, or escapes that root.
	/// </exception>
	public string EnsureOwnedPath(string candidate)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
		if (!Path.IsPathFullyQualified(candidate))
		{
			throw new ArgumentException("The update-owned path must be absolute.", nameof(candidate));
		}

		var fullPath = Path.GetFullPath(candidate);
		var root = Path.GetFullPath(_paths.UpdatesDirectory)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var rootPrefix = root + Path.DirectorySeparatorChar;
		if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException(
				"The path must be a strict descendant of the Pact Updates directory.",
				nameof(candidate));
		}

		return fullPath;
	}

	private static bool IsSafePathSegment(string value) =>
		!string.IsNullOrWhiteSpace(value)
		&& string.Equals(value, value.Trim(), StringComparison.Ordinal)
		&& value is not "." and not ".."
		&& !value.EndsWith('.')
		&& !Path.IsPathFullyQualified(value)
		&& value.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
		&& value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0;
}
