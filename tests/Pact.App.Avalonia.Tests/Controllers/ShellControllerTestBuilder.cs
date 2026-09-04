using System.Net;
using System.Net.Sockets;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.Tests.Fakes;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.Terminal;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

internal sealed class ShellControllerTestBuilder : IAsyncDisposable
{
	private readonly MainWindowViewModel _viewModel;
	private readonly SettingsFileStore _settingsFileStore;
	private readonly AppPaths _appPaths;
	private readonly ITerminalWebViewHost _terminalHost;
	private readonly Func<ITerminalBackend> _backendFactory;
	private IWebPageHostFactory? _webPageHostFactory;
	private IWebMonitorSnapshotReader _snapshotReader;
	private Func<string, IReadOnlyList<string>, Task<string?>> _resolveCommandAsync =
		(command, arguments) => Task.FromResult<string?>(
			arguments.Count == 0
				? command.Trim()
				: $"{command.Trim()} {string.Join(" ", arguments)}");
	private IGitCliRunner _gitCliRunner = new GitCliRunner();
	private IExecutableLocator _executableLocator = new AvaloniaExecutableLocator();
	private RecentDirectoryStore _recentDirectoryStore;
	private WebMonitorCoordinator? _webMonitorCoordinator;
	private WebMonitorCoordinator? _ownedWebMonitorCoordinator;
	private IUiTaskDispatcher _uiTaskDispatcher = new ImmediateUiTaskDispatcher();
	private ObservedTaskGroup _eventTasks =
		new((_, _) => Task.CompletedTask);
	private ScenarioDefinitionStore _scenarioDefinitionStore;
	private IClipboardService _clipboard = new EmptyClipboardService();
	private TimeProvider _timeProvider = TimeProvider.System;
	private IProcessTreeSnapshotReader? _processTreeSnapshotReader;
	private IWebProcessMetricsSnapshotReader? _webProcessMetricsSnapshotReader;

	public ShellControllerTestBuilder(
		MainWindowViewModel viewModel,
		SettingsFileStore settingsFileStore,
		AppPaths appPaths,
		ITerminalWebViewHost terminalHost,
		Func<ITerminalBackend> backendFactory)
	{
		_viewModel = viewModel;
		_settingsFileStore = settingsFileStore;
		_appPaths = appPaths;
		_terminalHost = terminalHost;
		_backendFactory = backendFactory;
		WebMonitorSnapshotStore snapshotStore = new(appPaths);
		_snapshotReader = new WebMonitorSnapshotReader(snapshotStore);
		_recentDirectoryStore = new RecentDirectoryStore(
			appPaths.RecentDirectoriesPath,
			appPaths.AtomicTempDirectory);
		_scenarioDefinitionStore = new ScenarioDefinitionStore(appPaths.ScenariosPath);
	}

	public ShellControllerTestBuilder WithWebPageHostFactory(IWebPageHostFactory value)
	{
		_webPageHostFactory = value;
		return this;
	}

	public ShellControllerTestBuilder WithSnapshotReader(IWebMonitorSnapshotReader value)
	{
		_snapshotReader = value;
		return this;
	}

	public ShellControllerTestBuilder WithCommandResolver(
		Func<string, IReadOnlyList<string>, Task<string?>> value)
	{
		_resolveCommandAsync = value;
		return this;
	}

	public ShellControllerTestBuilder WithGitCliRunner(IGitCliRunner value)
	{
		_gitCliRunner = value;
		return this;
	}

	public ShellControllerTestBuilder WithExecutableLocator(IExecutableLocator value)
	{
		_executableLocator = value;
		return this;
	}

	public ShellControllerTestBuilder WithRecentDirectoryStore(RecentDirectoryStore value)
	{
		_recentDirectoryStore = value;
		return this;
	}

	public ShellControllerTestBuilder WithWebMonitorCoordinator(WebMonitorCoordinator value)
	{
		_webMonitorCoordinator = value;
		return this;
	}

	public ShellControllerTestBuilder WithUiTaskDispatcher(IUiTaskDispatcher value)
	{
		_uiTaskDispatcher = value;
		return this;
	}

	public ShellControllerTestBuilder WithEventTasks(ObservedTaskGroup value)
	{
		_eventTasks = value;
		return this;
	}

	public ShellControllerTestBuilder WithScenarioDefinitionStore(
		ScenarioDefinitionStore value)
	{
		_scenarioDefinitionStore = value;
		return this;
	}

	public ShellControllerTestBuilder WithClipboard(IClipboardService value)
	{
		_clipboard = value;
		return this;
	}

	public ShellControllerTestBuilder WithTimeProvider(TimeProvider value)
	{
		_timeProvider = value;
		return this;
	}

	public ShellControllerTestBuilder WithProcessTreeSnapshotReader(
		IProcessTreeSnapshotReader value)
	{
		_processTreeSnapshotReader = value;
		return this;
	}

	public ShellControllerTestBuilder WithWebProcessMetricsSnapshotReader(
		IWebProcessMetricsSnapshotReader value)
	{
		_webProcessMetricsSnapshotReader = value;
		return this;
	}

	public AvaloniaMainShellController Build()
	{
		var monitor = _webMonitorCoordinator ??=
			_ownedWebMonitorCoordinator = new WebMonitorCoordinator(
				new WebMonitorSnapshotStore(_appPaths),
				_timeProvider,
				action => action());
		AvaloniaMainShellController controller = new(
			_viewModel,
			_settingsFileStore,
			_appPaths,
			_terminalHost,
			_webPageHostFactory ?? new FakeWebPageHostFactory(),
			_snapshotReader,
			_backendFactory,
			_resolveCommandAsync,
			_gitCliRunner,
			_executableLocator,
			_recentDirectoryStore,
			monitor,
			_uiTaskDispatcher,
			_eventTasks,
			_scenarioDefinitionStore,
			_clipboard,
			_timeProvider,
			FreePort(),
			_processTreeSnapshotReader,
			_webProcessMetricsSnapshotReader);
		if (_webPageHostFactory is null)
		{
			controller.WebPageHostFactory = null;
		}

		return controller;
	}

	public async ValueTask DisposeAsync()
	{
		if (_ownedWebMonitorCoordinator is not null)
		{
			await _ownedWebMonitorCoordinator.DisposeAsync();
		}
	}

	private sealed class EmptyClipboardService : IClipboardService
	{
		public Task<string> GetTextAsync() => Task.FromResult(string.Empty);

		public Task<bool> TrySetTextAsync(string text) => Task.FromResult(true);
	}

	internal static int FreePort()
	{
		using TcpListener listener = new(IPAddress.Loopback, 0);
		listener.Start();
		return ((IPEndPoint)listener.LocalEndpoint).Port;
	}
}
