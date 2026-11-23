using Pact.Core.Agents;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

/// <summary>A revision-checked physical package rename and the associated link and configuration changes.</summary>
public sealed class SkillRenamePreview
{
	internal SkillRenamePreview(string registry, string sharedRoot, NativeSkillInstallation installation, NativeSkillConnection connection, string name,
		IReadOnlyList<AgentProfileRecord> profiles, IReadOnlyList<string> projects)
	{
		Registry = registry;
		SharedRoot = sharedRoot;
		Installation = installation;
		Connection = connection;
		Name = name;
		Profiles = profiles.ToArray();
		Projects = projects.ToArray();
		SourceDirectory = Path.GetDirectoryName(Path.GetFullPath(connection.ManifestPath))!;
		DestinationDirectory = SourceDirectory;
	}

	internal NativeSkillInstallation Installation { get; }
	internal string Registry { get; }
	internal string SharedRoot { get; }
	internal NativeSkillConnection Connection { get; }
	internal string Name { get; }
	internal AgentProfileRecord[] Profiles { get; }
	internal string[] Projects { get; }
	internal string? Package { get; set; }
	internal List<RenameLink> Links { get; } = [];
	internal List<SkillConfigEdit> Edits { get; } = [];

	/// <summary>The verified physical package directory.</summary>
	public string SourceDirectory { get; }

	/// <summary>The destination beside the original package.</summary>
	public string DestinationDirectory { get; internal set; }

	/// <summary>A conflict that prevents the rename.</summary>
	public string? Conflict { get; internal set; }

	/// <summary>The physical move, native links and settings files affected by confirmation.</summary>
	public IReadOnlyList<string> Changes { get; internal set; } = [];

	/// <summary>The package, connection and settings paths changed by confirmation.</summary>
	public IReadOnlyList<string> AffectedFiles { get; internal set; } = [];

	/// <summary>The profiles that can observe the rename or a migrated setting.</summary>
	public IReadOnlyList<string> AffectedProfiles { get; internal set; } = [];

	/// <summary>Temporary entries that could not be cleaned after a successful rename.</summary>
	public string? CleanupWarning { get; internal set; }
}

internal sealed record RenameLink(string Path, string OriginalTarget, string NewTarget, bool IsSymbolic, bool RetireProjection = false);

public sealed partial class SkillCatalogService
{
	/// <summary>Checks physical ownership of a selected package without reading its complete contents.</summary>
	public bool CanOfferRename(NativeSkillInstallation installation, NativeSkillConnection connection) =>
		CanOfferMaintenance(installation, connection, makeShared: false) && new DirectoryInfo(Path.GetDirectoryName(connection.ManifestPath)!).LinkTarget is null;

