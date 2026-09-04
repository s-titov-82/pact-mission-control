using Pact.Core.AgentControl;
using Pact.Core.Agents;

namespace Pact.Core.Tests.AgentControl;

public sealed class AgentControlArgumentTemplatesTests
{
	[Test]
	public void Claude_receives_the_connection_through_a_configuration_file()
	{
		var template = AgentControlArgumentTemplates.For(AgentKind.Claude).ShouldNotBeNull();

		template.ShouldBe(["--mcp-config", "{configPath}"]);
	}

	[Test]
	public void Codex_receives_the_connection_through_overrides_and_the_environment()
	{
		var template = AgentControlArgumentTemplates.For(AgentKind.Codex).ShouldNotBeNull();

		template.ShouldBe(
		[
			"-c",
			"mcp_servers.pact.url={endpointUrl}",
			"-c",
			"mcp_servers.pact.bearer_token_env_var={tokenEnvVar}"
		]);
	}

	[Test]
	[TestCase(AgentKind.Pwsh)]
	[TestCase(AgentKind.Hermes)]
	[TestCase(AgentKind.Custom)]
	public void Kinds_with_no_command_line_carrier_stay_unconnected(AgentKind kind) =>
		AgentControlArgumentTemplates.For(kind).ShouldBeNull();
}
