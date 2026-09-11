using Pact.Core.Updates;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Reads the latest stable Pact release from the code-owned GitHub repository.
/// </summary>
public interface IGitHubReleaseClient
{
	/// <summary>
	/// Gets a validated newer stable release or a typed non-availability result.
	/// </summary>
	/// <param name="runningVersion">The version of the current Pact process.</param>
	/// <param name="cancellationToken">Cancels the network request.</param>
	/// <returns>A typed discovery result that never treats rate limiting as no update.</returns>
	Task<GitHubReleaseResponse> GetLatestStableAsync(
		StableReleaseVersion runningVersion,
		CancellationToken cancellationToken);
}
