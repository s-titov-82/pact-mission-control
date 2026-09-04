namespace Pact.Infrastructure.Orchestrator;

/// <summary>
/// Resolves the Hermes root that owns named profiles, mirroring Hermes' own resolution.
/// </summary>
/// <remarks>
/// Hermes stores named profiles under <c>&lt;root&gt;/profiles/&lt;name&gt;</c>, and that root is
/// platform-native: <c>%LOCALAPPDATA%\hermes</c> on Windows and <c>~/.hermes</c> elsewhere. A
/// <c>HERMES_HOME</c> pointing outside the native root replaces it, except when it names a profile
/// directory, in which case the profiles' own root is the grandparent. Assuming <c>~/.hermes</c>
/// everywhere makes Pact look for profiles Hermes never creates there.
/// </remarks>
public static class HermesHome
{
	private const string ProfilesDirectoryName = "profiles";

	/// <summary>Resolves the profile-owning root from the ambient environment.</summary>
	public static string ResolveRoot() =>
		ResolveRoot(
			Environment.GetEnvironmentVariable("HERMES_HOME"),
			PlatformDefaultRoot());

	/// <summary>
	/// Resolves the profile-owning root from an explicit <c>HERMES_HOME</c> and platform default.
	/// </summary>
	/// <param name="configuredHome">
	/// Value of <c>HERMES_HOME</c>, or <see langword="null"/>/blank when it is unset.
	/// </param>
	/// <param name="platformDefaultRoot">Native root for the current operating system.</param>
	public static string ResolveRoot(string? configuredHome, string platformDefaultRoot)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(platformDefaultRoot);

		var defaultRoot = Normalize(platformDefaultRoot);
		if (string.IsNullOrWhiteSpace(configuredHome))
		{
			return defaultRoot;
		}

		var configured = Normalize(configuredHome);
		if (IsSameOrUnder(configured, defaultRoot))
		{
			// Either the native root itself or one of its profiles; profiles live in the root.
			return defaultRoot;
		}

		var parent = Path.GetDirectoryName(configured);
		return parent is not null
			&& string.Equals(
				Path.GetFileName(parent),
				ProfilesDirectoryName,
				StringComparison.OrdinalIgnoreCase)
			&& Path.GetDirectoryName(parent) is { Length: > 0 } grandparent
				? grandparent
				: configured;
	}

	/// <summary>Returns the native Hermes root for the current operating system.</summary>
	public static string PlatformDefaultRoot()
	{
		if (!OperatingSystem.IsWindows())
		{
			return Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				".hermes");
		}

		var localApplicationData = Environment.GetFolderPath(
			Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localApplicationData))
		{
			localApplicationData = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				"AppData",
				"Local");
		}

		return Path.Combine(localApplicationData, "hermes");
	}

	/// <summary>Returns the directory Hermes uses for one named profile.</summary>
	public static string ProfileDirectory(string root, string profileName)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(root);
		ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

		return Path.Combine(root, ProfilesDirectoryName, profileName);
	}

	private static bool IsSameOrUnder(string candidate, string root)
	{
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;
		return string.Equals(candidate, root, comparison)
			|| candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
	}

	private static string Normalize(string path) =>
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
