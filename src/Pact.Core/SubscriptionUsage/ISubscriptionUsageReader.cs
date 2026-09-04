using Pact.Core.Agents;

namespace Pact.Core.SubscriptionUsage;
/// <summary>
/// Reads one profile's subscription usage.
/// </summary>
public interface ISubscriptionUsageReader
{
	/// <summary>
	/// Reads usage for <paramref name="profile"/>.
	/// </summary>
	/// <returns>
	/// A snapshot describing the outcome. A profile that cannot report usage, or a failed read,
	/// is returned as an unavailable or failed snapshot rather than throwing, so one bad profile
	/// does not stop the others refreshing.
	/// </returns>
	Task<SubscriptionUsageSnapshot> ReadAsync(
		AgentProfileRecord profile,
		CancellationToken cancellationToken);
}
