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
	public void FolderChoiceAppliesToNewProjectsWithNearestFolderAndProjectTakingPrecedence()
	{
		var group = Path.Combine(_root, "group");
		var nested = Path.Combine(group, "nested");
		var exception = Path.Combine(group, "project");
		var profile = Profile(AgentKind.Codex);
		Directory.CreateDirectory(Path.GetDirectoryName(_catalog.SettingsPath)!);
		File.WriteAllText(_catalog.SettingsPath, System.Text.Json.JsonSerializer.Serialize(new
		{
			servers = new[] { new { id = "docs", url = "https://example.test/mcp", arguments = Array.Empty<string>() } },
			selections = new[]
			{
				new { profileId = profile.Id, kind = "Codex", projectDirectory = (string?)null, serverId = "docs", enabled = true, isFolder = false },
				new { profileId = profile.Id, kind = "Codex", projectDirectory = (string?)group, serverId = "docs", enabled = false, isFolder = true },
				new { profileId = profile.Id, kind = "Codex", projectDirectory = (string?)nested, serverId = "docs", enabled = true, isFolder = true },
				new { profileId = profile.Id, kind = "Codex", projectDirectory = (string?)exception, serverId = "docs", enabled = true, isFolder = false }
			}
		}));

		Assert.That(_catalog.ComposeLaunch(profile, Path.Combine(group, "unopened"), Existing), Is.SameAs(Existing));
		Assert.That(_catalog.ComposeLaunch(profile, Path.Combine(nested, "new-project"), Existing).Arguments[^1], Does.Contain("pact_catalog_docs"));
		Assert.That(_catalog.ComposeLaunch(profile, exception, Existing).Arguments[^1], Does.Contain("pact_catalog_docs"));
		Assert.That(_catalog.ComposeLaunch(profile, group + "-other", Existing).Arguments[^1], Does.Contain("pact_catalog_docs"));
		Assert.That(_catalog.ComposeLaunch(profile, null, Existing).Arguments[^1], Does.Contain("pact_catalog_docs"));
	}

	[TestCase(AgentKind.Codex)]
	[TestCase(AgentKind.Claude)]
	public async Task FolderCanTurnOffNativeServerWithoutChangingItsSource(AgentKind kind)
	{
		var profile = Profile(kind);
		var group = Path.Combine(_root, "group");
		var project = Path.Combine(group, "project");
		var exception = Path.Combine(group, "exception");
		var config = Path.Combine(_root, kind == AgentKind.Codex ? "config.toml" : ".claude.json");
		var contents = kind == AgentKind.Codex ? "[mcp_servers.docs]\nurl = 'https://example.test/mcp'" :
			"""{"mcpServers":{"docs":{"type":"http","url":"https://example.test/mcp"}}}""";
		File.WriteAllText(config, contents);
		await Save([], [new(profile.Id, kind, group, "docs", false) { IsFolder = true, IsNative = true },
			new(profile.Id, kind, exception, "docs", true) { IsNative = true }]);
		var incoming = kind == AgentKind.Codex ? Existing : Existing with
		{
			Arguments = ["--settings", """{"skillOverrides":{"test":"off"},"deniedMcpServers":[{"serverName":"other"}]}"""]
		};

		var disabled = _catalog.ComposeLaunch(profile, project, incoming);
		var enabled = _catalog.ComposeLaunch(profile, exception, incoming);
		if (kind == AgentKind.Codex)
		{
			Assert.That(disabled.Arguments, Does.Contain("mcp_servers.docs.enabled=false"));
			Assert.That(enabled.Arguments, Does.Contain("mcp_servers.docs.enabled=true"));
		}
		else
		{
			Assert.That(disabled.Arguments.Count, Is.EqualTo(2));
			var settings = JsonNode.Parse(disabled.Arguments[1])!;
			Assert.That(settings["deniedMcpServers"]!.AsArray().Select(rule => rule!["serverName"]!.GetValue<string>()), Is.EqualTo(new[] { "other", "docs" }));
			Assert.That(settings["skillOverrides"]!["test"]!.GetValue<string>(), Is.EqualTo("off"));
			Assert.That(enabled, Is.SameAs(incoming));
		}
		Assert.That(_catalog.ComposeLaunch(profile, Path.Combine(_root, "other"), incoming), Is.SameAs(incoming));
		Assert.That(File.ReadAllText(config), Is.EqualTo(contents));
	}

	[Test]
	public async Task SharedNativeServerUsesTheOriginalInItsProfileAndPortableDefinitionElsewhere()
	{
		var claude = Profile(AgentKind.Claude);
		var codex = Profile(AgentKind.Codex) with { Id = "codex", NativeConfigDirectory = Path.Combine(_root, "codex") };
		var nativePath = Path.Combine(_root, ".claude.json");
		const string contents = """{"mcpServers":{"docs":{"type":"http","url":"https://example.test/mcp","headers":{"Authorization":"Bearer ${DOCS_TOKEN}"}}}}""";
		File.WriteAllText(nativePath, contents);
		var shared = new McpCatalogServer("docs", "https://example.test/mcp", null, [], "DOCS_TOKEN")
		{
			NativeOrigin = new(AgentKind.Claude, "docs", nativePath, null)
		};
		var group = Path.Combine(_root, "group");
		await Save([shared], [new(codex.Id, codex.Kind, null, shared.Id, true), new(claude.Id, claude.Kind, null, shared.Id, true),
			new(claude.Id, claude.Kind, group, shared.Id, false) { IsFolder = true }]);

		Assert.That(_catalog.ComposeLaunch(claude, null, Existing), Is.SameAs(Existing), "The owning profile already has its native declaration.");
		var blocked = _catalog.ComposeLaunch(claude, Path.Combine(group, "project"), Existing);
		Assert.That(JsonNode.Parse(blocked.Arguments[^1])!["deniedMcpServers"]![0]!["serverName"]!.GetValue<string>(), Is.EqualTo("docs"));
		Assert.That(_catalog.ComposeLaunch(codex, null, Existing).Arguments[^1], Does.Contain("bearer_token_env_var = \"DOCS_TOKEN\""));
		Assert.That(File.ReadAllText(nativePath), Is.EqualTo(contents));
	}

	[Test]
	public async Task CodexStdioDeclarationSharesArgumentsAndEnvironmentReferences()
	{
		var profile = Profile(AgentKind.Codex);
		File.WriteAllText(Path.Combine(_root, "config.toml"), "[mcp_servers.local]\ncommand = 'tools'\nargs = ['--docs']\nenv_vars = ['DOCS_TOKEN']\n[mcp_servers.local.env]\nDOCS_REGION = 'test'");
		var claude = Profile(AgentKind.Claude) with { NativeConfigDirectory = Path.Combine(_root, "claude") };
		var preview = McpCatalogService.PreviewNativeShare(profile, McpCatalogService.ReadNative(profile, null).Single(), [claude]);
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None), Is.True);
		var shared = JsonNode.Parse(File.ReadAllText(preview.Files.Single()))!["mcpServers"]!["local"]!;
		Assert.That(shared["command"]!.GetValue<string>(), Is.EqualTo("tools"));
		Assert.That(shared["args"]!.AsArray().Select(node => node!.GetValue<string>()), Is.EqualTo(new[] { "--docs" }));
		Assert.That(shared["env"]!["DOCS_TOKEN"]!.GetValue<string>(), Is.EqualTo("${DOCS_TOKEN}"));
		Assert.That(shared["env"]!["DOCS_REGION"]!.GetValue<string>(), Is.EqualTo("test"));
	}

	[Test]
	public async Task NativeCodexDisableAndRemovePreserveOtherServersAndSettings()
	{
		var profile = Profile(AgentKind.Codex);
		var file = Path.Combine(_root, "config.toml");
		const string original = "# Keep this preference\nmodel = 'local'\n[mcp_servers.docs]\n# Keep this comment inside the server\nurl = 'https://docs.test/mcp'\n[mcp_servers.other]\ncommand = 'tools'\n";
		File.WriteAllText(file, original);
		var source = McpCatalogService.ReadNative(profile, null).Single(server => server.Name == "docs");

		var disable = McpCatalogService.PreviewNativeEdit(profile, source, McpNativeEditAction.Disable);
		Assert.That(disable.Files, Is.EqualTo(new[] { file }));
		Assert.That(File.ReadAllText(file), Is.EqualTo(original));
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(disable, CancellationToken.None), Is.True);
		var disabled = File.ReadAllText(file);
		Assert.That(McpCatalogService.ReadNative(profile, null).Single(server => server.Name == "docs").IsEnabled, Is.False);
		var servers = (TomlTable)Toml.ToModel(disabled)["mcp_servers"];
		Assert.That(((TomlTable)servers["docs"])["enabled"], Is.False);
		Assert.That(((TomlTable)servers["other"])["command"], Is.EqualTo("tools"));
		Assert.That(disabled, Does.Contain("# Keep this preference").And.Contain("model = 'local'"));
		Assert.That(disabled, Does.Contain("# Keep this comment inside the server"));
		Assert.That(disabled.IndexOf("[mcp_servers.docs]", StringComparison.Ordinal), Is.LessThan(disabled.IndexOf("[mcp_servers.other]", StringComparison.Ordinal)));

		var remove = McpCatalogService.PreviewNativeEdit(profile, source, McpNativeEditAction.Remove);
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(remove, CancellationToken.None), Is.True);
		Assert.That(McpCatalogService.ReadNative(profile, null).Select(server => server.Name), Is.EqualTo(new[] { "other" }));
	}

	[Test]
	public async Task CodexLaunchDoesNotCreateAnOverrideForAnUnloadedProjectDeclaration()
	{
		var profile = Profile(AgentKind.Codex);
		var project = Path.Combine(_root, "project");
		var local = Directory.CreateDirectory(Path.Combine(project, ".codex")).FullName;
		File.WriteAllText(Path.Combine(local, "config.toml"), "[mcp_servers.docs]\nurl = 'https://example.test/mcp'\n");
		await Save([], [new(profile.Id, profile.Kind, project, "docs", false) { IsNative = true }]);
		Assert.That(_catalog.ComposeLaunch(profile, project, Existing), Is.SameAs(Existing));
		var config = Path.Combine(_root, "config.toml");
		File.WriteAllText(config, $"[projects.'{project}']\ntrust_level = 'trusted'\n");
		Assert.That(_catalog.ComposeLaunch(profile, project, Existing).Arguments, Does.Contain("mcp_servers.docs.enabled=false"));
		Assert.That(_catalog.ComposeLaunch(profile, project, Existing, _root), Is.SameAs(Existing));
	}

	[Test]
	public void ClaudeNativeDiscoveryCombinesUserAndProjectDenialsForNamesCommandsAndUrls()
	{
		var profile = Profile(AgentKind.Claude);
		var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var folder = Directory.CreateDirectory(Path.Combine(project, ".claude")).FullName;
		var userSettings = Path.Combine(_root, "settings.json");
		var projectSettings = Path.Combine(folder, "settings.json");
		var localSettings = Path.Combine(folder, "settings.local.json");
		File.WriteAllText(userSettings, """{"deniedMcpServers":[{"serverName":"docs"}]}""");
		File.WriteAllText(projectSettings, """{"deniedMcpServers":[{"serverUrl":"https://example.test/private/*"}]}""");
		File.WriteAllText(localSettings, """{"deniedMcpServers":[{"serverCommand":["tools","serve"]},{"serverName":"local"}]}""");
		File.WriteAllText(Path.Combine(project, ".mcp.json"), """
			{"mcpServers":{
			  "docs":{"type":"http","url":"https://example.test/mcp"},
			  "remote":{"type":"http","url":"https://example.test/private/mcp"},
			  "stdio":{"command":"tools","args":["serve"]},
			  "local":{"command":"local-tools"}, "open":{"command":"tools","args":["other"]}
			}}
			""");
		var declarations = McpCatalogService.ReadNative(profile, project).ToDictionary(server => server.Name);
		Assert.That(declarations["docs"].BlockingSettings, Is.EqualTo(userSettings));
		Assert.That(declarations["remote"].BlockingSettings, Is.EqualTo(projectSettings));
		Assert.That(declarations["stdio"].BlockingSettings, Is.EqualTo(localSettings));
		Assert.That(new[] { declarations["docs"], declarations["remote"], declarations["stdio"] }.All(server => server.IsEnabled == false && !server.CanEnable), Is.True);
		Assert.That(declarations["local"].CanEnable, Is.True);
		Assert.That(declarations["open"].IsEnabled, Is.True);
	}

	[Test]
	public void SharingDisabledCodexDefinitionToClaudeRequiresAnExplicitEnableFirst()
	{
		var source = Profile(AgentKind.Codex);
		var target = Profile(AgentKind.Claude);
		File.WriteAllText(Path.Combine(_root, "config.toml"), "[mcp_servers.docs]\nurl = 'https://example.test/mcp'\nenabled = false\n");
		var declaration = McpCatalogService.ReadNative(source, null).Single();
		Assert.That(() => McpCatalogService.PreviewNativeShare(source, declaration, [target]),
			Throws.InvalidOperationException.With.Message.Contain("disabled in Codex"));
		Assert.That(File.Exists(Path.Combine(_root, ".claude.json")), Is.False);
	}

	[Test]
	[Platform("Win")]
	public void SharingRollsBackAnEarlierDestinationWhenASecondNativeFileCannotBeReplaced()
	{
		var source = Profile(AgentKind.Codex);
		var first = Profile(AgentKind.Claude) with { Id = "first", NativeConfigDirectory = Path.Combine(_root, "first") };
		var second = Profile(AgentKind.Claude) with { Id = "second", NativeConfigDirectory = Path.Combine(_root, "second") };
		File.WriteAllText(Path.Combine(_root, "config.toml"), "[mcp_servers.docs]\nurl = 'https://example.test/mcp'\n");
		Directory.CreateDirectory(second.NativeConfigDirectory!);
		var secondPath = Path.Combine(second.NativeConfigDirectory!, ".claude.json");
		const string previous = """{"other":"untouched"}""";
		File.WriteAllText(secondPath, previous);
		var preview = McpCatalogService.PreviewNativeShare(source, McpCatalogService.ReadNative(source, null).Single(), [first, second]);
		var firstPath = Path.Combine(first.NativeConfigDirectory!, ".claude.json");
		Assert.That(preview.Files, Is.EqualTo(new[] { firstPath, secondPath }));
		using var locked = File.Open(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read);
		Assert.CatchAsync<IOException>(async () => await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None));
		Assert.That(File.Exists(firstPath), Is.False);
		Assert.That(File.ReadAllText(secondPath), Is.EqualTo(previous));
	}

	[Test]
	public async Task NativeClaudeDisableHonorsScopeAndRejectsAStalePreview()
	{
		var profile = Profile(AgentKind.Claude);
		var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
		var config = Path.Combine(project, ".mcp.json");
		File.WriteAllText(config, """{"mcpServers":{"docs":{"type":"http","url":"https://example.test/mcp"}}}""");
		var source = McpCatalogService.ReadNative(profile, project).Single();
		var settings = Path.Combine(Directory.CreateDirectory(Path.Combine(project, ".claude")).FullName, "settings.local.json");
		var stale = McpCatalogService.PreviewNativeEdit(profile, source, McpNativeEditAction.Disable);
		File.WriteAllText(settings, "{\r\n  \"otherSetting\": 17,\r\n  \"Проект\": \"имя + &\",\r\n  \"deniedMcpServers\": [{\"serverName\":\"other\"}]\r\n}\r\n");
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(stale, CancellationToken.None), Is.False);

		var disable = McpCatalogService.PreviewNativeEdit(profile, source, McpNativeEditAction.Disable);
		Assert.That(disable.Files, Is.EqualTo(new[] { settings }));
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(disable, CancellationToken.None), Is.True);
		var after = File.ReadAllText(settings);
		Assert.That(after, Does.Contain("\"Проект\": \"имя + &\"").And.EndWith("\r\n"));
		Assert.That(after.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n', StringComparison.Ordinal), Is.False);
		var saved = JsonNode.Parse(after)!;
		Assert.That(McpCatalogService.ReadNative(profile, project).Single().IsEnabled, Is.False);
		Assert.That(saved["otherSetting"]!.GetValue<int>(), Is.EqualTo(17));
		Assert.That(saved["deniedMcpServers"]!.AsArray().Select(rule => rule!["serverName"]!.GetValue<string>()), Is.EqualTo(new[] { "other", "docs" }));
		var enable = McpCatalogService.PreviewNativeEdit(profile, source, McpNativeEditAction.Enable);
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(enable, CancellationToken.None), Is.True);
		Assert.That(McpCatalogService.ReadNative(profile, project).Single().IsEnabled, Is.True);
		Assert.That(JsonNode.Parse(File.ReadAllText(settings))!["deniedMcpServers"]!.AsArray(), Has.Count.EqualTo(1));
	}

	[Test]
	public async Task UpdatingNativeBearerAndEndpointPreservesUnrelatedSettingsAndRejectsAStaleDraft()
	{
		var codex = Profile(AgentKind.Codex);
		var file = Path.Combine(_root, "config.toml");
		const string original = "model = 'keep'\n[mcp_servers.docs]\nurl = 'https://old.test/mcp'\nbearer_token_env_var = 'OLD_TOKEN'\nstartup_timeout_sec = 5\n[mcp_servers.docs.http_headers]\nX-Other = 'keep'\n[mcp_servers.other]\ncommand = 'other-tools'\n";
		File.WriteAllText(file, original);
		var source = McpCatalogService.ReadNative(codex, null).Single(server => server.Name == "docs");
		var draft = McpCatalogService.OpenNativeDefinition(codex, source);
		Assert.That(draft.Content, Does.Contain("OLD_TOKEN").And.Not.Contain("other-tools"));
		Assert.Throws<InvalidOperationException>(() => McpCatalogService.PreviewNativeDefinition(draft, draft.Content + "\n[mcp_servers.other]\ncommand = 'replace'"));
		var preview = McpCatalogService.PreviewNativeDefinition(draft, """
			[mcp_servers.docs]
			url = "https://new.test/mcp"
			startup_timeout_sec = 5
			[mcp_servers.docs.http_headers]
			# Keep this comment with the header.
			Authorization = "Bearer fixture-token"
			X-Other = "keep"
			""");
		Assert.That(preview.Files, Is.EqualTo(new[] { file }));
		Assert.That(File.ReadAllText(file), Is.EqualTo(original));
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None), Is.True);
		var savedContent = File.ReadAllText(file);
		Assert.That(savedContent, Does.Contain("# Keep this comment with the header."));
		var model = Toml.ToModel(savedContent);
		Assert.That(model["model"], Is.EqualTo("keep"));
		var servers = (TomlTable)model["mcp_servers"];
		Assert.That(((TomlTable)servers["other"])["command"], Is.EqualTo("other-tools"));
		var saved = (TomlTable)servers["docs"];
		Assert.That(saved["url"], Is.EqualTo("https://new.test/mcp"));
		Assert.That(saved.ContainsKey("bearer_token_env_var"), Is.False);
		Assert.That(saved["startup_timeout_sec"], Is.EqualTo(5));
		var headers = (TomlTable)saved["http_headers"];
		Assert.That(headers["Authorization"], Is.EqualTo("Bearer fixture-token"));
		Assert.That(headers["X-Other"], Is.EqualTo("keep"));
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None), Is.False);

		var current = McpCatalogService.OpenNativeDefinition(codex, source);
		var endpointOnly = McpCatalogService.PreviewNativeDefinition(current, current.Content.Replace("https://new.test/mcp", "https://next.test/mcp", StringComparison.Ordinal));
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(endpointOnly, CancellationToken.None), Is.True);
		var nextServers = (TomlTable)Toml.ToModel(File.ReadAllText(file))["mcp_servers"];
		Assert.That(((TomlTable)((TomlTable)nextServers["docs"])["http_headers"])["Authorization"], Is.EqualTo("Bearer fixture-token"));
	}

	[Test]
	public async Task UpdatingNativeStdioReplacesItsSelectedEnvironmentValue()
	{
		var codex = Profile(AgentKind.Codex);
		var file = Path.Combine(_root, "config.toml");
		File.WriteAllText(file, "[mcp_servers.docs]\ncommand = 'old-tools'\nargs = ['old']\nenv_vars = ['TOKEN', 'OTHER']\n");
		var draft = McpCatalogService.OpenNativeDefinition(codex, McpCatalogService.ReadNative(codex, null).Single());
		var preview = McpCatalogService.PreviewNativeDefinition(draft, """
			[mcp_servers.docs]
			command = "new-tools"
			args = ["--serve"]
			env_vars = ["OTHER"]
			[mcp_servers.docs.env]
			TOKEN = "fixture-token"
			""");
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None), Is.True);
		var servers = (TomlTable)Toml.ToModel(File.ReadAllText(file))["mcp_servers"];
		var saved = (TomlTable)servers["docs"];
		Assert.That(saved["command"], Is.EqualTo("new-tools"));
		Assert.That(saved["args"], Is.EqualTo(new[] { "--serve" }));
		Assert.That(saved["env_vars"], Is.EqualTo(new[] { "OTHER" }));
		Assert.That(((TomlTable)saved["env"])["TOKEN"], Is.EqualTo("fixture-token"));
	}

	[Test]
	public async Task SharingWritesCredentialsAndEnvironmentReferencesToTheDestinationAgent()
	{
		var claude = Profile(AgentKind.Claude);
		var codex = Profile(AgentKind.Codex) with { Id = "codex", NativeConfigDirectory = Path.Combine(_root, "codex") };
		var origin = Path.Combine(_root, ".claude.json");
		const string declaration = """{"mcpServers":{"docs":{"type":"http","url":"https://example.test/mcp","headers":{"Authorization":"Bearer fixture-value","X-Api-Key":"${DOCS_KEY}"}}}}""";
		File.WriteAllText(origin, declaration);
		var source = McpCatalogService.ReadNative(claude, null).Single();
		var destination = Path.Combine(codex.NativeConfigDirectory!, "config.toml");

		var preview = McpCatalogService.PreviewNativeShare(claude, source, [codex]);
		Assert.That(preview.Files, Is.EqualTo(new[] { destination }));
		Assert.That(preview.ToString(), Does.Not.Contain("fixture-value"));
		Assert.That(File.Exists(destination), Is.False);
		Assert.That(await McpCatalogService.ApplyNativeEditAsync(preview, CancellationToken.None), Is.True);
		var servers = (TomlTable)Toml.ToModel(File.ReadAllText(destination))["mcp_servers"];
		var shared = (TomlTable)servers["docs"];
		Assert.That(((TomlTable)shared["http_headers"])["Authorization"], Is.EqualTo("Bearer fixture-value"));
		Assert.That(((TomlTable)shared["env_http_headers"])["X-Api-Key"], Is.EqualTo("DOCS_KEY"));
		Assert.That(File.ReadAllText(origin), Is.EqualTo(declaration));
		Assert.That(File.Exists(_catalog.SettingsPath), Is.False);
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

		var rows = McpCatalogService.ReadNative(profile, project);
		Assert.That(rows, Is.EquivalentTo(new[] { new NativeMcpServer("docs", "HTTP", userConfig),
			new NativeMcpServer("docs", "stdio", projectConfig) { ProjectDirectory = project } }));
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
		Assert.That(McpCatalogService.ReadNative(Profile(AgentKind.Codex), project), Is.EquivalentTo(new[]
		{
			new NativeMcpServer("docs", "HTTP", userConfig), new NativeMcpServer("local", "stdio", projectConfig)
			{ ProjectDirectory = project, IsProjectConfigurationTrusted = false }
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
