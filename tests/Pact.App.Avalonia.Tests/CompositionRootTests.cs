using System.Reflection;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Platform;
using Pact.Core.Platform;
using Pact.Core.RootTabs;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Settings;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Updates;

namespace Pact.App.Avalonia.Tests;

public sealed class CompositionRootTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _root => _temporaryDirectory.Path;

	[Test]
	public void ProductAssemblyCarriesPublicDistributionMetadata()
	{
		var assembly = typeof(App).Assembly;

		assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product
			.ShouldBe("PACT:> Mission Control");
		assembly.GetCustomAttribute<AssemblyCopyrightAttribute>()!.Copyright
			.ShouldBe("Copyright (c) 2026 Sergei Titov");
		assembly.GetCustomAttribute<AssemblyDescriptionAttribute>()!.Description
			.ShouldBe("Windows mission control for persistent terminal-based AI agent sessions.");
		assembly.GetName().Version.ShouldBe(new Version(0, 1, 6, 0));
	}

	[Test]
	public async Task BuildServiceProvider_uses_preview_root_and_Avalonia_adapters()
	{
		await using var services = CompositionRoot.BuildServiceProvider(
			new AppDataProfile("test-preview", _root));

		services.GetRequiredService<AppPaths>().RootDirectory.ShouldBe(_root);
		services.GetRequiredService<IClipboardService>().ShouldBeOfType<AvaloniaClipboardService>();
		services.GetRequiredService<IFolderPicker>().ShouldBeOfType<AvaloniaFolderPicker>();
		services.GetRequiredService<IExecutableLocator>().ShouldBeOfType<AvaloniaExecutableLocator>();
		services.GetRequiredService<TerminalCommandResolver>().ShouldNotBeNull();
		services.GetRequiredService<WebMonitorSnapshotStore>().ShouldNotBeNull();
		services.GetRequiredService<WebMonitorCoordinator>().ShouldNotBeNull();
		services.GetRequiredService<ScenarioDefinitionStore>().ShouldNotBeNull();
		services.GetRequiredService<IGitHubReleaseClient>()
			.ShouldBeOfType<GitHubReleaseClient>();
		services.GetRequiredService<UpdateCoordinator>()
			.ShouldBeSameAs(services.GetRequiredService<UpdateCoordinator>());
		services.GetRequiredService<IRootTabsStore>().ShouldBeOfType<JsonRootTabsStore>();
		services.GetRequiredService<IProjectSettingsEditor>()
			.ShouldBeAssignableTo<IRootTabsSettingsEditor>();
	}

	[Test]
	public void BuildServiceProvider_does_not_run_startup_housekeeping()
	{
		var stalePath = Path.Combine(_root, "Temp", "stale", "payload.tmp");
		Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
		File.WriteAllText(stalePath, "stale");

		using var services = CompositionRoot.BuildServiceProvider(
			new AppDataProfile("test-preview", _root));

		File.Exists(stalePath).ShouldBeTrue();
	}

	[Test]
	public void Startup_housekeeping_clears_legacy_temp_children_and_preserves_retained_temp()
	{
		var legacyPath = Path.Combine(_root, "Temp", "stale", "payload.tmp");
		var legacyAtomicPath = Path.Combine(_root, "Temp", "atomic", "write.tmp");
		var retainedPath = Path.Combine(_root, "Temp", "Retained", "keep.tmp");
		Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(legacyAtomicPath)!);
		Directory.CreateDirectory(Path.GetDirectoryName(retainedPath)!);
		File.WriteAllText(legacyPath, "stale");
		File.WriteAllText(legacyAtomicPath, "stale");
		File.WriteAllText(retainedPath, "retained");

		AppPaths paths = new(_root);
		AppStartupHousekeeping.Run(paths);
		File.Exists(legacyPath).ShouldBeFalse();
		File.Exists(legacyAtomicPath).ShouldBeFalse();
		File.ReadAllText(retainedPath).ShouldBe("retained");
		Directory.EnumerateFileSystemEntries(paths.SessionTempDirectory).ShouldBeEmpty();
	}

	[Test]
	public void Startup_housekeeping_expires_old_logs_without_requiring_a_new_log_event()
	{
		var oldLog = Path.Combine(_root, "Logs", "pact-2026-01-01.0.log");
		Directory.CreateDirectory(Path.GetDirectoryName(oldLog)!);
		File.WriteAllText(oldLog, "old");
		File.SetLastWriteTimeUtc(oldLog, DateTime.UtcNow - TimeSpan.FromDays(4));

		AppStartupHousekeeping.Run(new AppPaths(_root));

		File.Exists(oldLog).ShouldBeFalse();
	}

	[Test]
	public void Startup_housekeeping_removes_only_consumed_update_handoffs()
	{
		AppPaths paths = new(_root);
		var consumed = Path.Combine(paths.UpdateHandoffsDirectory, new string('a', 64));
		var active = Path.Combine(paths.UpdateHandoffsDirectory, new string('b', 64));
		Directory.CreateDirectory(consumed);
		Directory.CreateDirectory(active);
		File.WriteAllText(Path.Combine(consumed, "Pact.Updater.exe"), "stale");
		File.WriteAllText(Path.Combine(consumed, "setup.log"), "stale");
		File.WriteAllText(Path.Combine(active, "Pact.Updater.exe"), "active");
		File.WriteAllText(Path.Combine(active, "update-resume.json"), "active");

		AppStartupHousekeeping.Run(paths);

		Directory.Exists(consumed).ShouldBeFalse();
		File.Exists(Path.Combine(active, "Pact.Updater.exe")).ShouldBeTrue();
		File.Exists(Path.Combine(active, "update-resume.json")).ShouldBeTrue();
	}

	[Test]
	public void Startup_housekeeping_never_follows_a_handoff_reparse_point()
	{
		AppPaths paths = new(_root);
		var outside = Path.Combine(_root, "outside-handoff");
		var link = Path.Combine(paths.UpdateHandoffsDirectory, new string('c', 64));
		Directory.CreateDirectory(outside);
		Directory.CreateDirectory(paths.UpdateHandoffsDirectory);
		var outsideHelper = Path.Combine(outside, "Pact.Updater.exe");
		var outsideLog = Path.Combine(outside, "setup.log");
		File.WriteAllText(outsideHelper, "outside");
		File.WriteAllText(outsideLog, "outside");
		try
		{
			Directory.CreateSymbolicLink(link, outside);
		}
		catch (Exception exception) when (exception is UnauthorizedAccessException
			or PlatformNotSupportedException
			or IOException)
		{
			ProcessStartInfo startInfo = new(Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			startInfo.ArgumentList.Add("/d");
			startInfo.ArgumentList.Add("/c");
			startInfo.ArgumentList.Add("mklink");
			startInfo.ArgumentList.Add("/J");
			startInfo.ArgumentList.Add(link);
			startInfo.ArgumentList.Add(outside);
			using var junction = Process.Start(startInfo)
				?? throw new InvalidOperationException("Junction helper did not start.", exception);
			junction.WaitForExit();
			junction.ExitCode.ShouldBe(0, $"Could not create a test junction: {exception.Message}");
		}

		AppStartupHousekeeping.Run(paths);

		File.Exists(outsideHelper).ShouldBeTrue();
		File.Exists(outsideLog).ShouldBeTrue();
		Directory.Exists(link).ShouldBeTrue();
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
