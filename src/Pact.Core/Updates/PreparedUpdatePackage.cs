namespace Pact.Core.Updates;

/// <summary>
/// Identifies a Setup package that was staged and verified for installation.
/// </summary>
/// <param name="Release">The release represented by the package.</param>
/// <param name="SetupPath">The owned absolute path to the staged Setup executable.</param>
/// <param name="SetupSha256">The verified lowercase SHA-256 digest.</param>
/// <param name="AuthenticodeStatus">The observed signature status, when available.</param>
public sealed record PreparedUpdatePackage(
	UpdateRelease Release,
	string SetupPath,
	string SetupSha256,
	string? AuthenticodeStatus);
