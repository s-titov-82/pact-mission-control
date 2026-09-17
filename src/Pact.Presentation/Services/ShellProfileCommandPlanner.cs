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
	/// a usable resume invocation, with or without a conversation id; a corrupted one falls back
	/// to a fresh start rather than failing the launch with a malformed command line.
	/// </returns>
	public static string GetStartCommand(
		SessionRecord session,
		bool preferResumeCommand) =>
		GetStartPlan(session, preferResumeCommand).CommandLine;

	/// <summary>
	/// Returns the actual command and mode, including a stable reason when a requested resume
	/// must cold-start instead. A stored conversation id resumes that conversation directly; a
	/// Codex or Claude resume command without an id still runs, so the agent's own picker can
	/// recover a conversation whose id was never captured — after a crash, for instance.
	/// </summary>
	public static SessionStartPlan GetStartPlan(
		SessionRecord session,
		bool preferResumeCommand)
	{
		ArgumentNullException.ThrowIfNull(session);

		if (!preferResumeCommand)
		{
			return Normal(session, fellBack: false, reason: null);
		}

		if (session.Kind is not (AgentKind.Codex or AgentKind.Claude))
		{
			return Normal(session, fellBack: true, "resume-not-supported");
		}

		if (string.IsNullOrWhiteSpace(session.ResumeCommand))
		{
			return Normal(session, fellBack: true, "resume-command-unavailable");
		}

		if (AgentResumeCommandExtractor.IsGenericResumeCommand(session.ResumeCommand))
		{
			return new SessionStartPlan(
				session.ResumeCommand,
				TerminalStartMode.ResumeSelection,
				FellBackToColdStart: false,
				FallbackReason: null);
		}

		if (!AgentResumeCommandExtractor.IsConcreteResumeCommand(session.ResumeCommand))
		{
			return Normal(session, fellBack: true, "resume-command-invalid");
		}

		return new SessionStartPlan(
			session.ResumeCommand,
			TerminalStartMode.Resume,
			FellBackToColdStart: false,
			FallbackReason: null);
	}

	private static SessionStartPlan Normal(
		SessionRecord session,
		bool fellBack,
		string? reason) => new(
			session.LaunchCommand,
			TerminalStartMode.Normal,
			fellBack,
			reason);
}
