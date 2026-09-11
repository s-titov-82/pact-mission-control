namespace Pact.Core.Updates;

/// <summary>
/// Captures one immutable snapshot of the update lifecycle.
/// </summary>
/// <param name="State">The current lifecycle stage.</param>
/// <param name="RunningVersion">The stable version of the running Pact process.</param>
/// <param name="AvailableRelease">The validated newer release, when one is known.</param>
/// <param name="PreparedPackage">The verified staged package, when download completed.</param>
/// <param name="Error">A user-safe failure summary for the current state.</param>
public sealed record UpdateStatus(
	UpdateState State,
	StableReleaseVersion RunningVersion,
	UpdateRelease? AvailableRelease,
	PreparedUpdatePackage? PreparedPackage,
	string? Error);
