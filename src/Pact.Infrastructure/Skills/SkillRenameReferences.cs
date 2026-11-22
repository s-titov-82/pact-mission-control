using System.Text.Json;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.Storage;
using Tomlyn;

namespace Pact.Infrastructure.Skills;

/// <summary>Finds settings that refer to a renamed directory entry while retaining rules on surviving aliases.</summary>
internal static class SkillRenameReferences
{
	private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	internal static IReadOnlyList<SkillConfigEdit> Preview(AppPaths paths, IReadOnlyList<AgentProfileRecord> profiles,
		IReadOnlyList<string> projects, IReadOnlyDictionary<string, string> directoryMoves, IReadOnlyList<NativeSkillConnection> connections,
		string declaredName, string userHome)
	{
		var edits = new Dictionary<string, SkillConfigEdit>(Paths);
		var moves = directoryMoves.Select(pair => new FolderMove(Path.GetFullPath(pair.Key), Path.GetFullPath(pair.Value))).ToArray();
		var roots = NativeRoots(profiles, connections, userHome);
		var claude = roots.Where(item => item.Kind == AgentKind.Claude).Select(item => item.Root).Distinct(Paths).ToArray();
		var projectClaude = projects.Select(project => Path.Combine(project, ".claude", "skills")).ToArray();
		foreach (var (kind, root) in roots)
		{
			var home = Directory.GetParent(root)!.FullName;
			if (kind == AgentKind.Codex)
			{
				var discovery = connections.Where(c => c.IsSharedUserDirectory && c.DiscoveryRoot != null).Select(c => c.DiscoveryRoot!).Append(root);
				Inspect(Path.Combine(home, "config.toml"), text => RenameCodex(text, discovery));
			}
			else
			{
				Inspect(Path.Combine(home, "settings.json"), text => RenameClaude(text, [root], [root, .. projectClaude]));
			}
		}
		foreach (var project in projects)
		{
			var local = Path.Combine(project, ".claude", "skills");
			string ClaudeProject(string text)
			{
				return RenameClaude(text, [local, .. claude], [local, .. claude]);
			}

			Inspect(Path.Combine(project, ".claude", "settings.local.json"), ClaudeProject);
			Inspect(Path.Combine(project, ".claude", "settings.json"), ClaudeProject);
			// Keep explicit project paths at their existing trust level. Do not promote project rules to CLI overrides.
			Inspect(Path.Combine(project, ".codex", "config.toml"), text => CodexSkillRules.RenamePaths(text, RenamedManifest));
		}
		foreach (var section in new[] { "Policies", "ProjectRules" })
		{
			var directory = Path.Combine(paths.SettingsDirectory, "Skills", section);
			if (!Directory.Exists(directory))
			{
				continue;
			}
			foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
			{
				Inspect(file, text =>
				{
					var doc = Object(text);
					var changed = false;
					if (section == "ProjectRules")
					{
						changed = RenamePolicyKeys(doc, false);
					}
					else
					{
						foreach (var field in new[] { "children", "materialized" })
						{
							if (doc[field] is JsonObject entries)
							{
								changed |= RenamePolicyKeys(entries, true);
							}
						}
					}
					return changed ? doc.ToJsonString(Indented) : text;
				});
			}
		}
		return edits.Values.ToArray();

		void Inspect(string path, Func<string, string> transform)
		{
			path = SkillCatalogService.ResolveManifest(path);
			if (!File.Exists(path))
			{
				return;
			}
			try
			{
				var original = File.ReadAllText(path);
				var updated = transform(original);
				if (original != updated)
				{
					if (edits.TryGetValue(path, out var previous) && previous.Updated != updated)
					{
						throw new IOException("The shared file has incompatible settings in two native scopes.");
					}
					edits[path] = new(path, original, updated);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or TomlException)
			{
				var reason = ex is JsonException or TomlException ? "Invalid settings syntax." : ex.Message;
				throw new IOException($"Cannot migrate skill settings in {path}. {reason}", ex);
			}
		}

		bool RenamePolicyKeys(JsonObject entries, bool uppercase)
		{
			foreach (var key in entries.Select(entry => entry.Key).Where(Path.IsPathFullyQualified))
			{
				if (!moves.Any(move => Paths.Equals(SkillCatalogService.ResolveManifest(key), Path.Combine(move.New, "SKILL.md"))))
				{
					continue;
				}
				if (!entries.Any(entry => Paths.Equals(RenamedManifest(entry.Key), key)))
				{
					throw new IOException($"An existing skill rule targets the destination {key}.");
				}
			}
			return RenameKeys(entries, path => uppercase ? RenamedManifest(path)?.ToUpperInvariant() : RenamedManifest(path));
		}

		string? RenamedManifest(string path)
		{
			if (!Path.IsPathFullyQualified(path) || !Paths.Equals(Path.GetFileName(path), "SKILL.md"))
			{
				return null;
			}
			var folder = Directory.GetParent(Path.GetFullPath(path))!;
			if (folder.Parent == null)
			{
				return null;
			}
			// Resolve ancestors but not the entry itself: a junction kept under its old name still works after retargeting.
			var parent = SkillCatalogService.ResolveDirectory(folder.Parent.FullName);
			var move = moves.FirstOrDefault(item => Paths.Equals(item.Parent, parent) && Paths.Equals(item.OldName, folder.Name));
			return move == null ? null : Path.Combine(folder.Parent.FullName, move.NewName, "SKILL.md");
		}

		string RenameCodex(string original, IEnumerable<string> discovery)
		{
			var before = CodexSkillRules.Read(original);
			var updated = CodexSkillRules.RenamePaths(original, RenamedManifest);
			foreach (var move in moves)
			{
				var oldFile = Path.Combine(move.Old, "SKILL.md");
				var newFile = Path.Combine(move.New, "SKILL.md");
				var oldName = string.IsNullOrEmpty(declaredName) ? move.OldName : declaredName;
				var newName = string.IsNullOrEmpty(declaredName) ? move.NewName : declaredName;
				var wasEnabled = CodexSkillRules.IsEnabled(before, oldName, oldFile);
				if (before.Any(rule => rule.MatchesPath(newFile) && rule.Enabled != wasEnabled))
				{
					throw new IOException($"A Codex rule for {newFile} conflicts with the existing skill.");
				}
				if (oldName == newName || !discovery.Any(root => Within(move.Parent, root)))
				{
					continue;
				}
				// A rule by name can still own other copies. Preserve it and scope any required exception to this path.
				var effective = before.LastOrDefault(rule => rule.Selector == "name" && rule.Value == newName
					|| rule.MatchesPath(oldFile) || rule.MatchesPath(newFile))?.Enabled ?? true;
				if (effective != wasEnabled)
				{
					updated = CodexSkillRules.Edit(updated, newName, newFile, wasEnabled ? SkillMode.Enabled : SkillMode.Disabled)!;
				}
			}
			return updated;
		}

		string RenameClaude(string original, string[] visibility, string[] sharedWith)
		{
			if (!moves.Any(move => visibility.Any(root => Within(move.Parent, root))))
			{
				return original;
			}
			var doc = Object(original);
			if (doc["skillOverrides"] is not JsonObject overrides)
			{
				return original;
			}
			var changed = false;
			foreach (var move in moves.Where(move => visibility.Any(root => Within(move.Parent, root))))
			{
				var oldKey = move.OldName;
				var newKey = move.NewName;
				if (oldKey == newKey)
				{
					continue;
				}
				var hadOld = overrides.TryGetPropertyValue(oldKey, out var oldValue);
				var hasNew = overrides.TryGetPropertyValue(newKey, out var newValue);
				if (hasNew && !JsonNode.DeepEquals(oldValue, newValue) && (hadOld || newValue?.GetValue<string>() != "on"))
				{
					throw new IOException($"The existing Claude override for {newKey} differs from {oldKey}.");
				}
				if (!hadOld)
				{
					continue;
				}
				if (!hasNew && sharedWith.Any(root => File.Exists(Path.Combine(root, newKey, "SKILL.md"))))
				{
					throw new IOException($"The Claude command {newKey} already belongs to another installed skill.");
				}
				overrides[newKey] = oldValue?.DeepClone();
				var keepAlias = sharedWith.Select(root => Path.Combine(root, oldKey, "SKILL.md"))
					.Any(file => File.Exists(file) && RenamedManifest(file) == null);
				if (!keepAlias)
				{
					overrides.Remove(oldKey);
				}
				changed |= !hasNew || !keepAlias;
			}
			return changed ? doc.ToJsonString(Indented) : original;
		}
	}

	private static JsonObject Object(string text) => JsonNode.Parse(text) as JsonObject ?? throw new JsonException("Expected a settings object.");

	private static bool RenameKeys(JsonObject entries, Func<string, string?> rename)
	{
		var changed = false;
		foreach (var (key, value) in entries.ToArray())
		{
			var next = rename(key);
			if (next == null || next == key)
			{
				continue;
			}
			var existing = entries.Select(pair => pair.Key).FirstOrDefault(candidate => Paths.Equals(candidate, next) && candidate != key);
			if (existing != null && !JsonNode.DeepEquals(entries[existing], value))
			{
				throw new IOException($"Conflicting skill rules for {key} and {next}.");
			}
			entries.Remove(key);
			entries[existing ?? next] = value?.DeepClone();
			changed = true;
		}
		return changed;
	}

	private static (AgentKind Kind, string Root)[] NativeRoots(IReadOnlyList<AgentProfileRecord> profiles, IReadOnlyList<NativeSkillConnection> connections, string userHome)
	{
		var roots = new List<(AgentKind Kind, string Root)>();
		var inheritEnvironment = Paths.Equals(Path.GetFullPath(userHome), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
		foreach (var kind in new[] { AgentKind.Codex, AgentKind.Claude })
		{
			roots.Add((kind, Path.Combine(userHome, kind == AgentKind.Codex ? ".codex" : ".claude", "skills")));
			var inherited = inheritEnvironment ? Environment.GetEnvironmentVariable(kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR") : null;
			if (inherited != null && Path.IsPathFullyQualified(inherited))
			{
				roots.Add((kind, Path.Combine(inherited, "skills")));
			}
		}
		foreach (var profile in profiles.Where(p => p.Kind is AgentKind.Codex or AgentKind.Claude))
		{
			var home = profile.NativeConfigDirectory;
			if (string.IsNullOrWhiteSpace(home) && inheritEnvironment)
			{
				home = Environment.GetEnvironmentVariable(profile.Kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR");
			}
			if (string.IsNullOrWhiteSpace(home))
			{
				home = Path.Combine(userHome, profile.Kind == AgentKind.Codex ? ".codex" : ".claude");
			}
			if (Path.IsPathFullyQualified(home))
			{
				roots.Add((profile.Kind, Path.Combine(home, "skills")));
			}
		}
		foreach (var connection in connections.Where(c => !c.IsSharedUserDirectory && c.DiscoveryRoot != null))
		{
			roots.AddRange(connection.Agents.Where(kind => kind is AgentKind.Codex or AgentKind.Claude).Select(kind => (kind, connection.DiscoveryRoot!)));
		}
		return roots.DistinctBy(item => (item.Kind, item.Root.ToUpperInvariant())).ToArray();
	}

	private static bool Within(string directory, string root)
	{
		var relative = Path.GetRelativePath(SkillCatalogService.ResolveDirectory(root), directory);
		return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private sealed record FolderMove(string Old, string New)
	{
		internal string Parent { get; } = SkillCatalogService.ResolveDirectory(Path.GetDirectoryName(Old)!);
		internal string OldName { get; } = Path.GetFileName(Old);
		internal string NewName { get; } = Path.GetFileName(New);
	}
}