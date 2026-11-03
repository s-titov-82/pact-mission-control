using Pact.Core.Platform;
using Pact.Infrastructure.Platform;

namespace Pact.Infrastructure.Tests.Platform;

public sealed class WindowsIdeDiscoveryTests
{
	[Test]
	public void Discovers_modern_versioned_toolbox_install_without_walking_installation_contents()
	{
		using var temp = TemporaryDirectory.Create();
		var rider = Touch(temp.Path, "Programs/Rider 2026.1/bin/rider64.exe");
		var legacy = Touch(temp.Path, "JetBrains/Toolbox/apps/IDEA-U/bin/idea64.exe");
		var installation = Path.GetDirectoryName(Path.GetDirectoryName(legacy))!;
		var discovery = new WindowsIdeDiscovery(new Locator([]), temp.Path, temp.Path, temp.Path, () => [], path =>
		{
			if (path == installation)
			{
				Assert.Fail("Discovery enumerated installed product contents");
			}
			return Directory.GetDirectories(path);
		});

		var result = discovery.Discover(CancellationToken.None);

		result.Select(x => x.ExecutablePath).ShouldBe([rider, legacy], ignoreOrder: true);
	}

	[Test]
	public void Discovers_per_user_machine_path_registry_and_toolbox_installs_and_deduplicates_paths()
	{
		using var temp = TemporaryDirectory.Create();
		var user = Path.Combine(temp.Path, "local");
		var machine = Path.Combine(temp.Path, "machine");
		var machine32 = Path.Combine(temp.Path, "machine32");
		var code = Touch(user, "Programs/Microsoft VS Code/Code.exe");
		var cursor = Touch(machine, "Cursor/Cursor.exe");
		var windsurf = Touch(machine32, "Windsurf/Windsurf.exe");
		var rider = Touch(user, "JetBrains/Toolbox/apps/Rider/ch-0/251.1/bin/rider64.exe");
		var webstorm = Touch(user, "JetBrains/Toolbox/apps/WebStorm/bin/webstorm64.exe");
		var idea = Touch(machine, "JetBrains/IntelliJ IDEA 2026.1/bin/idea64.exe");
		var pycharm = Touch(temp.Path, "custom-pycharm/bin/pycharm64.exe");
		var zed = Touch(temp.Path, "custom-zed/zed.exe");
		var vs = Touch(machine, "Microsoft Visual Studio/2026/Community/Common7/IDE/devenv.exe");
		var locator = new Locator(new Dictionary<string, string> { ["code"] = Touch(user, "Programs/Microsoft VS Code/bin/code.cmd"), ["zed"] = zed });
		var discovery = new WindowsIdeDiscovery(locator, user, machine, machine32,
			() => [new("code", code.ToUpperInvariant()), new("pycharm", Path.GetDirectoryName(Path.GetDirectoryName(pycharm))!)],
			Directory.GetDirectories);

		var result = discovery.Discover(CancellationToken.None);

		result.Select(x => x.ExecutablePath).ShouldBe([code, cursor, windsurf, rider, webstorm, idea, pycharm, zed, vs], ignoreOrder: true);
		result.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(9);
	}

	[Test]
	public void Keeps_distinguishable_installations_and_skips_missing_inaccessible_and_wrapper_only_entries()
	{
		using var temp = TemporaryDirectory.Create();
		var first = Touch(temp.Path, "first/Code.exe");
		var second = Touch(temp.Path, "second/Code.exe");
		var locator = new Locator(new Dictionary<string, string> { ["cursor"] = Touch(temp.Path, "wrappers/cursor.cmd") });
		var discovery = new WindowsIdeDiscovery(locator, temp.Path, temp.Path, temp.Path,
			() => [new("code", first), new("code", second), new("rider", Path.Combine(temp.Path, "missing.exe"))],
			_ => throw new UnauthorizedAccessException());

		var result = discovery.Discover(CancellationToken.None);

		result.Count.ShouldBe(2);
		result.Select(x => x.Name).Distinct().Count().ShouldBe(2);
		result.Select(x => x.Id).Distinct().Count().ShouldBe(2);
		result.Select(x => x.ExecutablePath).ShouldBe([first, second], ignoreOrder: true);
	}

	private static string Touch(string root, string relative)
	{
		var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, string.Empty);
		return path;
	}

	private sealed class Locator(Dictionary<string, string> paths) : IExecutableLocator
	{
		public string? FindOnPath(string executableName) => paths.GetValueOrDefault(executableName);
	}
}
