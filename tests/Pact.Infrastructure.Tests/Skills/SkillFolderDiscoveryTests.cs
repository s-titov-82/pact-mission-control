using Pact.Core.Agents;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Skills;
using Pact.Infrastructure.Storage;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillFolderDiscoveryTests
{
	[Test]
	public async Task Shared_source_exposes_only_enabled_packages_in_descendants_including_later_installs()
	{
		using var temp = TemporaryDirectory.Create();
		var paths = new AppPaths(Path.Combine(temp.Path, "pact"));
		var shared = Path.Combine(temp.Path, "shared");
		var folder = Path.Combine(temp.Path, "workspace");
		var child = Path.Combine(folder, "repo");
		var profile = new AgentProfileRecord("claude", AgentKind.Claude, "Claude", "claude", null, "pwsh")
		{ NativeConfigDirectory = Path.Combine(temp.Path, "home") };
		var service = new SkillActivationService(paths, shared);
		var catalog = new SkillCatalogService(paths.SettingsDirectory, shared, temp.Path);
		string Install(string name)
		{
			var directory = Directory.CreateDirectory(Path.Combine(shared, name));
			var file = Path.Combine(directory.FullName, "SKILL.md");
			File.WriteAllText(file, $"---\nname: {name}\n---\n");
			return file;
		}
		var first = Install("first");
		var excluded = Install("excluded");
		var members = catalog.DiscoverPolicyMembers(profile, service, null, includeShared: true);
		var source = members[0].Source;
		var preview = service.PreviewBatch([new(profile, "excluded", excluded, folder, SkillMode.Disabled, source, true),
			new(profile, "first", first, folder, SkillMode.Enabled, source, true)],
			[profile], [new(profile, source, folder, SkillMode.Enabled, true)], members);
		var link = preview.Links.Single();
		link.Target.ShouldBe(Path.GetDirectoryName(first));
		Directory.Exists(link.Path).ShouldBeFalse();
		await service.ApplyAsync(preview, default);
		Install("later");
		LaunchInjection Launch(string workingDirectory)
		{
			return service.ComposeLaunch(profile, null,
				new LaunchInjection([], new Dictionary<string, string>()), workingDirectory: workingDirectory);
		}
		var args = Launch(child).Arguments.ToList();
		var projections = args.Select((arg, index) => arg == "--add-dir" ? args[index + 1] : null).OfType<string>().ToArray();
		projections.Length.ShouldBe(2);
		var visible = projections.SelectMany(root => Directory.EnumerateDirectories(Path.Combine(root, ".claude", "skills")))
			.Select(SkillCatalogService.ResolveDirectory).Order().ToArray();
		visible.ShouldBe(new[] { Path.Combine(shared, "first"), Path.Combine(shared, "later") }.Order().ToArray());
		Launch(folder + "-other").Arguments.ShouldNotContain("--add-dir");
		Directory.Exists(profile.NativeConfigDirectory).ShouldBeFalse();
		Directory.Exists(folder).ShouldBeFalse();
		var installation = (await catalog.DiscoverInstalledAsync([profile], service)).Installations.Single(item => item.Skill.Name == "first");
		var rename = catalog.PreviewRename(installation, installation.Connections.Single(), "renamed", [profile], [child]);
		rename.Conflict.ShouldBeNull();
		catalog.Apply(rename);
		Directory.Exists(link.Path).ShouldBeFalse();
		File.Exists(link.ReceiptPath).ShouldBeFalse();
		var renamed = Path.Combine(shared, "renamed", "SKILL.md");
		service.InspectMatrix(profile, source, "first", renamed, folder, folderScope: true).ExplicitMode.ShouldBe(SkillMode.Enabled);
		var after = Launch(child).Arguments.ToList();
		var updated = after.Select((arg, index) => arg == "--add-dir" ? after[index + 1] : null).OfType<string>().ToArray();
		updated.Length.ShouldBe(2);
		updated.ShouldNotContain(link.ProjectionRoot!);
		updated.Any(root => File.Exists(Path.Combine(root, ".claude", "skills", "renamed", "SKILL.md"))).ShouldBeTrue();
		File.WriteAllText(Path.Combine(updated[0], "unexpected.txt"), "outside the grant");
		Launch(child).Arguments.ShouldNotContain(updated[0]);
	}
}
