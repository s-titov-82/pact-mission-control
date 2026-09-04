using Pact.Core.Agents;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class AgentResetCommandsTests
{
	[Test]
	[TestCase(AgentKind.Claude, "/clear")]
	[TestCase(AgentKind.Codex, "/new")]
	public void Resumable_agents_have_a_reset_command(AgentKind kind, string expected)
	{
		AgentResetCommands.TryGetResetCommand(kind, out var command).ShouldBeTrue();
		command.ShouldBe(expected);
	}

	[Test]
	[TestCase(AgentKind.Hermes)]
	[TestCase(AgentKind.Pwsh)]
	[TestCase(AgentKind.Custom)]
	public void Other_agents_have_no_reset_command(AgentKind kind)
	{
		AgentResetCommands.TryGetResetCommand(kind, out var command).ShouldBeFalse();
		command.ShouldBeNull();
	}
}