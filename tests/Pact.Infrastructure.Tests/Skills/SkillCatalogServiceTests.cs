using System.IO.Compression;
using System.Net;
using System.Text;
using Pact.Infrastructure.Skills;

namespace Pact.Infrastructure.Tests.Skills;

public sealed class SkillCatalogServiceTests
{
	[Test]
	public async Task Managed_git_package_tracks_original_branch_and_subdirectory_after_source_checkout_changes()
	{
		using var temp = TemporaryDirectory.Create();
		var repo = Directory.CreateDirectory(Path.Combine(temp.Path, "repo")).FullName;
		var selected = Directory.CreateDirectory(Path.Combine(repo, "packages", "one")).FullName;
		var second = Directory.CreateDirectory(Path.Combine(repo, "packages", "two")).FullName;
		var file = Path.Combine(selected, "SKILL.md");
		File.WriteAllText(file, "---\nname: first\n---\nv1");
		File.WriteAllText(Path.Combine(second, "SKILL.md"), "other");
		var runner = new GitCliRunner();
		async Task Git(params string[] args)
		{
			var result = await runner.RunAsync(repo, args, null, TestContext.CurrentContext.CancellationToken);
			result.Succeeded.ShouldBeTrue(result.StandardError);
		}

		async Task Commit(string message)
		{
			await Git("add", ".");
			await Git("-c", "user.name=Pact Test", "-c", "user.email=pact@example.com", "-c", "commit.gpgsign=false", "commit", "-m", message);
		}

		await Git("init", "--initial-branch=team");
		await Commit("first");
		var installed = Path.Combine(temp.Path, "installed");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), installed);
		var packages = await service.BrowseAsync(repo);
		packages.Count.ShouldBe(2);
		var package = packages.Single(p => p.Name == "first");
		service.Apply(service.Preview(package));
		var binding = service.GetBindings()["one"];
		binding.RelativePath.ShouldBe("packages/one");
		binding.Reference.ShouldBe("team");
		binding.Revision.ShouldBe(package.Revision);
		File.WriteAllText(file, "v2");
		await Commit("second");
		await Git("checkout", "--detach", "HEAD~1");
		var update = await service.CheckUpdateAsync("one");
		update.Skill.Revision.ShouldNotBe(binding.Revision);
		update.InstalledRevision.ShouldBe(binding.Revision);
		service.Apply(update);
		var local = (await service.BrowseAsync(installed)).Single();
		local.InstalledFilePath.ShouldBe(Path.Combine(installed, "one", "SKILL.md"));
		local.ReadFile("SKILL.md").ShouldBe("v2");
	}

	[Test]
	public async Task GitHub_release_preview_preserves_notes_reference_and_binary_assets()
	{
		using var temp = TemporaryDirectory.Create();
		using var buffer = new MemoryStream();
		using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
		{
			using (var manifest = archive.CreateEntry("repo-abc/skills/demo/SKILL.md").Open())
			{
				manifest.Write(Encoding.UTF8.GetBytes("demo"));
			}

			using var image = archive.CreateEntry("repo-abc/skills/demo/pic.png").Open();
			image.Write([255, 0, 127]);
		}

		using var handler = new ReleaseHandler(buffer.ToArray());
		using var http = new HttpClient(handler);
		var reader = new SkillPackageReader(temp.Path, http);
		var releases = await reader.ReadAsync("https://github.com/team/repo/releases/latest", false, null, TestContext.CurrentContext.CancellationToken);
		var installed = Path.Combine(temp.Path, "installed");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), installed);
		var preview = service.Preview(releases.Single());
		preview.Skill.RelativePath.ShouldBe("skills/demo");
		preview.Skill.Reference.ShouldBe("v2");
		preview.Skill.Revision.ShouldBe("abc");
		preview.ReleaseNotes.ShouldBe("Added binary asset");
		service.Apply(preview);
		File.ReadAllBytes(Path.Combine(installed, "demo", "pic.png")).ShouldBe(new byte[] { 255, 0, 127 });
	}

	[Test]
	public async Task Explicit_update_fetches_upstream_without_moving_the_source_checkout_or_tracking_ref()
	{
		using var temp = TemporaryDirectory.Create();
		var remote = Directory.CreateDirectory(Path.Combine(temp.Path, "remote")).FullName;
		var checkout = Path.Combine(temp.Path, "checkout");
		var runner = new GitCliRunner();
		async Task<string> Git(string directory, params string[] args)
		{
			var result = await runner.RunAsync(directory, args, null, TestContext.CurrentContext.CancellationToken);
			result.Succeeded.ShouldBeTrue(result.StandardError);
			return result.StandardOutput.Trim();
		}

		async Task Commit(string message)
		{
			await Git(remote, "add", ".");
			await Git(remote, "-c", "user.name=Pact Test", "-c", "user.email=pact@example.com", "-c", "commit.gpgsign=false", "commit", "-m", message);
		}

		await Git(remote, "init", "--initial-branch=team");
		File.WriteAllText(Path.Combine(remote, "SKILL.md"), "v1");
		await Commit("one");
		await Git(temp.Path, "clone", "--", new Uri(remote).AbsoluteUri, checkout);
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), Path.Combine(temp.Path, "installed"));
		var package = (await service.BrowseAsync(checkout)).Single();
		service.Apply(service.Preview(package));
		File.WriteAllText(Path.Combine(remote, "SKILL.md"), "v2");
		await Commit("two");
		var remoteRevision = await Git(remote, "rev-parse", "HEAD");

		var update = await service.CheckUpdateAsync(package.FolderName);
		update.Skill.Revision.ShouldBe(remoteRevision);
		update.Skill.ReadFile("SKILL.md").ShouldBe("v2");
		File.ReadAllText(Path.Combine(update.Destination, "SKILL.md")).ShouldBe("v1");
		(await Git(checkout, "rev-parse", "HEAD")).ShouldBe(package.Revision);
		(await Git(checkout, "rev-parse", "refs/remotes/origin/team")).ShouldBe(package.Revision);
		service.Apply(update);
		File.ReadAllText(Path.Combine(update.Destination, "SKILL.md")).ShouldBe("v2");
	}

	private sealed class ReleaseHandler(byte[] archive) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			HttpContent body = request.RequestUri!.AbsolutePath switch
			{
				"/repos/team/repo/releases/latest" => new StringContent("""{"tag_name":"v2"}"""),
				"/repos/team/repo/commits/v2" => new StringContent("""{"sha":"abc"}"""),
				"/repos/team/repo/releases/tags/v2" => new StringContent("""{"body":"Added binary asset"}"""),
				"/repos/team/repo/zipball/abc" => new ByteArrayContent(archive),
				_ => throw new InvalidOperationException(request.RequestUri.ToString())
			};
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
		}
	}

	[Test]
	public async Task Confirmed_install_copies_previewed_bytes_and_update_detects_local_edits()
	{
		using var temp = TemporaryDirectory.Create();
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "example")).FullName;
		var installed = Path.Combine(temp.Path, "installed");
		File.WriteAllText(Path.Combine(source, "SKILL.md"), "# Original");
		var script = Path.Combine(source, "run.ps1");
		File.WriteAllText(script, "first");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "settings"), installed);
		var package = (await service.BrowseAsync(source)).Single();
		var preview = service.Preview(package);
		Directory.Exists(installed).ShouldBeFalse();
		package.ReadFile("run.ps1").ShouldBe("first");
		File.WriteAllText(script, "second");
		service.Apply(preview);
		var installedScript = Path.Combine(installed, "example", "run.ps1");
		File.ReadAllText(installedScript).ShouldBe("first");
		var update = await service.CheckUpdateAsync("example");
		update.Changes.ShouldContain("Modified run.ps1");
		File.WriteAllText(installedScript, "user edit");
		Should.Throw<IOException>(() => service.Apply(update));
		File.ReadAllText(installedScript).ShouldBe("user edit");
	}

	[Test]
	public async Task Collision_preserves_manual_copy_and_identical_copy_can_bind_after_confirmation()
	{
		using var temp = TemporaryDirectory.Create();
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "example")).FullName;
		var installed = Directory.CreateDirectory(Path.Combine(temp.Path, "installed", "example")).FullName;
		File.WriteAllText(Path.Combine(source, "SKILL.md"), "team");
		var local = Path.Combine(installed, "SKILL.md");
		File.WriteAllText(local, "manual");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "settings"), Path.GetDirectoryName(installed)!);
		var package = (await service.BrowseAsync(source)).Single();
		var conflict = service.Preview(package);
		conflict.Conflict.ShouldNotBeNull();
		Should.Throw<IOException>(() => service.Apply(conflict));
		File.ReadAllText(local).ShouldBe("manual");
		File.WriteAllText(local, "team");
		var binding = service.Preview(package);
		binding.Conflict.ShouldBeNull();
		service.GetManagedNames().ShouldBeEmpty();
		service.Apply(binding);
		service.GetManagedNames().ShouldContain("example");
	}

	[Test]
	public async Task Manifest_name_collision_reports_both_locations_even_when_folder_names_differ()
	{
		using var temp = TemporaryDirectory.Create();
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "new")).FullName;
		var existing = Directory.CreateDirectory(Path.Combine(temp.Path, "installed", "old")).FullName;
		File.WriteAllText(Path.Combine(source, "SKILL.md"), "---\nname: shared-name\n---\nnew");
		var manual = Path.Combine(existing, "SKILL.md");
		File.WriteAllText(manual, "---\nname: shared-name\n---\nexisting");
		var service = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), Path.GetDirectoryName(existing)!);
		var package = (await service.BrowseAsync(source)).Single();
		var preview = service.Preview(package);
		preview.Conflict.ShouldNotBeNull().ShouldContain(manual);
		preview.Conflict.ShouldContain(source);
		Should.Throw<IOException>(() => service.Apply(preview));
		Directory.Exists(preview.Destination).ShouldBeFalse();
	}

	[Test]
	public async Task Failed_binding_write_restores_previously_installed_files()
	{
		using var temp = TemporaryDirectory.Create();
		var source = Directory.CreateDirectory(Path.Combine(temp.Path, "team", "demo")).FullName;
		var settings = Path.Combine(temp.Path, "Settings");
		var installed = Path.Combine(temp.Path, "installed");
		var service = new SkillCatalogService(settings, installed);
		var file = Path.Combine(source, "SKILL.md");
		File.WriteAllText(file, "v1");
		service.Apply(service.Preview((await service.BrowseAsync(source)).Single()));
		File.WriteAllText(file, "v2");
		var update = await service.CheckUpdateAsync("demo");
		using var lockedRegistry = File.Open(Path.Combine(settings, "skill-installations.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
		Should.Throw<IOException>(() => service.Apply(update));
		File.ReadAllText(Path.Combine(installed, "demo", "SKILL.md")).ShouldBe("v1");
	}

	[Test]
	public async Task Manually_cloned_skill_is_discovered_without_importing_or_modifying_git_metadata()
	{
		using var temp = TemporaryDirectory.Create();
		var installed = Path.Combine(temp.Path, "installed");
		var directory = Directory.CreateDirectory(Path.Combine(installed, "manual")).FullName;
		var git = Directory.CreateDirectory(Path.Combine(directory, ".git")).FullName;
		var config = Path.Combine(git, "config");
		File.WriteAllText(config, "unrelated repository metadata");
		var manifest = Path.Combine(directory, "SKILL.md");
		File.WriteAllText(manifest, "---\nname: cloned-manifest # YAML comment\n---\nbody");
		var catalog = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), installed);
		var discovered = (await catalog.BrowseAsync(installed)).Single();
		discovered.InstalledFilePath.ShouldBe(manifest);
		discovered.Name.ShouldBe("cloned-manifest");
		discovered.Files.ShouldBe(new[] { "SKILL.md" });
		catalog.GetBindings().ShouldBeEmpty();
		File.ReadAllText(config).ShouldBe("unrelated repository metadata");
	}

	[Test]
	public async Task Unreadable_native_asset_keeps_manifest_and_other_packages_available()
	{
		using var temp = TemporaryDirectory.Create();
		var installed = Path.Combine(temp.Path, "installed");
		var partial = Directory.CreateDirectory(Path.Combine(installed, "partial")).FullName;
		var other = Directory.CreateDirectory(Path.Combine(installed, "other")).FullName;
		File.WriteAllText(Path.Combine(partial, "SKILL.md"), "partial");
		File.WriteAllText(Path.Combine(other, "SKILL.md"), "other");
		var locked = Path.Combine(partial, "script.ps1");
		using var readLock = File.Open(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None);
		var catalog = new SkillCatalogService(Path.Combine(temp.Path, "Settings"), installed);
		var discovered = await catalog.BrowseAsync(installed);
		discovered.Count.ShouldBe(2);
		var incomplete = discovered.Single(p => p.Name == "partial");
		incomplete.InspectionWarning.ShouldNotBeNull();
		incomplete.ReadFile("SKILL.md").ShouldBe("partial");
		discovered.Single(p => p.Name == "other").InspectionWarning.ShouldBeNull();
	}
}