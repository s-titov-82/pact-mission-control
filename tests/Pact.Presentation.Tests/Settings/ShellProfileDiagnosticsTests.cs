using System.Text.Json.Nodes;
using Pact.Core.Agents;
using Pact.Core.Platform;
using Pact.Core.Sessions;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Settings;

public sealed class ShellProfileDiagnosticsTests
{
	[Test]
	public async Task Editor_validates_saves_and_removes_the_native_directory()
	{
		using var directory = TemporaryDirectory.Create();
		var paths = new AppPaths(directory.Path);
		var store = new SettingsFileStore(paths);
		await store.EnsureDefaultFilesAsync(CancellationToken.None);
		var section = new LaunchProfilesSectionViewModel(store);
		await section.LoadAsync(CancellationToken.None);
		var profile = section.Items.OfType<ShellProfileItemViewModel>().First();
		var before = await File.ReadAllTextAsync(paths.ShellProfilesPath);
		profile.NativeConfigDirectory = "relative";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeFalse();
		(await File.ReadAllTextAsync(paths.ShellProfilesPath)).ShouldBe(before);

		var native = Path.Combine(directory.Path, "separate config");
		profile.NativeConfigDirectory = $" {native} ";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		(await store.LoadShellProfilesAsync(CancellationToken.None))[0].NativeConfigDirectory.ShouldBe(native);
		await section.LoadAsync(CancellationToken.None);
		var reloaded = section.Items.OfType<ShellProfileItemViewModel>().First();
		reloaded.NativeConfigDirectory.ShouldBe(native);
		reloaded.NativeConfigDirectory = " ";
		(await section.SaveAsync(CancellationToken.None)).ShouldBeTrue();
		JsonNode.Parse(await File.ReadAllTextAsync(paths.ShellProfilesPath))![0]!.AsObject()
			.ContainsKey("nativeConfigDirectory").ShouldBeFalse();
	}

	[Test]
	public async Task Runtime_evidence_requires_the_same_profile_and_command()
	{
		using var directory = TemporaryDirectory.Create();
		var profile = new AgentProfileRecord("one", AgentKind.Codex, "Test", "./agent", null, "pwsh");
		var session = new SessionViewModel(new SessionRecord("session", profile.Kind, "Test", directory.Path,
			profile.CommandTemplate, null, SessionStatus.Running, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
		{
			LaunchProfileId = profile.Id
		})
		{ LastStartedMode = TerminalStartMode.ResumeSelection };

		var diagnostics = new ProfileDiagnostics(new AppPaths(directory.Path), new MissingExecutableLocator());
		var matching = await diagnostics.CheckAsync(profile, [session], _ => true, CancellationToken.None);
		matching.ShouldContain("started the conversation picker");
		matching.ShouldContain("authenticated initialization observed");
		matching.ShouldContain("Configured shell: 'pwsh' missing");
		foreach (var different in new[] { profile with { Id = "two" }, profile with { CommandTemplate = "./different" } })
		{
			var report = await diagnostics.CheckAsync(different, [session], _ => true, CancellationToken.None);
			report.ShouldContain("Launch: Not tested");
			report.ShouldContain("Restore: Not tested");
			report.ShouldContain("MCP: Not tested");
		}
	}

	private sealed class MissingExecutableLocator : IExecutableLocator
	{
		public string? FindOnPath(string executableName) => null;
	}

	[Test]
	public async Task Changed_command_discards_a_pending_diagnostic_result()
	{
		var item = new ShellProfileItemViewModel(new JsonObject { ["commandTemplate"] = "first-cli" });
		var result = new TaskCompletionSource<string>();
		var check = item.CheckAsync((profile, _) =>
		{
			profile.CommandTemplate.ShouldBe("first-cli");
			return result.Task;
		}, CancellationToken.None);
		item.CommandTemplate = "second-cli";
		result.SetResult("first-cli found");
		await check;
		item.Diagnostics.ShouldContain("Not tested");

		await item.CheckAsync((profile, _) => Task.FromResult(profile.CommandTemplate), CancellationToken.None);
		item.Diagnostics.ShouldBe("second-cli");
	}
}
