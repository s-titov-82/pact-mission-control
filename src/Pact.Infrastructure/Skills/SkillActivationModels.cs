namespace Pact.Infrastructure.Skills;

/// <summary>A project's explicit choice or its dependency on the current profile default.</summary>
public enum SkillMode
{
	/// <summary>Follow the next launch's effective native default.</summary>
	Inherit,
	/// <summary>Include the installed skill.</summary>
	Enabled,
	/// <summary>Hide the installed skill.</summary>
	Disabled
}

/// <summary>Effective visibility and the file owning the selected scope.</summary>
public sealed record SkillActivationState(bool DefaultEnabled, bool EffectiveEnabled,
	SkillMode ProjectMode, string Reason, string ConfigPath)
{
	/// <summary>The explicit native path or command rule. Inherit leaves native discovery and broader rules in control.</summary>
	public SkillMode DefaultMode { get; init; }

	/// <summary>The skill-specific choice for the inspected scope before source inheritance.</summary>
	public SkillMode ExplicitMode { get; init; }

	/// <summary>The source choice for this scope, independent of a child exception.</summary>
	public SkillMode SourceMode { get; init; }

	/// <summary>The folder whose rule supplies the effective choice, when folder inheritance applies.</summary>
	public string? OriginDirectory { get; init; }
}

/// <summary>A reviewed single-file edit, guarded by the contents observed during preview.</summary>
public sealed class SkillActivationPreview
{
	internal SkillActivationPreview(string filePath, IReadOnlyList<string> profiles, string? original, string? updated,
		SkillLinkEdit? link, string storageRoot, string selector)
	{
		(FilePath, AffectedProfiles, Original, Updated, Link) = (filePath, profiles, original, updated, link);
		List<string> files = [];
		if (original != updated)
		{
			files.Add(filePath);
		}
		if (link != null)
		{
			files.Add(link.Path);
			files.Add(link.ReceiptPath);
		}
		AffectedFiles = files;
		StorageRoot = storageRoot;
		Selector = selector;
	}

	/// <summary>The destination that Apply may change.</summary>
	public string FilePath { get; }

	/// <summary>Display names of configured profiles sharing this setting.</summary>
	public IReadOnlyList<string> AffectedProfiles { get; }

	/// <summary>Every file or directory entry that Apply may write.</summary>
	public IReadOnlyList<string> AffectedFiles { get; }

	/// <summary>Directory that becomes discoverable to native launches after Apply.</summary>
	public string? CreatedConnectionPath => Link is { Create: true } ? Link.Path : null;

	/// <summary>Pact-owned directory connection to remove while preserving its source package.</summary>
	public string? RemovedConnectionPath => Link is { Create: false } ? Link.Path : null;

	internal string? Original { get; }
	internal string Selector { get; }
	internal string StorageRoot { get; }
	internal string? Updated { get; }
	internal SkillLinkEdit? Link { get; }
}

internal sealed record SkillLinkEdit(string Path, string Target, string ReceiptPath, bool Create, string? ProjectionRoot = null);
