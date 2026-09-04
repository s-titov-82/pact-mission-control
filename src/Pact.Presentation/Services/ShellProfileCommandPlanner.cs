using Pact.Core.Agents;
using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// Chooses the command line used to start a session.
/// </summary>
public static class ShellProfileCommandPlanner
{
	/// <summary>
	/// Returns the command to launch <paramref name="session"/> with.
	/// </summary>
	/// <param name="session">Session being started or restarted.</param>
	/// <param name="preferResumeCommand">Whether the user asked to resume rather than start fresh.</param>
	/// <returns>
	/// The resume command when resuming is both requested and viable, otherwise the fresh launch
	/// command. For Codex and Claude the stored resume command must additionally still look like
	/// a usable resume invocation; a corrupted one falls back to a fresh start rather than
	/// failing the launch with a malformed command line.
	/// </returns>
	public static string GetStartCommand(
		SessionRecord session,
		bool preferResumeCommand)
	{
		ArgumentNullException.ThrowIfNull(session);

		if (!preferResumeCommand)
		{
			return session.LaunchCommand;
		}

		if (string.IsNullOrWhiteSpace(session.ResumeCommand))
		{
			return session.LaunchCommand;
		}

		if (session.Kind is not (AgentKind.Codex or AgentKind.Claude))
		{
			return session.ResumeCommand;
		}

		if (!AgentResumeCommandExtractor.IsConcreteResumeCommand(session.ResumeCommand)
			&& !AgentResumeCommandExtractor.IsGenericResumeCommand(session.ResumeCommand))
		{
			return session.LaunchCommand;
		}

		return session.ResumeCommand;
	}
}