namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Asks Codex itself for the account's current rate limits, isolated behind an interface so
/// usage parsing can be tested without launching a process.
/// </summary>
/// <remarks>
/// Unlike the session files this is a live read that does not depend on a recent Codex session,
/// so it costs a process launch and a network round trip and is worth throttling.
/// </remarks>
public interface ICodexUsageApiClient
{
	/// <summary>
	/// Reads the account rate limits through <paramref name="commandName"/>. Failures are
	/// reported in the result rather than thrown.
	/// </summary>
	/// <param name="commandName">
	/// The profile's Codex command, so a wrapper pointing at another account is honored.
	/// </param>
	/// <param name="cancellationToken">Cancels the read.</param>
	Task<CodexUsageApiResult> ReadRateLimitsAsync(
		string commandName,
		CancellationToken cancellationToken);
}
