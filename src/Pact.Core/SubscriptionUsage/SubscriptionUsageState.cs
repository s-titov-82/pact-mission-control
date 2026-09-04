namespace Pact.Core.SubscriptionUsage;
/// <summary>
/// Whether a profile's subscription usage could be read.
/// </summary>
public enum SubscriptionUsageState
{
	/// <summary>A read is in flight and no figures are available yet.</summary>
	Updating,

	/// <summary>Usage figures were read successfully.</summary>
	Ready,

	/// <summary>This agent exposes no usage data, so nothing can be shown.</summary>
	Unavailable,

	/// <summary>A read was attempted and failed; the reason is in the error details.</summary>
	Failed
}
