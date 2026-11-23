using Pact.Core.Agents;

namespace Pact.Infrastructure.Skills;

/// <summary>Distinguishes physical package ownership and confirmed catalog sources from agent discovery aliases.</summary>
public enum SkillCatalogSourceKind
{
	/// <summary>The package resides in the shared user skills directory.</summary>
	Shared,
	/// <summary>The CLI owns this built-in package directory.</summary>
	System,
	/// <summary>The package resides in an agent's native discovery directory.</summary>
	Native,
	/// <summary>A native link points to a package maintained in an external repository or folder.</summary>
	External,
	/// <summary>The package is available in a registered catalog source.</summary>
	Registered,
	/// <summary>The installed package has an explicitly confirmed update source.</summary>
	Bound
}

/// <summary>The catalog group label and its full source directory or repository URL.</summary>
public sealed record SkillCatalogSource(SkillCatalogSourceKind Kind, string Label, string Location);

/// <summary>The strength of the evidence when comparing installed packages.</summary>
public enum SkillDuplicateKind
{
	/// <summary>Independent packages have the same relative files and file contents.</summary>
	Identical,
	/// <summary>Independent packages use the same manifest name but have differing files.</summary>
	Different,
	/// <summary>At least one package could not be read completely.</summary>
	Incomplete
}

/// <summary>Read-only comparison of installed packages, their aliases, and paths that need review.</summary>
public sealed record SkillDuplicateGroup(string Name, SkillDuplicateKind Kind, IReadOnlyList<NativeSkillInstallation> Installations,
	IReadOnlyList<string> Recommendations, IReadOnlyList<string> DifferingFiles);

public sealed partial class SkillCatalogService
{
	/// <summary>Classifies a catalog row by the source of its files while preserving any explicit update binding.</summary>
	public SkillCatalogSource ClassifySource(CatalogSkill skill, NativeSkillInstallation? installation = null, SkillSourceBinding? binding = null)
	{
		ArgumentNullException.ThrowIfNull(skill);
		if (binding is not null)
		{
			return Source(SkillCatalogSourceKind.Bound, binding.Source);
		}
		if (installation is null)
		{
			return Source(SkillCatalogSourceKind.Registered, skill.Source);
		}

		var roots = installation.Connections.Select(connection => (Root: connection.DiscoveryRoot ?? skill.Source, connection.Agents));
		return ClassifyInstalledFolder(Path.GetDirectoryName(installation.ResolvedManifest)!, roots);
	}

	private SkillCatalogSource ClassifyInstalledFolder(string package, IEnumerable<(string Root, IReadOnlyList<AgentKind> Agents)> roots)
	{
		var shared = ResolveDirectory(_skillsDirectory);
		if (IsWithin(package, shared))
		{
			return new(SkillCatalogSourceKind.Shared, "Shared user skills", shared);
		}

		foreach (var connection in roots)
		{
			var root = ResolveDirectory(connection.Root);
			if (!IsWithin(package, root))
			{
				continue;
			}
			if (connection.Agents.Contains(AgentKind.Codex) && IsWithin(package, Path.Combine(root, ".system")))
			{
				return new(SkillCatalogSourceKind.System, "Codex system skills", Path.Combine(root, ".system"));
			}
			return new(SkillCatalogSourceKind.Native, string.Join(", ", connection.Agents) + " skills", root);
		}

		var repository = FindRepository(package);
		return Source(SkillCatalogSourceKind.External, repository ?? Path.GetDirectoryName(package) ?? package);
	}

