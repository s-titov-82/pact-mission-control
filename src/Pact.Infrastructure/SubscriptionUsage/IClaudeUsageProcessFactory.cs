using System.Diagnostics;

namespace Pact.Infrastructure.SubscriptionUsage;

internal interface IClaudeUsageProcessFactory
{
	IClaudeUsageProcess Start(ProcessStartInfo startInfo);
}
