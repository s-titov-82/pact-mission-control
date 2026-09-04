using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Terminal;
using Pact.Presentation.Services;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed record AvaloniaWindowServices(
	AppPaths AppPaths,
	IUiTaskDispatcher UiTaskDispatcher,
	ObservedTaskGroup EventTasks,
	SettingsFileStore SettingsFileStore,
	IFolderPicker FolderPicker,
	IProjectSettingsEditor ProjectSettingsEditor,
	IExternalLauncher ExternalLauncher,
	WindowLayoutStore WindowLayoutStore);

/// <summary>
/// Composes the application shell from DI-owned services and the two hosts owned by
/// <c>MainWindow</c>'s terminal and browser controls.
/// </summary>
internal sealed class AvaloniaShellControllerFactory
{
	private readonly MainWindowViewModel _viewModel;
	private readonly IWebMonitorSnapshotReader _webMonitorSnapshotReader;
	private readonly TerminalCommandResolver _terminalCommandResolver;
	private readonly IGitCliRunner _gitCliRunner;
	private readonly IExecutableLocator _executableLocator;
	private readonly RecentDirectoryStore _recentDirectoryStore;
	private readonly WebMonitorCoordinator _webMonitorCoordinator;
	private readonly ScenarioDefinitionStore _scenarioDefinitionStore;
	private readonly IClipboardService _clipboard;
	private readonly TimeProvider _timeProvider;

	public AvaloniaShellControllerFactory(
		MainWindowViewModel viewModel,
		AvaloniaWindowServices windowServices,
		IWebMonitorSnapshotReader webMonitorSnapshotReader,
		TerminalCommandResolver terminalCommandResolver,
		IGitCliRunner gitCliRunner,
		IExecutableLocator executableLocator,
		RecentDirectoryStore recentDirectoryStore,
		WebMonitorCoordinator webMonitorCoordinator,
		ScenarioDefinitionStore scenarioDefinitionStore,
		IClipboardService clipboard,
		TimeProvider timeProvider)
	{
		_viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		WindowServices =
			windowServices ?? throw new ArgumentNullException(nameof(windowServices));
		_webMonitorSnapshotReader =
			webMonitorSnapshotReader ?? throw new ArgumentNullException(nameof(webMonitorSnapshotReader));
		_terminalCommandResolver =
			terminalCommandResolver ?? throw new ArgumentNullException(nameof(terminalCommandResolver));
		_gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
		_executableLocator =
			executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
		_recentDirectoryStore =
			recentDirectoryStore ?? throw new ArgumentNullException(nameof(recentDirectoryStore));
		_webMonitorCoordinator =
			webMonitorCoordinator ?? throw new ArgumentNullException(nameof(webMonitorCoordinator));
		_scenarioDefinitionStore =
			scenarioDefinitionStore ?? throw new ArgumentNullException(nameof(scenarioDefinitionStore));
		_clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
	}

	internal AvaloniaWindowServices WindowServices { get; }

	/// <summary>
	/// Creates the single window shell while preserving view ownership of native host controls.
	/// </summary>
	public AvaloniaMainShellController Create(
		ITerminalWebViewHost terminalHost,
		IWebPageHostFactory webPageHostFactory)
	{
		ArgumentNullException.ThrowIfNull(terminalHost);
		ArgumentNullException.ThrowIfNull(webPageHostFactory);

		return new AvaloniaMainShellController(
			_viewModel,
			WindowServices.SettingsFileStore,
			WindowServices.AppPaths,
			terminalHost,
			webPageHostFactory,
			_webMonitorSnapshotReader,
			static () => new ConPtyTerminalBackend(),
			_terminalCommandResolver.ResolveCommandLineAsync,
			_gitCliRunner,
			_executableLocator,
			_recentDirectoryStore,
			_webMonitorCoordinator,
			WindowServices.UiTaskDispatcher,
			WindowServices.EventTasks,
			_scenarioDefinitionStore,
			_clipboard,
			_timeProvider);
	}
}
