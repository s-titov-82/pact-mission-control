using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class NativeSkillCatalogTests
{
	[TestCase(false)]
	[TestCase(true)]
	public async Task DiscoverReportsAStagedPackageWithoutOfferingOrRemovingIt(bool outsideDiscoveryRoot)
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var stagingParent = outsideDiscoveryRoot ? Path.GetDirectoryName(shared)! : shared;
		var stage = Directory.CreateDirectory(Path.Combine(stagingParent, ".pact-skill-interrupted")).FullName;
		var manifest = Path.Combine(stage, "SKILL.md");
		File.WriteAllText(manifest, "staged contents");
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);

		var snapshot = await catalog.DiscoverInstalledAsync([], new SkillActivationService(paths, shared));

		snapshot.Installations.ShouldBeEmpty();
		snapshot.Warnings.ShouldContain(warning => warning.Contains(stage, StringComparison.Ordinal));
		File.ReadAllText(manifest).ShouldBe("staged contents");
	}

	[Test]
	public async Task SkillConnectionPublishesWindowsJunctionBeyondTheLegacyPathLimit()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("This test verifies Windows junctions.");
		}
		using var temp = TemporaryDirectory.Create();
		var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target-folder")).FullName;
		File.WriteAllText(Path.Combine(target, "SKILL.md"), "contents");
		var deep = Directory.CreateDirectory(Path.Combine(temp.Path, new string('a', 100), new string('b', 100), "skills")).FullName;
		var alias = Path.Combine(deep, "alias");
		var receipt = Path.Combine(temp.Path, "receipt.txt");
		var link = new SkillLinkEdit(alias, target, receipt, true);
		var token = TestContext.CurrentContext.CancellationToken;

		await SkillDirectoryConnection.ApplyAsync(link, token);

		var unnormalizedAlias = Path.Combine(deep, "..", "skills", "alias")
			.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		SkillRenameLinkKind.IsSymbolic(unnormalizedAlias).ShouldBeFalse();
		File.ReadAllText(Path.Combine(alias, "SKILL.md")).ShouldBe("contents");
		File.ReadAllText(receipt).ShouldBe(SkillDirectoryConnection.FormatReceipt(target, alias));
		Directory.EnumerateFileSystemEntries(deep).ShouldBe([alias]);
		await SkillDirectoryConnection.ApplyAsync(link with { Create = false }, token);
		Directory.EnumerateFileSystemEntries(deep).ShouldBeEmpty();
	}

	[Test]
	public async Task Skill_junction_without_a_readable_manifest_is_not_published()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("This test verifies Windows junctions.");
		}
		using var temp = TemporaryDirectory.Create();
		var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName;
		var alias = Path.Combine(temp.Path, "alias");
		var link = new SkillLinkEdit(alias, target, Path.Combine(temp.Path, "receipt.txt"), true);

		var failure = await Should.ThrowAsync<IOException>(() => SkillDirectoryConnection.CreateLinkAsync(link));

		failure.Message.ShouldContain("Could not read SKILL.md through the staged skill junction");
		failure.Message.ShouldContain(target);
		Directory.EnumerateFileSystemEntries(temp.Path).ShouldBe([target]);
	}

	[Test]
	public async Task Failed_skill_junction_removal_restores_the_old_link_when_the_manifest_has_gone()
	{
		if (!OperatingSystem.IsWindows())
		{
			Assert.Ignore("This test verifies Windows junctions and file sharing.");
		}
		using var temp = TemporaryDirectory.Create();
		var target = Directory.CreateDirectory(Path.Combine(temp.Path, "target")).FullName;
		var manifest = Path.Combine(target, "SKILL.md");
		File.WriteAllText(manifest, "contents");
		var alias = Path.Combine(temp.Path, "alias");
		var receipt = Path.Combine(temp.Path, "receipt.txt");
		var link = new SkillLinkEdit(alias, target, receipt, true);
		var token = TestContext.CurrentContext.CancellationToken;
		await SkillDirectoryConnection.ApplyAsync(link, token);
		File.Delete(manifest);

		using var lockedReceipt = File.OpenRead(receipt);
		await Should.ThrowAsync<IOException>(() => SkillDirectoryConnection.ApplyAsync(link with { Create = false }, token));

		new DirectoryInfo(alias).LinkTarget.ShouldNotBeNull();
		File.ReadAllText(receipt).ShouldBe(SkillDirectoryConnection.FormatReceipt(target, alias));
	}

	[Test]
	public async Task Native_discovery_groups_junctions_across_default_and_custom_homes_but_keeps_distinct_copies()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var custom = Path.Combine(temp.Path, "team-claude");
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "repository", "skills", "same")).FullName;
		var manifest = Path.Combine(source, "SKILL.md");
		const string content = "---\nname: same\n---\ncontents";
		File.WriteAllText(manifest, content);
		var codex = Path.Combine(temp.Path, ".codex", "skills", "codex-alias");
		var claude = Path.Combine(custom, "skills", "claude-alias");
		var token = TestContext.CurrentContext.CancellationToken;
		await SkillDirectoryConnection.ApplyAsync(new(codex, source, Path.Combine(temp.Path, "first.txt"), true), token);
		await SkillDirectoryConnection.ApplyAsync(new(claude, source, Path.Combine(temp.Path, "second.txt"), true), token);
		var independent = Directory.CreateDirectory(Path.Combine(temp.Path, ".claude", "skills", "same")).FullName;
		File.WriteAllText(Path.Combine(independent, "SKILL.md"), content);
		var service = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		AgentProfileRecord profile = new("team", AgentKind.Claude, "Team Claude", "claude", null, "pwsh") { NativeConfigDirectory = custom };
		var found = await service.DiscoverInstalledAsync([profile], new SkillActivationService(paths), token);
		var packages = found.Installations.Where(item => item.Skill.Name == "same").ToArray();
		packages.Length.ShouldBe(2);
		var linked = packages.Single(item => item.ResolvedManifest == manifest);
		linked.Connections.Select(connection => connection.ManifestPath).ShouldBe(
			new[] { Path.Combine(codex, "SKILL.md"), Path.Combine(claude, "SKILL.md") }, ignoreOrder: true);
		linked.ManifestFor(profile).ShouldBe(Path.Combine(claude, "SKILL.md"));
		linked.Connections.SelectMany(connection => connection.Profiles).ShouldBe([profile]);
		var copy = packages.Single(item => item.ResolvedManifest != manifest);
		copy.Connections.Single().Agents.ShouldBe([AgentKind.Claude]);
		copy.Connections.Single().Profiles.ShouldBeEmpty();
	}
}
