namespace Pact.Core.SubscriptionUsage;
/// <summary>
/// One usage limit window and when it resets.
/// </summary>
/// <param name="UsedPercent">Portion of the window consumed, 0-100.</param>
/// <param name="ResetsAt">
/// When the window resets, or <see langword="null"/> when the agent did not report it.
/// </param>
public sealed record SubscriptionLimitSnapshot(int UsedPercent, DateTimeOffset? ResetsAt)
{
	/// <summary>Portion of the window still available, clamped to 0-100.</summary>
	public int RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

	/// <summary>
	/// Whether under 10% remains, ignoring any reset. Prefer <see cref="IsLowAt"/>, which
	/// accounts for a window that has already reset.
	/// </summary>
	public bool IsLow => RemainingPercent < 10;

	/// <summary>Whether the reset time has passed as of <paramref name="now"/>.</summary>
	public bool HasResetPassed(DateTimeOffset now) => ResetsAt is not null && ResetsAt <= now;

	/// <summary>
	/// Remaining portion as of <paramref name="now"/>, reporting a full window once the reset
	/// time has passed. This keeps a stale snapshot from showing an exhausted limit that has in
	/// fact already refilled.
	/// </summary>
	public int RemainingPercentAt(DateTimeOffset now) => HasResetPassed(now) ? 100 : RemainingPercent;

	/// <summary>Whether under 10% remains as of <paramref name="now"/>, honoring the reset.</summary>
	public bool IsLowAt(DateTimeOffset now) => RemainingPercentAt(now) < 10;
}
