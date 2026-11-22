using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;

namespace Pact.Infrastructure.Skills;

internal sealed class SkillMatrixPolicy(string? original)
{
	private readonly JsonObject _document = original == null ? [] : JsonNode.Parse(original) as JsonObject
		?? throw new InvalidOperationException("Expected an object in the skill policy document.");

	internal string? Original => original;
	internal string? Updated => JsonNode.DeepEquals(_document, original == null ? new JsonObject() : JsonNode.Parse(original))
		? original : _document.ToJsonString();

	internal SkillMode Source(SkillCatalogSource source) => GetMode("sources", SourceKey(source));

	internal SkillMode Child(string manifest, SkillMode native)
	{
		var key = ManifestKey(manifest);
		var explicitMode = GetMode("children", StoredManifestKey("children", key));
		return explicitMode != SkillMode.Inherit ? explicitMode
			: _document["materialized"]?[StoredManifestKey("materialized", key)] != null ? SkillMode.Inherit : native;
	}

	internal void SetSource(SkillCatalogSource source, SkillMode mode) => Set("sources", SourceKey(source), Encode(mode));
	internal void SetChild(string file, SkillMode mode) => SetManifest("children", file, Encode(mode));
	internal void SetMaterialized(string file, bool inherited) => SetManifest("materialized", file, inherited ? true : null);
	internal bool HasChild(string file) => GetMode("children", StoredManifestKey("children", ManifestKey(file))) != SkillMode.Inherit;

	internal string? Folder => _document["folder"]?.GetValue<string>();
	internal void IdentifyFolder(string folder)
	{
		if (_document.ContainsKey("sources") || _document.ContainsKey("children"))
		{
			_document["folder"] = folder;
		}
		else
		{
			_document.Remove("folder");
		}
	}

	// Make shared leaves a native alias in place. Policies saved before the move still use that path.
	private string StoredManifestKey(string section, string key) =>
		_document[section] is JsonObject entries && !entries.ContainsKey(key)
			? entries.Select(pair => pair.Key).FirstOrDefault(path => Path.IsPathFullyQualified(path) && ManifestKey(path) == key) ?? key
			: key;

	private void SetManifest(string section, string file, bool? value)
	{
		var key = ManifestKey(file);
		if (_document[section] is JsonObject entries)
		{
			foreach (var alias in entries.Select(pair => pair.Key)
				.Where(path => path != key && Path.IsPathFullyQualified(path) && ManifestKey(path) == key).ToArray())
			{
				Set(section, alias, null);
			}
		}
		Set(section, key, value);
	}

	internal static string SourceKey(SkillCatalogSource source)
	{
		var location = source.Location;
		if (Path.IsPathFullyQualified(location))
		{
			location = Path.TrimEndingDirectorySeparator(SkillCatalogService.ResolveDirectory(location)).ToUpperInvariant();
		}
		return location;
	}

	internal static string ManifestKey(string file) => Path.Combine(SkillCatalogService.ResolveDirectory(Path.GetDirectoryName(file)!), Path.GetFileName(file)).ToUpperInvariant();

	private SkillMode GetMode(string section, string key) => _document[section]?[key]?.GetValue<bool>() switch
	{
		true => SkillMode.Enabled,
		false => SkillMode.Disabled,
		null => SkillMode.Inherit
	};

	private static bool? Encode(SkillMode mode) => mode switch
	{
		SkillMode.Enabled => true,
		SkillMode.Disabled => false,
		SkillMode.Inherit => null,
		_ => throw new ArgumentOutOfRangeException(nameof(mode))
	};

	private void Set(string section, string key, bool? value)
	{
		if (value == null)
		{
			if (_document[section] is JsonObject items)
			{
				items.Remove(key);
				if (items.Count == 0)
				{
					_document.Remove(section);
				}
			}
			return;
		}
		_document[section] ??= new JsonObject();
		_document[section]![key] = value.Value;
	}
}

