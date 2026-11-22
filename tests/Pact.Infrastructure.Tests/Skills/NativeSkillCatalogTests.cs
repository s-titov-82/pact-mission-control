using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class NativeSkillCatalogTests
{
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