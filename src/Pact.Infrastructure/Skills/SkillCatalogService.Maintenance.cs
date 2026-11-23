using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pact.Core.Agents;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Skills;

/// <summary>Describes an exact native package or directory link selected for removal or migration.</summary>
public sealed class SkillMaintenancePreview
{
	internal SkillMaintenancePreview(NativeSkillInstallation installation, NativeSkillConnection connection, string source, string? shared,
		IReadOnlyList<AgentProfileRecord>? profiles)
	{
		Installation = installation;
		Connection = connection;
		SourceDirectory = source;
		SharedDirectory = shared;
		Profiles = profiles ?? [];
	}

	internal NativeSkillInstallation Installation { get; }
	internal NativeSkillConnection Connection { get; }
	internal IReadOnlyList<AgentProfileRecord> Profiles { get; }
	internal SkillMaintenanceRevision? Revision { get; set; }
	internal string[] IdenticalCopies { get; set; } = [];
	internal IReadOnlyList<SkillConfigEdit> Edits { get; set; } = [];

	/// <summary>The selected native path, which may be a link to a separately owned package.</summary>
	public string SourceDirectory { get; }

	/// <summary>The shared destination for migration, or null for removal.</summary>
	public string? SharedDirectory { get; }

	/// <summary>Whether removal unlinks only this directory entry.</summary>
	public bool IsLink { get; internal set; }

	/// <summary>An unresolved conflict prevents confirmation.</summary>
	public string? Conflict { get; internal set; }

	/// <summary>The paths and actions to present before confirmation.</summary>
	public IReadOnlyList<string> Changes { get; internal set; } = [];

	/// <summary>The package, connection and settings paths changed by confirmation.</summary>
	public IReadOnlyList<string> AffectedFiles { get; internal set; } = [];

	/// <summary>The profiles that can observe the package or connection change.</summary>
	public IReadOnlyList<string> AffectedProfiles { get; internal set; } = [];

	/// <summary>Residual staging paths when cleanup could not finish after a successful confirmation.</summary>
	public string? CleanupWarning { get; internal set; }
}

internal sealed record SkillMaintenanceRevision(string Physical, string? Link, string? Package, string? SharedPackage,
	string? Binding, string? DestinationBinding, string? ActivationReceipt, string? MigrationReceipt);

public sealed partial class SkillCatalogService
{
	/// <summary>Previews moving an independently owned native package into shared skills and retaining its original CLI path as a link.</summary>
	public SkillMaintenancePreview PreviewMakeShared(NativeSkillInstallation installation, NativeSkillConnection connection,
		IReadOnlyList<AgentProfileRecord>? profiles = null) =>
		PreviewMaintenance(installation, connection, makeShared: true, profiles);

	/// <summary>Previews removing the selected directory entry and its applicable binding or link receipt.</summary>
	public SkillMaintenancePreview PreviewDelete(NativeSkillInstallation installation, NativeSkillConnection connection) =>
		PreviewMaintenance(installation, connection, makeShared: false);

	/// <summary>Guards removal from an identical-copy report against deleting the last matching package.</summary>
	public SkillMaintenancePreview PreviewDelete(NativeSkillInstallation installation, NativeSkillConnection connection, SkillDuplicateGroup group)
	{
		ArgumentNullException.ThrowIfNull(group);
		var preview = PreviewDelete(installation, connection);
		if (group.Kind == SkillDuplicateKind.Identical && !preview.IsLink && preview.Conflict is null)
		{
			preview.IdenticalCopies = group.Installations.Where(item => !Same(item.ResolvedManifest, installation.ResolvedManifest))
				.Select(item => Path.GetDirectoryName(item.ResolvedManifest)!).ToArray();
			preview.Conflict = DuplicateConflict(preview);
		}
		return preview;
	}

