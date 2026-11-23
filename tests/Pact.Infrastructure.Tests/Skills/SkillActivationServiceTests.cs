using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillActivationServiceTests : IDisposable
{
	private readonly TemporaryDirectory _directory = TemporaryDirectory.Create();
	private SkillActivationService Service => new(new AppPaths(Path.Combine(_directory.Path, "pact")));
	private string Project => Path.Combine(_directory.Path, "project");
	public void Dispose() => _directory.Dispose();

	private AgentProfileRecord Profile(AgentKind kind, string id = "one", string home = "native") =>
		new(id, kind, id, kind.ToString().ToLowerInvariant(), null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(_directory.Path, home) };

	private static string Install(string root)
	{
		var folder = Directory.CreateDirectory(Path.Combine(root, "skills", "sample"));
		var file = Path.Combine(folder.FullName, "SKILL.md");
		File.WriteAllText(file, "---\nname: sample\ndescription: Sample\n---\n");
		return file;
	}

	[TestCase(AgentKind.Claude)]
	[TestCase(AgentKind.Codex)]
	public async Task InstallPredictionAppliesExistingProfileAndProjectRulesWithoutWritingTheManifest(AgentKind kind)
	{
		var service = Service;
		var profile = Profile(kind);
		var file = Install(profile.NativeConfigDirectory!);
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]), default);
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]), default);
		File.Delete(file);

		service.Inspect(profile, "sample", file, Project).EffectiveEnabled.ShouldBeFalse();
		service.InspectAfterInstallation(profile, "sample", file, Project).EffectiveEnabled.ShouldBeTrue();
		service.InspectAfterInstallation(profile, "sample", file, null).DefaultEnabled.ShouldBeFalse();
		service.InspectAfterInstallation(profile, "sample", file, Project + "2").EffectiveEnabled.ShouldBeFalse();
		service.InspectAfterInstallation(Profile(kind, "unrelated", "elsewhere"), "sample", file, null).EffectiveEnabled.ShouldBeFalse();
		File.Exists(file).ShouldBeFalse();
	}

	[Test]
	public async Task PredictionRecognizesARenamedNativeJunctionAndCodexPathRuleForItsTarget()
	{
		var service = Service;
		var profile = Profile(AgentKind.Codex);
		var external = Install(Path.Combine(_directory.Path, "external"));
		var alias = Path.Combine(profile.NativeConfigDirectory!, "skills", "my-alias");
		await SkillDirectoryConnection.ApplyAsync(new SkillLinkEdit(alias, Path.GetDirectoryName(external)!,
			Path.Combine(_directory.Path, "test-receipt"), true), default);
		File.Delete(external);
		service.InspectAfterInstallation(profile, "sample", external, null).EffectiveEnabled.ShouldBeTrue();
		File.WriteAllText(Path.Combine(profile.NativeConfigDirectory!, "config.toml"),
			$"[[skills.config]]\npath='{external}'\nenabled=false\n");
		service.InspectAfterInstallation(profile, "sample", Path.Combine(alias, "SKILL.md"), null).EffectiveEnabled.ShouldBeFalse();
		File.Exists(external).ShouldBeFalse();
	}

	[Test]
	public async Task ClaudeUsesItsDiscoveredCommandNameForProfileAndProjectOverrides()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var external = Install(Path.Combine(_directory.Path, "external"));
		var alias = Path.Combine(profile.NativeConfigDirectory!, "skills", "cli-alias");
		await SkillDirectoryConnection.ApplyAsync(new SkillLinkEdit(alias, Path.GetDirectoryName(external)!,
			Path.Combine(_directory.Path, "test-receipt"), true), default);
		var config = Path.Combine(profile.NativeConfigDirectory!, "settings.json");
		File.WriteAllText(config, """{"skillOverrides":{"sample":"off","cli-alias":"on"}}""");
		service.Inspect(profile, "sample", external, null).DefaultEnabled.ShouldBeTrue();
		await service.ApplyAsync(service.Preview(profile, "sample", external, null, SkillMode.Disabled, [profile]), default);
		JsonNode.Parse(File.ReadAllText(config))!["skillOverrides"]!["cli-alias"]!.GetValue<string>().ShouldBe("off");
		var projectEdit = service.Preview(profile, "sample", external, Project, SkillMode.Enabled, [profile]);
		await service.ApplyAsync(projectEdit, default);
		JsonNode.Parse(File.ReadAllText(projectEdit.FilePath))!["skillOverrides"]!["cli-alias"]!.GetValue<string>().ShouldBe("on");
		service.Inspect(profile, "sample", external, Project).EffectiveEnabled.ShouldBeTrue();
	}

	[TestCase(AgentKind.Codex)]
	[TestCase(AgentKind.Claude)]
	public async Task InheritingAnUnsetProjectRuleLeavesAbsentAndExistingFilesUntouched(AgentKind kind)
	{
		var profile = Profile(kind);
		var file = Install(profile.NativeConfigDirectory!);
		var service = Service;
		var absent = service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]);
		absent.AffectedFiles.ShouldBeEmpty();
		await service.ApplyAsync(absent, default);
		File.Exists(absent.FilePath).ShouldBeFalse();
		Directory.CreateDirectory(Path.GetDirectoryName(absent.FilePath)!);
		const string original = "{   }\n";
		File.WriteAllText(absent.FilePath, original);
		var existing = service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]);
		existing.AffectedFiles.ShouldBeEmpty();
		await service.ApplyAsync(existing, default);
		File.ReadAllText(absent.FilePath).ShouldBe(original);
	}

	[TestCase(AgentKind.Codex)]
	[TestCase(AgentKind.Claude)]
	public async Task RemovingPactDefaultBridgePreservesExplicitlyEnabledProject(AgentKind kind)
	{
		var profile = Profile(kind);
		var file = Install(Path.Combine(_directory.Path, "external"));
		var service = Service;
		var enable = service.Preview(profile, "sample", file, null, SkillMode.Enabled, [profile]);
		await service.ApplyAsync(enable, default);
		var project = service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]);
		project.CreatedConnectionPath.ShouldNotBeNull();
		await service.ApplyAsync(project, default);
		var disable = service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]);
		disable.AffectedFiles.ShouldContain(enable.CreatedConnectionPath!);
		await service.ApplyAsync(disable, default);
		Path.Exists(enable.CreatedConnectionPath!).ShouldBeFalse();
		File.Exists(Path.Combine(project.CreatedConnectionPath!, "SKILL.md")).ShouldBeTrue();
		service.Inspect(profile, "sample", file, Project).EffectiveEnabled.ShouldBeTrue();
		service.Inspect(profile, "sample", file, Project + "2").EffectiveEnabled.ShouldBeFalse();
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]), default);
		Path.Exists(project.CreatedConnectionPath!).ShouldBeFalse();
		File.Exists(file).ShouldBeTrue();
	}

	[TestCase("codex", "[skills\n", false)]
	[TestCase("codex", "skills.config = 3", false)]
	[TestCase("codex resume --profile other", "", true)]
	[TestCase("custom-wrapper", "", true)]
	public async Task StaleCodexOverrideFallsBackToNormalLaunchWithAnActionableDiagnostic(
		string command, string nativeContents, bool commandIsUnsupported)
	{
		var profile = Profile(AgentKind.Codex);
		var file = Install(profile.NativeConfigDirectory!);
		var service = Service;
		var edit = service.Preview(profile, "sample", file, Project, SkillMode.Disabled, [profile]);
		await service.ApplyAsync(edit, default);
		var native = Path.Combine(profile.NativeConfigDirectory!, "config.toml");
		File.WriteAllText(native, nativeContents);
		string? diagnostic = null;
		var input = new LaunchInjection(["--existing"], new Dictionary<string, string> { ["EXISTING"] = "yes" });
		var launch = service.ComposeLaunch(profile with { CommandTemplate = command }, Project, input, value => diagnostic = value);
		launch.Arguments.ShouldBe(input.Arguments);
		launch.EnvironmentVariables["EXISTING"].ShouldBe("yes");
		launch.EnvironmentVariables["CODEX_HOME"].ShouldBe(profile.NativeConfigDirectory);
		diagnostic.ShouldNotBeNull().ShouldContain(commandIsUnsupported ? edit.FilePath : native);
	}

	[Test]
	public async Task ClaudePreviewIdentifiesSharedNativeHomesAndGuardsConcurrentEdits()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(profile.NativeConfigDirectory!);
		var preview = service.Preview(profile, "sample", file, null, SkillMode.Disabled,
			[profile, Profile(AgentKind.Claude, "two"), Profile(AgentKind.Claude, "three", "other")]);
		preview.AffectedProfiles.ShouldBe(["one", "two"]);
		File.Exists(preview.FilePath).ShouldBeFalse();
		await File.WriteAllTextAsync(preview.FilePath, """{"untouched":42}""");
		await Should.ThrowAsync<InvalidOperationException>(() => service.ApplyAsync(preview, default));
		var updated = service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]);
		await service.ApplyAsync(updated, default);
		var json = JsonNode.Parse(await File.ReadAllTextAsync(updated.FilePath))!;
		json["untouched"]!.GetValue<int>().ShouldBe(42);
		json["skillOverrides"]!["sample"]!.GetValue<string>().ShouldBe("off");
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]), default);
		service.Inspect(profile, "sample", file, Project).EffectiveEnabled.ShouldBeTrue();
		service.Inspect(profile, "sample", file, Project + "2").EffectiveEnabled.ShouldBeFalse();
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]), default);
		service.Inspect(profile, "sample", file, Project).EffectiveEnabled.ShouldBeFalse();
	}

	[Test]
	public async Task ClaudeChangesOneOverrideWithoutRewritingOtherSettingsOrBom()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(profile.NativeConfigDirectory!);
		var settings = Path.Combine(profile.NativeConfigDirectory!, "settings.json");
		var utf8WithBom = new System.Text.UTF8Encoding(true);
		const string original = "{\r\n\t\"language\" : \"Français <test> \\\"quoted\\\"\",\r\n\t\"skillOverrides\": {\"sample\" : \"on\", \"another\" : \"name-only\"}\r\n}\r\n";
		File.WriteAllText(settings, original, utf8WithBom);

		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]), default);

		var expected = original.Replace("\"on\"", "\"off\"", StringComparison.Ordinal);
		File.ReadAllBytes(settings).ShouldBe(utf8WithBom.GetPreamble().Concat(utf8WithBom.GetBytes(expected)).ToArray());
	}

	[Test]
	public async Task ClaudeAddsAndRemovesAnOverrideWithoutChangingUnrelatedBytes()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(profile.NativeConfigDirectory!);
		var settings = Path.Combine(profile.NativeConfigDirectory!, "settings.json");
		var utf8 = new System.Text.UTF8Encoding(true);
		const string original = "{\r\n\t\"language\" : \"Français\",\r\n\t\"skillOverrides\": {\"other\" : \"name-only\"}\r\n}\r\n";
		File.WriteAllText(settings, original, utf8);
		var bytes = File.ReadAllBytes(settings);

		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]), default);
		var added = original.Replace("\"name-only\"", "\"name-only\", \"sample\": \"off\"", StringComparison.Ordinal);
		File.ReadAllBytes(settings).ShouldBe(utf8.GetPreamble().Concat(utf8.GetBytes(added)).ToArray());

		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Inherit, [profile]), default);
		File.ReadAllBytes(settings).ShouldBe(bytes);
	}

	[Test]
	public async Task FailedSettingsWriteRollsBackNewConnectionAndReceipt()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var second = Profile(AgentKind.Claude, "two", "other");
		var source = Install(Path.Combine(_directory.Path, "shared"));
		var other = Install(second.NativeConfigDirectory!);
		var twoConfig = Path.Combine(second.NativeConfigDirectory!, "settings.json");
		Directory.CreateDirectory(twoConfig);
		var batch = service.PreviewBatch([new(profile, "sample", source, Project, SkillMode.Enabled),
			new(second, "sample", other, null, SkillMode.Disabled)], [profile, second]);
		var link = batch.Changes[0].Link!;

		await Should.ThrowAsync<IOException>(() => service.ApplyAsync(batch, default));

		new DirectoryInfo(link.Path).LinkTarget.ShouldBeNull();
		File.Exists(link.ReceiptPath).ShouldBeFalse();
		File.Exists(batch.Changes[0].FilePath).ShouldBeFalse();
		File.Exists(source).ShouldBeTrue();
		Directory.Exists(twoConfig).ShouldBeTrue();
	}

	[Test]
	public async Task EnableRecreatesAnExternallyRemovedPactConnection()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(Path.Combine(_directory.Path, "shared"));
		var first = service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]);
		await service.ApplyAsync(first, default);
		Directory.Delete(first.Link!.Path);

		var recovery = service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]);
		await service.ApplyAsync(recovery, default);

		File.Exists(Path.Combine(first.Link.Path, "SKILL.md")).ShouldBeTrue();
		File.ReadAllLines(first.Link.ReceiptPath).ShouldBe([Path.GetDirectoryName(file)!, first.Link.Path]);
	}

	[Test]
	public async Task CodexLaunchPreservesOtherNativeRulesAndOverlaysOnlyOneProjectAndProfile()
	{
		var service = Service;
		var profile = Profile(AgentKind.Codex);
		var file = Install(profile.NativeConfigDirectory!);
		var config = Path.Combine(profile.NativeConfigDirectory!, "config.toml");
		const string original = "# preserve comment\nmodel = 'test'\n[[skills.config]]\nname = 'another'\nenabled = false\n";
		File.WriteAllText(config, original);
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]), default);
		File.ReadAllText(config).ShouldStartWith(original);
		var global = File.ReadAllText(config);
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]), default);
		static LaunchInjection Empty()
		{
			return new(["other-argument"], new Dictionary<string, string>());
		}

		var launch = service.ComposeLaunch(profile, Project, Empty());
		launch.EnvironmentVariables["CODEX_HOME"].ShouldBe(profile.NativeConfigDirectory);
		launch.Arguments[0].ShouldBe("other-argument");
		launch.Arguments[1].ShouldBe("-c");
		var rules = CodexSkillRules.Read(launch.Arguments[2]);
		CodexSkillRules.IsEnabled(rules, "sample", file).ShouldBeTrue();
		CodexSkillRules.IsEnabled(rules, "another", @"C:\other\SKILL.md").ShouldBeFalse();
		service.ComposeLaunch(profile, Project + "2", Empty()).Arguments.ShouldBe(["other-argument"]);
		service.ComposeLaunch(Profile(AgentKind.Codex, "two"), Project, Empty()).Arguments.ShouldBe(["other-argument"]);
		File.ReadAllText(config).ShouldBe(global);
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Enabled, [profile]), default);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeTrue();
	}

	[Test]
	public async Task ProjectConnectionFollowsUpdatedPackageAndInheritRemovesOnlyOwnedLink()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(Path.Combine(_directory.Path, "shared"));
		var preview = service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]);
		var discovered = Path.Combine(Project, ".claude", "skills", "sample", "SKILL.md");
		preview.AffectedFiles.ShouldContain(Path.GetDirectoryName(discovered)!);
		File.Exists(discovered).ShouldBeFalse();
		await service.ApplyAsync(preview, default);
		File.WriteAllText(file, "updated");
		File.ReadAllText(discovered).ShouldBe("updated");
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]), default);
		File.Exists(discovered).ShouldBeFalse();
		File.ReadAllText(file).ShouldBe("updated");
	}

	[Test]
	public async Task InheritKeepsExistingLinkToTheSameInstallationWithoutAPactReceipt()
	{
		var service = Service;
		var profile = Profile(AgentKind.Claude);
		var file = Install(Path.Combine(_directory.Path, "shared"));
		var create = service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]);
		await service.ApplyAsync(create, default);
		File.Delete(create.Link!.ReceiptPath);
		var inherit = service.Preview(profile, "sample", file, Project, SkillMode.Inherit, [profile]);
		inherit.AffectedFiles.ShouldBe([inherit.FilePath]);
		await service.ApplyAsync(inherit, default);
		File.Exists(Path.Combine(Project, ".claude", "skills", "sample", "SKILL.md")).ShouldBeTrue();
	}

	[Test]
	public async Task CodexInlineSettingsRetainCommentsAndUnrelatedPathEntries()
	{
		var service = Service;
		var profile = Profile(AgentKind.Codex);
		var file = Install(profile.NativeConfigDirectory!);
		var config = Path.Combine(profile.NativeConfigDirectory!, "config.toml");
		const string untouched = "{ name = 'other', enabled = false }";
		File.WriteAllText(config, $"[skills]\nconfig = [{untouched}] # keep this note\n");
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Disabled, [profile]), default);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeFalse();
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Enabled, [profile]), default);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeTrue();
		File.ReadAllText(config).ShouldContain(untouched);
		File.ReadAllText(config).ShouldContain("# keep this note");
	}

	[Test]
	public async Task LastMatchingCodexRuleCanEnableOnlyOneCopyOfANameDisabledSkill()
	{
		var service = Service;
		var profile = Profile(AgentKind.Codex);
		var file = Install(profile.NativeConfigDirectory!);
		var other = Install(Path.Combine(_directory.Path, "other"));
		var config = Path.Combine(profile.NativeConfigDirectory!, "config.toml");
		var byPath = $"[[skills.config]]\npath = '{file}'\nenabled = true\n";
		const string byName = "[[skills.config]]\nname = 'sample'\nenabled = false\n";
		File.WriteAllText(config, byName + byPath);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeTrue();
		File.WriteAllText(config, byPath + byName);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeFalse();
		await service.ApplyAsync(service.Preview(profile, "sample", file, Project, SkillMode.Enabled, [profile]), default);
		var launch = service.ComposeLaunch(profile, Project, new LaunchInjection([], new Dictionary<string, string>()));
		var projectRules = CodexSkillRules.Read(launch.Arguments[^1]);
		CodexSkillRules.IsEnabled(projectRules, "sample", file).ShouldBeTrue();
		CodexSkillRules.IsEnabled(projectRules, "sample", other).ShouldBeFalse();
		File.ReadAllText(config).ShouldBe(byPath + byName);
		await service.ApplyAsync(service.Preview(profile, "sample", file, null, SkillMode.Enabled, [profile]), default);
		service.Inspect(profile, "sample", file, null).DefaultEnabled.ShouldBeTrue();
		CodexSkillRules.IsEnabled(CodexSkillRules.Read(File.ReadAllText(config)), "sample", other).ShouldBeFalse();
	}
}
