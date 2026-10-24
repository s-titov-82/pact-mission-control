using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;
using Pact.Presentation.Services.AgentControl;

namespace Pact.App.Avalonia.Controllers;

/// <summary>
/// Sends each MCP request to the session or orchestrator tool set, by who is calling.
/// </summary>
internal sealed class AgentControlToolRouter(
	AgentControlToolSet sessionTools,
	AgentControlToolSet orchestratorTools)
{
	private readonly AgentControlToolSet _sessionTools = sessionTools ?? throw new ArgumentNullException(nameof(sessionTools));
	private readonly AgentControlToolSet _orchestratorTools = orchestratorTools ?? throw new ArgumentNullException(nameof(orchestratorTools));

	/// <summary>Lists the tools the caller may use.</summary>
	public JsonNode BuildToolsList(bool orchestrator) =>
		(orchestrator ? _orchestratorTools : _sessionTools).BuildToolsList();

	/// <summary>Validates a tool call's arguments and runs it.</summary>
	public Task<AgentControlResultData> InvokeAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken) =>
		(call.IsOrchestrator ? _orchestratorTools : _sessionTools).InvokeAsync(call, cancellationToken);
}
