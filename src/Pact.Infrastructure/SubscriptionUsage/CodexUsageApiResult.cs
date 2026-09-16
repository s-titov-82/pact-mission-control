namespace Pact.Infrastructure.SubscriptionUsage;
/// <summary>
/// Raw result of one Codex app-server usage request.
/// </summary>
/// <param name="Succeeded">Whether a usage response was received.</param>
/// <param name="ResponseJson">
/// The JSON-RPC response line, kept verbatim so an unexpected shape can be inspected.
/// </param>
/// <param name="FailureMessage">Failure description, or <see langword="null"/> on success.</param>
/// <param name="UpdatedAt">When the response was received.</param>
public sealed record CodexUsageApiResult(
	bool Succeeded,
	string ResponseJson,
	string? FailureMessage,
	DateTimeOffset UpdatedAt);
