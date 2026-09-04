namespace Pact.Presentation.Services;
/// <summary>
/// Decides how often usage is re-read.
/// </summary>
public static class SubscriptionUsageRefreshPolicy
{
	/// <summary>Interval used while every window has comfortable headroom.</summary>
	public static readonly TimeSpan NormalInterval = TimeSpan.FromMinutes(2);

	/// <summary>Shorter interval used once a window is nearly exhausted.</summary>
	public static readonly TimeSpan LowLimitInterval = TimeSpan.FromSeconds(30);

	/// <summary>
	/// Returns the delay before the next refresh, polling faster when any row is near its limit
	/// so an exhausted quota is noticed promptly.
	/// </summary>
	public static TimeSpan GetNextRefreshInterval(IEnumerable<SubscriptionUsageRow> rows)
	{
		ArgumentNullException.ThrowIfNull(rows);

		return rows.Any(row => row.IsNearLimit)
			? LowLimitInterval
			: NormalInterval;
	}
}