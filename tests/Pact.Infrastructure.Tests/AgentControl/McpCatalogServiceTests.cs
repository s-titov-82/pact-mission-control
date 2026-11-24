using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;
using Tomlyn;
using Tomlyn.Model;

namespace Pact.Infrastructure.Tests.AgentControl;

[TestFixture]
public sealed class McpCatalogServiceTests
{
	private string _root = null!;
	private McpCatalogService _catalog = null!;
	private static readonly LaunchInjection Existing = new(["--existing"], new Dictionary<string, string> { ["PACT_SESSION_ID"] = "test" });

	[SetUp]
	public void SetUp()
	{
		_root = Path.Combine(TestContext.CurrentContext.WorkDirectory, Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(_root);
		_catalog = new(new AppPaths(Path.Combine(_root, "data")));
	}

	[TearDown]
	public void TearDown() => Directory.Delete(_root, recursive: true);

	[Test]
	public async Task ProjectSelectionOverridesProfileDefaultAndUsesSavedProfileIdentity()
	{
		var project = Path.Combine(_root, "project");
		var profile = Profile(AgentKind.Codex);
		var server = new McpCatalogServer("docs", "https://example.test/mcp", null, [], "DOCS_TOKEN");
		await Save([server], [new(profile.Id, profile.Kind, null, server.Id, true), new(profile.Id, profile.Kind, project, server.Id, false)]);

		var forDefault = _catalog.ComposeLaunch(profile, null, Existing);
		Assert.That(forDefault.EnvironmentVariables, Is.SameAs(Existing.EnvironmentVariables));
		Assert.That(forDefault.Arguments, Is.EqualTo(new[] { "--existing", "-c", "mcp_servers.pact_catalog_docs={ url = \"https://example.test/mcp\", bearer_token_env_var = \"DOCS_TOKEN\" }" }));
		Assert.That(_catalog.ComposeLaunch(profile, project + Path.DirectorySeparatorChar, Existing), Is.SameAs(Existing));
		Assert.That(_catalog.ComposeLaunch(profile with { Id = "other" }, project, Existing), Is.SameAs(Existing));
		Assert.That(_catalog.ComposeLaunch(profile with { Kind = AgentKind.Claude }, null, Existing), Is.SameAs(Existing));
	}

	[Test]
	public async Task ClaudeReceivesOnlySelectedServersAndKeepsExistingMcpArguments()
	{
		var project = Path.Combine(_root, "project");
		var profile = Profile(AgentKind.Claude);
		var remote = new McpCatalogServer("docs", "https://example.test/mcp", null, [], "DOCS_TOKEN");
		var local = new McpCatalogServer("local", null, "C:\\some tool\\server.exe", ["quote\" and $"], null);
		await Save([remote, local], [new(profile.Id, profile.Kind, project, remote.Id, true), new(profile.Id, profile.Kind, null, local.Id, true)]);
		var existing = Existing with { Arguments = ["--mcp-config", "pact-mcp.json"] };

		var result = _catalog.ComposeLaunch(profile, project, existing);
		Assert.That(result.Arguments.Take(2), Is.EqualTo(existing.Arguments));
		Assert.That(result.Arguments[2], Is.EqualTo("--mcp-config"));
		Assert.That(result.Arguments[3], Does.StartWith(Path.Combine(_root, "data", "Temp", "Session")));
		var servers = JsonNode.Parse(File.ReadAllText(result.Arguments[3]))!["mcpServers"]!;
		Assert.That(servers["pact_catalog_docs"]!["headers"]!["Authorization"]!.GetValue<string>(), Is.EqualTo("Bearer ${DOCS_TOKEN}"));
		Assert.That(servers["pact_catalog_local"]!["args"]![0]!.GetValue<string>(), Is.EqualTo("quote\" and $"));
		var defaultPath = _catalog.ComposeLaunch(profile, null, existing).Arguments[3];
		Assert.That(JsonNode.Parse(File.ReadAllText(defaultPath))!["mcpServers"]!["pact_catalog_docs"], Is.Null);
		Assert.That(_catalog.ComposeLaunch(profile, project, existing).Arguments[3], Is.EqualTo(result.Arguments[3]));
	}

	[Test]
	public async Task CodexStdioDefinitionPreservesCommandArgumentsAndEnvironmentNames()
	{
		var profile = Profile(AgentKind.Codex);
		var server = new McpCatalogServer("local", null, @"C:\some tool\server.exe", ["quote\" and $"], null)
		{ EnvironmentVariables = ["MCP_KEY"] };
		await Save([server], [new(profile.Id, profile.Kind, null, server.Id, true)]);
		var launch = _catalog.ComposeLaunch(profile, null, Existing);
		var table = (TomlTable)((TomlTable)Toml.ToModel(launch.Arguments[^1])["mcp_servers"])[server.AgentName];
		Assert.That(table["command"], Is.EqualTo(server.Command));
		Assert.That(table["args"], Is.EqualTo(server.Arguments));
		Assert.That(table["env_vars"], Is.EqualTo(server.EnvironmentVariables));
	}

	[TestCase("http://example.test/mcp", null, "TOKEN")]
	[TestCase("https://user:secret@example.test/mcp", null, null)]
	[TestCase("https://example.test/mcp#tools", null, null)]
	[TestCase("https://example.test/mcp", "server", null)]
	[TestCase("https://example.test/mcp", null, "TOKEN=value")]
	public void RejectsInvalidOrInsecureHttpDefinitions(string url, string? command, string? variable)
	{
		Assert.Throws<ArgumentException>(() => McpCatalogService.ValidateServer(new("docs", url, command, [], variable)));
	}

	[Test]
	public async Task SaveRejectsAStalePreviewAndPreservesOtherSettings()
	{
		Directory.CreateDirectory(Path.GetDirectoryName(_catalog.SettingsPath)!);
		File.WriteAllText(_catalog.SettingsPath, """{"formatExtension":{"value":17}}""");
		var first = _catalog.Read();
		await _catalog.SaveAsync(first, first.Settings with { Servers = [new("docs", "https://example.test/mcp", null, [], null)] }, CancellationToken.None);

		Assert.That(await _catalog.SaveAsync(first, first.Settings, CancellationToken.None), Is.False);
		Assert.That(JsonNode.Parse(File.ReadAllText(_catalog.SettingsPath))!["formatExtension"]!["value"]!.GetValue<int>(), Is.EqualTo(17));
		Assert.That(_catalog.Read().Settings.Servers.Select(server => server.Id), Is.EqualTo(new[] { "docs" }));
	}

	[Test]
	public void NativeInventoryReturnsSourceAndTransportWithoutConfigurationValues()
	{
		var project = Path.Combine(_root, "project");
		Directory.CreateDirectory(project);
		var profile = Profile(AgentKind.Claude);
		var userConfig = Path.Combine(_root, ".claude.json");
		var projectConfig = Path.Combine(project, ".mcp.json");
		File.WriteAllText(userConfig, """{"mcpServers":{"docs":{"type":"http","url":"https://example.test","headers":{"Authorization":"private-value"}}}}""");
		File.WriteAllText(projectConfig, """{"mcpServers":{"docs":{"command":"server"}}}""");

		var rows = _catalog.ReadNative(profile, project);
		Assert.That(rows, Is.EquivalentTo(new[] { new NativeMcpServer("docs", "HTTP", userConfig), new NativeMcpServer("docs", "stdio", projectConfig) }));
	}

	[Test]
	public void CodexInventoryIncludesProfileAndProjectDeclarations()
	{
		var project = Path.Combine(_root, "project");
		var native = Directory.CreateDirectory(Path.Combine(project, ".codex")).FullName;
		var userConfig = Path.Combine(_root, "config.toml");
		var projectConfig = Path.Combine(native, "config.toml");
		File.WriteAllText(userConfig, "[mcp_servers.docs]\nurl = \"https://example.test\"");
		File.WriteAllText(projectConfig, "[mcp_servers.local]\ncommand = \"server\"");
		Assert.That(_catalog.ReadNative(Profile(AgentKind.Codex), project), Is.EquivalentTo(new[]
		{
			new NativeMcpServer("docs", "HTTP", userConfig), new NativeMcpServer("local", "stdio", projectConfig)
		}));
	}

	private AgentProfileRecord Profile(AgentKind kind) => new("primary", kind, "Primary",
		kind == AgentKind.Codex ? "codex" : "claude", null, "pwsh")
	{ NativeConfigDirectory = _root };

	private async Task Save(McpCatalogServer[] servers, McpCatalogSelection[] selections)
	{
		var snapshot = _catalog.Read();
		Assert.That(await _catalog.SaveAsync(snapshot, snapshot.Settings with { Servers = servers, Selections = selections }, CancellationToken.None), Is.True);
	}
}
