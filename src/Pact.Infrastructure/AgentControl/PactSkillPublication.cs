namespace Pact.Infrastructure.AgentControl;

/// <summary>
/// Identifies the Pact-owned guidance files confirmed to be available to a launched agent.
/// </summary>
public sealed record PactSkillPublication(
	string? McpSkillPath,
	string? CommonSkillPath)
{
	/// <summary>Gets the result used when publication did not complete.</summary>
	public static PactSkillPublication Empty { get; } = new(null, null);
}
