using Pact.Core.Agents;

namespace Pact.Infrastructure.AgentControl;

/// <summary>Composes agent-specific raw arguments that point at published Pact guidance.</summary>
public static class PactInstructionComposer
{
	/// <summary>
	/// Returns invocation-scoped guidance arguments for supported agents, or an empty list when
	/// the kind or confirmed publication cannot support the requested instruction.
	/// </summary>
	public static IReadOnlyList<string> BuildArguments(
		AgentKind kind,
		bool agentControlEnabled,
		PactSkillPublication publication)
	{
		ArgumentNullException.ThrowIfNull(publication);
		if (kind is not (AgentKind.Codex or AgentKind.Claude)
			|| string.IsNullOrWhiteSpace(publication.CommonSkillPath)
			|| (agentControlEnabled && string.IsNullOrWhiteSpace(publication.McpSkillPath)))
		{
			return [];
		}

		string shortText = PactInstructionText.Build(
			agentControlEnabled,
			publication.McpSkillPath,
			publication.CommonSkillPath);

		return kind switch
		{
			// The invocation value intentionally overrides the same key in a selected Codex
			// config profile; Settings help exposes this collision.
			AgentKind.Codex => ["-c", $"developer_instructions={shortText}"],
			AgentKind.Claude => ["--append-system-prompt", shortText],
			_ => []
		};
	}
}
