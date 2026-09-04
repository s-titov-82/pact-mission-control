using System.Diagnostics.CodeAnalysis;
using Pact.Core.Agents;

namespace Pact.Presentation.Services;

/// <summary>
/// Maps agents to the slash command that clears their conversation.
/// </summary>
public static class AgentResetCommands
{
	/// <summary>
	/// Gets the command that resets <paramref name="kind"/>'s conversation.
	/// </summary>
	/// <param name="kind">Agent running in the session.</param>
	/// <param name="command">The reset command when one exists.</param>
	/// <returns>
	/// <see langword="false"/> for agents and plain shells with no reset command, which is the
	/// signal to hide the reset action rather than to report an error.
	/// </returns>
	public static bool TryGetResetCommand(AgentKind kind, [NotNullWhen(true)] out string? command)
	{
		command = kind switch
		{
			AgentKind.Claude => "/clear",
			AgentKind.Codex => "/new",
			_ => null
		};

		return command is not null;
	}
}