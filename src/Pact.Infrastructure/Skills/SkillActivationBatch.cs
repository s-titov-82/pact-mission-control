using System.Text;
using Pact.Core.Agents;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

/// <summary>The final choice for a single matrix cell after applying any group choice and individual exceptions.</summary>
public sealed record SkillMatrixEdit(AgentProfileRecord Profile, string SkillName, string SkillFile, string? ProjectDirectory, SkillMode Mode,
	SkillCatalogSource? Source = null, bool FolderScope = false);

/// <summary>The persisted choice for current and future installations from one source.</summary>
public sealed record SkillSourceMatrixEdit(AgentProfileRecord Profile, SkillCatalogSource Source, string? ProjectDirectory, SkillMode Mode, bool FolderScope = false);

/// <summary>An installed member represented by its native manifest alias for a specific profile.</summary>
public sealed record SkillMatrixMember(SkillCatalogSource Source, string SkillName, string SkillFile, AgentProfileRecord Profile);

/// <summary>All native edits reviewed together, including shared configuration files and discovery connections.</summary>
public sealed class SkillActivationBatchPreview
{
	internal SkillActivationBatchPreview(string root, IReadOnlyList<SkillActivationPreview> changes,
		IReadOnlyList<SkillConfigEdit> files, IReadOnlyList<SkillLinkEdit> links, IEnumerable<string>? policyProfiles = null)
	{
		(StorageRoot, Changes, Files, Links) = (root, changes, files, links);
		AffectedFiles = files.Where(f => f.Original != f.Updated).Select(f => f.Path)
			.Concat(links.SelectMany(l => new[] { l.Path, l.ReceiptPath })).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		AffectedProfiles = changes.Where(c => c.AffectedFiles.Count != 0).SelectMany(c => c.AffectedProfiles)
			.Concat(policyProfiles ?? []).Distinct().ToArray();
	}

	/// <summary>Cell-level details in the order submitted to preview.</summary>
	public IReadOnlyList<SkillActivationPreview> Changes { get; }

	/// <summary>Paths that will change, counting a shared native file only once.</summary>
	public IReadOnlyList<string> AffectedFiles { get; }

	/// <summary>Configured profiles that observe any changes in this batch.</summary>
	public IReadOnlyList<string> AffectedProfiles { get; }

	internal IReadOnlyList<SkillConfigEdit> Files { get; }
	internal IReadOnlyList<SkillLinkEdit> Links { get; }
	internal string StorageRoot { get; }
}

internal sealed record SkillConfigEdit(string Path, string? Original, string? Updated);

public sealed partial class SkillActivationService
{
	/// <summary>Combines edits against one in-memory revision per native file and detects incompatible shared settings.</summary>
	public SkillActivationBatchPreview PreviewBatch(IReadOnlyList<SkillMatrixEdit> edits, IReadOnlyList<AgentProfileRecord> profiles) =>
		PreviewBatch(edits, profiles, [], []);

	private SkillActivationBatchPreview PrepareBatch(List<SkillMatrixEdit> edits, IReadOnlyList<AgentProfileRecord> profiles)
	{
		ArgumentNullException.ThrowIfNull(edits);
		ArgumentNullException.ThrowIfNull(profiles);
		var files = new Dictionary<string, SkillConfigEdit>(PathsEqual);
		var links = new Dictionary<string, SkillLinkEdit>(PathsEqual);
		var choices = new Dictionary<(string Path, string Selector), SkillMode>();
		var changes = new List<SkillActivationPreview>(edits.Count);

		string? CurrentContent(string path)
		{
			path = SkillCatalogService.ResolveManifest(path);
			if (!files.TryGetValue(path, out var entry))
			{
				var original = ReadFile(path);
				entry = new(path, original, original);
				files.Add(path, entry);
			}
			return entry.Updated;
		}

		foreach (var edit in edits)
		{
			ArgumentNullException.ThrowIfNull(edit);
			var change = Prepare(edit.Profile, edit.SkillName, edit.SkillFile, edit.ProjectDirectory, edit.Mode, profiles, CurrentContent);
			var key = (change.FilePath.ToUpperInvariant(), change.Selector);
			if (choices.TryGetValue(key, out var previous) && previous != edit.Mode)
			{
				throw new InvalidOperationException($"Choose the same setting for {edit.SkillName} in profiles sharing {change.FilePath}.");
			}
			choices[key] = edit.Mode;
			files[change.FilePath] = files[change.FilePath] with { Updated = change.Updated };
			if (change.Link is { } link)
			{
				if (links.TryGetValue(link.Path, out var other)
					&& (other.Create != link.Create || !PathsEqual.Equals(other.Target, link.Target)))
				{
					throw new InvalidOperationException($"Conflicting skill connections at {link.Path}: {other.Target} and {link.Target}.");
				}
				links[link.Path] = link;
			}
			changes.Add(change);
		}
		return new(_storage, changes, files.Values.ToArray(), links.Values.ToArray());
	}

	/// <summary>Checks the complete preview before committing. An interrupted operation restores previously applied edits.</summary>
	public async Task ApplyAsync(SkillActivationBatchPreview preview, CancellationToken token)
	{
		ArgumentNullException.ThrowIfNull(preview);
		if (!PathsEqual.Equals(_storage, preview.StorageRoot))
		{
			throw new InvalidOperationException("The Pact settings directory changed after preview.");
		}
		if (preview.AffectedFiles.Count == 0)
		{
			return;
		}
		await WriteGate.WaitAsync(token);
		try
		{
			foreach (var file in preview.Files)
			{
				CheckRevision(file.Path, file.Original);
			}
			foreach (var link in preview.Links)
			{
				SkillDirectoryConnection.Validate(link);
			}

			var completedLinks = new Stack<SkillLinkEdit>();
			var completedFiles = new Stack<(SkillConfigEdit Edit, byte[]? Original)>();
			try
			{
				foreach (var link in preview.Links)
				{
					await SkillDirectoryConnection.ApplyAsync(link, token);
					completedLinks.Push(link);
				}
				foreach (var file in preview.Files.Where(file => file.Updated != file.Original))
				{
					var bytes = File.Exists(file.Path) ? await File.ReadAllBytesAsync(file.Path, token) : null;
					CheckRevision(file.Path, file.Original);
					await AtomicFileWriter.WriteTextAsync(file.Path, file.Updated!, null, new UTF8Encoding(false), token);
					completedFiles.Push((file, bytes));
				}
			}
			catch (Exception error)
			{
				var failures = new List<Exception>();
				foreach (var (file, bytes) in completedFiles)
				{
					try
					{
						CheckRevision(file.Path, file.Updated);
						if (bytes == null)
						{
							File.Delete(file.Path);
						}
						else
						{
							await AtomicFileWriter.WriteBytesAsync(file.Path, bytes, CancellationToken.None);
						}
					}
					catch (Exception rollback) { failures.Add(rollback); }
				}
				foreach (var link in completedLinks)
				{
					try
					{ await SkillDirectoryConnection.ApplyAsync(link with { Create = !link.Create }, CancellationToken.None); }
					catch (Exception rollback) { failures.Add(rollback); }
				}
				if (failures.Count != 0)
				{
					throw new AggregateException("The settings changed during rollback. Review the affected paths.", [error, .. failures]);
				}
				throw;
			}
		}
		finally { WriteGate.Release(); }
	}

	private static void CheckRevision(string path, string? expected)
	{
		if (ReadFile(path) != expected)
		{
			throw new InvalidOperationException($"Settings changed after preview: {path}. Preview the change again.");
		}
	}
}