public sealed partial class SkillActivationService
{
	private LaunchInjection ComposeMatrixLaunch(AgentProfileRecord profile, string? projectDirectory, string? workingDirectory, LaunchInjection native)
	{
		var defaults = ReadFile(PolicyPath(profile, null));
		var local = projectDirectory == null ? null : ReadFile(PolicyPath(profile, projectDirectory));
		var folders = FolderPolicies(profile, workingDirectory);
		if (defaults == null && local == null && folders.Count == 0)
		{
			return native;
		}
		var globalPolicy = new SkillMatrixPolicy(defaults);
		var projectPolicy = projectDirectory == null ? null : new SkillMatrixPolicy(local);
		var catalog = new SkillCatalogService(Path.GetDirectoryName(_storage)!, _sharedSkillsDirectory, _userHome);
		var changes = new Dictionary<string, bool>(PathsEqual);
		var additions = new HashSet<string>(PathsEqual);
		foreach (var member in catalog.DiscoverPolicyMembers(profile, this, projectDirectory, includeShared: folders.Count > 0))
		{
			if (globalPolicy.Source(member.Source) == SkillMode.Inherit && !globalPolicy.HasChild(member.SkillFile)
				&& (projectPolicy == null || projectPolicy.Source(member.Source) == SkillMode.Inherit && !projectPolicy.HasChild(member.SkillFile))
				&& !folders.Any(folder => folder.Source(member.Source) != SkillMode.Inherit || folder.HasChild(member.SkillFile)))
			{
				continue;
			}
			var state = MatrixState(profile, member.Source, member.SkillName, member.SkillFile, projectDirectory, globalPolicy, projectPolicy, folders);
			var alias = profile.Kind == AgentKind.Claude ? Discover(SkillDirectories(profile, projectDirectory), member.SkillFile) : member.SkillFile;
			if (state.EffectiveEnabled && state.OriginDirectory != null
				&& alias == null
				&& FolderDiscoveryArgument(profile, state.OriginDirectory, member.SkillFile, materialize: true) is { } directory)
			{
				additions.Add(directory);
			}
			if (state.EffectiveEnabled != Inspect(profile, member.SkillName, member.SkillFile, projectDirectory).EffectiveEnabled)
			{
				var selector = profile.Kind == AgentKind.Codex ? member.SkillFile : FolderName(alias ?? member.SkillFile);
				changes[selector] = state.EffectiveEnabled;
			}
		}
		if (additions.Count != 0)
		{
			native = native with { Arguments = [.. native.Arguments, .. additions.SelectMany(directory => new[] { "--add-dir", directory })] };
		}
		if (changes.Count == 0)
		{
			return native;
		}
		if (profile.Kind == AgentKind.Claude)
		{
			var overrides = new JsonObject();
			foreach (var (name, enabled) in changes)
			{
				overrides[name] = enabled ? "on" : "off";
			}
			var arguments = native.Arguments.ToList();
			var settingIndex = arguments.FindLastIndex(arg => arg == "--settings" || arg.StartsWith("--settings=", StringComparison.Ordinal));
			if (settingIndex < 0)
			{
				var settings = new JsonObject { ["skillOverrides"] = overrides };
				arguments.Add("--settings");
				arguments.Add(settings.ToJsonString());
			}
			else
			{
				var inline = arguments[settingIndex] != "--settings";
				var valueIndex = inline ? settingIndex : settingIndex + 1;
				var original = valueIndex >= arguments.Count ? string.Empty
					: inline ? arguments[valueIndex]["--settings=".Length..] : arguments[valueIndex];
				if (!original.TrimStart().StartsWith('{'))
				{
					throw new InvalidOperationException("The existing Claude settings argument is not inline JSON.");
				}
				var settings = ParseObject(original);
				settings["skillOverrides"] ??= new JsonObject();
				foreach (var entry in changes)
				{
					settings["skillOverrides"]![entry.Key] = entry.Value ? "on" : "off";
				}
				arguments[valueIndex] = (inline ? "--settings=" : "") + settings.ToJsonString();
			}
			return native with { Arguments = arguments };
		}
		return WithCodexOverrides(native, ReadFile(NativeConfig(profile)), changes);
	}

	private string PolicyPath(AgentProfileRecord profile, string? project, bool folderScope = false)
	{
		if (folderScope)
		{
			_ = NativeHome(profile);
			return Path.Combine(_storage, "Policies", "folder-" + Hash(profile.Id + "\0" + NormalizeFolder(project).ToUpperInvariant()) + ".json");
		}
		return Path.Combine(_storage, "Policies", Hash(SkillCatalogService.ResolveManifest(ConfigPath(profile, project)).ToUpperInvariant()) + ".json");
	}

