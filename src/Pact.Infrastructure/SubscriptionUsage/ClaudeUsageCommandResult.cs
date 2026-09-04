namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Raw result of running an agent's usage command.
/// </summary>
/// <param name="Succeeded">Whether the command completed successfully.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
/// <param name="FailureMessage">Failure description, or <see langword="null"/> on success.</param>
/// <param name="UpdatedAt">When the command finished.</param>
public sealed record ClaudeUsageCommandResult(
	bool Succeeded,
	string StandardOutput,
	string StandardError,
	string? FailureMessage,
	DateTimeOffset UpdatedAt);
