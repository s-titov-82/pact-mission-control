using Pact.Core.Agents;

namespace Pact.Presentation.Services;
/// <summary>
/// Builds the initial set of usage rows.
/// </summary>
public static class SubscriptionUsageRows
{
	/// <summary>
	/// Creates a row per profile that can report usage, in the updating state.
	/// </summary>
	/// <returns>
	/// Rows for Codex and Claude profiles only; other agents and plain shells expose no usage
	/// data and are omitted rather than shown as unavailable.
	/// </returns>
	public static IEnumerable<SubscriptionUsageRow> CreatePendingRows(IEnumerable<AgentProfileRecord> profiles)
	{
		ArgumentNullException.ThrowIfNull(profiles);

		return profiles
			.Where(profile => profile.Kind is AgentKind.Codex or AgentKind.Claude)
			.Select(profile => new SubscriptionUsageRow(profile));
	}
}