using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.Core.Platform;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia;

internal static class CompositionRoot
{
	public static ServiceProvider BuildServiceProvider(AppDataProfile profile)
	{
		ArgumentNullException.ThrowIfNull(profile);
		ServiceCollection services = new();
		AppPaths appPaths = new(profile.RootDirectory);

		services.AddSingleton(profile);
		services.AddSingleton(appPaths);
		services.AddSingleton<IProjectStore, JsonProjectStore>();
		services.AddSingleton<IRootTabsStore, JsonRootTabsStore>();
		services.AddSingleton<IProjectNotesStore, ProjectNotesStore>();
		services.AddSingleton<IProjectMarkdownFileStore, ProjectMarkdownFileStore>();
		services.AddSingleton<IUiTaskDispatcher, UiTaskDispatcher>();
		services.AddSingleton<TimeProvider>(TimeProvider.System);
		services.AddSingleton(provider => new ObservedTaskGroup(
			(operationName, exception) => AppLog.AppendAsync(
				provider.GetRequiredService<AppPaths>().RootDirectory,
				$"Asynchronous event operation failed: {operationName}",
				exception)));
		services.AddSingleton(provider => new TerminalTabStatusCoordinator(
			provider.GetRequiredService<IUiTaskDispatcher>().Post));
		services.AddSingleton<WebMonitorSnapshotStore>();
		services.AddSingleton<IWebMonitorSnapshotReader, WebMonitorSnapshotReader>();
		services.AddSingleton(provider => new WebMonitorCoordinator(
			provider.GetRequiredService<WebMonitorSnapshotStore>(),
			TimeProvider.System,
			provider.GetRequiredService<IUiTaskDispatcher>().Post));
		services.AddSingleton<MainWindowViewModel>();
		services.AddSingleton<SettingsFileStore>();
		services.AddSingleton(provider =>
			new ScenarioDefinitionStore(provider.GetRequiredService<AppPaths>().ScenariosPath));
		services.AddSingleton<WindowLayoutStore>();
		services.AddSingleton(provider =>
			new AppearanceSettingsStore(
				provider.GetRequiredService<AppPaths>().AppearancePath,
				provider.GetRequiredService<AppPaths>().AtomicTempDirectory));
		services.AddSingleton(provider =>
			new RecentDirectoryStore(
				provider.GetRequiredService<AppPaths>().RecentDirectoriesPath,
				provider.GetRequiredService<AppPaths>().AtomicTempDirectory));
		services.AddSingleton(provider =>
			new ExternalGitHelperResolver(
				provider.GetRequiredService<AppPaths>().GitHelpersPath,
				provider.GetRequiredService<IExecutableLocator>()));
		services.AddSingleton(_ => new SubscriptionUsageRefreshService(
			LocalSubscriptionUsageReader.ForCurrentUser()));
		services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
		services.AddSingleton<IFolderPicker, AvaloniaFolderPicker>();
		services.AddSingleton<IExternalLauncher, AvaloniaExternalLauncher>();
		services.AddSingleton<IExecutableLocator, AvaloniaExecutableLocator>();
		services.AddSingleton<IProjectSettingsEditor, ProjectSettingsEditor>();
		services.AddSingleton(provider =>
			new TerminalCommandResolver(provider.GetRequiredService<IExecutableLocator>()));
		services.AddSingleton<IGitCliRunner, GitCliRunner>();
		services.AddSingleton<AvaloniaWindowServices>();
		services.AddSingleton<AvaloniaShellControllerFactory>();

		return services.BuildServiceProvider();
	}
}
