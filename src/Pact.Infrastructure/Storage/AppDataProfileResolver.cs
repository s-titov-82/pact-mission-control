namespace Pact.Infrastructure.Storage;

/// <summary>
/// Determines which data root to use, from the command line or the default location.
/// </summary>
public static class AppDataProfileResolver
{
	/// <summary>
	/// Resolves the data root, honoring <c>--data-root &lt;path&gt;</c> or
	/// <c>--data-root=&lt;path&gt;</c> and otherwise defaulting under <c>%APPDATA%</c>.
	/// </summary>
	/// <param name="args">Command-line arguments.</param>
	/// <param name="defaultDirectoryName">Directory name used under <c>%APPDATA%</c> by default.</param>
	/// <param name="profileName">Label recorded on the returned profile.</param>
	/// <returns>The profile, with its root made absolute.</returns>
	/// <exception cref="ArgumentException">
	/// <c>--data-root</c> was given without a value, or with a relative path. A relative root is
	/// rejected rather than resolved against the current directory, which would otherwise let
	/// the same launch target different roots depending on where it was started.
	/// </exception>
	public static AppDataProfile Resolve(
		string[] args,
		string defaultDirectoryName,
		string profileName)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentException.ThrowIfNullOrWhiteSpace(defaultDirectoryName);
		ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

		string? overridePath = null;
		for (var index = 0; index < args.Length; index++)
		{
			var argument = args[index];
			if (string.Equals(argument, "--data-root", StringComparison.Ordinal))
			{
				if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
				{
					throw new ArgumentException(
						"--data-root requires an absolute path.",
						nameof(args));
				}

				overridePath = args[index];
			}
			else if (argument.StartsWith("--data-root=", StringComparison.Ordinal))
			{
				overridePath = argument["--data-root=".Length..];
				if (string.IsNullOrWhiteSpace(overridePath))
				{
					throw new ArgumentException(
						"--data-root requires an absolute path.",
						nameof(args));
				}
			}
		}

		if (overridePath is not null && !Path.IsPathFullyQualified(overridePath))
		{
			throw new ArgumentException(
				"--data-root requires an absolute path.",
				nameof(args));
		}

		var root = overridePath ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			defaultDirectoryName);

		return new AppDataProfile(profileName, Path.GetFullPath(root));
	}
}