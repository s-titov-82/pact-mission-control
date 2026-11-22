using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillMaintenanceTests
{
	[Test]
	public async Task DuplicateCleanupRequiresAnotherMatchingCopyAtConfirmation()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var keep = CreatePackage(shared, "sample", "identical");
		var remove = CreatePackage(Path.Combine(temp.Path, ".claude", "skills"), "sample", "identical");
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var snapshot = await catalog.DiscoverInstalledAsync([], new SkillActivationService(paths, shared));
		var group = catalog.AnalyzeDuplicates(snapshot.Installations, catalog.GetBindings()).Single();
		var selected = group.Installations.Single(item => Path.GetDirectoryName(item.ResolvedManifest) == remove);
		var preview = catalog.PreviewDelete(selected, selected.Connections.Single(), group);
		preview.Conflict.ShouldBeNull();
		File.WriteAllText(Path.Combine(keep, "SKILL.md"), "changed since preview");
		Should.Throw<IOException>(() => catalog.Apply(preview));
		File.ReadAllText(Path.Combine(remove, "SKILL.md")).ShouldBe("identical");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Confirmed_migration_preserves_bytes_and_replaces_the_selected_native_folder_with_a_link(bool existingShared)
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var native = CreatePackage(Path.Combine(temp.Path, ".claude", "skills"), "example", "version");
		var destination = Path.Combine(shared, "example");
		if (existingShared)
		{ CreatePackage(shared, "example", "version"); }
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var item = await Find(service, paths, native);
		var preview = service.PreviewMakeShared(item, item.Connections.Single());
		preview.Conflict.ShouldBeNull();
		new DirectoryInfo(native).LinkTarget.ShouldBeNull();
		Directory.Exists(destination).ShouldBe(existingShared);
		service.Apply(preview);
		SkillCatalogService.ResolveDirectory(native).ShouldBe(destination);
		File.ReadAllText(Path.Combine(destination, "SKILL.md")).ShouldBe("version");
		Directory.Exists(Path.Combine(destination, "empty")).ShouldBeTrue();
		var linked = await Find(service, paths, native);
		var link = linked.Connections.Single(c => Path.GetDirectoryName(c.ManifestPath) == native);
		var remove = service.PreviewDelete(linked, link);
		remove.IsLink.ShouldBeTrue();
		service.Apply(remove);
		Directory.Exists(native).ShouldBeFalse();
		File.ReadAllText(Path.Combine(destination, "SKILL.md")).ShouldBe("version");
		Directory.EnumerateFiles(Path.Combine(paths.SettingsDirectory, "Skills", "MigratedConnections")).ShouldBeEmpty();
	}

	[Test]
	public async Task Divergent_shared_package_and_changed_source_block_maintenance_without_overwriting_bytes()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var native = CreatePackage(Path.Combine(temp.Path, ".codex", "skills"), "example", "v1");
		var destination = CreatePackage(shared, "example", "other");
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var item = await Find(service, paths, native);
		var conflict = service.PreviewMakeShared(item, item.Connections.Single());
		conflict.Conflict.ShouldNotBeNull();
		Should.Throw<IOException>(() => service.Apply(conflict));
		var remove = service.PreviewDelete(item, item.Connections.Single());
		File.WriteAllText(Path.Combine(native, "script.ps1"), "edit");
		Should.Throw<IOException>(() => service.Apply(remove));
		File.ReadAllText(Path.Combine(native, "script.ps1")).ShouldBe("edit");
		File.ReadAllText(Path.Combine(destination, "SKILL.md")).ShouldBe("other");
	}

	[Test]
	public async Task System_package_and_physical_target_with_a_project_receipt_cannot_be_removed()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var system = CreatePackage(Path.Combine(temp.Path, ".codex", "skills", ".system"), "builtin", "system");
		var native = CreatePackage(shared, "important", "owned");
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var builtin = await Find(service, paths, system);
		service.PreviewDelete(builtin, builtin.Connections.Single()).Conflict.ShouldNotBeNull();
		var item = await Find(service, paths, native);
		var before = service.PreviewDelete(item, item.Connections.Single());
		before.Conflict.ShouldBeNull();
		var receipt = Path.Combine(paths.SettingsDirectory, "Skills", "Connections", "project.txt");
		Directory.CreateDirectory(Path.GetDirectoryName(receipt)!);
		File.WriteAllText(receipt, native);
		Should.Throw<IOException>(() => service.Apply(before));
		File.ReadAllText(Path.Combine(native, "SKILL.md")).ShouldBe("owned");
	}

	[Test]
	public async Task External_link_removal_preserves_target_and_rejects_retarget_after_preview()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var external = CreatePackage(Path.Combine(temp.Path, "external"), "one", "outside");
		var other = CreatePackage(Path.Combine(temp.Path, "external"), "two", "new target");
		var native = Path.Combine(temp.Path, ".claude", "skills", "alias");
		var token = TestContext.CurrentContext.CancellationToken;
		await SkillDirectoryConnection.ApplyAsync(new(native, external, Path.Combine(temp.Path, "first.txt"), true), token);
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var item = await Find(service, paths, native);
		service.CanOfferMaintenance(item, item.Connections.Single(), makeShared: true).ShouldBeFalse();
		var removal = service.PreviewDelete(item, item.Connections.Single());
		removal.Conflict.ShouldBeNull();
		Directory.Delete(native);
		await SkillDirectoryConnection.ApplyAsync(new(native, other, Path.Combine(temp.Path, "second.txt"), true), token);
		Should.Throw<IOException>(() => service.Apply(removal));
		var refreshed = await Find(service, paths, native);
		service.Apply(service.PreviewDelete(refreshed, refreshed.Connections.Single()));
		Directory.Exists(native).ShouldBeFalse();
		File.ReadAllText(Path.Combine(external, "SKILL.md")).ShouldBe("outside");
		File.ReadAllText(Path.Combine(other, "SKILL.md")).ShouldBe("new target");
	}

	[Test]
	public async Task Bound_shared_delete_restores_the_package_after_a_failed_metadata_write_and_removes_only_its_binding_on_success()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var source = CreatePackage(Path.Combine(temp.Path, "team"), "bound", "source");
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		service.Apply(service.Preview((await service.BrowseAsync(source)).Single()));
		var installed = Path.Combine(shared, "bound");
		var item = await Find(service, paths, installed);
		var remove = service.PreviewDelete(item, item.Connections.Single());
		remove.Conflict.ShouldBeNull();
		using (File.Open(Path.Combine(paths.SettingsDirectory, "skill-installations.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			Should.Throw<IOException>(() => service.Apply(remove));
			File.ReadAllText(Path.Combine(installed, "SKILL.md")).ShouldBe("source");
			service.GetManagedNames().ShouldContain("bound");
		}
		service.Apply(remove);
		Directory.Exists(installed).ShouldBeFalse();
		service.GetManagedNames().ShouldBeEmpty();
		File.ReadAllText(Path.Combine(source, "SKILL.md")).ShouldBe("source");
	}

	[Test]
	public async Task Named_stream_changes_block_reuse_of_an_otherwise_identical_shared_package()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var native = CreatePackage(Path.Combine(temp.Path, ".claude", "skills"), "example", "same");
		var destination = CreatePackage(shared, "example", "same");
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var item = await Find(service, paths, native);
		var migration = service.PreviewMakeShared(item, item.Connections.Single());
		migration.Conflict.ShouldBeNull();
		File.WriteAllText(Path.Combine(native, "SKILL.md") + ":proof", "unique stream");
		Should.Throw<IOException>(() => service.Apply(migration));
		new DirectoryInfo(native).LinkTarget.ShouldBeNull();
		File.ReadAllText(Path.Combine(native, "SKILL.md") + ":proof").ShouldBe("unique stream");
		File.Exists(Path.Combine(destination, "SKILL.md") + ":proof").ShouldBeFalse();
	}

	private static async Task<NativeSkillInstallation> Find(SkillCatalogService service, AppPaths paths, string path)
	{
		var found = await service.DiscoverInstalledAsync([], new SkillActivationService(paths), TestContext.CurrentContext.CancellationToken);
		return found.Installations.Single(item => item.Connections.Any(c => Path.GetDirectoryName(c.ManifestPath) == path));
	}

	private static string CreatePackage(string root, string name, string text)
	{
		var path = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
		Directory.CreateDirectory(Path.Combine(path, "empty"));
		File.WriteAllText(Path.Combine(path, "SKILL.md"), text);
		return path;
	}
}