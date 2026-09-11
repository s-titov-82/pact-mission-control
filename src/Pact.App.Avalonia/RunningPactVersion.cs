using System.Reflection;
using Pact.Core.Updates;

namespace Pact.App.Avalonia;

/// <summary>
/// Resolves the stable product version from the entry assembly's release metadata.
/// </summary>
internal static class RunningPactVersion
{
	/// <summary>
	/// Reads only <see cref="AssemblyInformationalVersionAttribute"/> so build metadata
	/// cannot be confused with the assembly or file compatibility versions.
	/// </summary>
	/// <param name="entryAssembly">The running product entry assembly.</param>
	/// <returns>The parsed stable release version.</returns>
	/// <exception cref="InvalidOperationException">
	/// The assembly has no stable informational version.
	/// </exception>
	public static StableReleaseVersion Read(Assembly entryAssembly)
	{
		ArgumentNullException.ThrowIfNull(entryAssembly);
		var informationalVersion = entryAssembly
			.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
			?.InformationalVersion;
		if (!StableReleaseVersion.TryParseInformationalVersion(
				informationalVersion,
				out var version))
		{
			throw new InvalidOperationException(
				"The Pact entry assembly has no stable informational version.");
		}

		return version;
	}
}
