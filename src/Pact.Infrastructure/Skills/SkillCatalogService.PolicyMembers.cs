using Pact.Core.Agents;

namespace Pact.Infrastructure.Skills;

public sealed partial class SkillCatalogService
{
	/// <summary>Reads native manifests for launch-time policy evaluation without fetching repositories or reading package assets.</summary>
	public IReadOnlyList<SkillMatrixMember> DiscoverPolicyMembers(AgentProfileRecord profile, SkillActivationService activation, string? projectDirectory,
		bool includeShared = false)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ArgumentNullException.ThrowIfNull(activation);
		var native = activation.SkillDirectories(profile, projectDirectory);
		var roots = new List<string> { native[0] };
		if (profile.Kind == AgentKind.Codex || includeShared)
		{
			roots.Add(_skillsDirectory);
		}
		if (projectDirectory is not null)
		{
			roots.Add(native[^1]);
		}
		IReadOnlyList<AgentKind> agent = [profile.Kind];
		var sourceRoots = roots.Distinct(StringComparer.OrdinalIgnoreCase).Select(root => (Root: root, Agents: agent)).ToArray();
		var bound = new Dictionary<string, SkillSourceBinding>(StringComparer.OrdinalIgnoreCase);
		foreach (var (folder, binding) in GetBindings())
		{
			var path = Path.Combine(_skillsDirectory, folder);
			if (Directory.Exists(path))
			{
				bound[ResolveDirectory(path)] = binding;
			}
		}

		var members = new List<SkillMatrixMember>();
		foreach (var (root, _) in sourceRoots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}
			var pending = new Stack<string>();
			pending.Push(root);
			while (pending.TryPop(out var directory))
			{
				var file = Path.Combine(directory, "SKILL.md");
				if (File.Exists(file))
				{
					var physical = ResolveDirectory(directory);
					var source = bound.TryGetValue(physical, out var binding) ? Source(SkillCatalogSourceKind.Bound, binding.Source) :
						ClassifyInstalledFolder(physical, sourceRoots);
					using var reader = File.OpenText(file);
					var name = CatalogSkill.ManifestName(reader, Path.GetFileName(directory));
					members.Add(new SkillMatrixMember(source, name, file, profile));
					continue;
				}
				foreach (var child in Directory.EnumerateDirectories(directory, "*", new EnumerationOptions { AttributesToSkip = 0 }))
				{
					var info = new DirectoryInfo(child);
					if (!info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
						!info.Name.StartsWith(".pact-skill-", StringComparison.OrdinalIgnoreCase) &&
						(info.LinkTarget is null || File.Exists(Path.Combine(child, "SKILL.md"))))
					{
						pending.Push(child);
					}
				}
			}
		}
		return members;
	}
}
