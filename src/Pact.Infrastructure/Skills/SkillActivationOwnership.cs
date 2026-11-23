using Pact.Core.Agents;

namespace Pact.Infrastructure.Skills;

public sealed partial class SkillActivationService
{
	/// <summary>Explains how to make a private installation available to another profile.</summary>
	public const string PrivateSkillMessage = "Profile-specific skill. Use Make shared in Catalog to configure it for this profile.";

	/// <summary>Tests physical package ownership and existing discovery aliases without creating a cross-profile connection.</summary>
	public bool CanConfigureMember(AgentProfileRecord profile, SkillCatalogSource? source, string skillFile, string? projectDirectory,
		IReadOnlyList<AgentProfileRecord> profiles)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(profiles);
		ArgumentException.ThrowIfNullOrWhiteSpace(skillFile);
		var physical = ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(skillFile))!);
		return !IsPrivatePackage(source, physical, profiles) || Discover(SkillDirectories(profile, projectDirectory), skillFile) != null;
	}

	private bool IsPrivatePackage(SkillCatalogSource? source, string physical, IReadOnlyList<AgentProfileRecord> profiles) =>
		!IsWithinFolder(physical, ResolveDirectory(_sharedSkillsDirectory))
		&& ((source?.Kind is SkillCatalogSourceKind.Native or SkillCatalogSourceKind.System && IsWithinFolder(physical, ResolveDirectory(source.Location)))
			|| NativeOwnershipRoots(profiles).Distinct(PathsEqual).Any(root => IsWithinFolder(physical, ResolveDirectory(root))));

	private IEnumerable<string> NativeOwnershipRoots(IReadOnlyList<AgentProfileRecord> profiles)
	{
		foreach (var kind in new[] { AgentKind.Codex, AgentKind.Claude })
		{
			yield return Path.Combine(_userHome, kind == AgentKind.Codex ? ".codex" : ".claude", "skills");
			var inherited = InheritedHome(_userHome, kind);
			if (Path.IsPathFullyQualified(inherited ?? ""))
			{
				yield return Path.Combine(inherited!, "skills");
			}
		}
		foreach (var candidate in profiles)
		{
			if (candidate.Kind is AgentKind.Codex or AgentKind.Claude && candidate.NativeConfigDirectory is { } home && Path.IsPathFullyQualified(home))
			{
				yield return Path.Combine(home, "skills");
			}
		}
	}

	private static bool IsWithinFolder(string directory, string root)
	{
		var relative = Path.GetRelativePath(root, directory);
		return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
	}
}
