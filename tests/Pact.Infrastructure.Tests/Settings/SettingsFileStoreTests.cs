using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Settings;

public sealed class SettingsFileStoreTests
{
	[Test]
	public async Task EnsureDefaultFilesAsync_creates_all_editable_settings_files()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		store.Files.Select(file => file.FileName).ToArray().ShouldBe(["projects.json", "root-tabs.json", "shell-profiles.json", "review-profiles.json", "agent-control.json", "prompt-templates.json", "web-link-templates.json", "web-monitor-rules.json", "scenarios.json", "git-helpers.json", "recent-directories.json"]);
		foreach (var file in store.Files)
		{
			File.Exists(file.Path).ShouldBeTrue(file.FileName);
			string.IsNullOrWhiteSpace(file.HelpText).ShouldBeFalse();
		}
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_CreatesTheStableAgentControlPort()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		SettingsFileStore store = new(temporaryDirectory.Path);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var json = JsonNode.Parse(
			await File.ReadAllTextAsync(
				new AppPaths(temporaryDirectory.Path).AgentControlSettingsPath))!;
		json["port"]!.GetValue<int>().ShouldBe(8765);
		json["enabled"]!.GetValue<bool>().ShouldBeTrue();
	}

	[Test]
	public async Task ReadAgentControlEnabled_lets_settings_disarm_the_connection()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		SettingsFileStore store = new(paths);

		store.ReadAgentControlEnabled().ShouldBeTrue("a missing file must keep the tools connected");

		await File.WriteAllTextAsync(paths.AgentControlSettingsPath, """{"port":8765,"enabled":false}""");
		store.ReadAgentControlEnabled().ShouldBeFalse();

		await File.WriteAllTextAsync(paths.AgentControlSettingsPath, """{"port":8765}""");
		store.ReadAgentControlEnabled().ShouldBeTrue();

		await File.WriteAllTextAsync(paths.AgentControlSettingsPath, "not json");
		store.ReadAgentControlEnabled().ShouldBeTrue("an unreadable file must not silently disarm the tools");
	}

	[Test]
	public async Task ReadAgentControlPort_ReturnsThePersistedPort()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(paths.AgentControlSettingsPath, """{"port":9123}""");
		SettingsFileStore store = new(paths);

		store.ReadAgentControlPort().ShouldBe(9123);
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_SeedsReviewProfilesIntoAFreshDataRoot()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		SettingsFileStore store = new(paths);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		File.Exists(paths.ReviewProfilesPath).ShouldBeTrue();
		var seeded = JsonNode.Parse(await File.ReadAllTextAsync(paths.ReviewProfilesPath))!.AsArray();
		seeded.Select(entry => (string?)entry!["id"]).ShouldContain("claude-opus");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_LeavesAnExistingReviewProfilesFileUntouched()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		AppPaths paths = new(temporaryDirectory.Path);
		SettingsFileStore store = new(paths);
		Directory.CreateDirectory(Path.GetDirectoryName(paths.ReviewProfilesPath)!);
		await File.WriteAllTextAsync(
			paths.ReviewProfilesPath,
			/*lang=json,strict*/ """[{"id":"mine","kind":"claude"}]""");

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		(await File.ReadAllTextAsync(paths.ReviewProfilesPath)).ShouldContain("mine");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_disabled_web_monitor_rule_examples()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		SettingsFileStore store = new(paths);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		paths.WebMonitorRulesPath.ShouldBe(
			Path.Combine(root, "Settings", "web-monitor-rules.json"));
		File.Exists(paths.WebMonitorRulesPath).ShouldBeTrue();

		var rules =
			await store.LoadWebMonitorRulesAsync(CancellationToken.None);

		rules.Count.ShouldBe(2);
		rules.Select(rule => rule.Id).ShouldBe(
			["teamcity-builds-example", "gitlab-mr-discussions-example"]);
		rules.ShouldAllBe(rule => !rule.Enabled);

		var teamCity = rules[0];
		teamCity.Title.ShouldBe("TeamCity builds");
		teamCity.UrlPattern.ShouldBe(
			@"^https://CHANGE-ME-teamcity\.example\.invalid/(?:.*)$");
		teamCity.PollIntervalSeconds.ShouldBe(30);
		teamCity.Activity.ShouldBe(new WebMonitorExtractor(
			".build.running",
			WebMonitorValueSource.Count,
			AttributeName: null,
			MatchPattern: null,
			CaptureGroup: null));
		teamCity.Revision.ShouldBe(new WebMonitorExtractor(
			".build.finished:first-child",
			WebMonitorValueSource.Text,
			AttributeName: null,
			MatchPattern: @"Build #(\d+)",
			CaptureGroup: 1));

		var gitLab = rules[1];
		gitLab.Title.ShouldBe("GitLab merge request discussions");
		gitLab.UrlPattern.ShouldBe(
			@"^https://CHANGE-ME-gitlab\.example\.invalid/.*/-/merge_requests/\d+");
		gitLab.PollIntervalSeconds.ShouldBe(30);
		gitLab.Activity.ShouldBeNull();
		gitLab.Revision.ShouldBe(new WebMonitorExtractor(
			"[data-testid='discussion-count']",
			WebMonitorValueSource.Text,
			AttributeName: null,
			MatchPattern: @"(\d+)",
			CaptureGroup: 1));
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_empty_projects_document()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var projectsJson = await store.ReadAsync("projects.json", CancellationToken.None);
		var projectsDocument = JsonSerializer.Deserialize<ProjectsDocument>(
			projectsJson,
			SettingsFileStore.JsonOptions);

		projectsDocument.ShouldNotBeNull();
		projectsDocument.SchemaVersion.ShouldBe(1);
		projectsDocument.Projects.ShouldBeEmpty();
		File.Exists(Path.Combine(root, "settings.json")).ShouldBeFalse();
		File.Exists(Path.Combine(root, "registry.json")).ShouldBeFalse();
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_external_default_prompt_templates()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var promptTemplates =
			await store.LoadPromptTemplatesAsync(CancellationToken.None);

		promptTemplates.ShouldContain(template => template.Id == "handoff-summary"
			&& template.EffectiveType == PromptActionType.Prompt);
		promptTemplates.ShouldContain(template => template.Id == "review-agent-output"
			&& template.EffectiveType == PromptActionType.Prompt);
		promptTemplates.ShouldContain(template => template.Id == "git-status"
			&& template.EffectiveType == PromptActionType.TerminalCommand);
		promptTemplates.ShouldContain(template => template.Id == "restore-briefing"
			&& template.EffectiveType == PromptActionType.Prompt);
		promptTemplates
			.Where(template => template.EffectiveType == PromptActionType.Prompt)
			.ShouldAllBe(template => !template.SendByDefault);
		promptTemplates
			.Where(template => template.EffectiveType == PromptActionType.TerminalCommand)
			.ShouldAllBe(template => template.SendByDefault);
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_web_link_templates_with_reserved_hosts()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		store.Files.ShouldContain(file => file.FileName == "web-link-templates.json");
		var templates =
			await store.LoadWebLinkTemplatesAsync(CancellationToken.None);
		templates.ShouldContain(template => template.Id == "gitlab-root"
			&& template.StartUrl == "https://gitlab.example.com/%gitLabRepoId%");
		templates.ShouldContain(template => template.Id == "gitlab-merge-requests"
			&& template.StartUrl == "https://gitlab.example.com/%gitLabRepoId%/-/merge_requests");
		templates.ShouldContain(template => template.Id == "gitlab-tags"
			&& template.StartUrl == "https://gitlab.example.com/%gitLabRepoId%/-/tags");
		templates.ShouldContain(template => template.Id == "teamcity-project"
			&& template.StartUrl == "https://teamcity.example.com/project.html?projectId=%teamCityProjectId%");
		templates.ShouldContain(template => template.Id == "jira-root"
			&& template.StartUrl == "https://jira.example.com/");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_external_default_git_helpers()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		store.Files.ShouldContain(file => file.FileName == "git-helpers.json");
		var gitHelpersJson = await store.ReadAsync("git-helpers.json", CancellationToken.None);
		gitHelpersJson.ShouldContain("\"id\": \"tortoisegit\"");
		gitHelpersJson.ShouldContain("\"slot\": \"history\"");
		gitHelpersJson.ShouldContain("\"slot\": \"resolve\"");
		gitHelpersJson.ShouldContain("\"id\": \"pull\"");
		gitHelpersJson.ShouldContain("pull --no-rebase");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_adds_default_commands_to_existing_git_helpers_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		await File.WriteAllTextAsync(
			paths.GitHelpersPath,
								 /*lang=json,strict*/
								 """{ "helpers": [], "extra": "kept" }""",
			CancellationToken.None);
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var migratedJson = await store.ReadAsync("git-helpers.json", CancellationToken.None);
		migratedJson.ShouldContain("\"id\": \"pull\"");
		migratedJson.ShouldContain("\"kept\"");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_keeps_existing_commands_array_untouched()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		var original = /*lang=json,strict*/ """{ "helpers": [], "commands": [ { "id": "pull", "label": "Pull", "command": "pull --rebase" } ] }""";
		await File.WriteAllTextAsync(paths.GitHelpersPath, original, CancellationToken.None);
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var json = await store.ReadAsync("git-helpers.json", CancellationToken.None);
		json.ShouldContain("pull --rebase");
		json.ShouldNotContain("stash pop");
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_creates_external_default_scenarios()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var scenarios =
			await store.LoadScenarioDefinitionsAsync(CancellationToken.None);

		scenarios.Select(scenario => scenario.Id).ToArray().ShouldBe(["plan-review", "code-review"]);
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_copies_bundled_default_scenarios_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var scenariosJson = await store.ReadAsync("scenarios.json", CancellationToken.None);
		scenariosJson.ShouldContain("\"kind\": \"reviewLoop\"");
		scenariosJson.ShouldContain("\"startPromptTemplate\"");
		scenariosJson.ShouldContain("\"reviewerInstructions\"");
		scenariosJson.Contains("requiredRoles", StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
	}

	[Test]
	public void CreateDefaultShellProfiles_uses_picker_resume_commands_for_agent_profiles()
	{
		var profiles = SettingsFileStore.CreateDefaultShellProfiles();

		profiles.Single(profile => profile.Id == "codex").ResumeCommandTemplate.ShouldBe("codex resume");
		profiles.Single(profile => profile.Id == "claude").ResumeCommandTemplate.ShouldBe("claude --resume");
	}

	[Test]
	public async Task LoadPromptTemplatesAsync_reads_external_prompt_templates_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);
		PromptTemplateRecord[] expected =
		[
			new("custom", "Custom Prompt", "Body {task}", SendByDefault: true)
		];

		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		await store.SaveAsync(
			"prompt-templates.json",
			JsonSerializer.Serialize(expected, SettingsFileStore.JsonOptions),
			CancellationToken.None);

		var actual = await store.LoadPromptTemplatesAsync(CancellationToken.None);

		actual.Single().EffectiveType.ShouldBe(PromptActionType.Prompt);
		actual.ShouldBe(expected);
	}

	[Test]
	public async Task LoadWebLinkTemplatesAsync_reads_external_web_link_templates_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);
		WebLinkTemplateRecord[] expected =
		[
			new("custom-link", "Custom Link", "https://example/%gitLabRepoId%")
		];

		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		await store.SaveAsync(
			"web-link-templates.json",
			JsonSerializer.Serialize(expected, SettingsFileStore.JsonOptions),
			CancellationToken.None);

		var actual = await store.LoadWebLinkTemplatesAsync(CancellationToken.None);

		actual.ShouldBe(expected);
	}

	[Test]
	public async Task LoadShellProfilesAsync_reads_external_shell_profiles_file()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);
		AgentProfileRecord[] expected =
		[
			new("codex", AgentKind.Codex, "Custom Codex", "codex --model gpt-5", "codex resume --all", "pwsh")
		];

		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		await store.SaveAsync(
			"shell-profiles.json",
			JsonSerializer.Serialize(expected, SettingsFileStore.JsonOptions),
			CancellationToken.None);

		var actual = await store.LoadShellProfilesAsync(CancellationToken.None);

		actual.ShouldBe(expected);
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_migrates_legacy_resume_template_placeholders()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		AgentProfileRecord[] legacyProfiles =
		[
			new("codex", AgentKind.Codex, "Codex session", "codex", "codex resume {session}", "pwsh"),
			new("claude", AgentKind.Claude, "Claude session", "claude", "claude --resume {session}", "pwsh")
		];
		await File.WriteAllTextAsync(
			paths.ShellProfilesPath,
			JsonSerializer.Serialize(legacyProfiles, SettingsFileStore.JsonOptions),
			CancellationToken.None);
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var profiles = await store.LoadShellProfilesAsync(CancellationToken.None);
		profiles.Single(profile => profile.Id == "codex").ResumeCommandTemplate.ShouldBe("codex resume");
		profiles.Single(profile => profile.Id == "claude").ResumeCommandTemplate.ShouldBe("claude --resume");

		var migratedJson = await store.ReadAsync("shell-profiles.json", CancellationToken.None);
		migratedJson.Contains("{session}", StringComparison.Ordinal).ShouldBeFalse();
	}

	[Test]
	public async Task EnsureDefaultFilesAsync_migrates_legacy_pwsh_profile_kind()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		AppPaths paths = new(root);
		Directory.CreateDirectory(paths.SettingsDirectory);
		AgentProfileRecord[] legacyProfiles =
		[
			new("pwsh", AgentKind.Custom, "pwsh terminal", "pwsh", null, "pwsh"),
			new("ssh-prod", AgentKind.Custom, "prod ssh", "ssh user@server", null, "pwsh")
		];
		await File.WriteAllTextAsync(
			paths.ShellProfilesPath,
			JsonSerializer.Serialize(legacyProfiles, SettingsFileStore.JsonOptions),
			CancellationToken.None);
		SettingsFileStore store = new(root);

		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		var profiles = await store.LoadShellProfilesAsync(CancellationToken.None);
		profiles.Single(profile => profile.Id == "pwsh").Kind.ShouldBe(AgentKind.Pwsh);
		profiles.Single(profile => profile.Id == "ssh-prod").Kind.ShouldBe(AgentKind.Custom);
	}

	[Test]
	public async Task SaveAsync_rejects_invalid_json()
	{
		using var temporaryDirectory = TemporaryDirectory.Create();
		var root = temporaryDirectory.Path;
		SettingsFileStore store = new(root);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);

		await Should.ThrowAsync<JsonException>(
			() => store.SaveAsync("projects.json", "{not-json", CancellationToken.None));
	}
}
