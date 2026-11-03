using System.ComponentModel;
using System.Diagnostics;
using Pact.Core.Platform;
using Pact.Infrastructure.Platform;

namespace Pact.Infrastructure.Tests.Platform;

public sealed class WindowsInstalledIdeServiceTests
{
	[Test]
	public async Task Discover_caches_until_explicit_refresh_and_keeps_entry_when_icon_fails()
	{
		var calls = 0;
		using var service = new WindowsInstalledIdeService(_ =>
		{
			calls++;
			return [Ide("code", "Code", @"C:\Code.exe")];
		}, _ => throw new Win32Exception("no icon"), _ => Assert.Fail("Discovery launched an editor"));

		var first = await service.DiscoverAsync(false, CancellationToken.None);
		await service.DiscoverAsync(false, CancellationToken.None);
		calls.ShouldBe(1);
		first.ShouldHaveSingleItem().Name.ShouldBe("Code");
		first[0].IconPng.ShouldBeNull();
		await service.DiscoverAsync(true, CancellationToken.None);
		calls.ShouldBe(2);
	}

	[Test]
	public async Task Refresh_is_serialized_and_last_refresh_owns_cache()
	{
		using var entered = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var calls = 0;
		using var service = new WindowsInstalledIdeService(_ =>
		{
			var call = Interlocked.Increment(ref calls);
			if (call == 1)
			{
				entered.Set();
				release.Wait(CancellationToken.None);
			}
			return [Ide("code", $"Code {call}", @"C:\Code.exe")];
		}, _ => null, _ => Assert.Fail("Discovery launched an editor"));
		var earlier = service.DiscoverAsync(true, CancellationToken.None);
		try
		{
			entered.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
			var newer = service.DiscoverAsync(true, CancellationToken.None);
			calls.ShouldBe(1);
			release.Set();
			await earlier;
			(await newer).ShouldHaveSingleItem().Name.ShouldBe("Code 2");
			(await service.DiscoverAsync(false, CancellationToken.None))[0].Name.ShouldBe("Code 2");
		}
		finally
		{
			release.Set();
			await earlier;
		}
	}

	[Test]
	public async Task Open_uses_supplied_current_root_as_one_argument_even_after_discovery_for_another_project()
	{
		using var current = TemporaryDirectory.Create();
		var root = Directory.CreateDirectory(Path.Combine(current.Path, "space & dollar $ root")).FullName;
		ProcessStartInfo? launch = null;
		using var service = new WindowsInstalledIdeService(_ => [Ide("code", "Code", @"C:\Code.exe")],
			_ => null, info => launch = info);
		var ide = (await service.DiscoverAsync(false, CancellationToken.None))[0];

		await service.OpenAsync(ide, root, CancellationToken.None);

		launch.ShouldNotBeNull();
		launch.FileName.ShouldBe(@"C:\Code.exe");
		launch.ArgumentList.ShouldBe([root]);
		launch.UseShellExecute.ShouldBeFalse();
		launch.WorkingDirectory.ShouldBe(root);
	}

	[TestCase(0)]
	[TestCase(2)]
	public async Task VisualStudio_opens_folder_when_solution_is_not_unique(int solutionCount)
	{
		using var temp = TemporaryDirectory.Create();
		for (var i = 0; i < solutionCount; i++)
		{
			await File.WriteAllTextAsync(Path.Combine(temp.Path, $"solution{i}.sln"), string.Empty);
		}
		Directory.CreateDirectory(Path.Combine(temp.Path, "nested"));
		await File.WriteAllTextAsync(Path.Combine(temp.Path, "nested", "ignored.sln"), string.Empty);
		ProcessStartInfo? launch = null;
		using var service = Service(info => launch = info);

		await service.OpenAsync(Ide("visual-studio", "VS", @"C:\devenv.exe"), temp.Path, CancellationToken.None);

		launch.ShouldNotBeNull().ArgumentList.ShouldBe([temp.Path]);
	}

	[TestCase(".sln")]
	[TestCase(".slnx")]
	public async Task VisualStudio_opens_the_only_root_solution(string extension)
	{
		using var temp = TemporaryDirectory.Create();
		var solution = Path.Combine(temp.Path, "Project with space" + extension);
		await File.WriteAllTextAsync(solution, string.Empty);
		ProcessStartInfo? launch = null;
		using var service = Service(info => launch = info);

		await service.OpenAsync(Ide("visual-studio", "VS", @"C:\devenv.exe"), temp.Path, CancellationToken.None);

		launch.ShouldNotBeNull().ArgumentList.ShouldBe([solution]);
	}

	[Test]
	public void Open_rejects_missing_root_without_starting_process()
	{
		using var service = Service(_ => Assert.Fail("Missing root launched"));
		Should.ThrowAsync<DirectoryNotFoundException>(() => service.OpenAsync(Ide("code", "Code", @"C:\Code.exe"),
			Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));
	}

	[Test]
	public void Open_surfaces_launch_failure()
	{
		using var temp = TemporaryDirectory.Create();
		using var service = Service(_ => throw new Win32Exception("start failed"));
		Should.ThrowAsync<Win32Exception>(() => service.OpenAsync(Ide("code", "Code", @"C:\Code.exe"), temp.Path, CancellationToken.None));
	}

	[Test]
	public void Cancellation_prevents_discovery_and_launch()
	{
		using var temp = TemporaryDirectory.Create();
		using var service = new WindowsInstalledIdeService(_ => throw new AssertionException("Discovery ran"),
			_ => null, _ => Assert.Fail("Launch ran"));
		Should.ThrowAsync<OperationCanceledException>(() => service.DiscoverAsync(true, new CancellationToken(true)));
		Should.ThrowAsync<OperationCanceledException>(() => service.OpenAsync(Ide("code", "Code", @"C:\Code.exe"), temp.Path, new CancellationToken(true)));
	}

	private static WindowsInstalledIdeService Service(Action<ProcessStartInfo> start) => new(_ => [], _ => null, start);
	private static InstalledIde Ide(string id, string name, string path) => new(id + ":" + path, name, path, null);
}
