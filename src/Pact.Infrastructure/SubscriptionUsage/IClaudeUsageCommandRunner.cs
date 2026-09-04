namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Runs an agent's usage command, isolated behind an interface so usage parsing can be tested
/// without launching a process.
/// </summary>
public interface IClaudeUsageCommandRunner
{
	/// <summary>
	/// Runs <paramref name="commandName"/> and captures its output. Failures are reported in
	/// the result rather than thrown.
	/// </summary>
	Task<ClaudeUsageCommandResult> RunAsync(
		string commandName,
		CancellationToken cancellationToken);
}
