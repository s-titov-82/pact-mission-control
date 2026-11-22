using System.Text;
using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillMatrixPolicyTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private SkillActivationService Service => new(new AppPaths(Path.Combine(_directory.Path, "pact")));
	private string Project => Path.Combine(_directory.Path, "project");
	public void Dispose() => _directory.Dispose();

	private AgentProfileRecord Profile(AgentKind kind, string id = "one") =>
		new(id, kind, id, kind.ToString().ToLowerInvariant(), null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(_directory.Path, id) };

	private static string Install(AgentProfileRecord profile, string name)
	{
		var folder = Directory.CreateDirectory(Path.Combine(profile.NativeConfigDirectory!, "skills", name));
		var file = Path.Combine(folder.FullName, "SKILL.md");
		File.WriteAllText(file, $"---\nname: {name}\ndescription: isolated test\n---\n");
		return file;
	}

	[Test]
	public async Task PrivateSourceCanOnlyConfigureExistingAliasesForAnotherProfile()
	{
		var owner = Profile(AgentKind.Claude, "owner");
		var other = Profile(AgentKind.Codex, "other");
		var profiles = new[] { owner, other };
		var first = Install(owner, "already-linked");
		var second = Install(owner, "private");
		var source = new SkillCatalogSource(SkillCatalogSourceKind.Native, "Owner", Path.Combine(owner.NativeConfigDirectory!, "skills"));
		var service = Service;
		var members = new SkillMatrixMember[] { new(source, "already-linked", first, other), new(source, "private", second, other) };
		service.CanConfigureMember(owner, source, second, null, profiles).ShouldBeTrue();
		service.CanConfigureMember(other, source, second, Project, profiles).ShouldBeFalse();
		Should.Throw<InvalidOperationException>(() => service.Preview(other, "private", second, null, SkillMode.Enabled, profiles))
			.Message.ShouldContain("Make shared");
		Should.Throw<InvalidOperationException>(() => service.PreviewBatch(
			[new(other, "private", second, Project, SkillMode.Enabled, source)], profiles)).Message.ShouldContain("Make shared");
		Should.Throw<InvalidOperationException>(() => service.PreviewBatch([], profiles, [new(other, source, null, SkillMode.Enabled)], members))
			.Message.ShouldContain("Make shared");

		var alias = Path.Combine(other.NativeConfigDirectory!, "skills", "deliberate-alias");
		await SkillDirectoryConnection.ApplyAsync(new(alias, Path.GetDirectoryName(first)!, Path.Combine(_directory.Path, "alias-receipt"), true), default);
		service.CanConfigureMember(other, source, first, null, profiles).ShouldBeTrue();
		var batch = service.PreviewBatch([], profiles, [new(other, source, null, SkillMode.Enabled)], members);
		batch.Changes.Count.ShouldBe(1);
		batch.Changes[0].CreatedConnectionPath.ShouldBeNull();
		await service.ApplyAsync(batch, default);
		Directory.Exists(Path.Combine(other.NativeConfigDirectory!, "skills", "private")).ShouldBeFalse();
		var later = Install(owner, "new-private");
		var launch = service.ComposeLaunch(other, Project, new LaunchInjection([], new Dictionary<string, string>()));
		launch.Arguments.ShouldBeEmpty();
		service.CanConfigureMember(other, source, later, Project, profiles).ShouldBeFalse();
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Codex)]
	public async Task LaunchAndMatrixResolveTheClosestMatchingFolderThenTheExactProject(AgentKind kind)
	{
		var profile = Profile(kind);
		var file = Install(profile, "example");
		var source = new SkillCatalogSource(SkillCatalogSourceKind.Native, "Native", Path.Combine(profile.NativeConfigDirectory!, "skills"));
		var service = new SkillActivationService(new AppPaths(Path.Combine(_directory.Path, "pact")), Path.Combine(_directory.Path, "shared"));
		var nested = Path.Combine(Project, "team");
		var repo = Path.Combine(nested, "repo");
		var sibling = Project + "-other";
		var edits = service.PreviewBatch([new(profile, "example", file, nested, SkillMode.Enabled, source, true)], [profile],
			[new(profile, source, Project, SkillMode.Disabled, true)], []);
		edits.AffectedFiles.Count.ShouldBe(2);
		await service.ApplyAsync(edits, default);
		service.ListFolderScopes().ShouldContain(nested);
		service.InspectMatrix(profile, source, "example", file, repo).OriginDirectory.ShouldBe(nested);
		service.InspectMatrix(profile, source, "example", file, repo).EffectiveEnabled.ShouldBeTrue();
		service.InspectMatrix(profile, source, "example", file, Path.Combine(Project, "team2")).OriginDirectory.ShouldBe(Project);
		service.InspectMatrix(profile, source, "example", file, sibling).OriginDirectory.ShouldBeNull();
		service.InspectMatrix(profile, source, "example", file, sibling).EffectiveEnabled.ShouldBeTrue();
		var later = Install(profile, "later");
		var launch = service.ComposeLaunch(profile, null, new LaunchInjection([], new Dictionary<string, string>()), workingDirectory: repo);
		if (kind == AgentKind.Codex)
		{
			var rules = CodexSkillRules.Read(launch.Arguments[^1]);
			CodexSkillRules.IsEnabled(rules, "example", file).ShouldBeTrue();
			CodexSkillRules.IsEnabled(rules, "later", later).ShouldBeFalse();
		}
		else
		{
			var overrides = JsonNode.Parse(launch.Arguments[^1])!["skillOverrides"]!;
			overrides["later"]!.GetValue<string>().ShouldBe("off");
			overrides["example"].ShouldBeNull();
		}
		File.Exists(service.Inspect(profile, "example", file, null).ConfigPath).ShouldBeFalse();
		Directory.Exists(Project).ShouldBeFalse();
		await service.ApplyAsync(service.PreviewBatch([new(profile, "example", file, repo, SkillMode.Disabled, source)], [profile]), default);
		var state = service.InspectMatrix(profile, source, "example", file, repo);
		state.EffectiveEnabled.ShouldBeFalse();
		state.OriginDirectory.ShouldBeNull();
	}

	[Test]
	public async Task FolderChoiceIsProfileSpecificAndResetRestoresItsParent()
	{
		var first = Profile(AgentKind.Codex);
		var second = first with { Id = "second" };
		var file = Install(first, "example");
		var source = new SkillCatalogSource(SkillCatalogSourceKind.Native, "Native", Path.Combine(first.NativeConfigDirectory!, "skills"));
		var service = Service;
		var nested = Path.Combine(Project, "nested");
		await service.ApplyAsync(service.PreviewBatch([], [first, second],
			[new(first, source, Project, SkillMode.Disabled, true), new(first, source, nested, SkillMode.Enabled, true)], []), default);
		service.InspectSource(second, source, Project, true).ShouldBe(SkillMode.Inherit);
		service.InspectMatrix(second, source, "example", file, nested).EffectiveEnabled.ShouldBeTrue();
		var clear = service.PreviewBatch([], [first, second], [new(first, source, nested, SkillMode.Inherit, true)], []);
		await service.ApplyAsync(clear, default);
		service.ListFolderScopes().ShouldBe([Project]);
		var state = service.InspectMatrix(first, source, "example", file, nested, true);
		state.SourceMode.ShouldBe(SkillMode.Inherit);
		state.OriginDirectory.ShouldBe(Project);
		state.EffectiveEnabled.ShouldBeFalse();
	}

	[Test]
	public async Task ReplacingAReviewedExternalPackageWithAJunctionIntoAnotherProfileInvalidatesApply()
	{
		var owner = Profile(AgentKind.Claude, "owner");
		var other = Profile(AgentKind.Claude, "other");
		var privateFile = Install(owner, "example");
		var source = Directory.CreateDirectory(Path.Combine(_directory.Path, "team", "external")).FullName;
		var manifest = Path.Combine(source, "SKILL.md");
		File.WriteAllText(manifest, "# External");
		var service = Service;
		var preview = service.Preview(other, "external", manifest, null, SkillMode.Enabled, [owner, other]);
		File.Delete(manifest);
		Directory.Delete(source);
		await SkillDirectoryConnection.ApplyAsync(new(source, Path.GetDirectoryName(privateFile)!, Path.Combine(_directory.Path, "replacement-receipt"), true), default);

		await Should.ThrowAsync<IOException>(() => service.ApplyAsync(preview, default));
		Directory.Exists(preview.CreatedConnectionPath!).ShouldBeFalse();
		File.Exists(preview.FilePath).ShouldBeFalse();
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Codex)]
	public async Task SourceDefaultsSurviveNewInstallationsAndChildOverridesSurviveSourceChanges(AgentKind kind)
	{
		var profile = Profile(kind);
		var first = Install(profile, "first");
		var second = Install(profile, "second");
		var source = new SkillCatalogSource(SkillCatalogSourceKind.Native, "Native", Path.Combine(profile.NativeConfigDirectory!, "skills"));
		var alias = source with { Kind = SkillCatalogSourceKind.Bound, Label = "Bound" };
		var service = Service;
		var members = new SkillMatrixMember[] { new(source, "first", first, profile), new(source, "second", second, profile) };
		var set = service.PreviewBatch([new(profile, "first", first, null, SkillMode.Enabled, source)], [profile],
			[new(profile, alias, null, SkillMode.Disabled)], members);
		service.InspectSource(profile, source, null).ShouldBe(SkillMode.Inherit);
		await service.ApplyAsync(set, default);
		service.InspectSource(profile, source, null).ShouldBe(SkillMode.Disabled);
		service.InspectMatrix(profile, source, "first", first, null).ExplicitMode.ShouldBe(SkillMode.Enabled);
		service.InspectMatrix(profile, source, "second", second, null).ExplicitMode.ShouldBe(SkillMode.Inherit);
		service.Inspect(profile, "second", second, null).EffectiveEnabled.ShouldBeFalse();
		var config = service.Inspect(profile, "first", first, null).ConfigPath;
		var originalBytes = File.ReadAllBytes(config);
		var later = Install(profile, "later");
		service.InspectMatrix(profile, source, "later", later, Project).EffectiveEnabled.ShouldBeFalse();
		var native = service.ComposeLaunch(profile, Project, new LaunchInjection(["original"], new Dictionary<string, string>()));
		native.Arguments[0].ShouldBe("original");
		if (kind == AgentKind.Claude)
		{
			JsonNode.Parse(native.Arguments[^1])!["skillOverrides"]!["later"]!.GetValue<string>().ShouldBe("off");
		}
		else
		{
			CodexSkillRules.IsEnabled(CodexSkillRules.Read(native.Arguments[^1]), "later", later).ShouldBeFalse();
		}
		File.ReadAllBytes(config).ShouldBe(originalBytes);
		await service.ApplyAsync(service.PreviewBatch([], [profile], [new(profile, source, null, SkillMode.Inherit)], members), default);
		service.Inspect(profile, "first", first, null).DefaultMode.ShouldBe(SkillMode.Enabled);
		service.Inspect(profile, "second", second, null).DefaultMode.ShouldBe(SkillMode.Inherit);
		service.Inspect(profile, "second", second, null).EffectiveEnabled.ShouldBeTrue();
	}

	[Test]
	public async Task SharedMigrationPreservesAnExplicitProjectChoiceUntilItIsReset()
	{
		var paths = new AppPaths(Path.Combine(_directory.Path, "pact"));
		var shared = Path.Combine(_directory.Path, ".agents", "skills");
		var profile = Profile(AgentKind.Claude);
		var file = Install(profile, "private-review");
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, _directory.Path);
		var service = new SkillActivationService(paths, shared);
		var snapshot = await catalog.DiscoverInstalledAsync([profile], service);
		var item = snapshot.Installations.Single(i => i.Skill.Name == "private-review");
		var source = catalog.ClassifySource(item.Skill, item);
		await service.ApplyAsync(service.PreviewBatch([new(profile, item.Skill.Name, file, Project, SkillMode.Disabled, source)],
			[profile], [], []), default);
		catalog.Apply(catalog.PreviewMakeShared(item, item.Connections.Single()));

		// Remove the CLI override independently. Only the saved Pact choice now identifies the exception.
		await service.ApplyAsync(service.Preview(profile, item.Skill.Name, file, Project, SkillMode.Inherit, [profile]), default);
		var moved = (await catalog.DiscoverInstalledAsync([profile], service)).Installations.Single(i => i.Skill.Name == item.Skill.Name);
		var common = catalog.ClassifySource(moved.Skill, moved);
		common.Kind.ShouldBe(SkillCatalogSourceKind.Shared);
		service.InspectMatrix(profile, common, item.Skill.Name, moved.ResolvedManifest, Project).ExplicitMode.ShouldBe(SkillMode.Disabled);
		await service.ApplyAsync(service.PreviewBatch([new(profile, item.Skill.Name, moved.ResolvedManifest, Project, SkillMode.Inherit, common)],
			[profile], [], []), default);
		service.InspectMatrix(profile, common, item.Skill.Name, file, Project).ExplicitMode.ShouldBe(SkillMode.Inherit);
		File.Exists(file).ShouldBeTrue();
	}

	[Test]
	public async Task SharedNativeFileCombinesCellsAndRejectsContradictoryOrStaleChangesBeforeWriting()
	{
		var profile = Profile(AgentKind.Claude);
		var shared = profile with { Id = "alias", DisplayName = "alias" };
		var first = Install(profile, "first");
		var second = Install(profile, "second");
		var service = Service;
		var batch = service.PreviewBatch([new(profile, "first", first, null, SkillMode.Disabled),
			new(shared, "second", second, null, SkillMode.Enabled)], [profile, shared]);
		batch.AffectedFiles.Count.ShouldBe(1);
		Should.Throw<InvalidOperationException>(() => service.PreviewBatch(
			[new(profile, "first", first, null, SkillMode.Disabled), new(shared, "first", first, null, SkillMode.Enabled)], [profile, shared]));
		File.WriteAllText(batch.AffectedFiles[0], """{"unrelated":42}""");
		await Should.ThrowAsync<InvalidOperationException>(() => service.ApplyAsync(batch, default));
		File.ReadAllText(batch.AffectedFiles[0]).ShouldBe("""{"unrelated":42}""");
		await service.ApplyAsync(service.PreviewBatch([new(profile, "first", first, null, SkillMode.Disabled),
			new(shared, "second", second, null, SkillMode.Enabled)], [profile, shared]), default);
		var doc = JsonNode.Parse(File.ReadAllText(batch.AffectedFiles[0]))!;
		doc["unrelated"]!.GetValue<int>().ShouldBe(42);
		doc["skillOverrides"]!["first"]!.GetValue<string>().ShouldBe("off");
		doc["skillOverrides"]!["second"]!.GetValue<string>().ShouldBe("on");
	}

	[TestCase(AgentKind.Codex)]
	[TestCase(AgentKind.Claude)]
	public async Task FutureSourceMembersPreserveExistingInlineCliSettings(AgentKind kind)
	{
		var profile = Profile(kind);
		var source = new SkillCatalogSource(SkillCatalogSourceKind.Native, "native", Path.Combine(profile.NativeConfigDirectory!, "skills"));
		var service = Service;
		await service.ApplyAsync(service.PreviewBatch([], [profile],
			[new(profile, source, Project, SkillMode.Disabled)], []), default);
		var file = Install(profile, "future");
		var prefix = kind == AgentKind.Codex ? "--config=" : "--settings=";
		var setting = kind == AgentKind.Codex ? "skills.config=[{name='unrelated',enabled=false}]"
			: """{"keep":42,"skillOverrides":{"unrelated":"off"}}""";
		var initial = new LaunchInjection(["other", prefix + setting], new Dictionary<string, string>());
		var launch = service.ComposeLaunch(profile, Project, initial);
		launch.Arguments.Count.ShouldBe(2);
		launch.Arguments[0].ShouldBe("other");
		launch.Arguments[1].ShouldStartWith(prefix);
		var merged = launch.Arguments[1][prefix.Length..];
		if (kind == AgentKind.Claude)
		{
			var doc = JsonNode.Parse(merged)!;
			doc["keep"]!.GetValue<int>().ShouldBe(42);
			doc["skillOverrides"]!["unrelated"]!.GetValue<string>().ShouldBe("off");
			doc["skillOverrides"]!["future"]!.GetValue<string>().ShouldBe("off");
			string? diagnostic = null;
			var opaque = initial with { Arguments = ["--settings", "some-settings.json"] };
			service.ComposeLaunch(profile, Project, opaque, message => diagnostic = message).Arguments.ShouldBe(opaque.Arguments);
			diagnostic.ShouldNotBeNull().ShouldContain("existing Claude settings");
		}
		else
		{
			var rules = CodexSkillRules.Read(merged);
			CodexSkillRules.IsEnabled(rules, "unrelated", file + "-other").ShouldBeFalse();
			CodexSkillRules.IsEnabled(rules, "future", file).ShouldBeFalse();
		}
	}

	[Test]
	public async Task FailedSecondWriteRestoresOriginalBytesOfFirstNativeFile()
	{
		var firstProfile = Profile(AgentKind.Claude);
		var secondProfile = Profile(AgentKind.Claude, "two");
		var first = Install(firstProfile, "first");
		var second = Install(secondProfile, "second");
		var config = Path.Combine(firstProfile.NativeConfigDirectory!, "settings.json");
		File.WriteAllText(config, "{ }\n", Encoding.UTF8);
		var original = File.ReadAllBytes(config);
		var secondConfig = Path.Combine(secondProfile.NativeConfigDirectory!, "settings.json");
		Directory.CreateDirectory(secondConfig);
		var service = Service;
		var batch = service.PreviewBatch([new(firstProfile, "first", first, null, SkillMode.Disabled),
			new(secondProfile, "second", second, null, SkillMode.Disabled)], [firstProfile, secondProfile]);
		await Should.ThrowAsync<IOException>(() => service.ApplyAsync(batch, default));
		File.ReadAllBytes(config).ShouldBe(original);
	}
}
