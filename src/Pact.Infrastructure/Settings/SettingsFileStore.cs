using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.Agents;
using Pact.Core.AgentControl;
using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Core.RootTabs;
using Pact.Core.Scenarios;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Settings;

/// <summary>
/// Reads and writes the editable JSON settings files, and supplies the defaults used to seed
/// them on first run.
/// </summary>
public sealed class SettingsFileStore
{
	/// <summary>Port used when <c>agent-control.json</c> has not been created yet.</summary>
	public const int DefaultAgentControlPort = 8765;

	/// <summary>
	/// Serializer options shared by every settings file: indented, camel-cased, with enums
	/// written as camel-cased names. Reused so hand-edited files keep a consistent shape.
	/// </summary>
	public static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
	};
	private static readonly JsonSerializerOptions JsonWithoutNulls = new(JsonOptions)
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly AppPaths _paths;

	/// <summary>Creates a store over the layout derived from <paramref name="rootDirectory"/>.</summary>
	public SettingsFileStore(string rootDirectory)
		: this(new AppPaths(rootDirectory))
	{
	}

	/// <summary>Creates a store over an existing path layout.</summary>
	public SettingsFileStore(AppPaths paths)
	{
		ArgumentNullException.ThrowIfNull(paths);

		_paths = paths;
		Files =
		[
			new SettingsFileDescriptor(
				"projects.json",
				_paths.ProjectsPath,
				"Runtime project state. Each project contains its saved sessions; startCommand and resumeCommand are concrete commands that can be edited for a specific project."),
			new SettingsFileDescriptor(
				"root-tabs.json",
				_paths.RootTabsPath,
				"Project-independent terminal and browser tabs. pausedItemIds controls which saved items remain unloaded until explicitly resumed."),
			new SettingsFileDescriptor(
				"shell-profiles.json",
				_paths.ShellProfilesPath,
				"Shell launch profiles. Each entry becomes one launch button. commandTemplate starts a new terminal; resumeCommandTemplate is copied into new sessions as the initial resumeCommand and remains the fallback restore command."),
			new SettingsFileDescriptor(
				"review-profiles.json",
				_paths.ReviewProfilesPath,
				"Reviewer-only launch profiles used when an agent requests a review. These never appear in the project launch menu; commandTemplate carries the model and effort flags the reviewer should run with."),
			new SettingsFileDescriptor(
				"agent-control.json",
				_paths.AgentControlSettingsPath,
				"Loopback agent-control endpoint settings. The configured port must remain stable because durable consumers store its address."),
			new SettingsFileDescriptor(
				"prompt-templates.json",
				_paths.PromptTemplatesPath,
				"Prompt templates shown in the right panel. Supported placeholders include {project}, {task}, {selectedText}, and {otherSessionSummary}."),
			new SettingsFileDescriptor(
				"web-link-templates.json",
				_paths.WebLinkTemplatesPath,
				"Web link templates shown from a project. Supported placeholders: %gitLabRepoId% and %teamCityProjectId%. Templates are copied into saved web pages at creation time."),
			new SettingsFileDescriptor(
				"web-monitor-rules.json",
				_paths.WebMonitorRulesPath,
				"Declarative URL and DOM extractor rules for monitoring loaded web tabs. Starter records are disabled until their CHANGE-ME host markers are replaced."),
			new SettingsFileDescriptor(
				"scenarios.json",
				_paths.ScenariosPath,
				"Workspace scenario definitions shown in the right panel. Edit ids, iteration limits, prompt templates, reviewer instructions, and stop markers carefully; invalid values can break future scenario automation."),
			new SettingsFileDescriptor(
				"git-helpers.json",
				_paths.GitHelpersPath,
				"External git GUI helpers shown in the git popup. Each entry declares an executable probe and popup actions; helpers that do not resolve on this machine are hidden."),
			new SettingsFileDescriptor(
				"recent-directories.json",
				_paths.RecentDirectoriesPath,
				"Recent startup directories for the new-session dialog. Missing paths are ignored by the UI.")
		];
	}

	/// <summary>Every settings file this store manages, in settings-window order.</summary>
	public IReadOnlyList<SettingsFileDescriptor> Files { get; }

	/// <summary>Reads the configured stable loopback endpoint port.</summary>
	/// <returns>The configured port, or the default before the settings file is materialized.</returns>
	/// <exception cref="InvalidDataException">The persisted port is missing or outside 1-65535.</exception>
	public int ReadAgentControlPort()
	{
		if (!File.Exists(_paths.AgentControlSettingsPath))
		{
			return DefaultAgentControlPort;
		}

		using var document = JsonDocument.Parse(File.ReadAllText(_paths.AgentControlSettingsPath));
		if (!document.RootElement.TryGetProperty("port", out var portElement)
			|| !portElement.TryGetInt32(out var port)
			|| port is <= 0 or > ushort.MaxValue)
		{
			throw new InvalidDataException(
				$"'{_paths.AgentControlSettingsPath}' must contain a port from 1 through {ushort.MaxValue}.");
		}

		return port;
	}

	/// <summary>
	/// Reads whether sessions are connected to Pact's agent tools at launch. Absent or malformed
	/// settings keep the connection on, so an unreadable file never silently disarms the tools.
	/// </summary>
	public bool ReadAgentControlEnabled()
	{
		if (!File.Exists(_paths.AgentControlSettingsPath))
		{
			return true;
		}

		try
		{
			using var document = JsonDocument.Parse(File.ReadAllText(_paths.AgentControlSettingsPath));
			return !document.RootElement.TryGetProperty("enabled", out var enabled)
				|| enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
				|| enabled.GetBoolean();
		}
		catch (JsonException)
		{
			return true;
		}
	}

	/// <summary>
	/// Creates any missing settings file from its defaults. Existing files are left untouched,
	/// so user edits are never overwritten.
	/// </summary>
	public async Task EnsureDefaultFilesAsync(CancellationToken cancellationToken)
	{
		DataRootHousekeeping.Prepare(_paths);

		foreach (var file in Files)
		{
			if (File.Exists(file.Path))
			{
				continue;
			}

			var content = CreateDefaultContent(file.FileName);
			await AtomicFileWriter.WriteTextAsync(file.Path, content, _paths.AtomicTempDirectory, cancellationToken)
				.ConfigureAwait(false);
		}

		await MigrateShellProfilesAsync(cancellationToken).ConfigureAwait(false);
		await MigrateGitHelpersCommandsAsync(cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Reads a settings file's raw text, returning its default content when the file is absent.
	/// </summary>
	public async Task<string> ReadAsync(string fileName, CancellationToken cancellationToken)
	{
		var file = GetFile(fileName);
		if (!File.Exists(file.Path))
		{
			await EnsureDefaultFilesAsync(cancellationToken).ConfigureAwait(false);
		}

		return await File.ReadAllTextAsync(file.Path, cancellationToken).ConfigureAwait(false);
	}

	/// <summary>Writes a settings file's raw text atomically.</summary>
	public async Task SaveAsync(string fileName, string content, CancellationToken cancellationToken)
	{
		var file = GetFile(fileName);

		using (JsonDocument.Parse(content))
		{
		}

		await AtomicFileWriter.WriteTextAsync(file.Path, content, _paths.AtomicTempDirectory, cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Loads the launch profiles, falling back to defaults when the file is missing or unparseable.
	/// </summary>
	public async Task<IReadOnlyList<AgentProfileRecord>> LoadShellProfilesAsync(CancellationToken cancellationToken)
	{
		var json = await ReadAsync("shell-profiles.json", cancellationToken).ConfigureAwait(false);
		var profiles = JsonSerializer.Deserialize<AgentProfileRecord[]>(json, JsonOptions);
		return profiles?.Select(NormalizeLegacyResumeCommandTemplate).ToArray() ?? [];
	}

	/// <summary>
	/// Loads the prompt templates, falling back to defaults when the file is missing or unparseable.
	/// </summary>
	public async Task<IReadOnlyList<PromptTemplateRecord>> LoadPromptTemplatesAsync(CancellationToken cancellationToken)
	{
		var json = await ReadAsync("prompt-templates.json", cancellationToken).ConfigureAwait(false);
		var templates = JsonSerializer.Deserialize<PromptTemplateRecord[]>(json, JsonOptions);
		return templates ?? [];
	}

	/// <summary>
	/// Loads the web link templates, falling back to defaults when the file is missing or unparseable.
	/// </summary>
	public async Task<IReadOnlyList<WebLinkTemplateRecord>> LoadWebLinkTemplatesAsync(CancellationToken cancellationToken)
	{
		var json = await ReadAsync("web-link-templates.json", cancellationToken).ConfigureAwait(false);
		var templates = JsonSerializer.Deserialize<WebLinkTemplateRecord[]>(json, JsonOptions);
		return templates ?? [];
	}

	/// <summary>Loads the editable declarative web-monitoring rules in persisted file order.</summary>
	public async Task<IReadOnlyList<WebMonitorRule>> LoadWebMonitorRulesAsync(
		CancellationToken cancellationToken)
	{
		var json = await ReadAsync("web-monitor-rules.json", cancellationToken)
			.ConfigureAwait(false);
		var rules = JsonSerializer.Deserialize<WebMonitorRule[]>(json, JsonOptions);
		return rules ?? [];
	}

	/// <summary>
	/// Loads the scenario definitions through <see cref="ScenarioDefinitionStore"/>, which
	/// completes missing prompt templates from the shipped defaults.
	/// </summary>
	public Task<IReadOnlyList<ScenarioDefinition>> LoadScenarioDefinitionsAsync(CancellationToken cancellationToken)
	{
		ScenarioDefinitionStore store = new(_paths.ScenariosPath, _paths.AtomicTempDirectory);
		return store.LoadAsync(cancellationToken);
	}

	private SettingsFileDescriptor GetFile(string fileName) => Files.FirstOrDefault(file => string.Equals(file.FileName, fileName, StringComparison.OrdinalIgnoreCase))
			?? throw new ArgumentException($"Unknown settings file: {fileName}", nameof(fileName));

	private async Task MigrateShellProfilesAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.ShellProfilesPath))
		{
			return;
		}

		var json = await File.ReadAllTextAsync(_paths.ShellProfilesPath, cancellationToken)
			.ConfigureAwait(false);
		var profiles = JsonSerializer.Deserialize<AgentProfileRecord[]>(json, JsonOptions);
		if (profiles is null)
		{
			return;
		}

		var migratedProfiles = profiles
			.Select(NormalizeLegacyResumeCommandTemplate)
			.ToArray();
		if (profiles.SequenceEqual(migratedProfiles))
		{
			return;
		}

		var migratedJson = JsonSerializer.Serialize(migratedProfiles, JsonOptions);
		await AtomicFileWriter.WriteTextAsync(
			_paths.ShellProfilesPath,
			migratedJson,
			_paths.AtomicTempDirectory,
			cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Existing installs predate the "commands" array in git-helpers.json; write the built-in
	/// defaults into the file once so the settings section and raw JSON show them.
	/// </summary>
	private async Task MigrateGitHelpersCommandsAsync(CancellationToken cancellationToken)
	{
		if (!File.Exists(_paths.GitHelpersPath))
		{
			return;
		}

		var json = await File.ReadAllTextAsync(_paths.GitHelpersPath, cancellationToken)
			.ConfigureAwait(false);
		var migratedJson = WithDefaultGitCommands(json);
		if (migratedJson is null)
		{
			return;
		}

		await AtomicFileWriter.WriteTextAsync(
			_paths.GitHelpersPath,
			migratedJson,
			_paths.AtomicTempDirectory,
			cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>
	/// Returns <paramref name="json"/> with the default "commands" array added, or null when the
	/// document already has one (or is not an object / not parseable).
	/// </summary>
	private static string? WithDefaultGitCommands(string json)
	{
		System.Text.Json.Nodes.JsonNode? root;
		try
		{
			root = System.Text.Json.Nodes.JsonNode.Parse(json);
		}
		catch (JsonException)
		{
			return null;
		}

		if (root is not System.Text.Json.Nodes.JsonObject document || document["commands"] is not null)
		{
			return null;
		}

		document["commands"] = JsonSerializer.SerializeToNode(GitButtonCommandSet.Defaults, JsonWithoutNulls);
		return document.ToJsonString(JsonOptions);
	}

	private static AgentProfileRecord NormalizeLegacyResumeCommandTemplate(AgentProfileRecord profile)
	{
		var normalizedProfile = NormalizeLegacyProfileKind(profile);
		const string legacyPlaceholder = "{session}";
		if (string.IsNullOrWhiteSpace(normalizedProfile.ResumeCommandTemplate)
			|| !normalizedProfile.ResumeCommandTemplate.Contains(legacyPlaceholder, StringComparison.OrdinalIgnoreCase))
		{
			return normalizedProfile;
		}

		var withoutPlaceholder = normalizedProfile.ResumeCommandTemplate.Replace(
			legacyPlaceholder,
			string.Empty,
			StringComparison.OrdinalIgnoreCase);
		var normalized = string.Join(
			' ',
			withoutPlaceholder.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

		return normalizedProfile with
		{
			ResumeCommandTemplate = string.IsNullOrWhiteSpace(normalized) ? null : normalized
		};
	}

	private static AgentProfileRecord NormalizeLegacyProfileKind(AgentProfileRecord profile)
	{
		if (profile.Kind == AgentKind.Custom
			&& string.Equals(profile.Id, "pwsh", StringComparison.OrdinalIgnoreCase))
		{
			return profile with { Kind = AgentKind.Pwsh };
		}

		return profile;
	}

	private static string CreateDefaultContent(string fileName)
	{
		if (string.Equals(fileName, "scenarios.json", StringComparison.Ordinal))
		{
			return ScenarioDefinitionStore.ReadDefaultDefinitionsJson();
		}

		if (string.Equals(fileName, "git-helpers.json", StringComparison.Ordinal))
		{
			var defaults = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Defaults", "git-helpers-default.json"));
			return WithDefaultGitCommands(defaults) ?? defaults;
		}

		object document = fileName switch
		{
			"projects.json" => ProjectsDocument.CreateDefault(),
			"root-tabs.json" => RootTabsRecord.CreateDefault(),
			"shell-profiles.json" => CreateDefaultShellProfiles(),
			"review-profiles.json" => CreateDefaultReviewProfiles(),
			"agent-control.json" => new { Port = DefaultAgentControlPort, Enabled = true },
			"prompt-templates.json" => CreateDefaultPromptTemplates(),
			"web-link-templates.json" => CreateDefaultWebLinkTemplates(),
			"web-monitor-rules.json" => CreateDefaultWebMonitorRules(),
			"recent-directories.json" => Array.Empty<string>(),
			_ => throw new ArgumentException($"Unknown settings file: {fileName}", nameof(fileName))
		};

		return JsonSerializer.Serialize(document, JsonOptions);
	}

	/// <summary>Builds the built-in launch profiles used to seed <c>shell-profiles.json</c>.</summary>
	public static IReadOnlyList<AgentProfileRecord> CreateDefaultShellProfiles() => [
			new AgentProfileRecord(
				"codex",
				AgentKind.Codex,
				"Codex session",
				"codex",
				"codex resume",
				"pwsh"),
			new AgentProfileRecord("pwsh", AgentKind.Pwsh, "pwsh terminal", "pwsh", null, "pwsh"),
			new AgentProfileRecord(
				"claude",
				AgentKind.Claude,
				"Claude session",
				"claude",
				"claude --resume",
				"pwsh"),
			new AgentProfileRecord("hermes", AgentKind.Hermes, "Hermes session", "hermes", null, "pwsh")
		];

	/// <summary>Builds the built-in reviewer profiles used to seed <c>review-profiles.json</c>.</summary>
	public static IReadOnlyList<ReviewProfile> CreateDefaultReviewProfiles() => [
			new ReviewProfile(
				"claude-opus",
				"Claude Opus reviewer",
				AgentKind.Claude,
				"claude --model opus"),
			new ReviewProfile(
				"codex-high",
				"Codex high-effort reviewer",
				AgentKind.Codex,
				"codex -c model_reasoning_effort=high")
		];

	/// <summary>Builds the built-in prompt templates used to seed <c>prompt-templates.json</c>.</summary>
	public static IReadOnlyList<PromptTemplateRecord> CreateDefaultPromptTemplates() => [
			new PromptTemplateRecord(
				"handoff-summary",
				"Prepare handoff summary",
				"Summarize the current state for another coding agent. Include files touched, decisions made, open risks, and exact next commands.",
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"review-agent-output",
				"Review another agent output",
				"Review this other agent output and focus on correctness, missing tests, regressions, and risky assumptions:\n\n{selectedText}",
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"project-status",
				"Project status",
				"""
                Give a concise status update for project "{project}".
                Include current task "{task}", changed files, blockers, decisions made, risks, and the next concrete command or action.
                """,
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"review-git-diff",
				"Review git diff",
				"""
                Review the supplied git diff or change context for project "{project}".
                Focus on bugs, regressions, missing tests, unsafe assumptions, and changes needed before this is ready.

                Context:
                ```text
                {selectedText}
                ```
                """,
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"pause-summary",
				"Prepare pause summary",
				"""
                Prepare a pause summary for project "{project}" and task "{task}".
                Include what is in progress, important files, decisions, open risks, and the exact next step needed after resuming.
                """,
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"restore-briefing",
				"Restore briefing",
				"""
                Use this resume briefing for project "{project}" and task "{task}" to re-orient yourself.
                State what you understand, what needs checking first, and what action you will take next.

                Briefing:
                ```text
                {selectedText}
                ```
                """,
				SendByDefault: false,
				PromptActionType.Prompt),
			new PromptTemplateRecord(
				"git-status",
				"git status",
				"git status",
				SendByDefault: true,
				PromptActionType.TerminalCommand),
			new PromptTemplateRecord(
				"git-diff-stat",
				"git diff --stat",
				"git diff --stat",
				SendByDefault: true,
				PromptActionType.TerminalCommand)
		];

	/// <summary>Builds the built-in web link templates used to seed <c>web-link-templates.json</c>.</summary>
	public static IReadOnlyList<WebLinkTemplateRecord> CreateDefaultWebLinkTemplates() => [
			new WebLinkTemplateRecord("gitlab-root", "GitLab Repository", "https://gitlab.example.com/%gitLabRepoId%"),
			new WebLinkTemplateRecord("gitlab-merge-requests", "GitLab Requests", "https://gitlab.example.com/%gitLabRepoId%/-/merge_requests"),
			new WebLinkTemplateRecord("gitlab-tags", "GitLab Tags", "https://gitlab.example.com/%gitLabRepoId%/-/tags"),
			new WebLinkTemplateRecord("teamcity-project", "TeamCity Project", "https://teamcity.example.com/project.html?projectId=%teamCityProjectId%"),
			new WebLinkTemplateRecord("jira-root", "Jira", "https://jira.example.com/")
		];

	/// <summary>
	/// Creates stable disabled starter rules whose host markers must be customized before use.
	/// </summary>
	public static IReadOnlyList<WebMonitorRule> CreateDefaultWebMonitorRules() => [
			new WebMonitorRule(
				"teamcity-builds-example",
				"TeamCity builds",
				Enabled: false,
				@"^https://CHANGE-ME-teamcity\.example\.invalid/(?:.*)$",
				30,
				Activity: new WebMonitorExtractor(
					".build.running",
					WebMonitorValueSource.Count,
					AttributeName: null,
					MatchPattern: null,
					CaptureGroup: null),
				Revision: new WebMonitorExtractor(
					".build.finished:first-child",
					WebMonitorValueSource.Text,
					AttributeName: null,
					MatchPattern: @"Build #(\d+)",
					CaptureGroup: 1)),
			new WebMonitorRule(
				"gitlab-mr-discussions-example",
				"GitLab merge request discussions",
				Enabled: false,
				@"^https://CHANGE-ME-gitlab\.example\.invalid/.*/-/merge_requests/\d+",
				30,
				Activity: null,
				Revision: new WebMonitorExtractor(
					"[data-testid='discussion-count']",
					WebMonitorValueSource.Text,
					AttributeName: null,
					MatchPattern: @"(\d+)",
					CaptureGroup: 1))
		];
}

/// <summary>
/// One settings file the settings window can edit.
/// </summary>
/// <param name="FileName">File name, used as the key in read and save calls.</param>
/// <param name="Path">Absolute path on disk.</param>
/// <param name="HelpText">Short description shown alongside the file in the settings window.</param>
public sealed record SettingsFileDescriptor(
	string FileName,
	string Path,
	string HelpText);
