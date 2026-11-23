using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Storage;
using Tomlyn;

namespace Pact.Infrastructure.Skills;

/// <summary>Previews native skill visibility changes and composes project rules for visible terminal launches.</summary>
public sealed partial class SkillActivationService(AppPaths paths, string? sharedSkillsDirectory = null, string? userHome = null)
{
	internal static readonly SemaphoreSlim WriteGate = new(1);
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
	private static readonly StringComparer PathsEqual = StringComparer.OrdinalIgnoreCase;
	private readonly string _storage = Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).SettingsDirectory, "Skills");
	private readonly string _dataRoot = paths.RootDirectory;
	private readonly string _userHome = userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
	private readonly string _sharedSkillsDirectory = Path.GetFullPath(sharedSkillsDirectory ??
		Path.Combine(userHome ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agents", "skills"));

	/// <summary>Returns the native discovery directories relevant to a configured launch.</summary>
	public IReadOnlyList<string> SkillDirectories(AgentProfileRecord profile, string? projectDirectory)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var home = NativeHome(profile);
		List<string> directories = [Path.Combine(home, "skills")];
		if (profile.Kind == AgentKind.Codex)
		{
			directories.Add(_sharedSkillsDirectory);
		}

		if (projectDirectory is not null)
		{
			directories.Add(ProjectSkills(profile.Kind, projectDirectory));
		}

		return directories.Distinct(PathsEqual).ToArray();
	}

	/// <summary>Reads native settings and discovery paths without changing them.</summary>
	public SkillActivationState Inspect(AgentProfileRecord profile, string skillName, string skillFile, string? projectDirectory) =>
		InspectVisibility(profile, skillName, skillFile, projectDirectory, afterInstallation: false);

	/// <summary>Predicts visibility if the package is installed at the supplied manifest path, without writing files.</summary>
	public SkillActivationState InspectAfterInstallation(AgentProfileRecord profile, string skillName, string skillFile, string? projectDirectory) =>
		InspectVisibility(profile, skillName, skillFile, projectDirectory, afterInstallation: true);

	private SkillActivationState InspectVisibility(AgentProfileRecord profile, string skillName, string skillFile, string? projectDirectory,
		bool afterInstallation, NativeInspection? inspection = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentException.ThrowIfNullOrWhiteSpace(skillFile);
		ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
		inspection ??= new NativeInspection();
		var global = NativeConfig(profile);
		var globalFile = Discover(SkillDirectories(profile, null), skillFile, afterInstallation, inspection);
		var localFile = projectDirectory == null ? null : Discover([ProjectSkills(profile.Kind, projectDirectory)], skillFile, afterInstallation, inspection);
		var selected = localFile ?? globalFile ?? Path.GetFullPath(skillFile);
		var config = ConfigPath(profile, projectDirectory);
		var codex = profile.Kind == AgentKind.Codex;
		var rules = codex ? inspection.CodexRules(global) : [];
		var globalClaudeName = FolderName(globalFile ?? skillFile);
		var selectedClaudeName = FolderName(selected);
		var claudeSettings = codex ? null : inspection.JsonConfig(global);
		var globalSetting = codex
			? CodexSkillRules.IsEnabled(rules, skillName, selected, inspection.Manifest)
			: ClaudeMode(claudeSettings, globalClaudeName) != SkillMode.Disabled;
		var defaultEnabled = globalFile != null && globalSetting;
		var inherited = codex ? globalSetting : ClaudeMode(claudeSettings, selectedClaudeName) != SkillMode.Disabled;
		var mode = SkillMode.Inherit;
		if (projectDirectory != null)
		{
			if (!codex)
			{
				var shared = ClaudeMode(inspection.JsonConfig(Path.Combine(projectDirectory, ".claude", "settings.json")), selectedClaudeName);
				if (shared != SkillMode.Inherit)
				{
					inherited = shared == SkillMode.Enabled;
				}
			}
			var document = inspection.JsonConfig(config);
			mode = codex ? ProjectMode(document, inspection.Manifest(selected), inspection.Manifest) : ClaudeMode(document, selectedClaudeName);
		}
		var discovered = globalFile != null || localFile != null;
		var effective = discovered && (mode == SkillMode.Inherit ? inherited : mode == SkillMode.Enabled);
		var reason = !discovered ? "Outside this profile's skill directories."
			: mode == SkillMode.Inherit ? "Inherited from native settings."
			: codex ? "Project rule on launches through Pact." : "Project rule in Claude local settings.";
		var defaultMode = codex ? rules.LastOrDefault(rule => rule.MatchesPath(globalFile ?? selected, inspection.Manifest))?.Enabled switch
		{
			true => SkillMode.Enabled,
			false => SkillMode.Disabled,
			null => SkillMode.Inherit
		} : ClaudeMode(claudeSettings, globalClaudeName);
		return new(defaultEnabled, effective, mode, reason, config) { DefaultMode = defaultMode };
	}

	/// <summary>Prepares a single selected skill change and identifies every affected native profile and path.</summary>
	public SkillActivationPreview Preview(AgentProfileRecord profile, string skillName, string skillFile,
		string? projectDirectory, SkillMode mode, IReadOnlyList<AgentProfileRecord> profiles) =>
		Prepare(profile, skillName, skillFile, projectDirectory, mode, profiles, ReadFile);

	private SkillActivationPreview Prepare(AgentProfileRecord profile, string skillName, string skillFile,
		string? projectDirectory, SkillMode mode, IReadOnlyList<AgentProfileRecord> profiles, Func<string, string?> read)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentException.ThrowIfNullOrWhiteSpace(skillFile);
		ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
		if (!Enum.IsDefined(mode))
		{
			throw new ArgumentOutOfRangeException(nameof(mode));
		}

		if (!Path.IsPathFullyQualified(skillFile) || !File.Exists(skillFile)
			|| !PathsEqual.Equals(Path.GetFileName(skillFile), "SKILL.md"))
		{
			throw new InvalidOperationException("Select an installed SKILL.md.");
		}

		if (projectDirectory != null && !Path.IsPathFullyQualified(projectDirectory))
		{
			throw new InvalidOperationException("Select a project with an absolute directory.");
		}
		if (!CanConfigureMember(profile, null, skillFile, projectDirectory, profiles))
		{
			throw new InvalidOperationException(PrivateSkillMessage);
		}

		var config = SkillCatalogService.ResolveManifest(ConfigPath(profile, projectDirectory));
		var old = read(config);
		var global = Discover(SkillDirectories(profile, null), skillFile);
		var local = projectDirectory == null ? null : Discover([ProjectSkills(profile.Kind, projectDirectory)], skillFile);
		var discovered = local ?? global;
		var globalRoot = Path.Combine(NativeHome(profile), "skills");
		var linkRoot = projectDirectory == null ? globalRoot : ProjectSkills(profile.Kind, projectDirectory);
		var selectedFolder = Directory.GetParent(Path.GetFullPath(skillFile))!;
		var target = ResolveDirectory(selectedFolder.FullName);
		var scopeManifest = projectDirectory == null ? global : local;
		var linkPath = scopeManifest is null ? Path.Combine(linkRoot, FolderName(global ?? skillFile)) : Path.GetDirectoryName(scopeManifest)!;
		var receipt = ReceiptPath(linkPath);
		// A project explicitly enabled while the default bridge exists must survive disconnecting
		// that default later. Its own link goes directly to the installed package.
		var dependsOnManagedDefault = projectDirectory != null && local == null && global != null
			&& OwnedTarget(Path.GetDirectoryName(global)!, target) != null
			&& (!IsPrivatePackage(null, target, profiles) || IsWithinFolder(target, ResolveDirectory(globalRoot)));
		SkillLinkEdit? link = null;
		if (mode == SkillMode.Enabled && (discovered is null || dependsOnManagedDefault))
		{
			if (Path.Exists(linkPath) || new DirectoryInfo(linkPath).LinkTarget != null)
			{
				throw new InvalidOperationException($"Skill directory conflict between {target} and {linkPath}.");
			}

			link = new(linkPath, target, receipt, Create: true, PreviousReceipt: ReadFile(receipt));
			discovered = Path.Combine(linkPath, "SKILL.md");
		}
		else if ((mode == SkillMode.Inherit || projectDirectory == null && mode == SkillMode.Disabled)
			&& OwnedTarget(linkPath, target) is { } ownedTarget)
		{
			link = new(linkPath, ownedTarget, receipt, Create: false, ConnectionReceipt: ReadFile(receipt));
		}

		var selected = discovered ?? Path.GetFullPath(skillFile);
		string? updated;
		if (profile.Kind == AgentKind.Claude)
		{
			// Claude's override key is the discovered directory name, including junction aliases.
			updated = ClaudeSkillRules.Edit(old, FolderName(selected), mode switch
			{
				SkillMode.Enabled => "on",
				SkillMode.Disabled => "off",
				_ => null
			});
		}
		else
		{
			if (projectDirectory == null)
			{
				updated = CodexSkillRules.Edit(old, skillName, selected, mode);
			}
			else
			{
				if (mode != SkillMode.Inherit)
				{
					// Every native rule must be representable in the merged launch argument.
					_ = CodexSkillRules.Read(read(NativeConfig(profile)));
				}
				var doc = ParseObject(old);
				var key = FindPath(doc, Path.Combine(target, "SKILL.md"));
				if (mode == SkillMode.Inherit)
				{
					updated = doc.Remove(key) ? doc.ToJsonString(Indented) : old;
				}
				else
				{
					doc[key] = mode == SkillMode.Enabled;
					updated = doc.ToJsonString(Indented);
				}

			}
		}
		var affected = profiles.Where(candidate => candidate.Kind == profile.Kind && (
			projectDirectory != null ? profile.Kind == AgentKind.Claude || candidate.Id == profile.Id || link != null
			: SharesNativeConfig(candidate, config))).Append(profile).DistinctBy(p => p.Id)
			.Select(p => p.DisplayName).ToArray();
		var selector = profile.Kind == AgentKind.Claude ? FolderName(selected)
			: Path.Combine(target, "SKILL.md").ToUpperInvariant();
		return new(config, affected, old, updated, link, _storage, selector);
	}

	/// <summary>Applies the approved preview only while its inputs match the current configuration.</summary>
	public Task ApplyAsync(SkillActivationPreview preview, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(preview);
		var file = new SkillConfigEdit(preview.FilePath, preview.Original, preview.Updated);
		return ApplyAsync(new SkillActivationBatchPreview(preview.StorageRoot, [preview], [file],
			preview.Link is { } link ? [link] : []), token);
	}

	/// <summary>Combines current native skill rules with this project's explicit path choices at process start.</summary>
	public LaunchInjection ComposeLaunch(AgentProfileRecord profile, string? projectDirectory, LaunchInjection injection,
		Action<string>? reportDiagnostic = null, string? workingDirectory = null)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(injection);
		var skipped = false;
		var native = ComposeNativeLaunch(profile, projectDirectory, injection, message =>
		{
			skipped = true;
			reportDiagnostic?.Invoke(message);
		});
		if (skipped || profile.Kind is not (AgentKind.Claude or AgentKind.Codex) || !Directory.Exists(Path.Combine(_storage, "Policies")))
		{
			return native;
		}
		try
		{
			return ComposeMatrixLaunch(profile, projectDirectory, workingDirectory ?? projectDirectory, native);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or TomlException or ArgumentException)
		{
			var reason = ex is TomlException or JsonException ? "Invalid configuration syntax." : ex.Message;
			reportDiagnostic?.Invoke($"Source skill policies skipped for {profile.DisplayName}. Check {Path.Combine(_storage, "Policies")}. {reason}");
			return native;
		}
	}

	private LaunchInjection ComposeNativeLaunch(AgentProfileRecord profile, string? projectDirectory, LaunchInjection injection,
		Action<string>? reportDiagnostic)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(injection);
		if (profile.Kind is not (AgentKind.Codex or AgentKind.Claude))
		{
			return injection;
		}

		if (!string.IsNullOrWhiteSpace(profile.NativeConfigDirectory))
		{
			if (!Path.IsPathFullyQualified(profile.NativeConfigDirectory))
			{
				reportDiagnostic?.Invoke($"Skipped skill settings for {profile.DisplayName}. Choose an absolute native configuration directory: {profile.NativeConfigDirectory}.");
				return injection;
			}
			injection = injection with
			{
				EnvironmentVariables = new Dictionary<string, string>(injection.EnvironmentVariables, PathsEqual)
				{
					[HomeVariable(profile.Kind)] = profile.NativeConfigDirectory
				}
			};
		}
		if (profile.Kind != AgentKind.Codex || projectDirectory == null)
		{
			return injection;
		}

		var rulesPath = ProjectRulesPath(profile.Id, projectDirectory);
		var reading = rulesPath;
		try
		{
			var overrides = ParseObject(ReadFile(rulesPath));
			if (overrides.Count == 0)
			{
				return injection;
			}
			reading = NativeConfig(profile);
			var nativeContent = ReadFile(reading);
			reading = rulesPath;
			var changes = new Dictionary<string, bool>(PathsEqual);
			foreach (var entry in overrides)
			{
				if (!Path.IsPathFullyQualified(entry.Key) || entry.Value is not JsonValue value || !value.TryGetValue<bool>(out var enabled))
				{
					throw new InvalidOperationException("Expected an absolute skill path and a boolean setting.");
				}
				changes.Add(entry.Key, enabled);
			}
			reading = NativeConfig(profile);
			return WithCodexOverrides(injection, nativeContent, changes);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException or TomlException or ArgumentException)
		{
			var reason = ex is TomlException or JsonException ? "Invalid configuration syntax." : ex.Message;
			reportDiagnostic?.Invoke($"Project skill overrides skipped for {profile.DisplayName}. Check {reading}. {reason}");
			return injection;
		}
	}

	private static LaunchInjection WithCodexOverrides(LaunchInjection native, string? config, IReadOnlyDictionary<string, bool> changes,
		IReadOnlyList<CodexSkillRule>? parsed = null, Func<string, string>? resolve = null)
	{
		var args = native.Arguments.ToList();
		var index = -1;
		var prefix = "";
		for (var i = 0; i < args.Count; i++)
		{
			var candidate = args[i];
			var flag = candidate.StartsWith("--config=", StringComparison.Ordinal) ? "--config="
				: candidate.StartsWith("-c=", StringComparison.Ordinal) ? "-c=" : "";
			if ((flag.Length > 0 || i > 0 && args[i - 1] is "-c" or "--config")
				&& candidate.AsSpan(flag.Length).StartsWith("skills.config=", StringComparison.Ordinal))
			{
				index = i;
				prefix = flag;
			}
		}
		var rules = index < 0 && parsed is not null ? parsed.ToList() : CodexSkillRules.Read(index < 0 ? config : args[index][prefix.Length..]);
		foreach (var (file, enabled) in changes)
		{
			rules.RemoveAll(rule => rule.MatchesPath(file, resolve));
			rules.Add(new("path", file, enabled));
		}
		var update = $"skills.config=[{string.Join(", ", rules.Select(rule => rule.Inline))}]";
		if (index < 0)
		{
			args.Add("-c");
			args.Add(update);
		}
		else
		{
			args[index] = prefix + update;
		}
		return native with { Arguments = args };
	}

	private string NativeHome(AgentProfileRecord profile)
	{
		if (profile.Kind is not (AgentKind.Codex or AgentKind.Claude))
		{
			throw new InvalidOperationException("Skill configuration is available for Codex and Claude.");
		}

		var executable = profile.Kind == AgentKind.Codex ? "codex" : "claude";
		var match = ExecutablePattern().Match(profile.CommandTemplate.Trim());
		var command = Path.GetFileNameWithoutExtension(match.Value.Trim('"'));
		if (!PathsEqual.Equals(command, executable)
			|| CustomConfigPattern().IsMatch(profile.CommandTemplate))
		{
			throw new InvalidOperationException("This launch command's native skill configuration cannot be verified.");
		}

		var directory = profile.NativeConfigDirectory;
		if (string.IsNullOrWhiteSpace(directory))
		{
			directory = InheritedHome(_userHome, profile.Kind);
		}

		if (string.IsNullOrWhiteSpace(directory))
		{
			directory = Path.Combine(_userHome, "." + executable);
		}

		if (!Path.IsPathFullyQualified(directory))
		{
			throw new InvalidOperationException("The native configuration directory must be absolute.");
		}

		return Path.GetFullPath(directory);
	}

	private static string HomeVariable(AgentKind kind) => kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR";
	internal static string? InheritedHome(string userHome, AgentKind kind) =>
		PathsEqual.Equals(Path.GetFullPath(userHome), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
			? Environment.GetEnvironmentVariable(HomeVariable(kind)) : null;

	[GeneratedRegex("^(\"[^\"]+\"|[^\\s]+)")]
	private static partial Regex ExecutablePattern();

	[GeneratedRegex(@"(?:^|\s)(?:--settings(?:=|\s)|--setting-sources|--bare|--disable-slash-commands|-c(?:\s|=)|--config(?:\s|=)|--profile(?:\s|=)|-p(?:\s|=))")]
	private static partial Regex CustomConfigPattern();
	private static string ProjectSkills(AgentKind kind, string project) => Path.Combine(project, kind == AgentKind.Codex ? ".agents" : ".claude", "skills");
	private string NativeConfig(AgentProfileRecord profile) => Path.Combine(NativeHome(profile), profile.Kind == AgentKind.Codex ? "config.toml" : "settings.json");
	private string ProjectRulesPath(string profileId, string project) => Path.Combine(_storage, "ProjectRules", Hash(
		Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)).ToUpperInvariant() + "\0" + profileId) + ".json");
	private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

	private string ConfigPath(AgentProfileRecord profile, string? project)
	{
		var global = NativeConfig(profile);
		return project == null ? global : profile.Kind == AgentKind.Codex ? ProjectRulesPath(profile.Id, project)
			: Path.Combine(project, ".claude", "settings.local.json");
	}

	private bool SharesNativeConfig(AgentProfileRecord profile, string path)
	{
		try
		{ return PathsEqual.Equals(SkillCatalogService.ResolveManifest(NativeConfig(profile)), SkillCatalogService.ResolveManifest(path)); }
		catch (InvalidOperationException) { return false; }
	}

	private static string? Discover(IReadOnlyList<string> directories, string file, bool afterInstallation = false, NativeInspection? inspection = null)
	{
		var absolute = Path.GetFullPath(file);
		if (!PathsEqual.Equals(Path.GetFileName(absolute), "SKILL.md") || !afterInstallation && !File.Exists(absolute))
		{
			return null;
		}
		var physical = Resolve(absolute);
		var folder = Directory.GetParent(absolute)!;
		foreach (var directory in directories)
		{
			var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
			if (absolute.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return absolute;
			}
			var candidate = Path.Combine(root, folder.Name, "SKILL.md");
			if (PathsEqual.Equals(Resolve(candidate), physical))
			{
				return candidate;
			}
			if (!Directory.Exists(root))
			{
				continue;
			}
			// A native package directory can be a junction with a different name from its source.
			var aliases = inspection is null ? NativeInspection.ListAliases(root) : inspection.Aliases(root);
			foreach (var alias in aliases)
			{
				var manifest = Path.Combine(alias, "SKILL.md");
				if (PathsEqual.Equals(Resolve(manifest), physical))
				{
					return manifest;
				}
			}
		}
		return null;

		string Resolve(string path)
		{
			return inspection?.Manifest(path) ?? SkillCatalogService.ResolveManifest(path);
		}
	}

	internal static string ResolveDirectory(string directory) =>
		SkillCatalogService.ResolveDirectory(directory);

	private string ReceiptPath(string link) => Path.Combine(_storage, "Connections", Hash(link.ToUpperInvariant()) + ".txt");

	private static string FolderName(string manifest) => Directory.GetParent(Path.GetFullPath(manifest))!.Name;

	private string? OwnedTarget(string link, string target)
	{
		var original = SkillDirectoryConnection.ReadTarget(ReadFile(ReceiptPath(link)), link);
		return original != null && Directory.Exists(original) && new DirectoryInfo(link).LinkTarget != null
			&& PathsEqual.Equals(ResolveDirectory(original), target) && PathsEqual.Equals(ResolveDirectory(link), target)
			? original : null;
	}

	private static string? ReadFile(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
	private static JsonObject ParseObject(string? content) => content is null ? new() : JsonNode.Parse(content) as JsonObject
		?? throw new JsonException("Expected a settings object.");
	private static string FindPath(JsonObject doc, string file, Func<string, string>? resolve = null) => doc.Select(entry => entry.Key)
		.FirstOrDefault(key => PathsEqual.Equals(resolve?.Invoke(key) ?? SkillCatalogService.ResolveManifest(key), file)) ?? file;
	private static SkillMode ProjectMode(JsonObject doc, string file, Func<string, string> resolve) =>
		doc[FindPath(doc, file, resolve)]?.GetValue<bool>() switch { true => SkillMode.Enabled, false => SkillMode.Disabled, _ => SkillMode.Inherit };
	private static SkillMode ClaudeMode(JsonObject? doc, string name) => doc?["skillOverrides"]?[name]?.GetValue<string>() switch
	{
		null => SkillMode.Inherit,
		"off" => SkillMode.Disabled,
		"on" or "name-only" or "user-invocable-only" => SkillMode.Enabled,
		_ => throw new InvalidOperationException("Unrecognized native Claude skill visibility.")
	};

	private sealed class NativeInspection
	{
		private readonly Dictionary<string, JsonObject> _json = new(PathsEqual);
		private readonly Dictionary<string, List<CodexSkillRule>> _codex = new(PathsEqual);
		private readonly Dictionary<string, string> _manifests = new(PathsEqual);
		private readonly Dictionary<string, string[]> _aliases = new(PathsEqual);

		internal JsonObject JsonConfig(string path) => Get(_json, path, path => ParseObject(ReadFile(path)));
		internal List<CodexSkillRule> CodexRules(string path) => Get(_codex, path, path => CodexSkillRules.Read(ReadFile(path)));
		internal string Manifest(string path) => Get(_manifests, path, SkillCatalogService.ResolveManifest);
		internal string[] Aliases(string path) => Get(_aliases, path, ListAliases);
		internal static string[] ListAliases(string path) => new DirectoryInfo(path).EnumerateDirectories()
			.Where(alias => alias.LinkTarget is not null).Select(alias => alias.FullName).ToArray();

		private static T Get<T>(Dictionary<string, T> cache, string path, Func<string, T> read)
		{
			if (!cache.TryGetValue(path, out var value))
			{
				cache.Add(path, value = read(path));
			}
			return value;
		}
	}
}
