namespace Pact.Presentation.Services;

/// <summary>Durable file exchange the scenario is currently waiting to complete.</summary>
/// <param name="Iteration">One-based scenario iteration.</param>
/// <param name="Role">Role responsible for writing the response.</param>
/// <param name="SessionId">Live session bound to that role.</param>
/// <param name="TaskPath">Immutable task file already published for the exchange.</param>
/// <param name="ResponsePath">Response file whose completed contents advance the run.</param>
public sealed record ScenarioExpectedResponse(
	int Iteration,
	string Role,
	string SessionId,
	string TaskPath,
	string ResponsePath);
