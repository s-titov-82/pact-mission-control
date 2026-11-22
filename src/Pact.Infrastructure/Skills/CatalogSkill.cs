using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Pact.Infrastructure.Skills;

/// <summary>An in-memory snapshot of one package, including executable and binary assets.</summary>
public sealed class CatalogSkill
{
	internal CatalogSkill(string folderName, string source, string relativePath, string? reference,
		string? revision, string? releaseNotes, Dictionary<string, byte[]> content, string? installedFilePath, string? inspectionWarning = null)
	{
		FolderName = folderName;
		Source = source;
		RelativePath = relativePath;
		Reference = reference;
		Revision = revision;
		ReleaseNotes = releaseNotes;
		Content = content;
		Files = Array.AsReadOnly(content.Keys.Order(StringComparer.Ordinal).ToArray());
		InstalledFilePath = installedFilePath;
		InspectionWarning = inspectionWarning;
		if (content.TryGetValue("SKILL.md", out var bytes))
		{
			using var reader = new StringReader(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
			Name = ManifestName(reader, folderName);
		}
		else
		{
			Name = folderName;
		}
	}

	internal static string ManifestName(TextReader reader, string fallback)
	{
		if (reader.ReadLine()?.Trim() != "---")
		{
			return fallback;
		}

		var header = new StringBuilder();
		while (header.Length < 65_536 && reader.ReadLine() is { } line)
		{
			if (line.TrimEnd() != "---")
			{
				header.AppendLine(line);
				continue;
			}

			try
			{
				using var yaml = new StringReader(header.ToString());
				var stream = new YamlStream();
				stream.Load(yaml);
				return stream.Documents.FirstOrDefault()?.RootNode is YamlMappingNode metadata &&
					metadata.Children.TryGetValue("name", out var value) && value is YamlScalarNode { Value.Length: > 0 } scalar
					? scalar.Value : fallback;
			}
			catch (YamlException)
			{
				return fallback;
			}
		}

		return fallback;
	}

	/// <summary>The manifest name, falling back to the package directory name.</summary>
	public string Name { get; }

	/// <summary>The directory name used to install and track this particular package.</summary>
	public string FolderName { get; }

	/// <summary>The existing manifest path for packages discovered under the native skills root.</summary>
	public string? InstalledFilePath { get; }

	/// <summary>Explains why only the manifest could be previewed for a preexisting native installation.</summary>
	public string? InspectionWarning { get; }

	/// <summary>The source directory or public repository URL.</summary>
	public string Source { get; }

	/// <summary>The package directory relative to its source.</summary>
	public string RelativePath { get; }

	/// <summary>The branch or release used to resolve the revision.</summary>
	public string? Reference { get; }

	/// <summary>The immutable Git commit used for the snapshot, when available.</summary>
	public string? Revision { get; }

	/// <summary>The relative paths of all package files.</summary>
	public IReadOnlyList<string> Files { get; }

	internal Dictionary<string, byte[]> Content { get; }
	internal string? ReleaseNotes { get; }

	internal CatalogSkill ForInstallationFolder(string folder) => folder == FolderName ? this :
		new CatalogSkill(folder, Source, RelativePath, Reference, Revision, ReleaseNotes, Content, InstalledFilePath, InspectionWarning);

	/// <summary>Reads preview text from the same bytes that Apply will install.</summary>
	public string ReadFile(string path) => Encoding.UTF8.GetString(Content[path]);

	internal Dictionary<string, string> Hashes() => Content.ToDictionary(
		entry => entry.Key, entry => Convert.ToHexString(SHA256.HashData(entry.Value)), StringComparer.OrdinalIgnoreCase);
}

/// <summary>The source directory, repository reference, and revision recorded for an installed package.</summary>
public sealed record SkillSourceBinding(string Source, string RelativePath, string? Reference, string? Revision);

/// <summary>The proposed installation, including differences against the currently installed files.</summary>
public sealed class SkillInstallPreview
{
	internal SkillInstallPreview(CatalogSkill skill, string destination, string? conflict,
		IReadOnlyList<string> changes, bool isUpdate, string? installedRevision)
	{
		Skill = skill;
		Destination = destination;
		Conflict = conflict;
		Changes = changes;
		IsUpdate = isUpdate;
		InstalledRevision = installedRevision;
	}

	/// <summary>The immutable package to inspect before applying.</summary>
	public CatalogSkill Skill { get; }

	/// <summary>The absolute installation path.</summary>
	public string Destination { get; }

	/// <summary>The cause of a conflict that must be resolved before Apply.</summary>
	public string? Conflict { get; }

	/// <summary>Added, modified, and removed paths relative to the package root.</summary>
	public IReadOnlyList<string> Changes { get; }

	/// <summary>Release notes supplied by the source when available.</summary>
	public string? ReleaseNotes => Skill.ReleaseNotes;

	/// <summary>Whether the destination already has a source binding.</summary>
	public bool IsUpdate { get; }

	/// <summary>The revision recorded when the destination was last installed.</summary>
	public string? InstalledRevision { get; }
}