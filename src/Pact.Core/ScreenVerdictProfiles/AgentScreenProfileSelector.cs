using Pact.Core.Agents;
using Pact.Core.Sessions;

namespace Pact.Core.ScreenVerdictProfiles;

/// <summary>
/// Selects the stable screen classifier associated with an agent kind.
/// </summary>
public static class AgentScreenProfileSelector
{
	/// <summary>
	/// Returns the agent-specific classifier, falling back to quiescence semantics
	/// for agent kinds without screen markers.
	/// </summary>
	/// <param name="kind">The agent kind whose screen will be classified.</param>
	/// <returns>The shared stateless classifier for <paramref name="kind"/>.</returns>
	public static IAgentScreenProfile ForKind(AgentKind kind) => kind switch
	{
		AgentKind.Claude => ClaudeScreenProfile.Instance,
		AgentKind.Codex => CodexScreenProfile.Instance,
		AgentKind.Pwsh => PwshScreenProfile.Instance,
		_ => QuiescenceScreenProfile.Instance
	};
}