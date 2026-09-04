namespace Pact.Core.Platform;

/// <summary>
/// Resolves executables through the ambient <c>PATH</c>, letting launch profiles and git
/// helpers be validated before a session is started.
/// </summary>
public interface IExecutableLocator
{
	/// <summary>
	/// Resolves <paramref name="executableName"/> to a full path.
	/// </summary>
	/// <returns>
	/// The resolved path, or <see langword="null"/> when the executable is not installed.
	/// A null result is a normal "feature unavailable" signal, not an error.
	/// </returns>
	string? FindOnPath(string executableName);
}