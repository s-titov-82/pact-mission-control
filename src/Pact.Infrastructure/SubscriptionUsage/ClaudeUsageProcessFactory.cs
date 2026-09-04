using System.Diagnostics;

namespace Pact.Infrastructure.SubscriptionUsage;

internal sealed class ClaudeUsageProcessFactory : IClaudeUsageProcessFactory
{
	public IClaudeUsageProcess Start(ProcessStartInfo startInfo)
	{
		var process = Process.Start(startInfo)
			?? throw new InvalidOperationException("Unable to start pwsh.");
		return new ClaudeUsageProcess(process);
	}
}
