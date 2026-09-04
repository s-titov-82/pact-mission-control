using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class AgentResumeCommandExtractorTests
{
	[Test]
	public void TryExtract_returns_latest_codex_resume_command_with_session_id()
	{
		var output = """
            Work complete.
            Earlier hint: codex resume older-session
            To continue this session, run codex resume 7f9f9a2e-1b3c-4c7a-9b0e-123456789abc
            """;

		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBe("codex resume 7f9f9a2e-1b3c-4c7a-9b0e-123456789abc");
	}

	[Test]
	public void TryExtract_returns_claude_resume_command_with_session_id()
	{
		var output = """
            Claude session ended.
            Resume with claude --resume 123e4567-e89b-12d3-a456-426614174000
            """;

		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBe("claude --resume 123e4567-e89b-12d3-a456-426614174000");
	}

	[Test]
	public void TryExtract_strips_terminal_escape_sequences()
	{
		var output = "\u001b[32mResume with codex resume session-42\u001b[0m";

		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBe("codex resume session-42");
	}

	[Test]
	public void TryExtract_strips_bare_control_chars_from_session_id()
	{
		// A repaint TUI can leave a bare control byte adjacent to the id; it must
		// not end up in the saved resume command (would break the next launch).
		var bell = (char)0x07;
		var backspace = (char)0x08;
		var output = $"To resume, run: codex resume session-42{bell}{backspace} done";

		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBe("codex resume session-42");
	}

	[Test]
	public void TryExtract_returns_codex_resume_command_from_codex_session_id()
	{
		var output = """
            Codex session ready.
            Session ID: 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92
            """;

		var command = AgentResumeCommandExtractor.TryExtract(output, AgentKind.Codex);

		command.ShouldBe("codex resume 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92");
	}

	[Test]
	public void TryExtract_ignores_session_id_without_codex_context()
	{
		var output = "Session ID: 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92";

		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBeNull();
	}

	[Test]
	[TestCase("codex resume session-42")]
	[TestCase("codex.exe resume 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92")]
	[TestCase("claude --resume 123e4567-e89b-12d3-a456-426614174000")]
	[TestCase("claude-personal --resume 123e4567-e89b-12d3-a456-426614174000")]
	[TestCase("codex-personal resume 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92")]
	public void IsConcreteResumeCommand_accepts_commands_with_ids(string command) => AgentResumeCommandExtractor.IsConcreteResumeCommand(command).ShouldBeTrue();

	[Test]
	[TestCase(null)]
	[TestCase("")]
	[TestCase("codex resume")]
	[TestCase("codex resume --last")]
	[TestCase("codex resume <id>")]
	[TestCase("codex resume fallback")]
	[TestCase("claude --resume")]
	[TestCase("claude-personal --resume")]
	public void IsConcreteResumeCommand_rejects_generic_or_placeholder_commands(string? command) => AgentResumeCommandExtractor.IsConcreteResumeCommand(command).ShouldBeFalse();

	[Test]
	public void SetResumeCommandId_appends_id_to_saved_resume_command()
	{
		var updated = AgentResumeCommandExtractor.SetResumeCommandId(
			"claude-personal --resume",
			"123e4567-e89b-12d3-a456-426614174000");

		updated.ShouldBe("claude-personal --resume 123e4567-e89b-12d3-a456-426614174000");
	}

	[Test]
	public void SetResumeCommandId_replaces_only_trailing_id()
	{
		var updated = AgentResumeCommandExtractor.SetResumeCommandId(
			"claude-personal --resume old-session-id-41",
			"new-session-id-42");

		updated.ShouldBe("claude-personal --resume new-session-id-42");
	}

	[Test]
	public void SetResumeCommandId_clears_only_trailing_id()
	{
		var updated = AgentResumeCommandExtractor.SetResumeCommandId(
			"codex-personal resume 019f2a74-8e47-7a31-8d89-8f6c4d3d0a92",
			resumeId: null);

		updated.ShouldBe("codex-personal resume");
	}

	[Test]
	public void SetResumeCommandId_preserves_command_when_it_has_no_resume_marker()
	{
		var updated = AgentResumeCommandExtractor.SetResumeCommandId(
			"wsl claude-personal",
			"123e4567-e89b-12d3-a456-426614174000");

		updated.ShouldBe("wsl claude-personal");
	}

	[Test]
	public void TryGetResumeId_returns_trailing_resume_id()
	{
		var id = AgentResumeCommandExtractor.TryGetResumeId(
			"claude --resume 123e4567-e89b-12d3-a456-426614174000");

		id.ShouldBe("123e4567-e89b-12d3-a456-426614174000");
	}

	[Test]
	[TestCase(null)]
	[TestCase("")]
	[TestCase("claude --resume")]
	[TestCase("codex resume fallback")]
	public void TryGetResumeId_rejects_missing_or_non_concrete_ids(string? resumeCommand) => AgentResumeCommandExtractor.TryGetResumeId(resumeCommand).ShouldBeNull();

	[Test]
	[TestCase("Usage: codex resume <id>")]
	[TestCase("Run codex resume <SESSION_ID> to continue")]
	[TestCase("claude --resume <id>")]
	[TestCase("codex resume [session]")]
	[TestCase("codex resume fallback")]
	public void TryExtract_ignores_placeholder_usage_text(string output)
	{
		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBeNull();
	}

	[Test]
	[TestCase("codex resume")]
	[TestCase("codex resume --last")]
	[TestCase("claude --resume")]
	[TestCase("claude --continue")]
	public void TryExtract_ignores_generic_resume_commands(string output)
	{
		var command = AgentResumeCommandExtractor.TryExtract(output);

		command.ShouldBeNull();
	}
}