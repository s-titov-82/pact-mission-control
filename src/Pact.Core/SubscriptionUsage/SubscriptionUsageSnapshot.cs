using Pact.Core.Agents;

namespace Pact.Core.SubscriptionUsage;
/// <summary>
/// Usage figures read for one launch profile.
/// </summary>
/// <param name="ProfileId">Profile these figures belong to.</param>
/// <param name="ProfileName">Profile display name.</param>
/// <param name="Kind">Agent the profile launches.</param>
/// <param name="State">Whether the read succeeded.</param>
/// <param name="FiveHour">Rolling five-hour window, or <see langword="null"/> when not reported.</param>
/// <param name="Weekly">Weekly window, or <see langword="null"/> when not reported.</param>
/// <param name="StatusText">Short text summarizing the result for display.</param>
/// <param name="RawResponseText">
/// Unparsed agent output, kept so an unexpected format can be inspected. <see langword="null"/>
/// when there was nothing to keep.
/// </param>
/// <param name="ErrorDetailsText">Failure detail, or <see langword="null"/> on success.</param>
/// <param name="UpdatedAt">When these figures were read.</param>
/// <param name="FableWeekly">Separate weekly window for the Fable model, when reported.</param>
public sealed record SubscriptionUsageSnapshot(
	string ProfileId,
	string ProfileName,
	AgentKind Kind,
	SubscriptionUsageState State,
	SubscriptionLimitSnapshot? FiveHour,
	SubscriptionLimitSnapshot? Weekly,
	string StatusText,
	string? RawResponseText,
	string? ErrorDetailsText,
	DateTimeOffset UpdatedAt,
	SubscriptionLimitSnapshot? FableWeekly = null);
