namespace Pact.Infrastructure.AgentControl;

/// <summary>
/// Builds the shared short instruction that conditionally points agents at Pact guidance.
/// </summary>
public static class PactInstructionText
{
	/// <summary>
	/// Builds connected or common-only guidance without inlining the detailed miniskills.
	/// </summary>
	public static string Build(
		bool agentControlEnabled,
		string? mcpSkillPath,
		string commonSkillPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(commonSkillPath);
		if (!Path.IsPathFullyQualified(commonSkillPath))
		{
			throw new ArgumentException("The common Pact skill path must be absolute.", nameof(commonSkillPath));
		}

		if (!agentControlEnabled)
		{
			return $"You are running inside PACT Mission Control. For questions about Pact behavior, read `{commonSkillPath}`. Read the file only when that condition applies.";
		}

		ArgumentException.ThrowIfNullOrWhiteSpace(mcpSkillPath);
		if (!Path.IsPathFullyQualified(mcpSkillPath))
		{
			throw new ArgumentException("The Pact MCP skill path must be absolute.", nameof(mcpSkillPath));
		}

		return $"You are running inside PACT Mission Control. Before the first use of the `pact` MCP server, read `{mcpSkillPath}`. For questions about Pact behavior or when no suitable Pact MCP tool exists, read `{commonSkillPath}`. Read these files only when the stated condition applies.";
	}
}