	/// <summary>Previews renaming an owned native package, preserving file contents and tracked source identity.</summary>
	public SkillRenamePreview PreviewRename(NativeSkillInstallation installation, NativeSkillConnection connection, string newFolderName,
		IReadOnlyList<AgentProfileRecord>? profiles = null, IReadOnlyList<string>? projectDirectories = null)
	{
		ArgumentNullException.ThrowIfNull(installation);
		ArgumentNullException.ThrowIfNull(connection);
		ArgumentNullException.ThrowIfNull(newFolderName);
		var preview = new SkillRenamePreview(_registry, _skillsDirectory, installation, connection, newFolderName, profiles ?? [], projectDirectories ?? []);
		try
		{
			PrepareRename(preview);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
		{
			preview.Conflict = ex.Message;
		}
		return preview;
	}

	private void PrepareRename(SkillRenamePreview preview)
	{
		var owned = NewMaintenance(preview.Installation, preview.Connection, makeShared: false);
		var (_, link) = CheckOwnership(owned);
		if (link is not null)
		{ throw new IOException("Select the physical package directory to rename it."); }
		var name = preview.Name;
		SkillPackageReader.ValidatePath(name);
		if (name.Contains('/', StringComparison.Ordinal) || name.Equals(".system", StringComparison.OrdinalIgnoreCase) ||
			name.StartsWith(".pact-skill-", StringComparison.OrdinalIgnoreCase))
		{
			throw new IOException("Choose one package folder name.");
		}
		var source = preview.SourceDirectory;
		var destination = Path.Combine(Path.GetDirectoryName(source)!, name);
		preview.DestinationDirectory = destination;
		if (destination == source)
		{ throw new IOException("Choose a different folder name."); }
		if (!Same(destination, source) && (Path.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null))
		{
			throw new IOException($"The destination {destination} is already occupied.");
		}
		// On a case-sensitive directory, two sibling entries may differ only in casing.
		if (Same(destination, source) && Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(source)!).Any(path => path == destination))
		{
			throw new IOException($"The destination {destination} is already occupied.");
		}
		preview.Package = PackageRevision(source);
		using var manifest = File.OpenText(Path.Combine(source, "SKILL.md"));
		var declared = CatalogSkill.ManifestName(manifest, string.Empty);
		var roots = Volatile.Read(ref _maintenanceRoots).Concat(preview.Projects.SelectMany(project => new[]
			{ Path.Combine(project, ".agents", "skills"), Path.Combine(project, ".claude", "skills") }));
		foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists))
		{
			FindRenameLinks(root, source, destination, preview.Links);
		}
		var projections = Path.Combine(ResolveDirectory(Path.GetDirectoryName(Path.GetDirectoryName(_registry)!)!), "Settings", "Skills", "FolderDiscovery");
		if (Directory.Exists(projections))
		{
			var projectionLinks = new List<RenameLink>();
			FindRenameLinks(projections, source, destination, projectionLinks);
			foreach (var projection in projectionLinks)
			{
				var receipt = MaintenanceReceipt(projection.Path, false);
				if (SkillDirectoryConnection.ReadTarget(ReadReceipt(receipt), projection.Path) is not { } savedTarget || !Same(savedTarget, source))
				{
					throw new IOException($"Cannot verify the managed folder discovery connection at {projection.Path}.");
				}
				preview.Links.Add(projection with { RetireProjection = true });
			}
		}
		foreach (var observed in preview.Installation.Connections)
		{
			var alias = Path.GetDirectoryName(observed.ManifestPath)!;
			if (!Same(alias, source) && !preview.Links.Any(item => Same(item.Path, alias)) && Same(ResolveDirectory(alias), source))
			{
				throw new IOException($"The redirected discovery path {alias} cannot be retargeted independently.");
			}
		}
		preview.Links.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
		PreviewRenameReceipts(preview, roots);
		if (Same(Path.GetDirectoryName(source)!, _skillsDirectory) && File.Exists(_registry))
		{
			var original = File.ReadAllText(_registry);
			var registry = ParseRegistry(original);
			var oldKey = BindingKey(registry, Path.GetFileName(source));
			var newKey = BindingKey(registry, name);
			if (registry.ContainsKey(newKey) && newKey != oldKey)
			{ throw new IOException($"A binding already uses {name}."); }
			if (registry.ContainsKey(oldKey))
			{
				var binding = registry[oldKey]?.DeepClone();
				registry.Remove(oldKey);
				registry[name] = binding;
				preview.Edits.Add(new SkillConfigEdit(_registry, original, registry.ToJsonString()));
			}
		}
		var paths = new AppPaths(Path.GetDirectoryName(Path.GetDirectoryName(_registry)!)!);
		var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [source] = destination };
		preview.Edits.AddRange(SkillRenameReferences.Preview(paths, preview.Profiles, preview.Projects, moves, preview.Installation.Connections, declared, _userHome));
		preview.Edits.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
		preview.Changes = [$"Rename {source} to {destination}. Package files retain their contents.",
			.. preview.Links.Select(item => item.RetireProjection
				? $"Remove {item.Path}. The next matching Claude launch creates the renamed discovery connection."
				: $"Retarget {item.Path} to {item.NewTarget}."),
			.. preview.Edits.Select(item => $"Update {item.Path}.")];
		preview.AffectedFiles = new[] { source, destination }.Concat(preview.Links.Select(item => item.Path))
			.Concat(preview.Edits.Select(item => item.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
		var activation = MaintenanceActivation();
		preview.AffectedProfiles = ProfileNames(preview.Installation.Connections.SelectMany(connection => connection.Profiles)
			.Concat(preview.Profiles.Where(Affected)));

		bool Affected(AgentProfileRecord profile)
		{
			var native = VerifiedNativeRoot(activation, profile);
			if (native is null)
			{
				return false;
			}
			var nativeConfig = Path.Combine(Path.GetDirectoryName(native)!, profile.Kind == AgentKind.Codex ? "config.toml" : "settings.json");
			var config = ResolveManifest(nativeConfig);
			return IsWithin(source, ResolveDirectory(native)) ||
				profile.Kind == AgentKind.Codex && IsWithin(source, ResolveDirectory(_skillsDirectory)) ||
				preview.Links.Any(item => IsWithin(item.Path, native)) ||
				preview.Edits.Any(item => Same(item.Path, config)) ||
				preview.Projects.Any(project =>
				{
					var projectRoot = Path.Combine(project, profile.Kind == AgentKind.Codex ? ".agents" : ".claude");
					return IsWithin(source, Path.Combine(projectRoot, "skills")) ||
						preview.Links.Any(item => IsWithin(item.Path, Path.Combine(projectRoot, "skills"))) ||
						preview.Edits.Any(item => IsWithin(item.Path, Path.Combine(project, profile.Kind == AgentKind.Codex ? ".codex" : ".claude")));
				});
		}
	}

	private static void FindRenameLinks(string root, string source, string destination, List<RenameLink> found)
	{
		var pending = new Stack<string>();
		pending.Push(root);
		while (pending.TryPop(out var current))
		{
			var info = new DirectoryInfo(current);
			if (info.LinkTarget is { } rawTarget)
			{
				var target = Path.GetFullPath(rawTarget, Path.GetDirectoryName(current)!);
				var physical = ResolveDirectory(target);
				if (IsWithin(physical, source))
				{
					if (Same(root, current) || IsWithin(current, Path.Combine(root, ".system")) ||
						!Same(Path.GetDirectoryName(current)!, ResolveDirectory(Path.GetDirectoryName(current)!)))
					{
						throw new IOException($"The redirected or system-owned path {current} depends on this package.");
					}
					var relative = Path.GetRelativePath(source, physical);
					var next = relative == "." ? destination : Path.Combine(destination, relative);
					if (!found.Any(item => Same(item.Path, current)))
					{ found.Add(new(current, rawTarget, next, SkillRenameLinkKind.IsSymbolic(current))); }
				}
				continue;
			}
			if (Same(current, source) || File.Exists(Path.Combine(current, "SKILL.md")))
			{ continue; }
			foreach (var child in Directory.EnumerateDirectories(current))
			{
				var name = Path.GetFileName(child);
				if (!name.Equals(".git", StringComparison.OrdinalIgnoreCase) &&
					!name.StartsWith(".pact-skill-", StringComparison.OrdinalIgnoreCase))
				{ pending.Push(child); }
			}
		}
	}

	private void PreviewRenameReceipts(SkillRenamePreview preview, IEnumerable<string> roots)
	{
		foreach (var migration in new[] { false, true })
		{
			var folder = Path.GetDirectoryName(MaintenanceReceipt(preview.SourceDirectory, migration));
			if (!Directory.Exists(folder))
			{ continue; }
			foreach (var path in Directory.EnumerateFiles(folder, "*.txt"))
			{
				var original = File.ReadAllText(path);
				var target = SkillDirectoryConnection.ReadTarget(original);
				if (!Path.IsPathFullyQualified(target ?? ""))
				{ throw new IOException($"Invalid connection receipt at {path}."); }
				if (!IsWithin(target!, preview.SourceDirectory))
				{ continue; }
				var link = preview.Links.FirstOrDefault(item => Same(MaintenanceReceipt(item.Path, migration), path));
				if (link is null && !IsObsoleteReceipt(path, original, target!, roots, migration))
				{ throw new IOException($"An undiscovered Pact link depends on {preview.SourceDirectory}. Open its project to locate the connection in {path}."); }
				preview.Edits.Add(new(path, original, link is null || link.RetireProjection ? null :
					SkillDirectoryConnection.FormatReceipt(link.NewTarget, link.Path)));
			}
		}
	}

	/// <summary>Renames the package after checking all observed file, configuration and link revisions again.</summary>
	public void Apply(SkillRenamePreview preview)
	{
		ArgumentNullException.ThrowIfNull(preview);
		if (preview.Conflict is not null || preview.Package is null || !Same(preview.Registry, _registry) || !Same(preview.SharedRoot, _skillsDirectory))
		{
			throw new IOException(preview.Conflict ?? "Open a fresh rename preview for this catalog.");
		}
		WithInstallLock(() =>
		{
			SkillActivationService.WriteGate.Wait();
			try
			{
				var current = PreviewRename(preview.Installation, preview.Connection, preview.Name, preview.Profiles, preview.Projects);
				if (current.Conflict is not null || current.SourceDirectory != preview.SourceDirectory ||
					current.DestinationDirectory != preview.DestinationDirectory || preview.Package != current.Package ||
					!preview.Links.SequenceEqual(current.Links) || !preview.Edits.SequenceEqual(current.Edits))
				{
					throw new IOException(current.Conflict ?? "The package, connection, or configuration changed after preview.");
				}
				CommitRename(preview);
			}
			finally { SkillActivationService.WriteGate.Release(); }
		});
	}

	private static void CommitRename(SkillRenamePreview preview)
	{
		var rollback = new Stack<Action>();
		var backups = new List<string>();
		var source = preview.SourceDirectory;
		var destination = preview.DestinationDirectory;
		var stage = TemporarySibling(source);
		var recoveryLocations = new List<string> { $"Source: {source}", $"Destination: {destination}", $"Package staging: {stage}" };
		try
		{
			foreach (var link in preview.Links)
			{
				if (new DirectoryInfo(link.Path).LinkTarget != link.OriginalTarget || SkillRenameLinkKind.IsSymbolic(link.Path) != link.IsSymbolic)
				{
					throw new IOException($"The link changed at {link.Path}.");
				}
				var backup = TemporarySibling(link.Path);
				Directory.Move(link.Path, backup);
				recoveryLocations.Add($"Link {link.Path} staged at {backup}");
				rollback.Push(() => Directory.Move(backup, link.Path));
				backups.Add(backup);
			}
			Directory.Move(source, stage);
			rollback.Push(() => Directory.Move(stage, source));
			if (new DirectoryInfo(stage).LinkTarget is not null || PackageRevision(stage) != preview.Package)
			{
				throw new IOException("The package changed while staging the rename.");
			}
			Directory.Move(stage, destination);
			rollback.Push(() => Directory.Move(destination, stage));
			foreach (var link in preview.Links.Where(item => !item.RetireProjection))
			{
				var connection = new SkillLinkEdit(link.Path, link.NewTarget, "", true);
				var created = false;
				rollback.Push(() =>
				{
					if (created)
					{
						SkillDirectoryConnection.RemoveCreatedLink(connection);
					}
				});
				if (link.IsSymbolic)
				{
					var target = Path.IsPathFullyQualified(link.OriginalTarget) ? link.NewTarget :
						Path.GetRelativePath(Path.GetDirectoryName(link.Path)!, link.NewTarget);
					Directory.CreateSymbolicLink(link.Path, target);
				}
				else
				{
					SkillDirectoryConnection.CreateLinkAsync(connection).GetAwaiter().GetResult();
				}
				created = true;
			}
			foreach (var edit in preview.Edits)
			{
				if (ReadReceipt(edit.Path) != edit.Original)
				{ throw new IOException($"The file changed at {edit.Path}."); }
				var previous = File.Exists(edit.Path) ? File.ReadAllBytes(edit.Path) : null;
				if (edit.Updated is null)
				{ File.Delete(edit.Path); }
				else
				{ SkillActivationService.WriteSettingsAsync(edit.Path, edit.Updated, previous, CancellationToken.None).GetAwaiter().GetResult(); }
				rollback.Push(() =>
				{
					if (ReadReceipt(edit.Path) != edit.Updated)
					{ throw new IOException($"The file changed during rollback at {edit.Path}."); }
					if (previous is null)
					{ File.Delete(edit.Path); }
					else
					{ AtomicFileWriter.WriteBytesAsync(edit.Path, previous, CancellationToken.None).GetAwaiter().GetResult(); }
				});
			}
		}
		catch (Exception failure)
		{
			var errors = new List<Exception> { failure };
			while (rollback.TryPop(out var restore))
			{
				try
				{ restore(); }
				catch (Exception ex) { errors.Add(ex); }
			}
			if (errors.Count > 1)
			{ throw new AggregateException("Package rename failed and rollback needs attention. " + string.Join(". ", recoveryLocations) + ".", errors); }
			throw;
		}
		var retained = new List<string>();
		foreach (var backup in backups)
		{
			try
			{ Directory.Delete(backup); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { retained.Add(backup); }
		}
		if (retained.Count > 0)
		{ preview.CleanupWarning = "The package was renamed. Link cleanup is pending at " + string.Join(", ", retained); }
	}

	private static string TemporarySibling(string path) => Path.Combine(Path.GetDirectoryName(path)!, ".pact-skill-" + Guid.NewGuid().ToString("N"));
}
