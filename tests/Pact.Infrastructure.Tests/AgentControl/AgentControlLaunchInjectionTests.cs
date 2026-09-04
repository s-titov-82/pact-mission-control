using System.Text.Json.Nodes;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Tests.AgentControl;

public sealed class AgentControlLaunchInjectionTests
{
	private static readonly IReadOnlyList<string> ClaudeTemplate =
		["--mcp-config", "{configPath}"];
	private static readonly IReadOnlyList<string> CodexTemplate =
	[
		"-c",
		"mcp_servers.pact.url={endpointUrl}",
		"-c",
		"mcp_servers.pact.bearer_token_env_var={tokenEnvVar}"
	];
	private string _configurationDirectory = string.Empty;

	[SetUp]
	public void SetUp()
	{
		_configurationDirectory = Path.Combine(Path.GetTempPath(), "pact-agent-control", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_configurationDirectory);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_configurationDirectory))
		{
			Directory.Delete(_configurationDirectory, recursive: true);
		}
	}

	[Test]
	public void Create_WritesARemoteConfigFileWhenTheTemplateAsksForOne()
	{
		var injection = AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");

		injection.Arguments[0].ShouldBe("--mcp-config");
		var path = injection.Arguments[1];
		File.Exists(path).ShouldBeTrue();
		var server = JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["pact"]!;
		server["type"]!.GetValue<string>().ShouldBe(
			"http",
			"an agent that cannot tell a remote server from a local one rejects the configuration");
		server["url"]!.GetValue<string>().ShouldBe("http://127.0.0.1:5000/mcp/");
	}

	[Test]
	public void Create_KeepsTheCredentialOutOfTheConfigFile()
	{
		var injection = AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");

		var path = injection.Arguments[1];
		var contents = File.ReadAllText(path);
		contents.ShouldNotContain("token-1");
		JsonNode.Parse(contents)!["mcpServers"]!["pact"]!["headers"]!["Authorization"]!
			.GetValue<string>().ShouldBe("Bearer ${PACT_AGENT_CONTROL_TOKEN}");
		injection.EnvironmentVariables["PACT_AGENT_CONTROL_TOKEN"].ShouldBe("token-1");
	}

	[Test]
	public void Create_SharesOneConfigFileAcrossSessions()
	{
		var first = AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");
		var second = AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-2",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-2");

		second.Arguments[1].ShouldBe(first.Arguments[1]);
		second.EnvironmentVariables["PACT_AGENT_CONTROL_TOKEN"].ShouldBe("token-2");
	}

	[Test]
	public void Create_WritesNoConfigFileWhenTheTemplateDoesNotAskForOne()
	{
		AgentControlLaunchInjection.Create(
			CodexTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");

		File.Exists(Path.Combine(_configurationDirectory, "pact-mcp.json")).ShouldBeFalse();
	}

	[Test]
	public void Create_PutsTheEndpointInArgumentsAndTheTokenInTheEnvironment()
	{
		var injection = AgentControlLaunchInjection.Create(
			CodexTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");

		injection.Arguments.ShouldContain("mcp_servers.pact.url=http://127.0.0.1:5000/mcp/");
		injection.Arguments.ShouldNotContain(argument => argument.Contains("token-1", StringComparison.Ordinal));
		var variableName = injection.Arguments.Single(
			argument => argument.StartsWith("mcp_servers.pact.bearer_token_env_var=", StringComparison.Ordinal))
			.Split('=')[1];
		injection.EnvironmentVariables[variableName].ShouldBe("token-1");
		injection.EnvironmentVariables["PACT_SESSION_ID"].ShouldBe("session-1");
	}

	[Test]
	public void Create_GivesNoToolsWhenTheProfileHasNoTemplate()
	{
		var injection = AgentControlLaunchInjection.Create(
			null,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");

		injection.Arguments.ShouldBeEmpty();
		injection.EnvironmentVariables.ShouldBeEmpty();
		File.Exists(Path.Combine(_configurationDirectory, "pact-mcp.json")).ShouldBeFalse();
	}

	[Test]
	public void Create_RewritesTheConfigWhenTheEndpointMoves()
	{
		AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			"token-1");
		var second = AgentControlLaunchInjection.Create(
			ClaudeTemplate,
			[],
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5001/mcp/"),
			"token-2");

		var path = second.Arguments[1];
		JsonNode.Parse(File.ReadAllText(path))!["mcpServers"]!["pact"]!["url"]!
			.GetValue<string>().ShouldBe("http://127.0.0.1:5001/mcp/");
	}

	[Test]
	public void Create_keeps_instruction_arguments_without_an_mcp_template()
	{
		var instructionArguments = new[]
		{
			"--append-system-prompt",
			"""C:\Pact Root\$cash`tick's "quoted"\PactCommonSkill.md\"""
		};

		LaunchInjection injection = AgentControlLaunchInjection.Create(
			null,
			instructionArguments,
			_configurationDirectory,
			"session-1",
			new Uri("http://127.0.0.1:5000/mcp/"),
			token: null);

		injection.Arguments.ShouldBe(instructionArguments);
		injection.EnvironmentVariables.ShouldBeEmpty();
	}
}
