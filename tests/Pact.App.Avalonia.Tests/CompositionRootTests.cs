using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Platform;
using Pact.Core.Platform;
using Pact.Core.RootTabs;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;

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
		assembly.GetName().Version.ShouldBe(new Version(0, 1, 0, 0));
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
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
