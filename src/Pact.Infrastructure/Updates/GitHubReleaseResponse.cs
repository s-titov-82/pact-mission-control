using Pact.Core.Updates;
using System.Diagnostics.CodeAnalysis;

namespace Pact.Infrastructure.Updates;

/// <summary>
/// Represents every expected outcome of a GitHub release-discovery request.
/// </summary>
public abstract record GitHubReleaseResponse
{
	private GitHubReleaseResponse()
	{
	}

	/// <summary>No newer stable release exists.</summary>
	[SuppressMessage(
		"Design",
		"CA1034:Nested types should not be visible",
		Justification = "Nested variants form the intentional closed discovery-result contract.")]
	public sealed record NoUpdate : GitHubReleaseResponse;

	/// <summary>A newer release passed all discovery validation.</summary>
	/// <param name="Release">The validated release.</param>
	[SuppressMessage(
		"Design",
		"CA1034:Nested types should not be visible",
		Justification = "Nested variants form the intentional closed discovery-result contract.")]
	public sealed record Available(UpdateRelease Release) : GitHubReleaseResponse;

	/// <summary>GitHub requested that callers defer further requests.</summary>
	/// <param name="RetryAt">The earliest UTC time for another automatic request.</param>
	/// <param name="Reason">A bounded, user-safe explanation.</param>
	[SuppressMessage(
		"Design",
		"CA1034:Nested types should not be visible",
		Justification = "Nested variants form the intentional closed discovery-result contract.")]
	public sealed record RateLimited(DateTimeOffset RetryAt, string Reason)
		: GitHubReleaseResponse;

	/// <summary>The response could not be trusted as an update candidate.</summary>
	/// <param name="Reason">A bounded, user-safe validation explanation.</param>
	[SuppressMessage(
		"Design",
		"CA1034:Nested types should not be visible",
		Justification = "Nested variants form the intentional closed discovery-result contract.")]
	public sealed record Invalid(string Reason) : GitHubReleaseResponse;
}