	private static string NormalizeFolder(string? directory) => !string.IsNullOrWhiteSpace(directory) && Path.IsPathFullyQualified(directory)
		? Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) : throw new ArgumentException("Select an absolute folder path.");

	private List<SkillMatrixPolicy> FolderPolicies(AgentProfileRecord profile, string? directory)
	{
		var result = new List<SkillMatrixPolicy>();
		for (var current = directory == null ? null : new DirectoryInfo(NormalizeFolder(directory)); current != null; current = current.Parent)
		{
			var content = ReadFile(PolicyPath(profile, current.FullName, true));
			if (content != null && new SkillMatrixPolicy(content) is { Folder: not null } policy)
			{
				result.Add(policy);
			}
		}
		return result;
	}

	/// <summary>Lists folders with saved mappings, including folders that have no saved Pact project.</summary>
	public IReadOnlyList<string> ListFolderScopes(Action<string>? reportDiagnostic = null)
	{
		var directory = Path.Combine(_storage, "Policies");
		var folders = new SortedSet<string>(PathsEqual);
		if (Directory.Exists(directory))
		{
			foreach (var path in Directory.EnumerateFiles(directory, "folder-*.json"))
			{
				try
				{
					if (new SkillMatrixPolicy(ReadFile(path)).Folder is { } folder)
					{
						folders.Add(NormalizeFolder(folder));
					}
				}
				catch (Exception ex) when (ex is IOException or JsonException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
				{
					reportDiagnostic?.Invoke($"Could not read folder mapping {path}: {ex.Message}");
				}
			}
		}
		return folders.ToArray();
	}

	/// <summary>Reads the saved source choice without inspecting or connecting individual installations.</summary>
	public SkillMode InspectSource(AgentProfileRecord profile, SkillCatalogSource source, string? projectDirectory, bool folderScope = false)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(source);
		return new SkillMatrixPolicy(ReadFile(PolicyPath(profile, projectDirectory, folderScope))).Source(source);
	}

	/// <summary>Returns explicit child and source choices and visibility after resolving project and default inheritance.</summary>
	public SkillActivationState InspectMatrix(AgentProfileRecord profile, SkillCatalogSource source, string skillName,
		string skillFile, string? projectDirectory, bool folderScope = false)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(source);
		var global = new SkillMatrixPolicy(ReadFile(PolicyPath(profile, null)));
		var project = projectDirectory == null || folderScope ? null : new SkillMatrixPolicy(ReadFile(PolicyPath(profile, projectDirectory)));
		var folders = FolderPolicies(profile, projectDirectory);
		var state = MatrixState(profile, source, skillName, skillFile, folderScope ? null : projectDirectory, global, project, folders);
		if (!folderScope)
		{
			return state;
		}
		var path = PolicyPath(profile, projectDirectory, true);
		var selected = new SkillMatrixPolicy(ReadFile(path));
		return state with { ExplicitMode = selected.Child(skillFile, SkillMode.Inherit), SourceMode = selected.Source(source), ConfigPath = path };
	}

	private SkillActivationState MatrixState(AgentProfileRecord profile, SkillCatalogSource source, string name, string file,
		string? projectDirectory, SkillMatrixPolicy global, SkillMatrixPolicy? project, List<SkillMatrixPolicy> folders)
	{
		var native = Inspect(profile, name, file, projectDirectory);
		var globalChild = global.Child(file, native.DefaultMode);
		var globalSource = global.Source(source);
		var local = project ?? global;
		var child = project == null ? globalChild : local.Child(file, native.ProjectMode);
		var sourceMode = local.Source(source);
		var resolved = child != SkillMode.Inherit ? child : sourceMode;
		var reason = child != SkillMode.Inherit ? "Explicit skill setting." : "Inherited from the source.";
		var managed = local.HasChild(file) || child == SkillMode.Inherit && sourceMode != SkillMode.Inherit;
		string? origin = null;
		if (project != null && resolved == SkillMode.Inherit)
		{
			resolved = globalChild == SkillMode.Inherit ? globalSource : globalChild;
			reason = "Inherited from profile defaults.";
			managed = global.HasChild(file) || globalChild == SkillMode.Inherit && globalSource != SkillMode.Inherit;
			if (managed && profile.Kind == AgentKind.Claude && ClaudeMode(ReadFile(Path.Combine(projectDirectory!, ".claude", "settings.json")),
				FolderName(Discover(SkillDirectories(profile, projectDirectory), file) ?? file)) != SkillMode.Inherit)
			{
				managed = false;
			}
		}
		if ((project == null || child == SkillMode.Inherit && sourceMode == SkillMode.Inherit)
			&& (projectDirectory == null || profile.Kind != AgentKind.Claude
				|| ClaudeMode(ReadFile(Path.Combine(projectDirectory, ".claude", "settings.json")),
					FolderName(Discover(SkillDirectories(profile, projectDirectory), file) ?? file)) == SkillMode.Inherit))
		{
			foreach (var folder in folders)
			{
				var choice = folder.Child(file, folder.Source(source));
				if (choice != SkillMode.Inherit)
				{
					resolved = choice;
					origin = folder.Folder;
					reason = $"Inherited from folder {origin}.";
					managed = true;
					break;
				}
			}
		}
		var discoverable = managed && (Discover(SkillDirectories(profile, projectDirectory), file) != null
			|| origin != null && CanProjectFolderSkill(profile, file));
		var effective = managed ? resolved == SkillMode.Enabled && discoverable : native.EffectiveEnabled;
		return native with
		{
			ExplicitMode = child,
			SourceMode = sourceMode,
			OriginDirectory = origin,
			EffectiveEnabled = effective,
			Reason = !managed ? native.Reason : resolved == SkillMode.Enabled && !discoverable
				? "Enabled by policy. A native skill connection is required." : reason
		};
	}

	/// <summary>Previews persistent source and child choices with the native edits they imply for currently installed members.</summary>
	public SkillActivationBatchPreview PreviewBatch(IReadOnlyList<SkillMatrixEdit> edits, IReadOnlyList<AgentProfileRecord> profiles,
		IReadOnlyList<SkillSourceMatrixEdit> sourceEdits, IReadOnlyList<SkillMatrixMember> members)
	{
		ArgumentNullException.ThrowIfNull(edits);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(sourceEdits);
		ArgumentNullException.ThrowIfNull(members);
		var policies = new Dictionary<string, SkillMatrixPolicy>(PathsEqual);
		var selections = new Dictionary<(string Policy, string Key), SkillMode>();
		var nativeEdits = new List<SkillMatrixEdit>();
		var folderCandidates = new Dictionary<(string Policy, string File), (AgentProfileRecord Profile, string Folder, string File, SkillCatalogSource? Source)>();
		var manual = new HashSet<(string Policy, string File)>();
		var affected = new HashSet<string>();
		SkillMatrixPolicy Doc(string path)
		{
			if (!policies.TryGetValue(path, out var policy))
			{
				policy = new(ReadFile(path));
				policies.Add(path, policy);
			}
			return policy;
		}

		void Select(string path, string key, SkillMode mode)
		{
			if (selections.TryGetValue((path, key), out var prior) && prior != mode)
			{
				throw new InvalidOperationException($"Profiles sharing {path} must use the same choice for {key}.");
			}
			selections[(path, key)] = mode;
		}

		foreach (var group in sourceEdits)
		{
			var discoveryProject = group.FolderScope ? null : group.ProjectDirectory;
			if (group.Source.Kind is SkillCatalogSourceKind.Native or SkillCatalogSourceKind.System
				&& !SkillDirectories(group.Profile, discoveryProject).Any(root => IsWithinFolder(ResolveDirectory(group.Source.Location), ResolveDirectory(root)))
				&& !members.Any(member => member.Profile.Id == group.Profile.Id
					&& SkillMatrixPolicy.SourceKey(member.Source) == SkillMatrixPolicy.SourceKey(group.Source)
					&& CanConfigureMember(group.Profile, member.Source, member.SkillFile, discoveryProject, profiles)))
			{
				throw new InvalidOperationException(PrivateSkillMessage);
			}
			var path = PolicyPath(group.Profile, group.ProjectDirectory, group.FolderScope);
			var policy = Doc(path);
			Select(path, SkillMatrixPolicy.SourceKey(group.Source), group.Mode);
			policy.SetSource(group.Source, group.Mode);
			if (group.FolderScope)
			{
				var folder = NormalizeFolder(group.ProjectDirectory);
				policy.IdentifyFolder(folder);
				foreach (var member in members.Where(member => member.Profile.Id == group.Profile.Id
					&& SkillMatrixPolicy.SourceKey(member.Source) == SkillMatrixPolicy.SourceKey(group.Source)
					&& CanConfigureMember(group.Profile, member.Source, member.SkillFile, null, profiles)))
				{
					folderCandidates[(path, SkillMatrixPolicy.ManifestKey(member.SkillFile))] = (group.Profile, folder, member.SkillFile, member.Source);
				}
				affected.Add(group.Profile.DisplayName);
				continue;
			}
			foreach (var candidate in profiles.Where(p => p.Kind == group.Profile.Kind))
			{
				try
				{
					if (PathsEqual.Equals(path, PolicyPath(candidate, group.ProjectDirectory)))
					{
						affected.Add(candidate.DisplayName);
					}
				}
				catch (InvalidOperationException) { /* An unverifiable command cannot share this policy. */ }
			}
		}
		foreach (var edit in edits)
		{
			if (!CanConfigureMember(edit.Profile, edit.Source, edit.SkillFile, edit.FolderScope ? null : edit.ProjectDirectory, profiles))
			{
				throw new InvalidOperationException(PrivateSkillMessage);
			}
			if (edit.Source == null && !edit.FolderScope)
			{
				nativeEdits.Add(edit);
				continue;
			}
			var path = PolicyPath(edit.Profile, edit.ProjectDirectory, edit.FolderScope);
			var key = SkillMatrixPolicy.ManifestKey(edit.SkillFile);
			manual.Add((path, key));
			Select(path, key, edit.Mode);
			var policy = Doc(path);
			policy.SetChild(edit.SkillFile, edit.Mode);
			if (edit.FolderScope)
			{
				var folder = NormalizeFolder(edit.ProjectDirectory);
				policy.IdentifyFolder(folder);
				folderCandidates[(path, key)] = (edit.Profile, folder, edit.SkillFile, edit.Source);
				affected.Add(edit.Profile.DisplayName);
				continue;
			}
			var inherited = policy.Source(edit.Source!);
			var followsSource = edit.Mode == SkillMode.Inherit && inherited != SkillMode.Inherit;
			policy.SetMaterialized(edit.SkillFile, followsSource);
			nativeEdits.Add(edit with { Mode = followsSource ? inherited : edit.Mode });
			affected.Add(edit.Profile.DisplayName);
		}
		foreach (var group in sourceEdits.Where(group => !group.FolderScope))
		{
			var path = PolicyPath(group.Profile, group.ProjectDirectory);
			var policy = Doc(path);
			foreach (var member in members.Where(m => m.Profile.Id == group.Profile.Id
				&& SkillMatrixPolicy.SourceKey(m.Source) == SkillMatrixPolicy.SourceKey(group.Source)))
			{
				if (!CanConfigureMember(group.Profile, member.Source, member.SkillFile, group.ProjectDirectory, profiles))
				{
					continue;
				}
				if (!manual.Add((path, SkillMatrixPolicy.ManifestKey(member.SkillFile))))
				{
					continue;
				}
				var native = Inspect(group.Profile, member.SkillName, member.SkillFile, group.ProjectDirectory);
				var child = policy.Child(member.SkillFile, group.ProjectDirectory == null ? native.DefaultMode : native.ProjectMode);
				if (child != SkillMode.Inherit)
				{
					continue;
				}
				policy.SetMaterialized(member.SkillFile, group.Mode != SkillMode.Inherit);
				nativeEdits.Add(new(group.Profile, member.SkillName, member.SkillFile, group.ProjectDirectory, group.Mode, group.Source));
			}
		}
		var batch = PrepareBatch(nativeEdits, profiles);
		var folderLinks = new List<SkillLinkEdit>();
		foreach (var (key, candidate) in folderCandidates)
		{
			var policy = policies[key.Policy];
			var mode = policy.Child(candidate.File, candidate.Source == null ? SkillMode.Inherit : policy.Source(candidate.Source));
			var needsProjection = mode == SkillMode.Enabled && Discover(SkillDirectories(candidate.Profile, null), candidate.File) == null;
			if (FolderDiscoveryEdit(candidate.Profile, candidate.Folder, candidate.File, needsProjection) is { } link)
			{
				folderLinks.Add(link);
			}
		}
		var files = policies.Select(entry => new SkillConfigEdit(entry.Key, entry.Value.Original, entry.Value.Updated)).ToArray();
		return new(_storage, batch.Changes, [.. batch.Files, .. files], [.. batch.Links, .. folderLinks],
			files.Any(f => f.Original != f.Updated) || folderLinks.Count != 0 ? affected : []);
	}
}
