using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class CodexEmbeddedModeTests
{
	[Test]
	public void Codex_with_configuration_overrides_opts_out_of_the_shared_daemon()
	{
		var environment = new Dictionary<string, string> { ["PACT_SESSION_ID"] = "s1" };
		LaunchInjection injection = new(["-c", "mcp_servers.pact.url=http://127.0.0.1:1/"], environment);

		var applied = CodexEmbeddedMode.Apply(AgentKind.Codex, "codex", injection);

		applied.Arguments.ShouldBe(["--no-daemon", "-c", "mcp_servers.pact.url=http://127.0.0.1:1/"]);
		applied.EnvironmentVariables.ShouldBeSameAs(environment);
	}

	[Test]
	public void Codex_without_overrides_keeps_the_shared_daemon()
	{
		LaunchInjection injection = new([], new Dictionary<string, string>());

		CodexEmbeddedMode.Apply(AgentKind.Codex, "codex", injection).ShouldBeSameAs(injection);
	}

	[TestCase("codex --no-daemon")]
	[TestCase("codex resume 019a --no-daemon --search")]
	public void Codex_launch_that_already_names_the_option_is_not_given_it_twice(string commandLine)
	{
		LaunchInjection injection = new(["-c", "x=y"], new Dictionary<string, string>());

		CodexEmbeddedMode.Apply(AgentKind.Codex, commandLine, injection).ShouldBeSameAs(injection);
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Hermes)]
	[TestCase(AgentKind.Pwsh)]
	[TestCase(AgentKind.Custom)]
	public void Other_kinds_are_left_unchanged(AgentKind kind)
	{
		LaunchInjection injection = new(["-c", "x=y"], new Dictionary<string, string>());

		CodexEmbeddedMode.Apply(kind, "agent", injection).ShouldBeSameAs(injection);
	}
}
