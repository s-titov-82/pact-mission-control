using System.Text;
using Pact.Core.Agents;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillCatalogAnalysisTests
{
	[Test]
	public async Task Launch_discovery_classifies_new_repository_member_with_an_unreadable_asset_without_inspecting_its_contents()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var repo = Directory.CreateDirectory(Path.Combine(temp.Path, "team")).FullName;
		Directory.CreateDirectory(Path.Combine(repo, ".git"));
		var package = Directory.CreateDirectory(Path.Combine(repo, "skills", "new-package")).FullName;
		File.WriteAllText(Path.Combine(package, "SKILL.md"), "---\nname: manifest-name\n---\nbody");
		var home = Path.Combine(temp.Path, "native");
		var alias = Path.Combine(home, "skills", "renamed");
		await SkillDirectoryConnection.ApplyAsync(new(alias, package, Path.Combine(temp.Path, "receipt.txt"), true), TestContext.CurrentContext.CancellationToken);
		using var locked = File.Open(Path.Combine(package, "script.ps1"), FileMode.Create, FileAccess.Write, FileShare.None);
		var catalog = new SkillCatalogService(paths.SettingsDirectory, Path.Combine(temp.Path, "shared"), temp.Path);
		AgentProfileRecord profile = new("team", AgentKind.Claude, "Team", "claude", null, "pwsh") { NativeConfigDirectory = home };
		var member = catalog.DiscoverPolicyMembers(profile, new SkillActivationService(paths), null).Single();
		member.SkillName.ShouldBe("manifest-name");
		member.SkillFile.ShouldBe(Path.Combine(alias, "SKILL.md"));
		member.Source.Kind.ShouldBe(SkillCatalogSourceKind.External);
		member.Source.Location.ShouldBe(repo);
		member.Profile.ShouldBe(profile);
	}

	[Test]
	public void Source_classification_distinguishes_system_shared_native_repository_and_bound_sources()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var codex = Path.Combine(temp.Path, ".codex", "skills");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var repository = Directory.CreateDirectory(Path.Combine(temp.Path, "team", ".git")).Parent!.FullName;
		var external = Package(codex, "name", AgentKind.Codex, physical: Path.Combine(repository, "skills", "name"));
		var system = Package(codex, ".system/name", AgentKind.Codex);
		var native = Package(codex, "name", AgentKind.Codex);
		var common = Package(shared, "name", AgentKind.Codex, shared: true);
		service.ClassifySource(external.Skill, external).Location.ShouldBe(repository);
		service.ClassifySource(external.Skill, external).Kind.ShouldBe(SkillCatalogSourceKind.External);
		service.ClassifySource(system.Skill, system).Kind.ShouldBe(SkillCatalogSourceKind.System);
		service.ClassifySource(native.Skill, native).Kind.ShouldBe(SkillCatalogSourceKind.Native);
		service.ClassifySource(common.Skill, common).Kind.ShouldBe(SkillCatalogSourceKind.Shared);
		var binding = new SkillSourceBinding("https://github.com/team/repo", "skills/name", "main", "abc");
		service.ClassifySource(common.Skill, common, binding).Location.ShouldBe(binding.Source);
		service.ClassifySource(common.Skill, common, binding).Kind.ShouldBe(SkillCatalogSourceKind.Bound);
		service.ClassifySource(external.Skill).Kind.ShouldBe(SkillCatalogSourceKind.Registered);
	}

	[Test]
	public void Identical_copies_prefer_existing_shared_package_and_keep_claude_discovery_path()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, ".agents", "skills");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var common = Package(shared, "name", AgentKind.Codex, shared: true);
		var claude = Package(Path.Combine(temp.Path, ".claude", "skills"), "renamed", AgentKind.Claude);
		var group = service.AnalyzeDuplicates([common, claude], new Dictionary<string, SkillSourceBinding>()).Single();
		group.Kind.ShouldBe(SkillDuplicateKind.Identical);
		group.DifferingFiles.ShouldBeEmpty();
		var advice = string.Join("\n", group.Recommendations);
		advice.ShouldContain(Path.GetDirectoryName(common.ResolvedManifest)!);
		advice.ShouldContain(Path.GetDirectoryName(claude.ResolvedManifest)!);
		advice.ShouldContain("Claude");
		advice.ShouldContain("link", Case.Insensitive);
	}

	[Test]
	public void Same_manifest_with_different_scripts_and_extra_assets_is_a_conflict_and_partial_packages_stay_unverified()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var first = Package(shared, "first", AgentKind.Codex, shared: true);
		var other = Package(Path.Combine(temp.Path, "other"), "second", AgentKind.Claude, script: "changed", extra: true);
		var bindings = new Dictionary<string, SkillSourceBinding>();
		var different = service.AnalyzeDuplicates([first, other], bindings).Single();
		different.Kind.ShouldBe(SkillDuplicateKind.Different);
		different.DifferingFiles.ShouldBe(["asset.bin", "scripts/run.ps1"]);
		different.Recommendations.Any(text => text.StartsWith("Keep", StringComparison.Ordinal)).ShouldBeTrue();
		var partial = Package(shared, "third", AgentKind.Codex, warning: "Unreadable script.");
		service.AnalyzeDuplicates([first, partial], bindings).Single().Kind.ShouldBe(SkillDuplicateKind.Incomplete);
	}

	[Test]
	public void DuplicateAnalysisUsesFreshFileHashesForBothTheVerdictAndTheDifferingPaths()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var first = Package(shared, "first", AgentKind.Codex, shared: true);
		var other = Package(Path.Combine(temp.Path, "other"), "second", AgentKind.Claude);
		var script = Path.Combine(Path.GetDirectoryName(other.ResolvedManifest)!, "scripts", "run.ps1");
		File.WriteAllText(script, "edited after discovery");

		var group = service.AnalyzeDuplicates([first, other], new Dictionary<string, SkillSourceBinding>()).Single();

		group.Kind.ShouldBe(SkillDuplicateKind.Different);
		group.DifferingFiles.ShouldBe(["scripts/run.ps1"]);
	}

	[Test]
	public void DuplicateAnalysisIgnoresGitMetadataForAWorkingCopy()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var first = Package(shared, "first", AgentKind.Codex, shared: true);
		var checkout = Package(Path.Combine(temp.Path, "team"), "second", AgentKind.Claude);
		var git = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(checkout.ResolvedManifest)!, ".git"));
		File.WriteAllText(Path.Combine(git.FullName, "config"), "checkout metadata");

		var group = service.AnalyzeDuplicates([first, checkout], new Dictionary<string, SkillSourceBinding>()).Single();

		group.Kind.ShouldBe(SkillDuplicateKind.Identical);
		group.DifferingFiles.ShouldBeEmpty();
	}

	[Test]
	public void Directory_aliases_already_share_one_package_and_have_no_consolidation_action()
	{
		using var temp = TemporaryDirectory.Create();
		var shared = Path.Combine(temp.Path, "shared");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), shared, temp.Path);
		var first = Package(shared, "name", AgentKind.Codex, shared: true);
		var second = Package(Path.Combine(temp.Path, "claude"), "name", AgentKind.Claude, physical: Path.GetDirectoryName(first.ResolvedManifest));
		var connected = first with { Connections = [.. first.Connections, .. second.Connections] };
		var bindings = new Dictionary<string, SkillSourceBinding>();
		service.AnalyzeDuplicates([connected], bindings).ShouldBeEmpty();
		var independent = Package(Path.Combine(temp.Path, "codex"), "copy", AgentKind.Codex);
		var group = service.AnalyzeDuplicates([connected, independent], bindings).Single();
		group.Kind.ShouldBe(SkillDuplicateKind.Identical);
		group.Installations.Count.ShouldBe(2);
	}

	private static NativeSkillInstallation Package(string root, string relative, AgentKind agent, bool shared = false,
		string? physical = null, string script = "echo ok", bool extra = false, string? warning = null)
	{
		var folder = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
		var manifest = Path.Combine(folder, "SKILL.md");
		var content = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
		{
			["SKILL.md"] = Encoding.UTF8.GetBytes("---\nname: same\n---\nbody"),
			["scripts/run.ps1"] = Encoding.UTF8.GetBytes(script)
		};
		if (extra)
		{
			content["asset.bin"] = [0, 255];
		}
		foreach (var (path, bytes) in content)
		{
			var file = Path.Combine(physical ?? folder, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllBytes(file, bytes);
		}
		var package = new CatalogSkill(Path.GetFileName(folder), root, relative, null, null, null, content, manifest, warning);
		return new NativeSkillInstallation(package, Path.Combine(physical ?? folder, "SKILL.md"),
			[new NativeSkillConnection(manifest, [agent], [], shared) { DiscoveryRoot = root }]);
	}
}
