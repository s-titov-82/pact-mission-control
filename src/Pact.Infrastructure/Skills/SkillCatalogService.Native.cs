using Pact.Core.Agents;

namespace Pact.Infrastructure.Skills;

/// <summary>A native manifest alias, its agent search paths and the verified launch profiles that scan it.</summary>
public sealed record NativeSkillConnection(string ManifestPath, IReadOnlyList<AgentKind> Agents, IReadOnlyList<AgentProfileRecord> Profiles,
	bool IsSharedUserDirectory)
{
	/// <summary>The discovery directory containing this package alias, including for nested system packages.</summary>
	public string? DiscoveryRoot { get; init; }
}

/// <summary>A physical installation with every alias through which agent CLIs discover it.</summary>
public sealed record NativeSkillInstallation(CatalogSkill Skill, string ResolvedManifest, IReadOnlyList<NativeSkillConnection> Connections)
{
	/// <summary>Prefers the profile's discovered alias when preparing a path-specific native rule.</summary>
	public string ManifestFor(AgentProfileRecord profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		return Connections.Where(connection => connection.Profiles.Any(item => item.Id == profile.Id))
			.OrderBy(connection => connection.IsSharedUserDirectory).FirstOrDefault()?.ManifestPath ?? Skill.InstalledFilePath!;
	}
}

/// <summary>Installed packages and nonfatal discovery errors from other roots or profiles.</summary>
public sealed record NativeSkillCatalog(IReadOnlyList<NativeSkillInstallation> Installations, IReadOnlyList<string> Warnings);

public sealed partial class SkillCatalogService
{
	private string[] _maintenanceRoots = [];

	/// <summary>Discovers native Codex, Claude, shared and profile-specific installations without modifying their files.</summary>
	public Task<NativeSkillCatalog> DiscoverInstalledAsync(IReadOnlyList<AgentProfileRecord> profiles,
		SkillActivationService activation, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentNullException.ThrowIfNull(activation);
		return Task.Run(async () =>
		{
			var roots = new Dictionary<string, NativeRoot>(StringComparer.OrdinalIgnoreCase);
			var warnings = new List<string>();
			void Add(string directory, AgentKind kind, AgentProfileRecord? profile = null)
			{
				directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
				if (!roots.TryGetValue(directory, out var root))
				{
					root = new NativeRoot();
					roots.Add(directory, root);
				}
				root.Agents.Add(kind);
				if (profile is not null)
				{
					root.Profiles[profile.Id] = profile;
				}
			}

			Add(_skillsDirectory, AgentKind.Codex);
			foreach (var kind in new[] { AgentKind.Codex, AgentKind.Claude })
			{
				var defaultHome = Path.Combine(_userHome, kind == AgentKind.Codex ? ".codex" : ".claude");
				Add(Path.Combine(defaultHome, "skills"), kind);
				var inherited = Environment.GetEnvironmentVariable(kind == AgentKind.Codex ? "CODEX_HOME" : "CLAUDE_CONFIG_DIR");
				if (!string.IsNullOrWhiteSpace(inherited) && Path.IsPathFullyQualified(inherited))
				{
					Add(Path.Combine(inherited, "skills"), kind);
				}

				foreach (var profile in profiles.Where(item => item.Kind == kind))
				{
					var home = string.IsNullOrWhiteSpace(profile.NativeConfigDirectory) ? inherited : profile.NativeConfigDirectory;
					home = string.IsNullOrWhiteSpace(home) ? defaultHome : home;
					if (!Path.IsPathFullyQualified(home))
					{
						warnings.Add($"{profile.DisplayName}: the native configuration directory must be absolute.");
						continue;
					}
					Add(Path.Combine(home, "skills"), kind);
					try
					{
						var verified = activation.SkillDirectories(profile with { NativeConfigDirectory = home }, null);
						Add(verified[0], kind, profile);
						if (kind == AgentKind.Codex)
						{
							Add(_skillsDirectory, kind, profile);
						}
					}
					catch (InvalidOperationException ex)
					{
						warnings.Add($"{profile.DisplayName}: {ex.Message}");
					}
				}
			}

			var installations = new Dictionary<string, (CatalogSkill Package, List<NativeSkillConnection> Connections)>(StringComparer.OrdinalIgnoreCase);
			foreach (var (root, ownership) in roots)
			{
				try
				{
					var packages = await _reader.ReadAsync(root, true, null, cancellationToken).ConfigureAwait(false);
					foreach (var package in packages)
					{
						var manifest = package.InstalledFilePath!;
						// Only a directory junction shares the whole package. Two directories can link
						// their SKILL.md to one file while providing different scripts beside it.
						var identity = Path.Combine(ResolveDirectory(Path.GetDirectoryName(manifest)!), Path.GetFileName(manifest));
						if (!installations.TryGetValue(identity, out var installation))
						{
							installation = (package, []);
							installations.Add(identity, installation);
						}

						installation.Connections.Add(new NativeSkillConnection(manifest, ownership.Agents.Order().ToArray(), ownership.Profiles.Values.ToArray(),
							string.Equals(root, _skillsDirectory, StringComparison.OrdinalIgnoreCase))
						{ DiscoveryRoot = root });
					}
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					warnings.Add($"{root}: {ex.Message}");
				}
			}

			Volatile.Write(ref _maintenanceRoots, roots.Keys.ToArray());
			return new NativeSkillCatalog(installations.Select(pair => new NativeSkillInstallation(pair.Value.Package, pair.Key, pair.Value.Connections)).ToArray(), warnings);
		}, cancellationToken);
	}

	internal static string ResolveManifest(string path) => ResolvePath(path, isDirectory: false);

	internal static string ResolveDirectory(string path) => ResolvePath(path, isDirectory: true);

	private static string ResolvePath(string path, bool isDirectory)
	{
		var absolute = Path.GetFullPath(path);
		var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		while (true)
		{
			var current = Path.GetPathRoot(absolute)!;
			var parts = Path.GetRelativePath(current, absolute).Split(Path.DirectorySeparatorChar);
			var followed = false;
			for (var i = 0; i < parts.Length; i++)
			{
				var parent = current;
				current = Path.Combine(current, parts[i]);
				FileSystemInfo entry = i == parts.Length - 1 && !isDirectory ? new FileInfo(current) : new DirectoryInfo(current);
				if (entry.LinkTarget is not { } target)
				{
					continue;
				}

				if (!visited.Add(current) || visited.Count > 64)
				{
					throw new IOException($"Cannot resolve the skill link at {current}.");
				}

				// A planned install may complete a dangling junction. Resolve its recorded target
				// and the target's own ancestors even when the leaf manifest doesn't exist yet.
				absolute = Path.Combine([Path.GetFullPath(target, parent), .. parts[(i + 1)..]]);
				followed = true;
				break;
			}

			if (!followed)
			{
				return current;
			}
		}
	}

	private sealed class NativeRoot
	{
		internal HashSet<AgentKind> Agents { get; } = [];
		internal Dictionary<string, AgentProfileRecord> Profiles { get; } = new(StringComparer.Ordinal);
	}
}