	private static string? DuplicateConflict(SkillMaintenancePreview preview)
	{
		foreach (var path in preview.IdenticalCopies)
		{
			try
			{
				if (Same(path, ResolveDirectory(path)) && File.Exists(Path.Combine(path, "SKILL.md"))
					&& PackageRevision(path, InstalledHashes(path, skipGitMetadata: true), skipGitMetadata: true) == preview.Revision?.Package)
				{
					return null;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// Another independent copy in this group may still match.
			}
		}
		return "The other copies changed or could not be verified. Run Find duplicates again.";
	}

	/// <summary>Tests the selected path's ownership without reading package files or scanning dependent links.</summary>
	public bool CanOfferMaintenance(NativeSkillInstallation installation, NativeSkillConnection connection, bool makeShared)
	{
		ArgumentNullException.ThrowIfNull(installation);
		ArgumentNullException.ThrowIfNull(connection);
		var preview = NewMaintenance(installation, connection, makeShared);
		try
		{
			CheckOwnership(preview);
			return true;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	/// <summary>Commits the confirmed operation after revalidating package bytes, aliases, metadata and ownership.</summary>
	public void Apply(SkillMaintenancePreview preview)
	{
		ArgumentNullException.ThrowIfNull(preview);
		if (preview.Conflict is not null || preview.Revision is null)
		{
			throw new IOException(preview.Conflict ?? "Open a fresh maintenance preview.");
		}

		WithInstallLock(() =>
		{
			SkillActivationService.WriteGate.Wait();
			try
			{
				var current = PreviewMaintenance(preview.Installation, preview.Connection, preview.SharedDirectory is not null, preview.Profiles);
				if (current.Conflict is not null || current.Revision != preview.Revision || current.SharedDirectory != preview.SharedDirectory ||
					!current.Edits.SequenceEqual(preview.Edits))
				{
					throw new IOException(current.Conflict ?? "The skill changed after preview. Open a fresh preview.");
				}
				if (preview.IdenticalCopies.Length > 0 && DuplicateConflict(preview) is { } conflict)
				{
					throw new IOException(conflict);
				}
				CommitMaintenance(current);
				preview.CleanupWarning = current.CleanupWarning;
			}
			finally
			{
				SkillActivationService.WriteGate.Release();
			}
		});
	}

	private SkillMaintenancePreview PreviewMaintenance(NativeSkillInstallation installation, NativeSkillConnection connection, bool makeShared,
		IReadOnlyList<AgentProfileRecord>? profiles = null)
	{
		ArgumentNullException.ThrowIfNull(installation);
		ArgumentNullException.ThrowIfNull(connection);
		var preview = NewMaintenance(installation, connection, makeShared, profiles);
		try
		{
			InspectMaintenance(preview);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
		{
			preview.Conflict = ex.Message;
		}
		return preview;
	}

	private SkillMaintenancePreview NewMaintenance(NativeSkillInstallation installation, NativeSkillConnection connection, bool makeShared,
		IReadOnlyList<AgentProfileRecord>? profiles = null)
	{
		var source = Path.GetDirectoryName(Path.GetFullPath(connection.ManifestPath))!;
		var shared = makeShared ? Path.Combine(_skillsDirectory, Path.GetFileName(source)) : null;
		return new(installation, connection, source, shared, profiles);
	}

	private (string Physical, string? Link) CheckOwnership(SkillMaintenancePreview preview)
	{
		var source = preview.SourceDirectory;
		var connection = preview.Connection;
		var roots = Volatile.Read(ref _maintenanceRoots);
		var root = connection.DiscoveryRoot;
		if (root is null || !roots.Contains(root, StringComparer.OrdinalIgnoreCase) ||
			!preview.Installation.Connections.Contains(connection) || !IsWithin(source, root) || Same(source, root) ||
			!Same(connection.ManifestPath, Path.Combine(source, "SKILL.md")) || !File.Exists(connection.ManifestPath))
		{
			throw new IOException("Select an existing installation inside a discovered native skills directory.");
		}
		var parent = Path.GetDirectoryName(source)!;
		if (!Same(parent, ResolveDirectory(parent)))
		{
			throw new IOException($"The parent of {source} is redirected. Its contents belong to the linked source.");
		}

		var physical = ResolveDirectory(source);
		if (!Same(Path.Combine(physical, "SKILL.md"), preview.Installation.ResolvedManifest))
		{
			throw new IOException($"The selected package at {source} changed since discovery.");
		}
		var link = new DirectoryInfo(source).LinkTarget;
		preview.IsLink = link is not null;
		if (roots.Any(dir => IsWithin(source, Path.Combine(dir, ".system")) || IsWithin(physical, Path.Combine(ResolveDirectory(dir), ".system"))))
		{
			throw new IOException("CLI-managed system packages must remain in place.");
		}
		if (link is null && FindRepository(source) is { } repository)
		{
			throw new IOException($"The files at {source} belong to repository {repository}.");
		}
		if (preview.SharedDirectory is { } shared && (link is not null || IsWithin(source, _skillsDirectory) ||
			IsWithin(_skillsDirectory, source) || !Same(_skillsDirectory, ResolveDirectory(_skillsDirectory)) || new DirectoryInfo(shared).LinkTarget is not null))
		{
			throw new IOException("Choose a physical native package and an independently owned shared destination.");
		}
		return (physical, link);
	}

	private void InspectMaintenance(SkillMaintenancePreview preview)
	{
		var (physical, link) = CheckOwnership(preview);
		var source = preview.SourceDirectory;
		var parent = Path.GetDirectoryName(source)!;
		var registry = LoadRegistry();
		var name = Path.GetFileName(source);
		var binding = Same(parent, _skillsDirectory) ? registry[BindingKey(registry, name)]?.ToJsonString() : null;
		var activation = MaintenanceReceipt(source, migration: false);
		var migration = MaintenanceReceipt(source, migration: true);
		var activationText = ReadReceipt(activation);
		var migrationText = ReadReceipt(migration);
		var changes = new List<string>();
		var files = new List<string> { source };
		preview.Changes = changes;
		var package = link is null ? PackageRevision(source) : null;
		string? sharedRevision = null;
		string? destinationBinding = null;
		if (preview.SharedDirectory is { } shared)
		{
			if (Directory.Exists(shared))
			{
				sharedRevision = PackageRevision(shared);
				if (sharedRevision != package)
				{
					throw new IOException($"The shared package at {shared} differs from {source}.");
				}
			}
			else if (Path.Exists(shared) || SkillPackageReader.FindNameCollision(_skillsDirectory, shared, preview.Installation.Skill.Name) is not null)
			{
				throw new IOException($"A shared package or file conflicts with {shared}.");
			}
			destinationBinding = registry[BindingKey(registry, name)]?.ToJsonString();
			if (destinationBinding is not null && sharedRevision is null)
			{
				throw new IOException($"A saved source binding already claims the missing destination {shared}.");
			}
			changes.Add(sharedRevision is null ? $"Move package files from {source} to {shared}." :
				$"Reuse {shared}. File contents and named streams match. The shared directory keeps its own permissions and timestamps.");
			changes.Add($"Replace {source} with a directory link to {shared}. Existing CLI paths continue to work.");
			changes.Add($"Record the migrated link in {migration}.");
			files.AddRange([shared, migration]);
			if (activationText is not null)
			{
				changes.Add($"Remove the obsolete connection receipt {activation}.");
				files.Add(activation);
			}
			var activationService = MaintenanceActivation();
			var observing = preview.Installation.Connections.SelectMany(connection => connection.Profiles).Concat(
				preview.Profiles.Where(profile => profile.Kind == AgentKind.Codex && VerifiedNativeRoot(activationService, profile) is not null));
			preview.AffectedProfiles = ProfileNames(observing);
		}
		else
		{
			CheckDependents(source, physical, link is not null, Volatile.Read(ref _maintenanceRoots), activation, migration);
			changes.Add(link is null ? $"Delete the package directory and its files at {source}." :
				$"Remove the directory link at {source}. Its package remains at {physical}.");
			if (link is null)
			{
				changes.AddRange(Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase));
			}
			if (binding is not null)
			{
				changes.Add($"Remove the update binding for {name} from {_registry}.");
				files.Add(_registry);
			}
			if (activationText is not null)
			{
				changes.Add($"Remove the connection receipt {activation}.");
				files.Add(activation);
			}
			if (migrationText is not null)
			{
				changes.Add($"Remove the migration receipt {migration}.");
				files.Add(migration);
			}
			var recordedTarget = SkillDirectoryConnection.ReadTarget(migrationText, source);
			var survivingManifest = link is not null && recordedTarget is not null && Same(recordedTarget, physical) &&
				Same(Path.GetDirectoryName(physical)!, _skillsDirectory) ? Path.Combine(physical, "SKILL.md") : null;
			if (survivingManifest is not null && (!File.Exists(survivingManifest) || !Same(ResolveManifest(survivingManifest), survivingManifest)))
			{
				throw new IOException($"Cannot verify the shared manifest at {survivingManifest} before removing {source}.");
			}
			preview.Edits = SkillRenameReferences.PreviewDeletion(Path.GetDirectoryName(_registry)!, source, link is not null, survivingManifest);
			changes.AddRange(preview.Edits.Select(edit => survivingManifest is null ? $"Remove skill rules for {name} from {edit.Path}." :
				$"Move skill rules for {name} to {survivingManifest} in {edit.Path}."));
			files.AddRange(preview.Edits.Select(edit => edit.Path));
			preview.AffectedProfiles = ProfileNames(preview.Connection.Profiles);
		}
		preview.AffectedFiles = files;
		preview.Revision = new(physical, link, package, sharedRevision, binding, destinationBinding, activationText, migrationText);
	}

	private SkillActivationService MaintenanceActivation() =>
		new(new AppPaths(Path.GetDirectoryName(Path.GetDirectoryName(_registry)!)!), _skillsDirectory, _userHome);

	private static string? VerifiedNativeRoot(SkillActivationService activation, AgentProfileRecord profile)
	{
		try
		{
			return activation.SkillDirectories(profile, null)[0];
		}
		catch (InvalidOperationException)
		{
			return null;
		}
	}

	private static string[] ProfileNames(IEnumerable<AgentProfileRecord> profiles) =>
		profiles.Select(profile => profile.DisplayName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

	private void CheckDependents(string source, string physical, bool isLink, string[] roots, string ownActivation, string ownMigration)
	{
		foreach (var receiptDir in new[] { Path.GetDirectoryName(ownActivation), Path.GetDirectoryName(ownMigration) })
		{
			if (!Directory.Exists(receiptDir))
			{ continue; }
			foreach (var receipt in Directory.EnumerateFiles(receiptDir, "*.txt"))
			{
				if (Same(receipt, ownActivation) || Same(receipt, ownMigration))
				{ continue; }
				var content = File.ReadAllText(receipt);
				var target = SkillDirectoryConnection.ReadTarget(content);
				if (!Path.IsPathFullyQualified(target ?? ""))
				{
					throw new IOException($"The connection receipt {receipt} has an unreadable target.");
				}
				if ((IsWithin(target!, source) || !isLink && IsWithin(ResolveDirectory(target!), physical)) &&
					!IsObsoleteReceipt(receipt, content, target!, roots, Same(receiptDir!, Path.GetDirectoryName(ownMigration)!)))
				{
					throw new IOException($"A Pact connection still depends on {source}. Review {receipt} before removal.");
				}
			}
		}
		foreach (var root in roots.Where(Directory.Exists))
		{
			if (new DirectoryInfo(root).LinkTarget is not null && IsWithin(ResolveDirectory(root), physical) && !isLink)
			{
				throw new IOException($"The native discovery directory {root} depends on {source}.");
			}
			var pending = new Stack<string>();
			pending.Push(root);
			while (pending.TryPop(out var directory))
			{
				foreach (var child in Directory.EnumerateDirectories(directory))
				{
					if (Same(source, child))
					{ continue; }
					var info = new DirectoryInfo(child);
					if (info.LinkTarget is { } target)
					{
						var absolute = Path.GetFullPath(target, directory);
						if (IsWithin(absolute, source) || !isLink && IsWithin(ResolveDirectory(child), physical))
						{
							throw new IOException($"The native link at {child} still depends on {source}. Remove its connection first.");
						}
					}
					else if (!File.Exists(Path.Combine(child, "SKILL.md")) && !info.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
					{
						pending.Push(child);
					}
				}
			}
		}
	}

	private bool IsObsoleteReceipt(string receipt, string content, string target, IEnumerable<string> roots, bool migration)
	{
		var recorded = SkillDirectoryConnection.RecordedPath(content);
		var candidates = recorded is null ? roots.Select(root => Path.Combine(root, Path.GetFileName(target))) : [recorded];
		return candidates.Any(path => Path.IsPathFullyQualified(path) && Same(MaintenanceReceipt(path, migration), receipt) &&
			IsProvenMissing(path));
	}

	private static bool IsProvenMissing(string path)
	{
		// Exists conflates missing entries with inaccessible drives and network shares. Walk to
		// an accessible directory before treating a missing leaf or missing ancestor as obsolete.
		try
		{
			for (var current = path; ; current = Path.GetDirectoryName(current)!)
			{
				try
				{
					var attributes = File.GetAttributes(current);
					if (Same(current, path) || (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
					{
						return false;
					}
					using var entries = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
					_ = entries.MoveNext();
					return true;
				}
				catch (Exception ex) when ((ex is FileNotFoundException or DirectoryNotFoundException) && Path.GetDirectoryName(current) is not null)
				{
					// Try the next parent. If even the root is inaccessible, the receipt remains active.
				}
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return false;
		}
	}

	private string MaintenanceReceipt(string source, bool migration)
	{
		var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.ToUpperInvariant())));
		return Path.Combine(Path.GetDirectoryName(_registry)!, "Skills", migration ? "MigratedConnections" : "Connections", hash + ".txt");
	}

	private static string? ReadReceipt(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
	private static bool Same(string first, string second) => string.Equals(first, second, StringComparison.OrdinalIgnoreCase);

	private static string PackageRevision(string directory) => PackageRevision(directory, InstalledHashes(directory), skipGitMetadata: false);

	private static string PackageRevision(string directory, Dictionary<string, string> files, bool skipGitMetadata)
	{
		using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
		foreach (var (file, digest) in files.OrderBy(entry => entry.Key, StringComparer.Ordinal))
		{
			hash.AppendData(Encoding.UTF8.GetBytes("F" + file + "\0" + digest));
			SkillPackageStreams.AppendHashes(hash, Path.Combine(directory, file));
		}
		hash.AppendData("R\0"u8);
		SkillPackageStreams.AppendHashes(hash, directory);
		foreach (var folder in SafeSubdirectories(directory, skipGitMetadata).Order(StringComparer.Ordinal))
		{
			hash.AppendData(Encoding.UTF8.GetBytes("D" + Path.GetRelativePath(directory, folder) + "\0"));
			SkillPackageStreams.AppendHashes(hash, folder);
		}
		return Convert.ToHexString(hash.GetHashAndReset());
	}

	private static IEnumerable<string> SafeSubdirectories(string directory, bool skipGitMetadata = false)
	{
		var pending = new Stack<string>();
		pending.Push(directory);
		while (pending.TryPop(out var parent))
		{
			foreach (var child in Directory.EnumerateDirectories(parent))
			{
				if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException($"Linked package path: {child}");
				}
				if (skipGitMetadata && Path.GetFileName(child).Equals(".git", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				pending.Push(child);
				yield return child;
			}
		}
	}

	private void CommitMaintenance(SkillMaintenancePreview preview)
	{
		var state = preview.Revision!;
		var source = preview.SourceDirectory;
		var backup = Path.Combine(Path.GetDirectoryName(preview.Connection.DiscoveryRoot!)!, ".pact-skill-" + Guid.NewGuid().ToString("N"));
		var shared = preview.SharedDirectory;
		var migration = MaintenanceReceipt(source, migration: true);
		var activation = MaintenanceReceipt(source, migration: false);
		var receipts = new List<(string Path, string Backup)>();
		var settings = new Stack<(SkillConfigEdit Edit, byte[] Original)>();
		var moved = false;
		var sharedCreated = false;
		var renamedToShared = false;
		var linkCreated = false;
		try
		{
			Directory.Move(source, backup);
			moved = true;
			var movedLink = new DirectoryInfo(backup).LinkTarget;
			if (movedLink != state.Link || movedLink is null && PackageRevision(backup) != state.Package)
			{
				throw new IOException("The selected package changed during confirmation.");
			}
			if (shared is not null)
			{
				if (state.SharedPackage is null)
				{
					Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
					try
					{
						Directory.Move(backup, shared);
						renamedToShared = true;
					}
					catch (IOException ex) when ((ex.HResult & 0xFFFF) == 17 || !Same(Path.GetPathRoot(backup)!, Path.GetPathRoot(shared)!))
					{
						PublishShared(backup, shared, state.Package!);
					}
					sharedCreated = true;
				}
				if (new DirectoryInfo(shared).LinkTarget is not null || PackageRevision(shared) != state.Package)
				{
					throw new IOException("The shared package changed during confirmation.");
				}
				SkillDirectoryConnection.ApplyAsync(new(source, shared, migration, true, PreviousReceipt: state.MigrationReceipt),
					CancellationToken.None).GetAwaiter().GetResult();
				linkCreated = true;
				StageReceipt(activation, state.ActivationReceipt);
			}
			else
			{
				foreach (var (path, expected) in new[] { (activation, state.ActivationReceipt), (migration, state.MigrationReceipt) })
				{
					StageReceipt(path, expected);
				}
				foreach (var edit in preview.Edits)
				{
					if (ReadReceipt(edit.Path) != edit.Original)
					{ throw new IOException($"The settings changed at {edit.Path}."); }
					var original = File.ReadAllBytes(edit.Path);
					SkillActivationService.WriteSettingsAsync(edit.Path, edit.Updated!, original, CancellationToken.None).GetAwaiter().GetResult();
					settings.Push((edit, original));
				}
				if (state.Binding is not null)
				{
					var registry = LoadRegistry();
					var key = BindingKey(registry, Path.GetFileName(source));
					if (registry[key]?.ToJsonString() != state.Binding)
					{ throw new IOException("The update binding changed."); }
					registry.Remove(key);
					AtomicFileWriter.WriteTextAsync(_registry, registry.ToJsonString(), CancellationToken.None).GetAwaiter().GetResult();
				}
			}
		}
		catch (Exception error)
		{
			var failures = new List<Exception>();
			while (settings.TryPop(out var setting))
			{
				Restore(() =>
				{
					if (ReadReceipt(setting.Edit.Path) != setting.Edit.Updated)
					{ throw new IOException($"The settings changed during rollback at {setting.Edit.Path}."); }
					AtomicFileWriter.WriteBytesAsync(setting.Edit.Path, setting.Original, CancellationToken.None).GetAwaiter().GetResult();
				});
			}
			if (linkCreated)
			{
				Restore(() => SkillDirectoryConnection.ApplyAsync(new(source, shared!, migration, false, PreviousReceipt: state.MigrationReceipt),
					CancellationToken.None).GetAwaiter().GetResult());
			}
			if (moved)
			{ Restore(() => Directory.Move(renamedToShared ? shared! : backup, source)); }
			foreach (var receipt in receipts)
			{ Restore(() => File.Move(receipt.Backup, receipt.Path)); }
			if (sharedCreated && !renamedToShared)
			{
				Restore(() =>
				{
					if (new DirectoryInfo(shared!).LinkTarget is null && PackageRevision(shared!) == state.Package)
					{
						Directory.Delete(shared!, recursive: true);
					}
				});
			}
			if (failures.Count != 0)
			{ throw new AggregateException($"Skill maintenance failed and rollback needs attention. Package staging path: {backup}", [error, .. failures]); }
			throw;

			void Restore(Action action)
			{
				try
				{
					action();
				}
				catch (Exception rollback)
				{
					failures.Add(rollback);
				}
			}
		}

		// Removing a reparse point is always non-recursive, even when its target is on another volume.
		var leftovers = new List<string>();
		if (!renamedToShared)
		{
			try
			{
				if (new DirectoryInfo(backup).LinkTarget != state.Link || !preview.IsLink && PackageRevision(backup) != state.Package)
				{
					leftovers.Add(backup);
				}
				else
				{
					Directory.Delete(backup, recursive: !preview.IsLink);
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add(backup); }
		}
		foreach (var receipt in receipts)
		{
			try
			{ File.Delete(receipt.Backup); }
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { leftovers.Add(receipt.Backup); }
		}
		if (leftovers.Count != 0)
		{
			preview.CleanupWarning = "The requested change was applied. Cleanup is pending at " + string.Join(", ", leftovers);
		}

		void StageReceipt(string path, string? expected)
		{
			if (ReadReceipt(path) != expected)
			{ throw new IOException($"The receipt changed at {path}."); }
			if (expected is null)
			{ return; }
			var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
			File.Move(path, temp);
			receipts.Add((path, temp));
		}
	}

	private static void PublishShared(string backup, string shared, string revision)
	{
		var root = Path.GetDirectoryName(shared)!;
		Directory.CreateDirectory(root);
		var stage = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(root)!, ".pact-skill-" + Guid.NewGuid().ToString("N"))).FullName;
		try
		{
			SkillPackageStreams.CopyDirectoryStreams(backup, stage);
			foreach (var folder in SafeSubdirectories(backup))
			{
				var copied = Directory.CreateDirectory(Path.Combine(stage, Path.GetRelativePath(backup, folder))).FullName;
				SkillPackageStreams.CopyDirectoryStreams(folder, copied);
			}
			foreach (var file in Directory.EnumerateFiles(backup, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
			{
				File.Copy(file, Path.Combine(stage, Path.GetRelativePath(backup, file)));
			}
			if (PackageRevision(stage) != revision)
			{ throw new IOException("The package changed while staging the shared installation."); }
			Directory.Move(stage, shared);
		}
		finally
		{
			if (Directory.Exists(stage))
			{ Directory.Delete(stage, recursive: true); }
		}
	}
}
