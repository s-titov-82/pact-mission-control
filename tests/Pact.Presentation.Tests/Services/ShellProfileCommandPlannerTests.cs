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
	public void GetStartCommand_keeps_generic_profile_default_resume_command_saved_on_session()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Codex,
			launchCommand: "codex",
			resumeCommand: "codex resume");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("codex resume");
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
	public void GetStartCommand_keeps_generic_wrapper_resume_command_saved_on_session()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Claude,
			launchCommand: "claude-personal",
			resumeCommand: "claude-personal --resume");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("claude-personal --resume");
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
	public void GetStartCommand_keeps_custom_resume_command_without_validating_agent_format()
	{
		var session = CreateSessionRecord(
			kind: AgentKind.Custom,
			launchCommand: "ssh user@server",
			resumeCommand: "ssh user@server -t tmux attach");

		var command = ShellProfileCommandPlanner.GetStartCommand(
			session,
			preferResumeCommand: true);

		command.ShouldBe("ssh user@server -t tmux attach");
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