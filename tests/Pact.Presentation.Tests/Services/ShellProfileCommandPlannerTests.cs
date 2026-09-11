using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class ShellProfileCommandPlannerTests
{
	[Test]
	public void GetStartCommand_uses_saved_resume_command_when_restoring_session()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: "codex resume codex-session-123");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("codex resume codex-session-123");
	}

	[Test]
	public void GetStartPlan_reports_a_concrete_agent_resume()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Claude,
			launchCommand: "claude-personal",
			resumeCommand: "claude-personal --resume 123e4567-e89b-12d3-a456-426614174000");

		var plan = ShellProfileCommandPlanner.GetStartPlan(session, preferResumeCommand: true);

		plan.ShouldBe(new SessionStartPlan(
			"claude-personal --resume 123e4567-e89b-12d3-a456-426614174000",
			TerminalStartMode.Resume,
			FellBackToColdStart: false,
			FallbackReason: null));
	}

	[TestCase(null, "resume-command-unavailable")]
	[TestCase("codex resume", "resume-id-unavailable")]
	[TestCase("codex resume fallback", "resume-command-invalid")]
	public void GetStartPlan_cold_starts_agent_when_resume_is_unavailable(
		string? resumeCommand,
		string reason)
	{
		var session = CreateSessionRecord(AgentKind.Codex, "codex", resumeCommand);

		var plan = ShellProfileCommandPlanner.GetStartPlan(session, preferResumeCommand: true);

		plan.ShouldBe(new SessionStartPlan(
			"codex",
			TerminalStartMode.Normal,
			FellBackToColdStart: true,
			reason));
	}

	[Test]
	public void GetStartPlan_cold_starts_non_agent_terminal_even_with_a_saved_command()
	{
		var session = CreateSessionRecord(
			AgentKind.Custom,
			"ssh user@server",
			"ssh user@server -t tmux attach");

		var plan = ShellProfileCommandPlanner.GetStartPlan(session, preferResumeCommand: true);

		plan.CommandLine.ShouldBe("ssh user@server");
		plan.Mode.ShouldBe(TerminalStartMode.Normal);
		plan.FellBackToColdStart.ShouldBeTrue();
		plan.FallbackReason.ShouldBe("resume-not-supported");
	}

	[Test]
	public void GetStartCommand_falls_back_to_launch_command_when_not_restoring_session()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: "codex resume codex-session-123");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: false);

		command.ShouldBe("codex");
	}

	[Test]
	public void GetStartCommand_falls_back_to_launch_command_without_saved_resume_command()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: null);

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("codex");
	}

	[Test]
	public void GetStartCommand_replaces_invalid_codex_resume_command_with_launch_command()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: "codex resume fallback");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("codex");
	}

	[Test]
	public void GetStartCommand_cold_starts_when_only_generic_resume_template_is_saved()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: "codex resume");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("codex");
	}

	[Test]
	public void GetStartCommand_uses_wrapper_executable_resume_command()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Claude,
			launchCommand: "claude-personal",
			resumeCommand: "claude-personal --resume 123e4567-e89b-12d3-a456-426614174000");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("claude-personal --resume 123e4567-e89b-12d3-a456-426614174000");
	}

	[Test]
	public void GetStartCommand_cold_starts_when_wrapper_has_no_concrete_resume_id()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Claude,
			launchCommand: "claude-personal",
			resumeCommand: "claude-personal --resume");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("claude-personal");
	}

	[Test]
	public void GetStartCommand_does_not_rewrite_saved_resume_command_executable()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Claude,
			launchCommand: "claude-personal",
			resumeCommand: "claude --resume 123e4567-e89b-12d3-a456-426614174000");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("claude --resume 123e4567-e89b-12d3-a456-426614174000");
	}

	[Test]
	public void GetStartCommand_cold_starts_non_agent_terminal()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Custom,
			launchCommand: "ssh user@server",
			resumeCommand: "ssh user@server -t tmux attach");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("ssh user@server");
	}

	[Test]
	public void GetStartCommand_falls_back_to_launch_command_without_saved_or_profile_resume_command()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Custom,
			launchCommand: "pwsh",
			resumeCommand: null);

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("pwsh");
	}

	private static SessionRecord CreateSessionRecord(
		AgentKind kind,
		string launchCommand,
		string? resumeCommand)
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionRecord(
			"session-1",
			kind,
			"Task",
			"D:\\Work",
			launchCommand,
			resumeCommand,
			SessionStatus.Stopped,
			now,
			now);
	}
}
