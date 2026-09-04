namespace Pact.Infrastructure.SubscriptionUsage;

internal interface IClaudeUsageProcess : IDisposable
{
	TextReader StandardOutput { get; }
	TextReader StandardError { get; }
	int ExitCode { get; }
	bool HasExited { get; }
	Task WaitForExitAsync(CancellationToken cancellationToken);
	void Kill(bool entireProcessTree);
}