	/// <summary>Compares fully read installed packages, including scripts and binary assets, without changing any connection or file.</summary>
	public IReadOnlyList<SkillDuplicateGroup> AnalyzeDuplicates(IReadOnlyList<NativeSkillInstallation> installations,
		IReadOnlyDictionary<string, SkillSourceBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(installations);
		ArgumentNullException.ThrowIfNull(bindings);
		var groups = new List<SkillDuplicateGroup>();
		var fingerprints = new Dictionary<NativeSkillInstallation, string>();
		var fileHashes = new Dictionary<NativeSkillInstallation, Dictionary<string, string>>();
		var warnings = new Dictionary<NativeSkillInstallation, string>();
		var nameClusters = installations.GroupBy(item => item.Skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();
		// Equal packages must have equal file counts and byte counts, including packages whose
		// manifests fall back to different folder names. Re-read only possible matches and name conflicts.
		var candidates = nameClusters.Where(cluster => cluster.Skip(1).Any()).SelectMany(cluster => cluster)
			.Concat(installations.GroupBy(item => (item.Skill.Content.Count, Bytes: item.Skill.Content.Values.Sum(bytes => (long)bytes.Length)))
				.Where(cluster => cluster.Skip(1).Any()).SelectMany(cluster => cluster)).Distinct();
		foreach (var item in candidates)
		{
			if (item.Skill.InspectionWarning is { } warning)
			{
				warnings[item] = warning;
				continue;
			}
			try
			{
				var folder = Folder(item);
				var hashes = InstalledHashes(folder, skipGitMetadata: true);
				fingerprints[item] = PackageRevision(folder, hashes, skipGitMetadata: true);
				fileHashes[item] = new(hashes, StringComparer.Ordinal);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				warnings[item] = ex.Message;
			}
		}

		foreach (var cluster in nameClusters)
		{
			var items = cluster.OrderBy(item => item.ResolvedManifest, StringComparer.OrdinalIgnoreCase).ToArray();
			if (items.Length == 1)
			{
				continue;
			}
			if (items.Any(item => !fingerprints.ContainsKey(item)))
			{
				groups.Add(new(cluster.Key, SkillDuplicateKind.Incomplete, items,
					["Keep each package until every file can be compared.", .. items.Where(warnings.ContainsKey)
						.Select(item => $"{Folder(item)}: {warnings[item]}")], []));
				continue;
			}
			if (items.Select(item => fingerprints[item]).Distinct(StringComparer.Ordinal).Skip(1).Any())
			{
				var differingFiles = DifferingFiles(items.Select(item => fileHashes[item]).ToArray());
				groups.Add(new(cluster.Key, SkillDuplicateKind.Different, items, differingFiles.Length == 0
					? ["The directory entries or named data streams differ. Keep each version until these differences are reviewed."]
					: ["Keep each version. Review the differing files before choosing which package each agent should use."], differingFiles));
			}
		}

		// Identical bytes can also occur under different fallback names. Group by the complete
		// package independently of name collisions, so an exact subset of a conflict remains visible.
		foreach (var cluster in fingerprints.GroupBy(pair => pair.Value, StringComparer.Ordinal))
		{
			var items = cluster.Select(pair => pair.Key).OrderBy(item => item.ResolvedManifest, StringComparer.OrdinalIgnoreCase).ToArray();
			if (items.Length > 1)
			{
				var name = string.Join(", ", items.Select(item => item.Skill.Name).Distinct(StringComparer.OrdinalIgnoreCase));
				groups.Add(new(name, SkillDuplicateKind.Identical, items, IdenticalRecommendations(items, bindings), []));
			}
		}
		return groups.OrderBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ThenBy(group => group.Kind).ToArray();
	}

	private List<string> IdenticalRecommendations(NativeSkillInstallation[] items, IReadOnlyDictionary<string, SkillSourceBinding> bindings)
	{
		var maintained = new List<NativeSkillInstallation>();
		foreach (var item in items)
		{
			var system = ClassifySource(item.Skill, item).Kind == SkillCatalogSourceKind.System;
			if (system)
			{
				return ["Keep the CLI-managed system package in place. Other copies can have a different update lifecycle."];
			}
			var bound = item.Connections.Any(connection => connection.IsSharedUserDirectory &&
				string.Equals(connection.ManifestPath, Path.Combine(_skillsDirectory, item.Skill.FolderName, "SKILL.md"), StringComparison.OrdinalIgnoreCase))
				&& bindings.ContainsKey(item.Skill.FolderName);
			if (bound || FindRepository(Folder(item)) is not null)
			{
				maintained.Add(item);
			}
		}
		if (maintained.Count > 1)
		{
			return ["Keep the independently maintained installations. Their current files match, but each can receive different updates."];
		}

		var keep = maintained.FirstOrDefault() ?? items.FirstOrDefault(item => ClassifySource(item.Skill, item).Kind == SkillCatalogSourceKind.External)
			?? items.FirstOrDefault(item => ClassifySource(item.Skill, item).Kind == SkillCatalogSourceKind.Shared) ?? items[0];
		var target = Folder(keep);
		var sharedTarget = IsWithin(target, ResolveDirectory(_skillsDirectory));
		var advice = new List<string> { $"Keep {target} as the source." };
		foreach (var item in items.Where(item => item != keep))
		{
			foreach (var connection in item.Connections)
			{
				if (!string.Equals(connection.ManifestPath, item.ResolvedManifest, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				var alias = Path.GetDirectoryName(connection.ManifestPath);
				if (sharedTarget && connection.Agents.Count == 1 && connection.Agents[0] == AgentKind.Codex)
				{
					advice.Add($"Codex already discovers {target} in the shared directory. The separate copy at {alias} is redundant for discovery. "
						+ $"If profile or project rules reference its path, preserve that path with a directory link to {target}.");
					continue;
				}
				advice.Add($"To share those files, use a directory link at {alias} pointing to {target}. "
					+ $"This preserves the discovery path for {string.Join(", ", connection.Agents)}.");
			}
		}
		if (maintained.Count == 0 && items.All(item => ClassifySource(item.Skill, item).Kind == SkillCatalogSourceKind.Native)
			&& items.SelectMany(item => item.Connections).SelectMany(connection => connection.Agents).Distinct().Count() > 1)
		{
			var destination = Path.Combine(_skillsDirectory, keep.Skill.FolderName);
			if (!Path.Exists(destination) && new DirectoryInfo(destination).LinkTarget is null)
			{
				advice.Add($"Alternatively, move one copy to {destination} if it should be discovered by every Codex profile. "
					+ "Keep links under the existing native paths for Claude and profiles with path-specific rules.");
			}
		}
		return advice;
	}

	private static string Folder(NativeSkillInstallation installation) => Path.GetDirectoryName(installation.ResolvedManifest)!;

	private static string[] DifferingFiles(Dictionary<string, string>[] contents)
	{
		var paths = contents.SelectMany(files => files.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
		return paths.Where(path => !contents[0].TryGetValue(path, out var original) ||
			contents.Any(files => !files.TryGetValue(path, out var digest) || digest != original)).ToArray();
	}

	private static bool IsWithin(string directory, string root)
	{
		var relative = Path.GetRelativePath(root, directory);
		return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}

	private static string? FindRepository(string directory)
	{
		for (var candidate = new DirectoryInfo(directory); candidate is not null; candidate = candidate.Parent)
		{
			var marker = Path.Combine(candidate.FullName, ".git");
			if (Directory.Exists(marker) || File.Exists(marker))
			{
				return candidate.FullName;
			}
		}
		return null;
	}

	private static SkillCatalogSource Source(SkillCatalogSourceKind kind, string location)
	{
		var label = Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
			? uri.AbsolutePath.Trim('/').Split('/').Take(2) : [Path.GetFileName(Path.TrimEndingDirectorySeparator(location))];
		var name = string.Join('/', label);
		return new(kind, string.IsNullOrEmpty(name) ? location : name, location);
	}
}
