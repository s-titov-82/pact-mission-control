using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillRenameTests
{
	[Test]
	public async Task Rename_moves_bound_package_retargets_native_and_project_links_and_preserves_update_source()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var native = Path.Combine(temp.Path, ".codex", "skills", "finish-task");
		var project = Path.Combine(temp.Path, "project");
		var local = Path.Combine(project, ".claude", "skills", "my-alias");
		var team = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "finish-task")).FullName;
		var content = "---\nname: finish-task\n---\nbody";
		File.WriteAllText(Path.Combine(team, "SKILL.md"), content);
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var from = Path.Combine(shared, "finish-task");
		var to = Path.Combine(shared, "finish-work");
		catalog.Apply(catalog.Preview((await catalog.BrowseAsync(team)).Single()));
		var token = TestContext.CurrentContext.CancellationToken;
		var receiptDirectory = Path.Combine(paths.SettingsDirectory, "Skills", "Connections");
		foreach (var alias in new[] { native, local })
		{
			var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(alias.ToUpperInvariant())));
			await SkillDirectoryConnection.ApplyAsync(new(alias, from, Path.Combine(receiptDirectory, key + ".txt"), true), token);
		}
		var info = (await catalog.DiscoverInstalledAsync([], new SkillActivationService(paths), token)).Installations.Single(i => i.ResolvedManifest == Path.Combine(from, "SKILL.md"));
		var preview = catalog.PreviewRename(info, info.Connections.Single(c => c.IsSharedUserDirectory), "finish-work", [], [project]);
		preview.Conflict.ShouldBeNull();
		preview.Changes.Any(line => line.Contains(local, StringComparison.Ordinal)).ShouldBeTrue();
		Directory.Exists(to).ShouldBeFalse();
		catalog.Apply(preview);
		Directory.Exists(from).ShouldBeFalse();
		File.ReadAllText(Path.Combine(to, "SKILL.md")).ShouldBe(content);
		SkillCatalogService.ResolveDirectory(native).ShouldBe(to);
		SkillCatalogService.ResolveDirectory(local).ShouldBe(to);
		Directory.EnumerateFiles(receiptDirectory).Select(File.ReadAllText).Distinct().ShouldBe([to]);
		catalog.GetBindings().Keys.ShouldBe(["finish-work"]);
		(await catalog.CheckUpdateAsync("finish-work")).Destination.ShouldBe(to);
	}

	[Test]
	public async Task Case_only_rename_preserves_the_relative_target_of_a_native_symbolic_link()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var source = Directory.CreateDirectory(Path.Combine(shared, "before")).FullName;
		File.WriteAllText(Path.Combine(source, "SKILL.md"), "body");
		var native = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills")).FullName;
		var alias = Path.Combine(native, "stable-command");
		try
		{
			Directory.CreateSymbolicLink(alias, Path.GetRelativePath(native, source));
		}
		catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) == 1314)
		{
			Assert.Ignore("Directory symbolic links require Windows developer mode or link privileges.");
		}
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		var info = (await catalog.DiscoverInstalledAsync([], new SkillActivationService(paths))).Installations.Single();
		var rename = catalog.PreviewRename(info, info.Connections.Single(c => c.IsSharedUserDirectory), "Before");
		rename.Conflict.ShouldBeNull();
		catalog.Apply(rename);
		Directory.GetDirectories(shared).Select(Path.GetFileName).ShouldBe(["Before"]);
		new DirectoryInfo(alias).LinkTarget.ShouldBe(Path.GetRelativePath(native, Path.Combine(shared, "Before")));
		File.ReadAllText(Path.Combine(alias, "SKILL.md")).ShouldBe("body");
	}

	[Test]
	public async Task Rename_rejects_unsafe_names_collisions_and_edits_after_preview()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var root = Path.Combine(temp.Path, ".agents", "skills");
		var from = Directory.CreateDirectory(Path.Combine(root, "old")).FullName;
		File.WriteAllText(Path.Combine(from, "SKILL.md"), "first");
		Directory.CreateDirectory(Path.Combine(root, "taken"));
		var svc = new SkillCatalogService(paths.SettingsDirectory, root, temp.Path);
		var info = (await svc.DiscoverInstalledAsync([], new SkillActivationService(paths))).Installations.Single();
		foreach (var bad in new[] { "../escape", "CON.txt", "taken", "old", ".system", "a/b" })
		{
			svc.PreviewRename(info, info.Connections.Single(), bad).Conflict.ShouldNotBeNull();
		}
		var preview = svc.PreviewRename(info, info.Connections.Single(), "new");
		preview.Conflict.ShouldBeNull();
		File.WriteAllText(Path.Combine(from, "SKILL.md"), "edited");
		Should.Throw<IOException>(() => svc.Apply(preview));
		File.ReadAllText(Path.Combine(from, "SKILL.md")).ShouldBe("edited");
		Directory.Exists(Path.Combine(root, "new")).ShouldBeFalse();
	}

	[Test]
	public async Task Rename_rollback_restores_physical_directory_original_link_and_binding_on_write_failure()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var team = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "before")).FullName;
		File.WriteAllText(Path.Combine(team, "SKILL.md"), "unchanged");
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		catalog.Apply(catalog.Preview((await catalog.BrowseAsync(team)).Single()));
		var before = Path.Combine(shared, "before");
		var alias = Path.Combine(temp.Path, ".claude", "skills", "my-name");
		var receipt = Path.Combine(paths.SettingsDirectory, "Skills", "Connections",
			Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(alias.ToUpperInvariant()))) + ".txt");
		await SkillDirectoryConnection.ApplyAsync(new(alias, before, receipt, true), TestContext.CurrentContext.CancellationToken);
		var info = (await catalog.DiscoverInstalledAsync([], new SkillActivationService(paths))).Installations.Single(i => i.Skill.Name == "before");
		var change = catalog.PreviewRename(info, info.Connections.Single(c => c.IsSharedUserDirectory), "after");
		change.Conflict.ShouldBeNull();
		using var locked = File.Open(Path.Combine(paths.SettingsDirectory, "skill-installations.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
		Should.Throw<IOException>(() => catalog.Apply(change));
		File.ReadAllText(Path.Combine(before, "SKILL.md")).ShouldBe("unchanged");
		Directory.Exists(Path.Combine(shared, "after")).ShouldBeFalse();
		SkillCatalogService.ResolveDirectory(alias).ShouldBe(before);
		File.ReadAllText(receipt).ShouldBe(before);
		catalog.GetBindings().Keys.ShouldBe(["before"]);
	}
}