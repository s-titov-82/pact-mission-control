using Pact.Infrastructure.Orchestrator;
using Pact.Infrastructure.AgentControl;
using YamlDotNet.Serialization;

namespace Pact.Infrastructure.Tests.Orchestrator;

public sealed class HermesProvisionerTests
{
	private string _home = null!;

	[SetUp]
	public void SetUp()
	{
		_home = Path.Combine(
			Path.GetTempPath(),
			"Pact.Tests",
			Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_home);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(_home))
		{
			Directory.Delete(_home, recursive: true);
		}
	}

	[Test]
	public async Task Provision_stops_before_creating_anything_when_hermes_is_missing()
	{
		HermesProvisioner provisioner = new(
			new FakeHermesCli(_home) { Installed = false });

		var steps = await Provision(provisioner);

		steps[0].Outcome.ShouldBe(ProvisionOutcome.Failed);
		steps[0].Detail.ShouldContain("hermes");
		Directory.Exists(Path.Combine(_home, "profiles")).ShouldBeFalse();
	}

	[Test]
	public async Task Provision_creates_the_profile_through_hermes()
	{
		FakeHermesCli cli = new(_home);
		HermesProvisioner provisioner = new(cli);

		await Provision(provisioner);

		cli.CreatedProfiles.ShouldBe(["pact"]);
	}

	[Test]
	public async Task Provision_does_not_recreate_an_existing_profile()
	{
		FakeHermesCli cli = new(_home);
		HermesProvisioner provisioner = new(cli);
		await Provision(provisioner);
		cli.CreatedProfiles.Clear();

		await Provision(provisioner);

		cli.CreatedProfiles.ShouldBeEmpty();
	}

	[Test]
	public async Task Provision_reports_a_failed_profile_creation_and_stops()
	{
		FakeHermesCli cli = new(_home) { CreateSucceeds = false };
		HermesProvisioner provisioner = new(cli);

		var steps = await Provision(provisioner);

		steps.ShouldContain(step =>
			step.Name == "profile" && step.Outcome == ProvisionOutcome.Failed);
		File.Exists(ConfigPath()).ShouldBeFalse();
	}

	[Test]
	public async Task Provision_writes_the_mcp_server_block()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));

		await Provision(provisioner);

		var config = await File.ReadAllTextAsync(ConfigPath());
		config.ShouldContain("${PACT_MCP_URL}");
		config.ShouldContain("Bearer ${PACT_MCP_TOKEN}");
	}

	[Test]
	public async Task Provision_preserves_every_other_node_semantically()
	{
		FakeHermesCli cli = new(_home);
		await cli.CreateProfileAsync("pact", CancellationToken.None);
		await File.WriteAllTextAsync(
			ConfigPath(),
			"""
			model: gpt-5
			toolsets:
			  - terminal
			  - browser
			limits: &shared
			  timeout: 30
			gateway:
			  telegram: *shared
			mcp_servers:
			  other:
			    url: http://example
			""");
		HermesProvisioner provisioner = new(cli);

		await Provision(provisioner);

		var root = ParseYaml(await File.ReadAllTextAsync(ConfigPath()));
		root["model"].ShouldBe("gpt-5");
		AsList(root["toolsets"]).ShouldBe(["terminal", "browser"]);
		AsMap(AsMap(root["gateway"])["telegram"])["timeout"].ShouldBe("30");
		AsMap(AsMap(root["mcp_servers"])["other"])["url"].ShouldBe("http://example");
		AsMap(root["mcp_servers"]).ShouldContainKey("pact");
	}

	[Test]
	public async Task Provision_backs_up_the_original_config_before_the_first_edit()
	{
		FakeHermesCli cli = new(_home);
		await cli.CreateProfileAsync("pact", CancellationToken.None);
		await File.WriteAllTextAsync(ConfigPath(), "# my comment\nmodel: gpt-5\n");
		HermesProvisioner provisioner = new(cli);

		var steps = await Provision(provisioner);

		steps.ShouldContain(step =>
			step.Name == "config.yaml" && step.Outcome == ProvisionOutcome.BackedUp);
		var backups = Directory.GetFiles(ProfileDirectory(), "config.yaml.*.bak");
		backups.ShouldNotBeEmpty();
		(await File.ReadAllTextAsync(backups[0])).ShouldContain("# my comment");
	}

	[Test]
	public async Task Provision_backs_up_a_hand_edited_soul_file()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));
		await Provision(provisioner);
		await File.WriteAllTextAsync(SoulPath(), "my own carefully tuned prompt");

		var steps = await Provision(provisioner);

		steps.ShouldContain(step =>
			step.Name == "SOUL.md" && step.Outcome == ProvisionOutcome.BackedUp);
		Directory.GetFiles(ProfileDirectory(), "SOUL.md.*.bak").ShouldNotBeEmpty();
	}

	[Test]
	public async Task Provision_writes_both_pact_keys_into_env()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));

		await Provision(provisioner);

		var env = await File.ReadAllTextAsync(EnvPath());
		env.ShouldContain("PACT_MCP_URL=http://127.0.0.1:8765/mcp/");
		env.ShouldContain("PACT_MCP_TOKEN=token");
	}

	[Test]
	public async Task Provision_leaves_other_env_keys_alone()
	{
		FakeHermesCli cli = new(_home);
		await cli.CreateProfileAsync("pact", CancellationToken.None);
		await File.WriteAllTextAsync(EnvPath(), "TELEGRAM_BOT_TOKEN=abc\n");
		HermesProvisioner provisioner = new(cli);

		await Provision(provisioner);

		(await File.ReadAllTextAsync(EnvPath())).ShouldContain("TELEGRAM_BOT_TOKEN=abc");
	}

	[Test]
	public async Task Provision_installs_the_status_report_skill()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));

		await Provision(provisioner);

		File.Exists(Path.Combine(
			ProfileDirectory(),
			"skills",
			"pact-status-report",
			"SKILL.md")).ShouldBeTrue();
	}

	[Test]
	public async Task Provisioned_soul_documents_every_orchestrator_tool()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));

		await Provision(provisioner);

		string soul = await File.ReadAllTextAsync(SoulPath());
		var toolNames = OrchestratorToolCatalog.BuildToolsListResult()["tools"]!.AsArray()
			.Select(tool => tool!["name"]!.GetValue<string>());
		foreach (var toolName in toolNames)
		{
			soul.ShouldContain(toolName);
		}
	}

	[Test]
	public async Task Provision_reports_every_step_separately()
	{
		HermesProvisioner provisioner = new(new FakeHermesCli(_home));

		var steps = await Provision(provisioner);

		var names = steps.Select(step => step.Name).ToList();
		names.ShouldContain("profile");
		names.ShouldContain("config.yaml");
		names.ShouldContain("SOUL.md");
		names.ShouldContain(".env");
		names.ShouldContain("pact-status-report");
	}

	private Task<IReadOnlyList<ProvisionStep>> Provision(HermesProvisioner provisioner) =>
		provisioner.ProvisionAsync(
			_home,
			"pact",
			"http://127.0.0.1:8765/mcp/",
			"token",
			CancellationToken.None);

	private string ProfileDirectory() => Path.Combine(_home, "profiles", "pact");

	private string ConfigPath() => Path.Combine(ProfileDirectory(), "config.yaml");

	private string SoulPath() => Path.Combine(ProfileDirectory(), "SOUL.md");

	private string EnvPath() => Path.Combine(ProfileDirectory(), ".env");

	private static Dictionary<string, object> ParseYaml(string yaml)
	{
		var value = new DeserializerBuilder().Build().Deserialize<object>(yaml);
		return AsMap(value);
	}

	private static Dictionary<string, object> AsMap(object value) =>
		((Dictionary<object, object>)value).ToDictionary(
			pair => pair.Key.ToString()!,
			pair => pair.Value);

	private static string[] AsList(object value) =>
		((List<object>)value).Select(item => item.ToString()!).ToArray();

	private sealed class FakeHermesCli(string home) : IHermesCli
	{
		public bool Installed { get; init; } = true;

		public bool CreateSucceeds { get; init; } = true;

		public List<string> CreatedProfiles { get; } = [];

		public bool IsInstalled() => Installed;

		public Task<HermesCliResult> CreateProfileAsync(
			string profileName,
			CancellationToken cancellationToken)
		{
			CreatedProfiles.Add(profileName);
			var profilePath = Path.Combine(home, "profiles", profileName);
			if (CreateSucceeds)
			{
				Directory.CreateDirectory(profilePath);
			}

			return Task.FromResult(new HermesCliResult(
				CreateSucceeds,
				profilePath,
				CreateSucceeds ? "created" : "creation failed"));
		}
	}
}
