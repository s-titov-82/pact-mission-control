using Pact.Core.Agents;

namespace Pact.Infrastructure.Skills;

public sealed partial class SkillActivationService
{
	private bool CanProjectFolderSkill(AgentProfileRecord profile, string skillFile)
	{
		if (profile.Kind != AgentKind.Claude || !File.Exists(skillFile))
		{
			return false;
		}
		var target = ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(skillFile))!);
		var shared = ResolveDirectory(_sharedSkillsDirectory);
		return !PathsEqual.Equals(target, shared) && IsWithinFolder(target, shared)
			&& PathsEqual.Equals(SkillCatalogService.ResolveManifest(skillFile), Path.Combine(target, "SKILL.md"));
	}

	private SkillLinkEdit Projection(AgentProfileRecord profile, string folder, string skillFile, bool create)
	{
		var target = ResolveDirectory(Path.GetDirectoryName(Path.GetFullPath(skillFile))!);
		var scope = Hash(profile.Id + "|" + NormalizeFolder(folder).ToUpperInvariant())[..32];
		var root = Path.Combine(ResolveDirectory(_dataRoot), "Settings", "Skills", "FolderDiscovery", scope, Hash(target.ToUpperInvariant())[..32]);
		var alias = Path.Combine(root, ".claude", "skills", Path.GetFileName(target));
		var receipt = ReceiptPath(alias);
		return new(alias, target, receipt, create, root, PreviousReceipt: create ? ReadFile(receipt) : null,
			ConnectionReceipt: create ? null : ReadFile(receipt));
	}

	private SkillLinkEdit? FolderDiscoveryEdit(AgentProfileRecord profile, string folder, string skillFile, bool enabled)
	{
		if (!CanProjectFolderSkill(profile, skillFile))
		{
			return null;
		}
		var link = Projection(profile, folder, skillFile, enabled);
		ValidateProjection(link);
		if (OwnedTarget(link.Path, link.Target) is not null)
		{
			return enabled ? null : link;
		}
		if (!enabled)
		{
			return null;
		}
		SkillDirectoryConnection.Validate(link);
		return link;
	}

	private string? FolderDiscoveryArgument(AgentProfileRecord profile, string folder, string skillFile, bool materialize = false)
	{
		if (!CanProjectFolderSkill(profile, skillFile))
		{
			return null;
		}
		var link = Projection(profile, folder, skillFile, true);
		if (materialize)
		{
			WriteGate.Wait();
			try
			{
				if (FolderDiscoveryEdit(profile, folder, skillFile, true) is { } missing)
				{
					Task.Run(() => SkillDirectoryConnection.ApplyAsync(missing, CancellationToken.None)).GetAwaiter().GetResult();
				}
			}
			finally { WriteGate.Release(); }
		}
		ValidateProjection(link);
		return OwnedTarget(link.Path, link.Target) is not null ? link.ProjectionRoot : null;
	}

	// --add-dir grants access to its entire root, so accept only the one explicitly approved package.
	internal static void ValidateProjection(SkillLinkEdit link)
	{
		var directory = link.ProjectionRoot!;
		if (!PathsEqual.Equals(ResolveDirectory(directory), directory))
		{
			throw new IOException($"The skill discovery directory was redirected: {directory}.");
		}
		foreach (var child in new[] { ".claude", "skills", Path.GetFileName(link.Path) })
		{
			var expected = Path.Combine(directory, child);
			if (File.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null ||
				Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(entry => !PathsEqual.Equals(entry, expected)))
			{
				throw new IOException($"Unexpected contents in the skill discovery directory {directory}.");
			}
			directory = expected;
		}
	}
}
