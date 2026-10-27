using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Web;
using Pact.Core.Agents;
using Pact.Core.Git;
using Pact.Core.Orchestrator;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Core.Updates;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Reviews;
using Pact.Infrastructure.Orchestrator;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Diagnostics;
using Pact.Infrastructure.Updates;
using Pact.Presentation.Services;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.Services.Orchestrator;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AvaloniaMainShellController : INotifyPropertyChanged, IAsyncDisposable
{
	private static readonly TimeSpan NativeCopyWindow = TimeSpan.FromSeconds(1);
	private static readonly TimeSpan NativeCopyPollInterval = TimeSpan.FromMilliseconds(50);
	private readonly SettingsFileStore _settingsFileStore;
	private readonly AppPaths _appPaths;
	private readonly IWebMonitorSnapshotReader _webMonitorSnapshotReader;
	private readonly WebMonitorCoordinator _webMonitorCoordinator;
	private readonly ITerminalWebViewHost _terminalHost;
	private readonly Func<string, IReadOnlyList<string>, Task<string?>> _resolveCommandAsync;
	private readonly SessionRuntimeCoordinator _runtimeCoordinator;
	private readonly AvaloniaWebPageCoordinator _webPageCoordinator;
	private readonly WebViewDiagnosticTrace _diagnostics = new("controller");
	private readonly IGitCliRunner _gitCliRunner;
	private readonly IExecutableLocator _executableLocator;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly TimeProvider _timeProvider;
	private readonly Dictionary<string, GitPanelViewModel> _gitPanelViewModels = new(StringComparer.Ordinal);
	private readonly RecentDirectoryStore _recentDirectoryStore;
	private readonly ReviewProfileProvider _reviewProfileProvider;
	private readonly AgentControlTokenRegistry _agentControlTokens;
	private readonly AgentControlEndpoint _agentControlEndpoint;
	private readonly SessionLauncher _launcher;
	private readonly OrchestratorStore _orchestratorStore;
	private readonly OrchestratorDispatcher _orchestratorDispatcher;
	private readonly OrchestratorSupervisor _orchestrator;
	private readonly ReviewLogWriter _reviewLog = new();
	private readonly ReviewRelay _reviewRelay;
	/// <summary>The projects' remarks, shared by the Changes tabs and the agent tools.</summary>
	internal RemarkBooks RemarkBooks { get; }
	private readonly ResumeCaptureService _resumeCapture;
	private IReadOnlyList<ResolvedGitHelperAction> _resolvedGitHelperActions = [];
	private GitButtonCommandSet _gitButtonCommands = GitButtonCommandSet.Create(null);
	private ExternalGitHelperResolver? _gitHelperResolver;
	private bool _disposed;
	private int _acceptingInput = 1;
	private int _eventProducersAttached = 1;
	private readonly Lock _shutdownGate = new();
	private bool _shutdownBegun;
	private Task? _eventDrainTask;
	private Task? _shutdownTask;
	private SelectionActionSnapshot? _selectionSnapshot;
	private int _selectionCaptureVersion;
	private int _rightClickPasteInProgress;
	private bool _webMonitorStartupRulesApplied;
	private bool _webMonitorWindowVisible;
	private bool _webMonitorWindowActive;
	private bool _terminalHostInitialized;
	private INotifyPropertyChanged? _selectedDetailsSource;
	private object? _selectedTabDetailsOwner;
	private readonly SelectedProcessMetricsMonitor _processMetricsMonitor;
	private readonly SelectedWebProcessMetricsMonitor _webProcessMetricsMonitor;
	private bool _externalProcessMetricsEnabled;
	private readonly ISoftRestartTicketStore? _softRestartTicketStore;
	private readonly AppLaunchOptions? _launchOptions;
	private readonly SoftRestartRestorer? _softRestartRestorer;

	internal AvaloniaMainShellController(
		MainWindowViewModel viewModel,
		SettingsFileStore settingsFileStore,
		AppPaths appPaths,
		ITerminalWebViewHost terminalHost,
		IWebPageHostFactory webPageHostFactory,
		IWebMonitorSnapshotReader webMonitorSnapshotReader,
		Func<ITerminalBackend> backendFactory,
		Func<string, IReadOnlyList<string>, Task<string?>> resolveCommandAsync,
		IGitCliRunner gitCliRunner,
		IExecutableLocator executableLocator,
		RecentDirectoryStore recentDirectoryStore,
		WebMonitorCoordinator webMonitorCoordinator,
		IUiTaskDispatcher uiTaskDispatcher,
		ObservedTaskGroup eventTasks,
		IClipboardService clipboard,
		TimeProvider timeProvider,
		int? agentControlPort = null,
		IProcessTreeSnapshotReader? processTreeSnapshotReader = null,
		IWebProcessMetricsSnapshotReader? webProcessMetricsSnapshotReader = null,
		AvaloniaUpdateController? updateController = null,
		AppLaunchOptions? launchOptions = null,
		ISoftRestartTicketStore? softRestartTicketStore = null)
	{
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_settingsFileStore = settingsFileStore ?? throw new ArgumentNullException(nameof(settingsFileStore));
		_appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
		_reviewProfileProvider = new ReviewProfileProvider(_appPaths.ReviewProfilesPath);
		_terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
		if (terminalHost is AvaloniaTerminalWebViewHost avaloniaTerminalHost)
		{
			avaloniaTerminalHost.SlowOperationLog = AppendDiagnosticLog;
		}
		ArgumentNullException.ThrowIfNull(backendFactory);
		_resolveCommandAsync = resolveCommandAsync ?? throw new ArgumentNullException(nameof(resolveCommandAsync));
		_runtimeCoordinator = new SessionRuntimeCoordinator(
			terminalHost,
			() => new TerminalController(backendFactory()));
		_gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
		WorkingTreeReader = new GitWorkingTreeReader(_gitCliRunner);
		_executableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
		_runtimeCoordinator.SessionFacts = new ShellSessionFacts(
			ReadScreenState,
			StartSessionByIdAsync);
		_runtimeCoordinator.DeliveryTrace = AppendDiagnosticLog;
		var effectiveProcessTreeSnapshotReader =
			processTreeSnapshotReader ?? new ProcessTreeSnapshotReader();
		_processMetricsMonitor = new SelectedProcessMetricsMonitor(
			effectiveProcessTreeSnapshotReader,
			_timeProvider);
		_processMetricsMonitor.MetricsChanged += OnProcessMetricsChanged;
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_webMonitorSnapshotReader =
			webMonitorSnapshotReader ?? throw new ArgumentNullException(nameof(webMonitorSnapshotReader));
		_webMonitorCoordinator =
			webMonitorCoordinator ?? throw new ArgumentNullException(nameof(webMonitorCoordinator));
		_webPageCoordinator = new AvaloniaWebPageCoordinator(_webMonitorCoordinator);
		_webProcessMetricsMonitor = new SelectedWebProcessMetricsMonitor(
			webProcessMetricsSnapshotReader ?? new WebProcessMetricsSnapshotReader(
				pageId => _webPageCoordinator.TryGetHost(pageId, out var host)
					? host as IWebPageProcessAttributionSource
					: null,
				new ProcessSetSnapshotReader(),
				effectiveProcessTreeSnapshotReader),
			_timeProvider,
			reportFailureAsync: exception => AppLog.AppendAsync(
				_appPaths.RootDirectory,
				"Web process metrics failed",
				exception));
		_webProcessMetricsMonitor.MetricsChanged += OnWebProcessMetricsChanged;
		_webPageCoordinator.HostFactory =
			webPageHostFactory ?? throw new ArgumentNullException(nameof(webPageHostFactory));
		_webPageCoordinator.SourceChanged += OnWebPageSourceChanged;
		_webPageCoordinator.TitleChanged += OnWebPageTitleChanged;
		_webPageCoordinator.NavigationStateChanged += OnWebPageNavigationStateChanged;
		_webPageCoordinator.NavigationFailed += OnWebPageNavigationFailed;
		_webPageCoordinator.NewWindowRequested += OnWebPageNewWindowRequested;
		_webPageCoordinator.StableUrlChanged += OnWebMonitorStableUrlChanged;
		_webMonitorCoordinator.LiveDiagnosticsChanged += OnWebMonitorLiveDiagnosticsChanged;
		_recentDirectoryStore =
			recentDirectoryStore ?? throw new ArgumentNullException(nameof(recentDirectoryStore));
		_agentControlTokens = new AgentControlTokenRegistry();
		_orchestratorStore = new OrchestratorStore(_appPaths.OrchestratorPath);
		_launcher = new SessionLauncher(
			_runtimeCoordinator,
			_terminalHost,
			ViewModel,
			_agentControlTokens,
			_appPaths,
			GetAgentControlAddress,
			_settingsFileStore.ReadAgentControlEnabled(),
			_resolveCommandAsync,
			_uiTaskDispatcher,
			_eventTasks,
			OnOutputReceived,
			ResetSnapshotBaselineSafeAsync,
			OnSessionProcessDied,
			ReportStatusAsync);
		_orchestrator = new OrchestratorSupervisor(
			_runtimeCoordinator,
			_terminalHost,
			ViewModel,
			_launcher,
			_agentControlTokens,
			GetAgentControlAddress,
			_resolveCommandAsync,
			_timeProvider,
			_eventTasks,
			ReportStatusAsync,
			IsShutdownBegun,
			() => _terminalHostInitialized);
		_reviewRelay = new ReviewRelay(
			new ReviewRelayHost(
				ViewModel,
				_runtimeCoordinator,
				_terminalHost,
				_launcher,
				_uiTaskDispatcher,
				_settingsFileStore.ReadAgentControlEnabled(),
				session => CloseSessionAsync(session),
				OpenProjectDocumentAsync,
				AppendDiagnosticLog),
			() => ViewModel.ScenarioDefinitionsSnapshot,
			() => _reviewProfileProvider.Current,
			_reviewLog,
			_timeProvider);
		// Badges read the relay when they run, so posts that pile up all show the latest state.
		_reviewRelay.Changed += (_, _) => Dispatcher.UIThread.Post(ShowReviewBadges);
		_resumeCapture = new ResumeCaptureService(
			_runtimeCoordinator,
			ViewModel,
			_timeProvider,
			ShowSessionTerminalUnderBusyAsync,
			ReportStatusAsync);
		RemarkBooks = new RemarkBooks(_timeProvider);
		AgentControlHost agentControlHost = new(ViewModel, _uiTaskDispatcher, OpenProjectDocumentAsync);
		AgentControlDispatcher agentControlDispatcher = new(agentControlHost);
		OrchestratorHost orchestratorHost = new(
			ViewModel,
			_uiTaskDispatcher,
			IsLiveSession,
			_orchestrator.SendMessageAsync,
			_webPageCoordinator.ResumeInBackgroundAsync,
			_webPageCoordinator.ReadDocumentHtmlAsync,
			() => _orchestrator.Session is null ? null : OrchestratorSupervisor.SessionId,
			_runtimeCoordinator.StartAndWaitReadyAsync,
			_reviewRelay);
		_orchestratorDispatcher = new OrchestratorDispatcher(orchestratorHost);
		AgentControlToolRouter toolRouter = new(
			SessionTools.Create(
				agentControlDispatcher,
				_reviewRelay,
				() => ViewModel.ScenarioDefinitionsSnapshot,
				() => _reviewProfileProvider.Current,
				status => _uiTaskDispatcher.InvokeAsync(() => ReportStatusAsync(status)),
				_orchestratorDispatcher.GetSubscriptionUsage,
				new RemarkTools(
					agentControlDispatcher,
					RemarkBooks,
					ReadHeadCommitAsync,
					status => _uiTaskDispatcher.InvokeAsync(() => ReportStatusAsync(status)))),
			OrchestratorTools.Create(_orchestratorDispatcher));
		AgentControlJsonRpc agentControlRpc = new(
			caller => toolRouter.BuildToolsList(caller.IsOrchestrator),
			toolRouter.InvokeAsync);
		_agentControlEndpoint = new AgentControlEndpoint(_agentControlTokens, agentControlRpc);
		AgentControlAddress = _agentControlEndpoint.Start(
			agentControlPort ?? _settingsFileStore.ReadAgentControlPort());
		_terminalHost.InputReceived += OnInputReceived;
		_terminalHost.ResizeReceived += OnResizeReceived;
		_terminalHost.ScreenSnapshotReceived += OnScreenSnapshotReceived;
		_terminalHost.SelectionChanged += OnSelectionChanged;
		_terminalHost.SelectionCompleted += OnSelectionCompleted;
		_terminalHost.SelectionDismissed += OnSelectionDismissed;
		_terminalHost.AgentMouseReleased += OnAgentMouseReleased;
		_terminalHost.LinkRequested += OnTerminalLinkRequested;
		_terminalHost.BusyOverlayActionRequested += OnBusyOverlayActionRequested;
		_terminalHost.PasteRequested += OnPasteRequested;
		_terminalHost.CopyRequested += OnCopyRequested;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		ViewModel.TerminalTabStatuses.DiagnosticsChanged += OnTerminalDiagnosticsChanged;
		RebindSelectedDetailsSource();
		Clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
		_runtimeCoordinator.ClientPasteClipboard = Clipboard;
		UpdateController = updateController;
		_launchOptions = launchOptions;
		_softRestartTicketStore = softRestartTicketStore;
		if (launchOptions is not null && softRestartTicketStore is not null)
		{
			SoftRestartSafetyPolicy safetyPolicy = new(
				_runtimeCoordinator.GetActiveSessionIds,
				ViewModel.TerminalTabStatuses.GetDiagnosticsSnapshot,
				() => _reviewRelay.ListActive().Count > 0,
				IsShutdownBegun);
			SoftRestartSnapshotBuilder snapshotBuilder = new(
				ViewModel,
				_runtimeCoordinator.GetActiveSessionIds,
				ViewModel.TerminalTabStatuses.GetDiagnosticsSnapshot,
				_webPageCoordinator.GetLoadedPageIds);
			SoftRestartCoordinator = new SoftRestartCoordinator(
				launchOptions,
				softRestartTicketStore,
				safetyPolicy,
				snapshotBuilder);
			updateController?.ConfigureRestart(
				safetyPolicy.Evaluate,
				SoftRestartCoordinator.RequestApplyUpdateAsync);
			_softRestartRestorer = new SoftRestartRestorer(
				FindRestorableSession,
				StartRestoredSessionAsync,
				FindRestorableWebPage,
				_webPageCoordinator.ResumeInBackgroundAsync,
				ViewModel.TerminalTabStatuses.RestoreUnreadCompletion,
				_orchestrator.RestoreAsync,
				RestoreSelectionAsync,
				id => string.Equals(id, OrchestratorSupervisor.SessionId, StringComparison.Ordinal),
				_timeProvider);
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public event EventHandler<string>? StatusMessage;
	public event EventHandler? BusyOverlayActionRequested;
	/// <summary>Reports whether the selected terminal must remain covered until its first output reaches xterm.</summary>
	public event EventHandler<bool>? TerminalLoadingChanged;

	public MainWindowViewModel ViewModel { get; }
	internal AvaloniaUpdateController? UpdateController { get; }
	internal SoftRestartCoordinator? SoftRestartCoordinator { get; }
	internal RestorationSummary? LastRestorationSummary { get; private set; }
	public IReadOnlyDictionary<string, SessionRuntime> Runtimes => _runtimeCoordinator.Runtimes;
	internal Uri AgentControlAddress { get; }
	private Uri GetAgentControlAddress() => AgentControlAddress;
	internal WebViewDiagnosticEntry[] DiagnosticSnapshot => _diagnostics.Snapshot();
	internal ObservedTaskGroup GetEventTasks() => _eventTasks;
	internal IUiTaskDispatcher GetUiTaskDispatcher() => _uiTaskDispatcher;
	internal SelectionActionAnchor? SelectionActionsAnchor
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	internal Task ReportUiFailureAsync(string owner, Exception exception)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(owner);
		ArgumentNullException.ThrowIfNull(exception);
		return ReportStatusAsync($"{owner} action failed: {exception.Message}");
	}

	internal async Task ReportDocumentSaveFailureAsync(Exception exception)
	{
		ArgumentNullException.ThrowIfNull(exception);
		await AppLog.AppendAsync(
			_appPaths.RootDirectory,
			"Markdown autosave failed",
			exception);
		await ReportUiFailureAsync("Notes", exception);
	}
	internal IWebPageHost? GetWebPageHostForDiagnostics(string id) =>
		_webPageCoordinator.TryGetHost(id, out var host) ? host : null;
	public IClipboardService Clipboard { get; }
	public IWebPageHostFactory? WebPageHostFactory
	{
		get => _webPageCoordinator.HostFactory;
		set => _webPageCoordinator.HostFactory = value;
	}

	/// <summary>
	/// Set by MainWindow to re-publish visible and active window facts before each screen snapshot is evaluated.
	/// </summary>
	public Action? RefreshWindowFacts { get; set; }

	public IReadOnlyList<WebLinkTemplateRecord> WebLinkTemplates { get; private set; } = [];
	/// <summary>What the center pane shows; assigned whole through <see cref="Present"/>.</summary>
	internal PresentedSurface PresentedSurface { get; private set; } = new PresentedSurface.None();
	public ProjectNoteDocument? CurrentNoteDocument => (PresentedSurface as PresentedSurface.Notes)?.Document;
	/// <summary>Current project documentation workspace displayed in the center pane.</summary>
	public DocsAndNotesWorkspaceViewModel? CurrentDocsAndNotes => (PresentedSurface as PresentedSurface.Notes)?.Workspace;
	public GitPanelViewModel? CurrentGitPanel { get; private set { if (ReferenceEquals(field, value)) { return; } field = value; OnPropertyChanged(); } }
	public bool IsTerminalVisible => PresentedSurface is PresentedSurface.Terminal;
	/// <summary>
	/// Whether the selected ROOT item is intentionally paused and should show the paused
	/// placeholder instead of starting or loading its surface.
	/// </summary>
	public bool IsPausedItemVisible =>
		PresentedSurface is PresentedSurface.PausedSession or PresentedSurface.PausedWebPage;
	public bool IsSelectionActionsOpen { get; private set { if (field == value) { return; } field = value; OnPropertyChanged(); } }
	internal bool IsNotesSelectionSource =>
		_selectionSnapshot?.Source.Kind == SelectionActionSourceKind.Notes;
	/// <summary>Transient action or error text, kept separate from selected-tab facts.</summary>
	public string? StatusText { get; private set { if (field == value) { return; } field = value; OnPropertyChanged(); } }

	/// <summary>Runtime facts for the selected terminal or web tab, including opt-in process metrics.</summary>
	public SelectedTabDetailsViewModel? SelectedTabDetails
	{
		get;
		private set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Enables or disables opt-in process metrics for the selected live tab.</summary>
	internal void SetExternalProcessMetricsEnabled(bool enabled)
	{
		if (_externalProcessMetricsEnabled == enabled)
		{
			return;
		}

		_externalProcessMetricsEnabled = enabled;
		RefreshSelectedTabDetails();
	}

	public async Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _settingsFileStore.EnsureDefaultFilesAsync(cancellationToken);
		try
		{
			_launcher.SkillPublication =
				await new PactSkillPublisher(_appPaths).PublishAsync(cancellationToken);
		}
		catch (Exception exception)
		{
			_launcher.SkillPublication = PactSkillPublication.Empty;
			RecordDiagnostic(
				"pact-skill-publication-failed",
				$"category={exception.GetType().Name}");
		}

		await ViewModel.LoadAsync(cancellationToken);
		await RestoreRetainedWebMonitorStateAsync(cancellationToken);
		await CleanupAbandonedReviewExchangesAsync(cancellationToken);
		await ReloadExternalSettingsAsync(cancellationToken);
		await _terminalHost.InitializeAsync(terminalPage, cancellationToken);
		_terminalHostInitialized = true;
		if (!await TryRestoreSoftRestartAsync(cancellationToken))
		{
			if (_orchestrator.Record.Enabled
				&& _orchestrator.Record.IsProvisioned
				&& !IsOrchestratorRunning)
			{
				await StartOrchestratorAsync(cancellationToken);
			}
			if (ViewModel.SelectedSession is { } selected)
			{
				await SelectSessionAsync(selected, startIfNeeded: true, cancellationToken: cancellationToken);
			}
			else if (ViewModel.SelectedWebPage is { } webPage)
			{
				await SelectWebPageAsync(webPage, cancellationToken);
			}
			if (ViewModel.SelectedWorkspace is { } workspace)
			{
				await SelectWorkspaceAsync(workspace);
			}
		}
	}

	private async Task RestoreRetainedWebMonitorStateAsync(
		CancellationToken cancellationToken)
	{
		var pages = ViewModel.Workspaces
			.Concat(ViewModel.PausedWorkspaces)
			.SelectMany(workspace => workspace.WebPages)
			.Concat(ViewModel.RootTabs.WebPages)
			.ToArray();
		try
		{
			await _webMonitorSnapshotReader.SweepAsync(
				pages
					.Select(page => page.Record.Id)
					.ToHashSet(StringComparer.Ordinal),
				cancellationToken);
		}
		catch (Exception exception) when (
			IsExpectedWebMonitorSnapshotRestoreFailure(exception))
		{
			RecordDiagnostic(
				"web-monitor-snapshot-sweep-failed",
				$"category={WebMonitorSnapshotFailureCategory(exception)}");
		}

		foreach (var page in pages)
		{
			cancellationToken.ThrowIfCancellationRequested();
			WebMonitorSnapshot? snapshot = null;
			try
			{
				snapshot = await _webMonitorSnapshotReader
					.LoadAsync(page.Record.Id, cancellationToken);
			}
			catch (Exception exception) when (
				IsExpectedWebMonitorSnapshotRestoreFailure(exception))
			{
				RecordDiagnostic(
					"web-monitor-snapshot-load-failed",
					$"page={page.Record.Id};"
					+ $"category={WebMonitorSnapshotFailureCategory(exception)}");
			}

			page.SetMonitorUnread(snapshot?.Unread == true);
		}
	}

	private async Task<bool> TryRestoreSoftRestartAsync(CancellationToken cancellationToken)
	{
		if (_launchOptions?.SoftRestartId is not { } restartId
			|| _softRestartTicketStore is null
			|| _softRestartRestorer is null)
		{
			return false;
		}

		var ticket = await _softRestartTicketStore.ConsumeExactAsync(
			restartId,
			RunningPactVersion.Read(typeof(App).Assembly),
			cancellationToken);
		if (ticket is null)
		{
			return false;
		}

		LastRestorationSummary = await _softRestartRestorer.RestoreAsync(
			ticket,
			cancellationToken);
		return true;
	}

	private static bool IsExpectedWebMonitorSnapshotRestoreFailure(
		Exception exception) =>
		exception is IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException
			or System.Text.Json.JsonException
			or NotSupportedException
			or ArgumentException;

	private static string WebMonitorSnapshotFailureCategory(
		Exception exception) =>
		exception switch
		{
			UnauthorizedAccessException => "access",
			System.Security.SecurityException => "security",
			IOException => "io",
			System.Text.Json.JsonException
				or NotSupportedException
				or ArgumentException => "data",
			_ => "unknown"
		};

	public Task SelectItemAsync(object? item, CancellationToken cancellationToken = default) => item switch
	{
		SessionViewModel session => SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken),
		WebPageViewModel webPage => SelectWebPageAsync(webPage, cancellationToken),
		ProjectNoteViewModel note => SelectNoteAsync(note, cancellationToken),
		WorkspaceViewModel workspace => SelectProjectRowAsync(workspace, cancellationToken),
		_ => Task.CompletedTask
	};

	// A user click on a project row: the git popup's selection plus the project's screen.
	private async Task SelectProjectRowAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken)
	{
		await SelectWorkspaceAsync(workspace);
		await OpenProjectScreenAsync(workspace, ProjectScreenTab.Overview, cancellationToken);
	}

	public async Task SelectSessionAsync(
		SessionViewModel session,
		bool startIfNeeded,
		bool preferResumeCommand = true,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ThrowIfRejectingInput();
		SessionSwitchTiming timing = new();
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		timing.Mark("note");
		await HideActiveWebPageAsync(cancellationToken);
		timing.Mark("web");
		if (session.IsManuallyPaused)
		{
			ViewModel.SelectedSession = session;
			await ViewModel.SetActiveItemAsync(session.Record.Id, cancellationToken);
			Present(new PresentedSurface.PausedSession(session));
			return;
		}

		var startsRuntime = startIfNeeded
			&& !_runtimeCoordinator.TryGetActiveController(
				session.Record.Id,
				out _,
				out _);
		Present(new PresentedSurface.Terminal(session, Loading: startsRuntime, FocusWhenLoaded: false));

		await _runtimeCoordinator.ActivateSessionAsync(
			session,
			async (target, ct) =>
			{
				timing.Mark("gate");
				await PrepareSessionAsync(target, ct);
				timing.Mark("prepare");
			},
			static (_, _) => Task.FromResult<IDisposable?>(null),
			async (target, ct) =>
			{
				timing.Mark("show");
				await _launcher.ActivateAsync(target, startIfNeeded, preferResumeCommand, ct);
				timing.Mark("runtime");
			},
			async _ =>
			{
				// A starting terminal is hidden behind the loading surface and cannot take
				// focus yet; it is focused when that surface clears.
				if (PresentedSurface is PresentedSurface.Terminal terminal
					&& ReferenceEquals(terminal.Session, session))
				{
					Present(terminal with { FocusWhenLoaded = terminal.Loading });
				}

				await _terminalHost.FocusAsync();
				timing.Mark("focus");
			},
			(target, ct) => ViewModel.UpdateSessionStatusAsync(target.Record.Id, SessionStatus.Failed, ct),
			cancellationToken);
		LogSlowSessionSwitch(session, timing, startsRuntime);
		if (startsRuntime
			&& !_runtimeCoordinator.TryGetActiveController(
				session.Record.Id,
				out _,
				out _))
		{
			ClearTerminalLoading(session.Record.Id);
		}
	}

	public async Task SelectWebPageAsync(WebPageViewModel webPage, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		ViewModel.SelectedWebPage = webPage;
		await ViewModel.SetActiveItemAsync(webPage.Record.Id, cancellationToken);
		if (webPage.IsManuallyPaused)
		{
			Present(new PresentedSurface.PausedWebPage(webPage));
			return;
		}

		// NativeWebView must enter the visual tree at a non-zero visible size.
		// Showing the pane only after creating/navigating the native child can
		// leave its first frame black until a later visibility cycle.
		Present(new PresentedSurface.Web());
		if (WebPageHostFactory is not null)
		{
			var wasLoaded = webPage.IsBrowserLoaded;
			await _webPageCoordinator.PresentAsync(webPage, cancellationToken);
			if (!wasLoaded && webPage.IsBrowserLoaded)
			{
				RecordDiagnostic(
					"web-monitor-register",
					$"page={webPage.Record.Id}");
			}
		}
	}

	/// <summary>
	/// Evaluates an edited monitoring rule once against the selected loaded web tab without
	/// replacing live rules or mutating monitoring state.
	/// </summary>
	public Task<WebMonitorTestResult> TestWebMonitorRuleOnCurrentTabAsync(
		WebMonitorRule rule,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(rule);

		if (ViewModel.SelectedWebPage is not { } selected
			|| !selected.IsBrowserLoaded
			|| !_webPageCoordinator.IsActive(selected.Record.Id))
		{
			return Task.FromResult(new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: "No selected loaded web tab is available for testing."));
		}

		return _webPageCoordinator.TestAsync(
			selected.Record.Id,
			rule,
			cancellationToken);
	}

	public async Task SelectNoteAsync(ProjectNoteViewModel note, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(note);
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		ViewModel.SelectedProjectNote = note;
		await ViewModel.SetActiveItemAsync(note.Record.Id, cancellationToken);
		var document = ViewModel.GetOrCreateNoteDocument(note);
		var workspace = ViewModel.GetOrCreateDocsAndNotesWorkspace(note);
		await workspace.RefreshAsync(cancellationToken);
		Present(new PresentedSurface.Notes(note, workspace, document));
	}

	/// <summary>Opens the log of the review a session takes part in.</summary>
	public Task OpenReviewLogAsync(SessionViewModel session)
	{
		ArgumentNullException.ThrowIfNull(session);
		return session.Review is { } review ? _reviewRelay.OpenLogAsync(review.ReviewId) : Task.CompletedTask;
	}

	private void ShowReviewBadges()
	{
		var reviews = _reviewRelay.ListActive();
		foreach (var session in ViewModel.Workspaces
			.Concat(ViewModel.PausedWorkspaces)
			.SelectMany(workspace => workspace.Sessions)
			.Concat(ViewModel.Sessions)
			.Distinct())
		{
			session.ApplyReviews(reviews);
		}
	}

	/// <summary>
	/// Shows a project Markdown file in the project's Notes tab, opening the tab when it is
	/// closed, and switches the user to it.
	/// </summary>
	/// <returns><see langword="false"/> when the project is not open or its Docs pane does not list the file.</returns>
	public async Task<bool> OpenProjectDocumentAsync(
		string projectId,
		string relativePath,
		CancellationToken cancellationToken = default)
	{
		if (ViewModel.Workspaces.FirstOrDefault(workspace => string.Equals(
				workspace.Id,
				projectId,
				StringComparison.Ordinal)) is not { } workspace)
		{
			return false;
		}

		if (IsReviewLogPath(relativePath))
		{
			return await OpenReviewLogOnScreenAsync(workspace, relativePath, cancellationToken);
		}

		var note = await ViewModel.ShowNotesTabAsync(workspace.Id, cancellationToken);
		await SelectNoteAsync(note, cancellationToken);
		return await ViewModel.GetOrCreateDocsAndNotesWorkspace(note)
			.OpenDocumentAsync(relativePath, cancellationToken);
	}

	public async Task RunQuickActionAsync(PromptTemplateRecord template, CancellationToken cancellationToken = default)
	{
		if (ViewModel.SelectedSession is not { } target)
		{
			return;
		}

		var text = new PromptTemplateRenderer().Render(template.Body, BuildPromptVariables(target, string.Empty));
		await SendTextToSessionAsync(target, text, PromptActionPolicy.ShouldSubmit(template), cancellationToken);
	}

	/// <summary>
	/// Routes the current selection to a live terminal and reports whether the route completed.
	/// </summary>
	public async Task<bool> SendSelectionToSessionAsync(
		SessionViewModel target,
		CancellationToken cancellationToken = default)
	{
		var snapshot = _selectionSnapshot;
		if (snapshot is not null && !IsSelectionSourceCurrent(snapshot.Source))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
			AppendDiagnosticLog($"selection-send target={target.Record.Id} reason=stale-source");
			await ReportStatusAsync("Nothing sent: the selection's terminal is no longer the active one.");
			return false;
		}

		var selectionVersion = Volatile.Read(ref _selectionCaptureVersion);
		var selectedText = snapshot?.Text ?? string.Empty;
		if (string.IsNullOrEmpty(selectedText))
		{
			selectedText = await _terminalHost.GetSelectedTextAsync();
		}

		if (string.IsNullOrEmpty(selectedText) && Clipboard is not null)
		{
			selectedText = await Clipboard.GetTextAsync();
		}

		if (string.IsNullOrEmpty(selectedText))
		{
			AppendDiagnosticLog($"selection-send target={target.Record.Id} reason=empty-selection");
			await ReportStatusAsync("Nothing to send: no selected text.");
			return false;
		}

		SelectionActionRouter router = new(
			ViewModel,
			new PromptTemplateRenderer(),
			SendTextToSessionAsync);
		await router.SendToSessionAsync(target, selectedText, cancellationToken);
		CloseSelectionActionsIfCurrent(selectionVersion);
		return true;
	}

	/// <summary>
	/// Appends the current selection to project Notes and reports whether the route completed.
	/// </summary>
	public async Task<bool> SendSelectionToNotesAsync(
		ProjectNotesTargetViewModel target,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(target);
		var snapshot = _selectionSnapshot;
		if (snapshot is null || !IsSelectionSourceCurrent(snapshot.Source))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
			return false;
		}

		var selectionVersion = snapshot.Version;
		SelectionActionRouter router = new(ViewModel, new PromptTemplateRenderer(), SendTextToSessionAsync);
		await router.SendToNotesAsync(target.ProjectId, snapshot.Text, cancellationToken);
		CloseSelectionActionsIfCurrent(selectionVersion);
		await ReportStatusAsync($"Selection appended to '{target.ProjectName}' notes.");
		return true;
	}

	public void UpdateSelectionText(string selectedText)
	{
		var version = Interlocked.Increment(ref _selectionCaptureVersion);
		var sourceKind = ViewModel.SelectedSession is not null
			? SelectionActionSourceKind.Terminal
			: SelectionActionSourceKind.Notes;
		ApplySelectionText(
			version,
			selectedText,
			anchor: null,
			CaptureSelectionSource(sourceKind));
	}

	/// <summary>
	/// Completes a selection that originated in the Notes editor and records its popup anchor.
	/// </summary>
	public void CompleteNotesSelection(string text, SelectionActionAnchor anchor)
	{
		var version = Interlocked.Increment(ref _selectionCaptureVersion);
		ApplySelectionText(
			version,
			text,
			anchor,
			CaptureSelectionSource(SelectionActionSourceKind.Notes));
	}

	private void ApplySelectionText(
		int version,
		string selectedText,
		SelectionActionAnchor? anchor,
		SelectionActionSourceIdentity? source)
	{
		if (string.IsNullOrWhiteSpace(selectedText) || source is null)
		{
			_selectionSnapshot = null;
			SelectionActionsAnchor = null;
			IsSelectionActionsOpen = false;
			return;
		}

		_selectionSnapshot = new SelectionActionSnapshot(
			version,
			source,
			selectedText,
			anchor);
		ViewModel.ResetSelectionActionChoice();
		SelectionActionsAnchor = anchor;
		IsSelectionActionsOpen = true;
	}

	public void CloseSelectionActions() => ClearSelectionActions(restoreTerminalFocus: true);

	private void ClearSelectionActions(bool restoreTerminalFocus)
	{
		Interlocked.Increment(ref _selectionCaptureVersion);
		_selectionSnapshot = null;
		SelectionActionsAnchor = null;
		IsSelectionActionsOpen = false;
		if (restoreTerminalFocus && IsTerminalVisible && _runtimeCoordinator.ActiveSessionId is not null)
		{
			RunEventOperation(
				"terminal-focus-restore",
				RestoreTerminalFocusAsync,
				"Terminal focus restore failed");
		}
	}

	private void CloseSelectionActionsIfCurrent(int selectionVersion)
	{
		if (selectionVersion == Volatile.Read(ref _selectionCaptureVersion))
		{
			CloseSelectionActions();
		}
	}

	private SelectionActionSourceIdentity? CaptureSelectionSource(
		SelectionActionSourceKind sourceKind) =>
		sourceKind switch
		{
			SelectionActionSourceKind.Terminal => CaptureTerminalSelectionSource(
				_runtimeCoordinator.ActiveSessionId),
			SelectionActionSourceKind.Notes => CaptureNotesSelectionSource(),
			_ => null
		};

	private SelectionActionSourceIdentity? CaptureTerminalSelectionSource(string? sessionId)
	{
		var session = ViewModel.SelectedSession;
		if (session is null ||
			!string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal))
		{
			return null;
		}

		return new SelectionActionSourceIdentity(
			SelectionActionSourceKind.Terminal,
			CaptureTerminalSelectionOwner(session),
			session.Record.Id,
			session,
			ContentInstance: null);
	}

	/// <remarks>
	/// Ownership scopes project actions and invalidation; it does not establish that the
	/// selection happened. A session that belongs to no collection — the pinned orchestrator —
	/// therefore reports no owner rather than voiding its own identity, so a missing owner
	/// narrows the offered actions instead of discarding the gesture.
	/// </remarks>
	private SelectionActionOwnerIdentity? CaptureTerminalSelectionOwner(SessionViewModel session)
	{
		if (session.IsRootItem)
		{
			return new SelectionActionOwnerIdentity(
				SelectionActionOwnerKind.Root,
				ProjectId: null,
				Project: null);
		}

		var workspace = ViewModel.Workspaces
			.Concat(ViewModel.PausedWorkspaces)
			.FirstOrDefault(candidate => candidate.Sessions.Any(candidateSession =>
				ReferenceEquals(candidateSession, session)));
		return workspace is null
			? null
			: new SelectionActionOwnerIdentity(
				SelectionActionOwnerKind.Project,
				workspace.Id,
				workspace);
	}

	private SelectionActionSourceIdentity? CaptureNotesSelectionSource()
	{
		var note = ViewModel.SelectedProjectNote;
		var document = CurrentDocsAndNotes?.ActiveDocument;
		if (note is null || document is null)
		{
			return null;
		}

		var workspace = ViewModel.Workspaces.FirstOrDefault(candidate =>
			candidate.Notes.Any(workspaceNote => ReferenceEquals(workspaceNote, note)));
		return workspace is null
			? null
			: new SelectionActionSourceIdentity(
				SelectionActionSourceKind.Notes,
				new SelectionActionOwnerIdentity(
					SelectionActionOwnerKind.Project,
					workspace.Id,
					workspace),
				note.Record.Id,
				note,
				document);
	}

	private bool IsSelectionSourceCurrent(SelectionActionSourceIdentity source) =>
		source.Kind switch
		{
			SelectionActionSourceKind.Terminal =>
				source.SourceInstance is SessionViewModel session
				&& ReferenceEquals(ViewModel.SelectedSession, session)
				&& string.Equals(session.Record.Id, source.SourceId, StringComparison.Ordinal)
				&& string.Equals(
					_runtimeCoordinator.ActiveSessionId,
					source.SourceId,
					StringComparison.Ordinal)
				&& source.Owner switch
				{
					// An unowned live terminal is already proven current by the active-session
					// checks above; only an owner adds a further condition to satisfy.
					null => true,
					{ Kind: SelectionActionOwnerKind.Root } =>
						ViewModel.RootTabs.Sessions.Any(candidate =>
							ReferenceEquals(candidate, session) && !candidate.IsManuallyPaused),
					{ Kind: SelectionActionOwnerKind.Project, Project: { } owner } =>
						ViewModel.Workspaces.Any(workspace =>
							ReferenceEquals(workspace, owner)
							&& string.Equals(
								workspace.Id,
								source.Owner.ProjectId,
								StringComparison.Ordinal)
							&& workspace.Sessions.Any(candidate =>
								ReferenceEquals(candidate, session))),
					_ => false
				},
			SelectionActionSourceKind.Notes =>
				source.SourceInstance is ProjectNoteViewModel note
				&& source.ContentInstance is IMarkdownEditorDocument document
				&& source.Owner is
				{
					Kind: SelectionActionOwnerKind.Project,
					Project: { } owner
				}
				&& ReferenceEquals(ViewModel.SelectedProjectNote, note)
				&& string.Equals(note.Record.Id, source.SourceId, StringComparison.Ordinal)
				&& ReferenceEquals(CurrentDocsAndNotes?.ActiveDocument, document)
				&& ViewModel.Workspaces.Any(workspace =>
					ReferenceEquals(workspace, owner)
					&& string.Equals(
						workspace.Id,
						source.Owner.ProjectId,
						StringComparison.Ordinal)
					&& workspace.Notes.Any(candidate => ReferenceEquals(candidate, note))),
			_ => false
		};

	private void InvalidateSelectionIfSourceChanged()
	{
		if (_selectionSnapshot is { } snapshot &&
			!IsSelectionSourceCurrent(snapshot.Source))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
		}
	}

	private void InvalidateSelectionOwner(WorkspaceViewModel owner)
	{
		if (_selectionSnapshot is
			{
				Source.Owner:
				{
					Kind: SelectionActionOwnerKind.Project,
					Project: { } sourceOwner
				}
			} &&
			ReferenceEquals(sourceOwner, owner) &&
			string.Equals(
				_selectionSnapshot.Source.Owner.ProjectId,
				owner.Id,
				StringComparison.Ordinal))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
		}
	}

	private void InvalidateSelectionSession(SessionViewModel session)
	{
		if (_selectionSnapshot is
			{
				Source:
				{
					Kind: SelectionActionSourceKind.Terminal,
					SourceInstance: SessionViewModel sourceSession
				}
			} &&
			ReferenceEquals(sourceSession, session))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
		}
	}

	private void InvalidateNotesSelection(WorkspaceViewModel owner)
	{
		if (_selectionSnapshot is
			{
				Source:
				{
					Kind: SelectionActionSourceKind.Notes,
					Owner:
					{
						Kind: SelectionActionOwnerKind.Project,
						Project: { } sourceOwner
					}
				}
			} &&
			ReferenceEquals(sourceOwner, owner) &&
			string.Equals(
				_selectionSnapshot.Source.Owner.ProjectId,
				owner.Id,
				StringComparison.Ordinal))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
		}
	}

	private Task RestoreTerminalFocusAsync() => _terminalHost.FocusAsync();

	public Task SelectWorkspaceAsync(WorkspaceViewModel workspace)
	{
		ViewModel.SelectedWorkspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
		CurrentGitPanel = workspace.IsGitRepository
			? GetOrCreateGitPanel(workspace)
			: null;
		return Task.CompletedTask;
	}

	private GitPanelViewModel GetOrCreateGitPanel(WorkspaceViewModel workspace)
	{
		if (_gitPanelViewModels.TryGetValue(workspace.Id, out var panel))
		{
			return panel;
		}

		panel = new GitPanelViewModel(
			workspace.RootPath,
			_gitCliRunner,
			_resolvedGitHelperActions,
			(action, root, branch) =>
			{
				if (_gitHelperResolver is not null)
				{
					ExternalGitHelperResolver.Launch(action, root, branch);
				}
			},
			Directory.Exists,
			_gitButtonCommands);
		_gitPanelViewModels.Add(workspace.Id, panel);
		return panel;
	}

	public AvaloniaGitActionCoordinator CreateGitActionCoordinator(
		Func<GitCommitDialogViewModel, Task<GitCommitDialogResult?>> commitDialog,
		Func<GitPushDialogViewModel, Task<GitPushDialogResult?>> pushDialog,
		Func<GitBranchDialogRequest, Task<GitBranchPickDialogResult?>> branchDialog) =>
		new(_gitCliRunner, commitDialog, pushDialog, branchDialog);

	public async Task<bool> ReloadExternalSettingsAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		string previousAgentControlCatalog = GetAgentControlCatalogFingerprint();
		List<string> failures = [];

		try
		{
			var shellProfiles =
				await _settingsFileStore.LoadShellProfilesAsync(cancellationToken);
			ViewModel.ReplaceShellProfiles(shellProfiles);
		}
		catch (Exception exception)
		{
			ViewModel.ReplaceShellProfiles(SettingsFileStore.CreateDefaultShellProfiles());
			failures.Add($"shell-profiles.json: {exception.Message}");
		}

		try
		{
			ViewModel.ReplacePromptTemplates(
				await _settingsFileStore.LoadPromptTemplatesAsync(cancellationToken));
		}
		catch (Exception exception)
		{
			failures.Add($"prompt-templates.json: {exception.Message}");
		}

		try
		{
			ViewModel.ReplaceScenarioDefinitions(
				await _settingsFileStore.LoadScenarioDefinitionsAsync(cancellationToken));
		}
		catch (Exception exception)
		{
			ViewModel.ReplaceScenarioDefinitions(ScenarioDefinitionStore.LoadDefaultDefinitions());
			failures.Add($"scenarios.json: {exception.Message}");
		}

		try
		{
			await _reviewProfileProvider.RefreshAsync(cancellationToken);
		}
		catch (Exception exception)
		{
			failures.Add($"review-profiles.json: {exception.Message}");
		}

		if (!string.Equals(
			previousAgentControlCatalog,
			GetAgentControlCatalogFingerprint(),
			StringComparison.Ordinal))
		{
			_agentControlEndpoint.PublishToolsListChanged();
		}

		try
		{
			WebLinkTemplates = await _settingsFileStore.LoadWebLinkTemplatesAsync(cancellationToken);
		}
		catch (Exception exception)
		{
			WebLinkTemplates = [];
			failures.Add($"web-link-templates.json: {exception.Message}");
		}

		try
		{
			var rules =
				await _settingsFileStore.LoadWebMonitorRulesAsync(cancellationToken);
			await ApplyWebMonitorRulesAsync(rules, cancellationToken);
		}
		catch (Exception exception)
		{
			if (!_webMonitorStartupRulesApplied)
			{
				await ApplyWebMonitorRulesAsync([], cancellationToken);
			}

			failures.Add($"web-monitor-rules.json: {exception.Message}");
		}

		try
		{
			_gitHelperResolver = new ExternalGitHelperResolver(
				_appPaths.GitHelpersPath,
				_executableLocator);
			_resolvedGitHelperActions = await _gitHelperResolver.ResolveAsync(cancellationToken);
			_gitButtonCommands = await _gitHelperResolver.LoadCommandsAsync(cancellationToken);
			_gitPanelViewModels.Clear();
			CurrentGitPanel = null;
		}
		catch (Exception exception)
		{
			failures.Add($"git-helpers.json: {exception.Message}");
		}

		try
		{
			await ApplyOrchestratorSettingsAsync(
				await _orchestratorStore.LoadAsync(cancellationToken),
				cancellationToken);
		}
		catch (Exception exception)
		{
			failures.Add($"orchestrator.json: {exception.Message}");
		}

		if (failures.Count == 0)
		{
			return true;
		}

		await ReportStatusAsync($"Settings load failed: {failures[0]}");
		return false;
	}

	private string GetAgentControlCatalogFingerprint()
	{
		IEnumerable<string> scenarioEntries = ViewModel.ScenarioDefinitions
			.Select(definition => $"scenario:{definition.Id}\n");
		IEnumerable<string> reviewProfileEntries = _reviewProfileProvider.Current
			.Select(profile => $"review-profile:{profile.Id}\n");
		return string.Concat(
			scenarioEntries
				.Concat(reviewProfileEntries)
				.Order(StringComparer.Ordinal));
	}

	public Task ApplyOrchestratorSettingsAsync(
		OrchestratorRecord record,
		CancellationToken cancellationToken) =>
		_orchestrator.ApplySettingsAsync(record, cancellationToken);

	internal OrchestratorSectionViewModel CreateOrchestratorSectionViewModel() =>
		new(
			_orchestratorStore,
			new HermesProvisioner(new HermesCli(_executableLocator)),
			HermesHome.ResolveRoot(),
			AgentControlAddress.AbsoluteUri);

	internal void AttachWorkstationLockMonitor(IntPtr windowHandle) =>
		_orchestrator.AttachWorkstationLockMonitor(windowHandle);

	internal bool IsOrchestratorRunning => _orchestrator.IsRunning;

	internal Task StartOrchestratorAsync(CancellationToken cancellationToken = default) =>
		_orchestrator.StartAsync(cancellationToken);

	internal Task StopOrchestratorAsync() => _orchestrator.StopAsync();

	internal Task SelectOrchestratorAsync(CancellationToken cancellationToken = default) =>
		_orchestrator.Session is { } session && _orchestrator.IsRunning
			? SelectSessionAsync(
				session,
				startIfNeeded: false,
				cancellationToken: cancellationToken)
			: Task.CompletedTask;

	internal Task DeliverWorkstationLockPromptAsync(bool locked) =>
		_orchestrator.DeliverWorkstationLockPromptAsync(locked);

	private bool IsLiveSession(string sessionId) =>
		_runtimeCoordinator.TryGetActiveController(sessionId, out _, out _);

	public async Task PauseWorkspaceAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		// Resume capture selects each session it shows, so read the user's choice first. A
		// selection outside this project leaves the project's own remembered item in place.
		var selectedItemId = ViewModel.SelectedSession?.Record.Id
			?? ViewModel.SelectedWebPage?.Record.Id
			?? ViewModel.SelectedProjectNote?.Record.Id;
		var activeItemId = selectedItemId is not null && OwnsItem(workspace, selectedItemId)
			? selectedItemId
			: workspace.Record.ActiveItemId;
		ReleaseProjectScreen(workspace);
		InvalidateSelectionOwner(workspace);
		await EndWorkspaceReviewsAsync(workspace);
		await UnloadWorkspaceWebPagesAsync(
			workspace,
			deleteSnapshots: false,
			cancellationToken);
		await _resumeCapture.CaptureBeforeStopAsync(workspace.Sessions.ToArray(), cancellationToken);
		foreach (var session in workspace.Sessions.ToArray())
		{
			await StopSessionAsync(session, SessionStatus.Stopped);
		}
		_gitPanelViewModels.Remove(workspace.Id);
		await ViewModel.PauseWorkspaceAsync(workspace.Id, activeItemId, cancellationToken);
		// Resume capture shows each session's terminal, which takes up the project's change list again.
		ReleaseProjectScreen(workspace);
	}

	private static bool OwnsItem(WorkspaceViewModel workspace, string itemId) =>
		workspace.Sessions.Any(session => string.Equals(session.Record.Id, itemId, StringComparison.Ordinal))
		|| workspace.WebPages.Any(page => string.Equals(page.Record.Id, itemId, StringComparison.Ordinal))
		|| workspace.Notes.Any(note => string.Equals(note.Record.Id, itemId, StringComparison.Ordinal));

	public Task<IReadOnlyList<string>> LoadRecentDirectoriesAsync(
		CancellationToken cancellationToken = default) =>
		_recentDirectoryStore.LoadAsync(cancellationToken);

	public async Task AddProjectFromDirectoryAsync(
		string workingDirectory,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		var directory = Path.GetFullPath(workingDirectory.Trim());
		if (!Directory.Exists(directory))
		{
			throw new DirectoryNotFoundException($"Directory does not exist: {directory}");
		}

		var workspace = await ViewModel.EnsureWorkspaceForDirectoryAsync(directory, cancellationToken);
		await SelectWorkspaceAsync(workspace);
		await AddRecentWorkingDirectoryAsync(directory, cancellationToken);
	}

	public async Task ResumeWorkspaceAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		await ViewModel.RestoreWorkspaceAsync(workspace.Id, cancellationToken);
		var restoredWorkspace = ViewModel.Workspaces.FirstOrDefault(
			item => string.Equals(item.Id, workspace.Id, StringComparison.Ordinal)) ?? workspace;
		await SelectWorkspaceAsync(restoredWorkspace);

		// Mirror WPF's RestoreWorkspaceAsync: eagerly restart every session's
		// backend so they resume producing output in the background, not just
		// the previously-active one. Starting is non-visible (CreateTerminalAsync
		// without ShowTerminalAsync); a failure on one session must not abort
		// the rest. GitLab enrichment remains outside this shell; the window
		// owns the busy overlay around this operation.
		var failedSessionCount = 0;
		foreach (var workspaceSession in restoredWorkspace.Sessions.ToArray())
		{
			try
			{
				await StartPromptRuntimeAsync(workspaceSession, cancellationToken);
			}
			catch (Exception exception)
			{
				failedSessionCount++;
				await ViewModel.UpdateSessionStatusAsync(workspaceSession.Record.Id, SessionStatus.Failed, cancellationToken);
				await ReportStatusAsync($"Workspace resume failed for '{workspaceSession.Record.Id}': {exception.Message}");
			}
		}

		if (ViewModel.SelectedSession is { } session)
		{
			await SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken);
		}
		else if (ViewModel.SelectedProjectNote is { } note)
		{
			await SelectNoteAsync(note, cancellationToken);
		}

		await ReportStatusAsync(failedSessionCount == 0
			? "Workspace restored."
			: $"Workspace restore completed with {failedSessionCount} failed session(s).");
	}

	private async Task AddRecentWorkingDirectoryAsync(string workingDirectory, CancellationToken cancellationToken)
	{
		try
		{
			await _recentDirectoryStore.AddAsync(workingDirectory, cancellationToken);
		}
		catch (Exception exception)
		{
			await ReportStatusAsync($"Recent directory update failed for '{workingDirectory}': {exception.Message}");
		}
	}

	public async Task AddSessionAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		var profile = ViewModel.ShellProfiles.FirstOrDefault();
		if (profile is null)
		{
			return;
		}

		await AddSessionAsync(workspace, profile, cancellationToken);
	}

	public async Task AddSessionAsync(
		WorkspaceViewModel workspace,
		AgentProfileRecord profile,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(profile);
		var resolvedCommand = await _resolveCommandAsync(profile.CommandTemplate, []);
		if (string.IsNullOrWhiteSpace(resolvedCommand))
		{
			return;
		}

		var session = await ViewModel.CreateSessionAsync(
			sessionId: Guid.NewGuid().ToString("N"),
			projectId: "default",
			kind: profile.Kind,
			title: profile.DisplayName,
			workingDirectory: workspace.RootPath,
			launchCommand: profile.CommandTemplate,
			resumeCommand: profile.ResumeCommandTemplate,
			cancellationToken: cancellationToken,
			workspaceId: workspace.Id);

		await SelectSessionAsync(
			session,
			startIfNeeded: true,
			preferResumeCommand: false,
			cancellationToken);
	}

	/// <summary>
	/// Adds a ROOT terminal from a launch profile. Its initial working directory is the existing
	/// Windows user profile directory.
	/// </summary>
	public async Task AddRootSessionAsync(
		AgentProfileRecord profile,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(profile);
		var resolvedCommand = await _resolveCommandAsync(profile.CommandTemplate, []);
		if (string.IsNullOrWhiteSpace(resolvedCommand))
		{
			return;
		}

		var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		if (string.IsNullOrWhiteSpace(userHome) || !Directory.Exists(userHome))
		{
			throw new DirectoryNotFoundException("The Windows user profile directory does not exist.");
		}

		var session = await ViewModel.CreateRootSessionAsync(
			profile.Kind,
			profile.DisplayName,
			userHome,
			profile.CommandTemplate,
			profile.ResumeCommandTemplate,
			cancellationToken);
		await SelectSessionAsync(
			session,
			startIfNeeded: true,
			preferResumeCommand: false,
			cancellationToken);
	}

	public async Task AddWebPageAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		var template = WebLinkTemplates.Count > 0 ? WebLinkTemplates[0] : null;
		if (template is null)
		{
			return;
		}

		await AddWebPageAsync(workspace, template, cancellationToken);
	}

	/// <summary>Opens a terminal hyperlink as a saved web page owned by the same project or ROOT.</summary>
	public async Task OpenTerminalLinkAsync(
		string sessionId,
		Uri uri,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(uri);

		if (ViewModel.RootTabs.Sessions.Any(session =>
			string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal)))
		{
			var rootPage = await ViewModel.CreateRootWebPageAsync(
				"Web page",
				uri.AbsoluteUri,
				cancellationToken);
			await SelectWebPageAsync(rootPage, cancellationToken);
			return;
		}

		var workspace = ViewModel.Workspaces.FirstOrDefault(candidate =>
			candidate.Sessions.Any(session =>
				string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal)));
		if (workspace is null)
		{
			await ReportStatusAsync($"Could not open terminal link: session '{sessionId}' no longer exists.");
			return;
		}

		var page = await ViewModel.CreateWebPageAsync(
			workspace.Id,
			"Web page",
			uri.AbsoluteUri,
			cancellationToken);
		await SelectWebPageAsync(page, cancellationToken);
	}

	public async Task AddWebPageAsync(
		WorkspaceViewModel workspace,
		WebLinkTemplateRecord template,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(template);
		var result = WebLinkTemplateRenderer.Render(template, workspace.Record);
		if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Url))
		{
			await ReportStatusAsync(result.ErrorMessage ?? "Web link template failed.");
			return;
		}

		var webPage = await ViewModel.CreateWebPageAsync(
			workspace.Id,
			template.Title,
			result.Url,
			cancellationToken);
		await SelectWebPageAsync(webPage, cancellationToken);
	}

	/// <summary>Adds a project browser page from an exact validated HTTP(S) address.</summary>
	public async Task AddWebPageAsync(
		WorkspaceViewModel workspace,
		Uri uri,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(uri);
		var webPage = await ViewModel.CreateWebPageAsync(
			workspace.Id,
			"Web page",
			uri.AbsoluteUri,
			cancellationToken);
		await SelectWebPageAsync(webPage, cancellationToken);
	}

	/// <summary>Persists and applies a requested terminal or browser tab reorder.</summary>
	public Task<bool> MoveTreeItemAsync(
		object source,
		object target,
		bool insertAfter,
		CancellationToken cancellationToken = default) =>
		ViewModel.MoveTreeItemAsync(source, target, insertAfter, cancellationToken);

	/// <summary>Adds a ROOT browser page from a configured web-link template.</summary>
	public async Task AddRootWebPageAsync(
		WebLinkTemplateRecord template,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(template);
		var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var now = DateTimeOffset.UtcNow;
		ProjectRecord rootContext = new("root", "ROOT", userHome, now, now, null);
		var result = WebLinkTemplateRenderer.Render(template, rootContext);
		if (!result.IsSuccess || string.IsNullOrWhiteSpace(result.Url))
		{
			await ReportStatusAsync(result.ErrorMessage ?? "Web link template failed.");
			return;
		}

		var webPage = await ViewModel.CreateRootWebPageAsync(
			template.Title,
			result.Url,
			cancellationToken);
		await SelectWebPageAsync(webPage, cancellationToken);
	}

	/// <summary>Adds a ROOT browser page from an exact validated HTTP(S) address.</summary>
	public async Task AddRootWebPageAsync(
		Uri uri,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(uri);
		var webPage = await ViewModel.CreateRootWebPageAsync(
			"Web page",
			uri.AbsoluteUri,
			cancellationToken);
		await SelectWebPageAsync(webPage, cancellationToken);
	}

	public async Task ToggleNotesAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);

		if (workspace.IsNotesTabOpen)
		{
			InvalidateNotesSelection(workspace);
			await FlushCurrentNoteAsync(cancellationToken);
			await ViewModel.HideNotesTabAsync(workspace.Id, cancellationToken);
			StopPresentingNotes();

			if (ViewModel.SelectedSession is { } session)
			{
				await SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken);
			}
			else if (ViewModel.SelectedWebPage is { } webPage)
			{
				await SelectWebPageAsync(webPage, cancellationToken);
			}
			else if (PresentedSurface is PresentedSurface.Terminal)
			{
				Present(new PresentedSurface.None());
			}

			return;
		}

		var note = await ViewModel.ShowNotesTabAsync(workspace.Id, cancellationToken);
		await SelectNoteAsync(note, cancellationToken);
	}

	public async Task CloseWorkspaceAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		ReleaseProjectScreen(workspace);
		InvalidateSelectionOwner(workspace);
		await EndWorkspaceReviewsAsync(workspace);
		await UnloadWorkspaceWebPagesAsync(
			workspace,
			deleteSnapshots: true,
			cancellationToken);
		foreach (var session in workspace.Sessions.ToArray())
		{
			await StopSessionAsync(session, SessionStatus.Exited);
		}
		_gitPanelViewModels.Remove(workspace.Id);
		await ViewModel.RemoveWorkspaceAsync(workspace.Id, cancellationToken);
		ReleaseProjectScreen(workspace);
		await RestoreSelectedHostAsync(cancellationToken);
	}

	public async Task CloseSessionAsync(SessionViewModel session, CancellationToken cancellationToken = default)
	{
		// The review learns first, so it ends as Aborted rather than as a dead process.
		await _reviewRelay.NotifySessionClosedAsync(session.Record.Id);
		await StopSessionAsync(session, SessionStatus.Exited);
		await _terminalHost.DisposeTerminalAsync(session.Record.Id);
		await ViewModel.RemoveSessionAsync(session.Record.Id, cancellationToken);
		await RestoreSelectedHostAsync(cancellationToken);
	}

	/// <summary>Returns whether the session currently owns a live terminal controller.</summary>
	public bool HasActiveTerminalProcess(SessionViewModel session)
	{
		ArgumentNullException.ThrowIfNull(session);
		return _runtimeCoordinator.TryGetActiveController(session.Record.Id, out _, out _);
	}

	/// <summary>Returns sessions that currently own a live terminal controller.</summary>
	public IReadOnlyList<SessionViewModel> GetActiveSessions(
		IEnumerable<SessionViewModel> sessions)
	{
		ArgumentNullException.ThrowIfNull(sessions);
		return sessions.Where(HasActiveTerminalProcess).ToArray();
	}

	/// <summary>Returns every loaded session that currently owns a live terminal controller.</summary>
	public IReadOnlyList<SessionViewModel> GetActiveSessions() => GetActiveSessions(
		ViewModel.RootTabs.Sessions
			.Concat(ViewModel.Workspaces.SelectMany(workspace => workspace.Sessions))
			.Concat(ViewModel.PausedWorkspaces.SelectMany(workspace => workspace.Sessions))
			.DistinctBy(session => session.Record.Id));

	/// <summary>Stops and parks one ROOT terminal without changing the selected row.</summary>
	public async Task PauseRootSessionAsync(
		SessionViewModel session,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (!session.IsRootItem || session.IsManuallyPaused)
		{
			return;
		}

		await _resumeCapture.CaptureBeforeStopAsync([session], cancellationToken);
		await StopSessionAsync(session, SessionStatus.Stopped);
		await ViewModel.SetRootItemPausedAsync(session.Record.Id, true, cancellationToken);
		if (ReferenceEquals(ViewModel.SelectedSession, session))
		{
			Present(new PresentedSurface.PausedSession(session));
		}
	}

	/// <summary>Resumes one ROOT terminal only through the explicit row action.</summary>
	public async Task ResumeRootSessionAsync(
		SessionViewModel session,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (!session.IsRootItem || !session.IsManuallyPaused)
		{
			return;
		}

		await ViewModel.SetRootItemPausedAsync(session.Record.Id, false, cancellationToken);
		await SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken);
	}

	public async Task RestartSessionAsync(
		SessionViewModel session,
		bool preferResumeCommand,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(session);
		if (!AgentResetCommands.TryGetResetCommand(session.Record.Kind, out _))
		{
			return;
		}

		var sessionId = session.Record.Id;
		InvalidateSelectionSession(session);
		_runtimeCoordinator.CancelActivation();
		_agentControlTokens.Revoke(sessionId);
		await _runtimeCoordinator.StopAsync(sessionId);

		await _terminalHost.DisposeTerminalAsync(sessionId);

		if (!preferResumeCommand)
		{
			await ViewModel.ClearSessionResumeCommandAsync(sessionId, cancellationToken);
		}

		await SelectSessionAsync(
			session,
			startIfNeeded: true,
			preferResumeCommand,
			cancellationToken);
	}

	public async Task ReloadWebPageAsync(WebPageViewModel webPage, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		await SelectWebPageAsync(webPage, cancellationToken);
		await _webPageCoordinator.ReloadAsync(webPage, cancellationToken);
	}

	public async Task CloseWebPageAsync(WebPageViewModel webPage, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		await _webPageCoordinator.CloseAsync(
			webPage.Record.Id,
			deleteSnapshot: true,
			cancellationToken);

		await ViewModel.RemoveWebPageAsync(webPage.Record.Id, cancellationToken);
		await RestoreSelectedHostAsync(cancellationToken);
	}

	/// <summary>Unloads and parks one ROOT browser page without changing the selected row.</summary>
	public async Task PauseRootWebPageAsync(
		WebPageViewModel webPage,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		if (!webPage.IsRootItem || webPage.IsManuallyPaused)
		{
			return;
		}

		await _webPageCoordinator.CloseAsync(
			webPage.Record.Id,
			deleteSnapshot: false,
			cancellationToken);
		await ViewModel.SetRootItemPausedAsync(webPage.Record.Id, true, cancellationToken);
		if (ReferenceEquals(ViewModel.SelectedWebPage, webPage))
		{
			Present(new PresentedSurface.PausedWebPage(webPage));
		}
	}

	/// <summary>Resumes one ROOT browser page only through the explicit row action.</summary>
	public async Task ResumeRootWebPageAsync(
		WebPageViewModel webPage,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		if (!webPage.IsRootItem || !webPage.IsManuallyPaused)
		{
			return;
		}

		await ViewModel.SetRootItemPausedAsync(webPage.Record.Id, false, cancellationToken);
		await SelectWebPageAsync(webPage, cancellationToken);
	}

	public async Task CloseNoteAsync(ProjectNoteViewModel note, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(note);
		var workspace = ViewModel.Workspaces.FirstOrDefault(item => item.Notes.Contains(note));
		if (workspace is null)
		{
			return;
		}

		InvalidateNotesSelection(workspace);
		await FlushCurrentNoteAsync(cancellationToken);
		await ViewModel.HideNotesTabAsync(workspace.Id, cancellationToken);
		StopPresentingNotes();
		await RestoreSelectedHostAsync(cancellationToken);
	}

	private SessionScreenState? ReadScreenState(string sessionId) =>
		ViewModel.TerminalTabStatuses.TryGetScreenState(sessionId, out var state) ? state : null;

	public Task WriteInputAsync(string sessionId, string input)
	{
		ThrowIfRejectingInput();
		return _runtimeCoordinator.WriteInputAsync(
			sessionId,
			input,
			(_, message) => ReportStatusAsync(message));
	}

	public async Task ResizeAsync(string sessionId, int columns, int rows)
	{
		if (_runtimeCoordinator.TryGetActiveController(
			sessionId,
			out _,
			out var controller))
		{
			await controller.ResizeAsync(columns, rows);
		}
	}

	public void SetTerminalWindowFacts(bool visible, bool active, DateTimeOffset occurredAt) => ViewModel.TerminalTabStatuses.SetWindowFacts(visible, active, occurredAt);

	/// <summary>
	/// Publishes the same foreground-root visibility and activation facts used by terminal
	/// acknowledgement to the selected loaded web-page registration.
	/// </summary>
	public void SetWebMonitorWindowFacts(bool visible, bool active)
	{
		_webMonitorWindowVisible = visible;
		_webMonitorWindowActive = active;
		PublishWebMonitorPresentationFacts();
	}

	public async Task CopyAsync()
	{
		if (Clipboard is null)
		{
			return;
		}

		var selectedText = await _terminalHost.GetSelectedTextAsync();
		if (!string.IsNullOrEmpty(selectedText))
		{
			if (!await Clipboard.TrySetTextAsync(selectedText))
			{
				await ReportStatusAsync("Could not copy the terminal selection to the clipboard.");
			}
		}
	}

	/// <summary>
	/// Copies the web page's current full address without using its compact display form.
	/// </summary>
	public async Task CopyWebPageAddressAsync(WebPageViewModel webPage)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		if (Clipboard is not null && !await Clipboard.TrySetTextAsync(webPage.ResumeUrl))
		{
			await ReportStatusAsync("Could not copy the web page address to the clipboard.");
		}
	}

	public async Task PasteAsync()
	{
		if (Clipboard is null)
		{
			return;
		}

		var text = await Clipboard.GetTextAsync();
		var selectedSession = ViewModel.SelectedSession;
		if (!string.IsNullOrEmpty(text) && selectedSession is not null)
		{
			await _runtimeCoordinator.PasteAsKeyboardAsync(selectedSession.Record.Id, text);
		}
	}

	public Task SetBusyOverlayAsync(
		string message,
		bool isVisible,
		bool dimBackground,
		string? actionLabel = null) =>
		_terminalHost.SetBusyOverlayAsync(message, isVisible, dimBackground, actionLabel);

	public Task ShutdownAsync()
	{
		lock (_shutdownGate)
		{
			return _shutdownTask ??= ShutdownCoreAsync();
		}
	}

	internal void BeginShutdown()
	{
		lock (_shutdownGate)
		{
			if (_shutdownBegun)
			{
				return;
			}

			_shutdownBegun = true;
			Interlocked.Exchange(ref _acceptingInput, 0);
			_runtimeCoordinator.CancelActivation();
			// MCP handlers still drain for a while; a review they end must stay quiet.
			_reviewRelay.BeginShutdown();
			DetachEventProducers();
			_eventDrainTask = _eventTasks.CompleteAndDrainAsync();
		}
	}

	public Task StopAsync() => ShutdownAsync();

	private async Task ShutdownCoreAsync()
	{
		BeginShutdown();
		Task eventDrain;
		lock (_shutdownGate)
		{
			eventDrain = _eventDrainTask!;
		}

		List<Exception> failures = [];
		try
		{
			var endpointShutdown = await _agentControlEndpoint.ShutdownAsync(
				TimeSpan.FromSeconds(5));
			if (!endpointShutdown.DrainedCleanly)
			{
				failures.Add(new TimeoutException(
					"Agent control handlers required cancellation during shutdown."));
			}
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}

		await RunCleanupStepAsync(_reviewRelay.ShutdownAsync, failures);
		await RunCleanupStepAsync(StopOrchestratorAsync, failures);
		await RunCleanupStepAsync(() => eventDrain, failures);
		await RunCleanupStepAsync(() => ViewModel.FlushAllNoteDocumentsAsync(CancellationToken.None), failures);
		await RunCleanupStepAsync(
			() => _resumeCapture.CaptureBeforeStopAsync(ViewModel.Sessions.ToArray(), CancellationToken.None),
			failures);
		foreach (var sessionId in ViewModel.Sessions
			.Select(session => session.Record.Id)
			.Distinct(StringComparer.Ordinal))
		{
			_agentControlTokens.Revoke(sessionId);
		}

		await RunCleanupStepAsync(
			() => _runtimeCoordinator.StopAllAsync(
				(id, status) => ViewModel.UpdateSessionStatusAsync(id, status, CancellationToken.None),
				static () => { }), failures);
		await RunCleanupStepAsync(
			() => InvokeUiActionAsync(() =>
			{
				if (PresentedSurface is PresentedSurface.Terminal)
				{
					Present(new PresentedSurface.None());
				}
			}),
			failures);
		await RunCleanupStepAsync(DisposeWebViewsAsync, failures);
		if (failures.Count > 0)
		{
			throw new AggregateException("Avalonia shell shutdown failed.", failures);
		}
	}

	public async ValueTask DisposeAsync()
	{
		if (_disposed)
		{
			return;
		}

		_disposed = true;
		try
		{
			await ShutdownAsync();
		}
		finally
		{
			DisposeProjectScreens();
			RemarkBooks.Dispose();
			_processMetricsMonitor.MetricsChanged -= OnProcessMetricsChanged;
			_processMetricsMonitor.Dispose();
			_webProcessMetricsMonitor.MetricsChanged -= OnWebProcessMetricsChanged;
			await _webProcessMetricsMonitor.DisposeAsync();
			_orchestrator.DetachWorkstationLockMonitor();
			_agentControlEndpoint.Dispose();
			_reviewLog.Dispose();
			_webPageCoordinator.Dispose();
			_runtimeCoordinator.Dispose();
		}
	}

	private async Task PrepareSessionAsync(SessionViewModel session, CancellationToken cancellationToken)
	{
		ViewModel.SelectedSession = session;
		await ViewModel.SetActiveItemAsync(session.Record.Id, cancellationToken);
	}

	private SessionViewModel? FindRestorableSession(string sessionId) =>
		ViewModel.Workspaces
			.SelectMany(workspace => workspace.Sessions)
			.Concat(ViewModel.RootTabs.Sessions.Where(session => !session.IsManuallyPaused))
			.FirstOrDefault(session => string.Equals(
				session.Record.Id,
				sessionId,
				StringComparison.Ordinal));

	private WebPageViewModel? FindRestorableWebPage(string pageId) =>
		ViewModel.Workspaces
			.SelectMany(workspace => workspace.WebPages)
			.Concat(ViewModel.RootTabs.WebPages.Where(page => !page.IsManuallyPaused))
			.FirstOrDefault(page => string.Equals(
				page.Record.Id,
				pageId,
				StringComparison.Ordinal));

	private async Task<SessionStartPlan> StartRestoredSessionAsync(
		SessionViewModel session,
		CancellationToken cancellationToken)
	{
		if (!_runtimeCoordinator.TryGetActiveController(session.Record.Id, out _, out _))
		{
			await _terminalHost.CreateTerminalAsync(session.Record.Id);
		}
		return await _launcher.ActivateAsync(
			session,
			startIfNeeded: true,
			preferResumeCommand: true,
			cancellationToken);
	}

	private async Task<bool> RestoreSelectionAsync(
		SoftRestartSelection selection,
		CancellationToken cancellationToken)
	{
		if (selection.ItemId is { } itemId)
		{
			if (_orchestrator.Session is not null
				&& string.Equals(itemId, OrchestratorSupervisor.SessionId, StringComparison.Ordinal)
				&& IsOrchestratorRunning)
			{
				await SelectOrchestratorAsync(cancellationToken);
				return true;
			}

			var session = FindRestorableSession(itemId);
			if (session is not null)
			{
				await SelectSessionAsync(
					session,
					startIfNeeded: false,
					cancellationToken: cancellationToken);
				return true;
			}
			var page = FindRestorableWebPage(itemId);
			if (page is not null)
			{
				await SelectWebPageAsync(page, cancellationToken);
				return true;
			}
			if (selection.ProjectId is { } noteProjectId)
			{
				var workspace = ViewModel.Workspaces.FirstOrDefault(item =>
					string.Equals(item.Id, noteProjectId, StringComparison.Ordinal));
				var note = workspace?.Notes.FirstOrDefault(item =>
					string.Equals(item.Record.Id, itemId, StringComparison.Ordinal));
				if (note is not null)
				{
					await SelectNoteAsync(note, cancellationToken);
					return true;
				}
			}
			return false;
		}

		if (selection.ProjectId is not { } projectId)
		{
			return false;
		}
		var selectedWorkspace = ViewModel.Workspaces.FirstOrDefault(workspace =>
			string.Equals(workspace.Id, projectId, StringComparison.Ordinal));
		if (selectedWorkspace is null)
		{
			return false;
		}
		await SelectWorkspaceAsync(selectedWorkspace);
		return true;
	}

	private bool IsShutdownBegun()
	{
		lock (_shutdownGate)
		{
			return _shutdownBegun;
		}
	}

	private async Task SendTextToSessionAsync(SessionViewModel target, string text, bool submit, CancellationToken cancellationToken)
	{
		var result = await _runtimeCoordinator.DeliverPromptAsync(
			target.Record.Id,
			text,
			submit,
			PromptSender.Outsider,
			startIfNotRunning: true,
			cancellationToken);
		if (PromptDeliveryText.DescribeUndelivered(result) is { } reason)
		{
			await ReportStatusAsync($"{target.Title}: {reason}");
		}
	}

	private async Task StartSessionByIdAsync(string sessionId, CancellationToken cancellationToken)
	{
		SessionViewModel? session = null;
		_uiTaskDispatcher.Post(() => session = ViewModel.FindSession(sessionId));
		if (session is null)
		{
			throw new InvalidOperationException($"Session '{sessionId}' is not registered.");
		}

		// A delivery or an MCP call can arrive here on a pool thread; starting a session updates view
		// models, so it runs on the UI thread like activating a tab does.
		await _uiTaskDispatcher.InvokeAsync(() => StartPromptRuntimeAsync(session, cancellationToken));
	}

	private async Task StartPromptRuntimeAsync(SessionViewModel session, CancellationToken cancellationToken)
	{
		await _terminalHost.CreateTerminalAsync(session.Record.Id);
		await _launcher.ActivateAsync(session, startIfNeeded: true, preferResumeCommand: true, cancellationToken);
	}

	private Dictionary<string, string> BuildPromptVariables(SessionViewModel target, string selectedText)
	{
		var workspace = ViewModel.Workspaces.FirstOrDefault(item => item.Sessions.Contains(target));
		return new Dictionary<string, string>
		{
			["project"] = workspace?.Name ?? string.Empty,
			["task"] = target.Title,
			["selectedText"] = selectedText,
			["otherSessionSummary"] = string.Empty
		};
	}

	private async Task FlushCurrentNoteAsync(CancellationToken cancellationToken)
	{
		if (CurrentDocsAndNotes is { } workspace)
		{
			await workspace.FlushAsync(cancellationToken);
		}
		else if (CurrentNoteDocument is { } document)
		{
			await document.FlushAsync(cancellationToken);
		}
	}

	private Task HideActiveWebPageAsync(CancellationToken cancellationToken) =>
		_webPageCoordinator.HideActiveAsync(cancellationToken);

	private async Task UnloadWorkspaceWebPagesAsync(
		WorkspaceViewModel workspace,
		bool deleteSnapshots,
		CancellationToken cancellationToken)
	{
		foreach (var page in workspace.WebPages.ToArray())
		{
			await _webPageCoordinator.CloseAsync(
				page.Record.Id,
				deleteSnapshots,
				cancellationToken);
		}
	}

	private async Task RestoreSelectedHostAsync(CancellationToken cancellationToken)
	{
		if (ViewModel.SelectedSession is { } session)
		{
			if (IsTerminalVisible &&
				string.Equals(
					_runtimeCoordinator.ActiveSessionId,
					session.Record.Id,
					StringComparison.Ordinal))
			{
				return;
			}

			await SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken);
		}
		else if (ViewModel.SelectedWebPage is { } webPage)
		{
			await SelectWebPageAsync(webPage, cancellationToken);
		}
		else if (ViewModel.SelectedProjectNote is { } note)
		{
			await SelectNoteAsync(note, cancellationToken);
		}
		// A selected project row has no tab selected; its screen stays while the project is open.
		else if (PresentedSurface is not PresentedSurface.Project project
			|| !ViewModel.Workspaces.Contains(project.Screen.Workspace))
		{
			Present(new PresentedSurface.None());
		}
	}

	private Task HandleBrowserFailureAsync(string message) =>
		ReportStatusAsync($"Web page navigation failed: {message}");

	// A paused or closed project's tabs stop without a process exit the relay would hear about.
	private Task EndWorkspaceReviewsAsync(WorkspaceViewModel workspace) =>
		_reviewRelay.EndProjectReviewsAsync(workspace.Id);

	// Older Pact versions exchanged review files in .pact-reviews; reviews now go through MCP.
	private async Task CleanupAbandonedReviewExchangesAsync(CancellationToken cancellationToken)
	{
		var projectRoots = ViewModel.Workspaces
			.Concat(ViewModel.PausedWorkspaces)
			.Select(workspace => workspace.RootPath)
			.Where(root => !string.IsNullOrWhiteSpace(root))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
		foreach (var projectRoot in projectRoots)
		{
			cancellationToken.ThrowIfCancellationRequested();
			try
			{
				ReviewExchangeDirectory.CleanupAbandoned(projectRoot);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				await AppLog.AppendAsync(
					_appPaths.RootDirectory,
					$"Abandoned review exchange cleanup failed for {projectRoot}",
					exception);
			}
		}
	}

	private void OnSessionProcessDied(string sessionId)
	{
		RunEventOperation(
			"review-session-died",
			() => _reviewRelay.NotifySessionDiedAsync(sessionId),
			"Review update failed");
	}

	private void OnWebPageSourceChanged(
		object? sender,
		(WebPageViewModel Page, Uri Uri) e)
	{
		RunEventOperation(
			"web-page-source-changed",
			() => ViewModel.UpdateWebPageResumeUrlAsync(
				e.Page.Record.Id,
				e.Uri.AbsoluteUri,
				null,
				CancellationToken.None),
			"Web page address update failed");
	}

	private void OnWebPageTitleChanged(
		object? sender,
		(WebPageViewModel Page, string Title) e)
	{
		RunEventOperation(
			"web-page-title-changed",
			() => ViewModel.UpdateWebPageTitleAsync(
				e.Page.Record.Id,
				e.Title,
				CancellationToken.None),
			"Web page title update failed");
	}

	private void OnWebPageNavigationStateChanged(
		object? sender,
		(WebPageViewModel Page, bool Navigating, bool Failed) e)
	{
		RecordDiagnostic(
			e.Navigating
				? "web-monitor-navigation-started"
				: "web-monitor-navigation-completed",
			$"page={e.Page.Record.Id}"
			+ (e.Failed ? ";failed=True" : string.Empty));
	}

	private void OnWebPageNavigationFailed(
		object? sender,
		(WebPageViewModel Page, string Message) e)
	{
		RunEventOperation(
			"web-page-navigation-failed",
			() => HandleBrowserFailureAsync(e.Message),
			"Web page failure reporting failed");
	}

	private void OnWebPageNewWindowRequested(
		object? sender,
		(WebPageViewModel Page, Uri Uri) e)
	{
		RunEventOperation(
			"web-page-new-window",
			() => OpenPopupAsWebPageAsync(e.Page, e.Uri),
			"Opening web page popup failed");
	}

	private void OnTerminalLinkRequested(
		object? sender,
		(string SessionId, Uri Uri) request)
	{
		RunEventOperation(
			"terminal-link",
			() => OpenTerminalLinkAsync(request.SessionId, request.Uri, CancellationToken.None),
			"Opening terminal link failed");
	}

	private void OnOutputReceived(string sessionId, string text)
	{
		if (!_runtimeCoordinator.TryGetRuntime(sessionId, out var runtime))
		{
			return;
		}

		var displayText = runtime.DisplayOutputFilter.Filter(text);
		if (displayText.Length == 0)
		{
			return;
		}

		runtime.InputModes.Scan(displayText);
		runtime.AppendRecentOutput(displayText);
		RunEventOperation(
			"terminal-output",
			() => WriteOutputAsync(sessionId, displayText, runtime),
			"Terminal output failed");
	}

	private void OnScreenSnapshotReceived(object? sender, (string SessionId, TerminalScreenSnapshot Snapshot) e)
	{
		RefreshWindowFacts?.Invoke();
		ViewModel.TerminalTabStatuses.OnScreenSnapshot(e.SessionId, e.Snapshot, DateTimeOffset.UtcNow);
		if (e.Snapshot.Stable && !string.IsNullOrWhiteSpace(e.Snapshot.Text))
		{
			ClearTerminalLoading(e.SessionId);
		}
	}

	private async Task OpenPopupAsWebPageAsync(WebPageViewModel sourcePage, Uri uri)
	{
		if (sourcePage.IsRootItem)
		{
			var rootPage = await ViewModel.CreateRootWebPageAsync(
				"Web page",
				uri.AbsoluteUri,
				CancellationToken.None);
			await SelectWebPageAsync(rootPage, CancellationToken.None);
			return;
		}

		var workspace = ViewModel.Workspaces.FirstOrDefault(item =>
			item.WebPages.Any(page => page.Record.Id == sourcePage.Record.Id));
		if (workspace is null)
		{
			return;
		}

		var page = await ViewModel.CreateWebPageAsync(
			workspace.Id, "Web page", uri.AbsoluteUri, CancellationToken.None);
		await SelectWebPageAsync(page, CancellationToken.None);
	}

	private async Task WriteOutputAsync(string sessionId, string text, SessionRuntime runtime)
	{
		await _terminalHost.WriteOutputAsync(sessionId, text);
		runtime.NotifyOutputRendered();
	}

	private async Task ResetSnapshotBaselineSafeAsync(string sessionId)
	{
		try
		{
			await _terminalHost.ResetSnapshotBaselineAsync(sessionId);
		}
		catch (Exception exception)
		{
			await ReportStatusAsync($"Terminal snapshot baseline reset failed: {exception.Message}");
		}
	}

	private async Task StopSessionAsync(SessionViewModel session, SessionStatus status)
	{
		InvalidateSelectionSession(session);
		_agentControlTokens.Revoke(session.Record.Id);
		await _runtimeCoordinator.StopAsync(session.Record.Id);
		await ViewModel.UpdateSessionStatusAsync(session.Record.Id, status, CancellationToken.None);
	}

	private async Task ShowSessionTerminalUnderBusyAsync(SessionViewModel session)
	{
		await InvokeUiActionAsync(() =>
		{
			ViewModel.SelectedSession = session;
			Present(PresentedSurface is PresentedSurface.Terminal current
				&& ReferenceEquals(current.Session, session)
					? current
					: new PresentedSurface.Terminal(session, Loading: false, FocusWhenLoaded: false));
		});
		await _terminalHost.ShowTerminalAsync(session.Record.Id);
	}

	private async Task DisposeWebViewsAsync()
	{
		await _webPageCoordinator.DisposeHostsAsync(
			deleteSnapshots: false,
			CancellationToken.None);
		if (_terminalHost is IAsyncDisposable lifetime)
		{
			await lifetime.DisposeAsync();
		}
	}

	private static async Task RunCleanupStepAsync(Func<Task> step, List<Exception> failures)
	{
		try
		{ await step(); }
		catch (Exception exception) { failures.Add(exception); }
	}

	private void ThrowIfRejectingInput()
	{
		if (Volatile.Read(ref _acceptingInput) == 0)
		{
			throw new InvalidOperationException("Application is shutting down.");
		}
	}

	private void LogSlowSessionSwitch(SessionViewModel session, SessionSwitchTiming timing, bool startedRuntime)
	{
		var elapsed = timing.Elapsed;
		if (elapsed < SessionSwitchTiming.SlowThreshold)
		{
			return;
		}

		var show = _terminalHost is AvaloniaTerminalWebViewHost host
			? $" showFlush={host.LastShowTiming.Flush.TotalMilliseconds:F0} showScript={host.LastShowTiming.Script.TotalMilliseconds:F0}"
			: string.Empty;
		AppendDiagnosticLog(
			$"Slow session switch to {session.Record.Kind} {session.Record.Id}: total={elapsed.TotalMilliseconds:F0} ms "
			+ $"{timing.Describe()}{show} startedRuntime={startedRuntime}");
	}

	/// <summary>
	/// Focuses a terminal whose activation asked for focus while it was still hidden behind
	/// the loading surface, provided it is still the visible active terminal.
	/// </summary>
	internal async Task FocusTerminalAfterLoadingAsync()
	{
		if (PresentedSurface is not PresentedSurface.Terminal { FocusWhenLoaded: true, Loading: false } terminal)
		{
			return;
		}

		Present(terminal with { FocusWhenLoaded = false });
		if (!string.Equals(
				_runtimeCoordinator.ActiveSessionId,
				terminal.Session.Record.Id,
				StringComparison.Ordinal))
		{
			return;
		}

		await _terminalHost.FocusAsync();
	}

	private void AppendDiagnosticLog(string message) =>
		_eventTasks.TryRun(
			"diagnostic-log",
			() => AppLog.AppendAsync(_appPaths.RootDirectory, message),
			static _ => Task.CompletedTask);

	private Task ReportStatusAsync(string message)
	{
		StatusText = message;
		StatusMessage?.Invoke(this, message);
		return Task.CompletedTask;
	}

	private bool RunEventOperation(
		string operationName,
		Func<Task> operation,
		string userFailurePrefix) =>
		_eventTasks.TryRun(
			operationName,
			operation,
			exception => ReportStatusAsync(
				$"{userFailurePrefix}: {exception.Message}"));

	private void OnInputReceived(object? sender, (string SessionId, string Data) e)
	{
		if (Volatile.Read(ref _acceptingInput) == 0)
		{
			return;
		}

		RunEventOperation(
			"terminal-input",
			() => HandleInputReceivedAsync(e),
			"Terminal input failed");
	}

	private async Task HandleInputReceivedAsync((string SessionId, string Data) e)
	{
		var runtimeActive = _runtimeCoordinator.TryGetActiveController(
			e.SessionId,
			out _,
			out _);
		RecordDiagnostic(
			"terminal-input-received",
			$"session={e.SessionId};length={e.Data.Length};runtimeActive={runtimeActive}");
		await WriteInputAsync(e.SessionId, e.Data);
		RecordDiagnostic(
			"terminal-input-processed",
			$"session={e.SessionId};length={e.Data.Length};runtimeActive={runtimeActive}");
	}

	private Task InvokeUiActionAsync(Action action) =>
		_uiTaskDispatcher.InvokeAsync(() =>
		{
			action();
			return Task.CompletedTask;
		});

	private void DetachEventProducers()
	{
		if (Interlocked.Exchange(ref _eventProducersAttached, 0) == 0)
		{
			return;
		}

		_terminalHost.InputReceived -= OnInputReceived;
		_terminalHost.ResizeReceived -= OnResizeReceived;
		_terminalHost.ScreenSnapshotReceived -= OnScreenSnapshotReceived;
		_terminalHost.SelectionChanged -= OnSelectionChanged;
		_terminalHost.SelectionCompleted -= OnSelectionCompleted;
		_terminalHost.SelectionDismissed -= OnSelectionDismissed;
		_terminalHost.AgentMouseReleased -= OnAgentMouseReleased;
		_terminalHost.LinkRequested -= OnTerminalLinkRequested;
		_terminalHost.BusyOverlayActionRequested -= OnBusyOverlayActionRequested;
		_terminalHost.PasteRequested -= OnPasteRequested;
		_terminalHost.CopyRequested -= OnCopyRequested;
		_webPageCoordinator.SourceChanged -= OnWebPageSourceChanged;
		_webPageCoordinator.TitleChanged -= OnWebPageTitleChanged;
		_webPageCoordinator.NavigationStateChanged -= OnWebPageNavigationStateChanged;
		_webPageCoordinator.NavigationFailed -= OnWebPageNavigationFailed;
		_webPageCoordinator.NewWindowRequested -= OnWebPageNewWindowRequested;
		_webPageCoordinator.StableUrlChanged -= OnWebMonitorStableUrlChanged;
		_webMonitorCoordinator.LiveDiagnosticsChanged -= OnWebMonitorLiveDiagnosticsChanged;
		ViewModel.TerminalTabStatuses.DiagnosticsChanged -= OnTerminalDiagnosticsChanged;
		_selectedDetailsSource?.PropertyChanged -= OnSelectedDetailsSourcePropertyChanged;
		_selectedDetailsSource = null;
		CurrentDocsAndNotes?.PropertyChanged -= OnDocsAndNotesPropertyChanged;
		ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
		if (_terminalHost is AvaloniaTerminalWebViewHost terminalHost)
		{
			terminalHost.DetachEventProducers();
		}
		_webPageCoordinator.DetachEventProducers();
	}

	private void OnResizeReceived(
		object? sender,
		(string SessionId, int Columns, int Rows) e) =>
		RunEventOperation(
			"terminal-resize",
			() => ResizeAsync(e.SessionId, e.Columns, e.Rows),
			"Terminal resize failed");

	private void OnSelectionChanged(
		object? sender,
		(string SessionId, bool HasSelection) e)
	{
		if (!string.Equals(e.SessionId, _runtimeCoordinator.ActiveSessionId, StringComparison.Ordinal))
		{
			return;
		}

		if (!e.HasSelection)
		{
			CloseSelectionActions();
		}
	}

	private void OnSelectionDismissed(object? sender, string sessionId)
	{
		if (_selectionSnapshot is { Source: { Kind: SelectionActionSourceKind.Terminal } source }
			&& string.Equals(source.SourceId, sessionId, StringComparison.Ordinal))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
		}
	}

	private void OnSelectionCompleted(object? sender, TerminalSelectionCompleted completed)
	{
		var source = CaptureTerminalSelectionSource(completed.SessionId);
		if (source is null)
		{
			return;
		}

		var version = Interlocked.Increment(ref _selectionCaptureVersion);

		_eventTasks.TryRun(
			"terminal-selection-completed",
			() => HandleSelectionCompletedAsync(version, source, completed),
			exception => version == Volatile.Read(ref _selectionCaptureVersion)
				? ReportStatusAsync($"Selection actions failed: {exception.Message}")
				: Task.CompletedTask);
	}

	private async Task HandleSelectionCompletedAsync(
		int version,
		SelectionActionSourceIdentity source,
		TerminalSelectionCompleted completed)
	{
		var text = await _terminalHost.GetSelectedTextAsync();
		if (string.IsNullOrWhiteSpace(text) && Clipboard is not null)
		{
			text = await Clipboard.GetTextAsync();
		}

		if (version == Volatile.Read(ref _selectionCaptureVersion) &&
			IsSelectionSourceCurrent(source))
		{
			ApplySelectionText(
				version,
				text ?? string.Empty,
				new SelectionActionAnchor(
					SelectionActionSourceKind.Terminal,
					completed.Anchor.X,
					completed.Anchor.Y,
					IsAvailable: true),
				source);
		}
	}

	private void RecordDiagnostic(string phase, string? detail = null) =>
		_diagnostics.Record(
			phase,
			Dispatcher.UIThread.CheckAccess(),
			isVisible: null,
			isAttached: null,
			hasPlatformHandle: null,
			detail);

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		UpdateController?.RefreshRestartSafety();
		if (e.PropertyName == nameof(MainWindowViewModel.SelectedWebPage))
		{
			PublishWebMonitorPresentationFacts();
		}

		if (e.PropertyName is nameof(MainWindowViewModel.SelectedSession)
			or nameof(MainWindowViewModel.SelectedProjectNote)
			or nameof(MainWindowViewModel.SelectedWebPage))
		{
			InvalidateSelectionIfSourceChanged();
		}

		if (e.PropertyName is nameof(MainWindowViewModel.SelectedSession)
			or nameof(MainWindowViewModel.SelectedWebPage))
		{
			RebindSelectedDetailsSource();
		}
	}

	private void RebindSelectedDetailsSource()
	{
		_selectedDetailsSource?.PropertyChanged -= OnSelectedDetailsSourcePropertyChanged;

		_selectedDetailsSource = ViewModel.SelectedSession ?? (INotifyPropertyChanged?)ViewModel.SelectedWebPage;
		_selectedDetailsSource?.PropertyChanged += OnSelectedDetailsSourcePropertyChanged;

		RefreshSelectedTabDetails();
	}

	private void OnSelectedDetailsSourcePropertyChanged(object? sender, PropertyChangedEventArgs e) =>
		RefreshSelectedTabDetails();

	private void OnTerminalDiagnosticsChanged(
		object? sender,
		TerminalClassifierDiagnosticsChangedEventArgs e)
	{
		_reviewRelay.NotifyStatusChanged(e.Diagnostics.SessionId);
		UpdateController?.RefreshRestartSafety();
		if (string.Equals(
			ViewModel.SelectedSession?.Record.Id,
			e.Diagnostics.SessionId,
			StringComparison.Ordinal))
		{
			RefreshSelectedTabDetails();
		}
	}

	private void OnWebMonitorLiveDiagnosticsChanged(
		object? sender,
		WebMonitorDiagnosticsChangedEventArgs e)
	{
		if (string.Equals(
			ViewModel.SelectedWebPage?.Record.Id,
			e.Diagnostics.WebPageId,
			StringComparison.Ordinal))
		{
			RefreshSelectedTabDetails();
		}
	}

	private void OnProcessMetricsChanged(object? sender, EventArgs e) =>
		_uiTaskDispatcher.Post(() =>
		{
			if (!_disposed)
			{
				RefreshSelectedTabDetails();
			}
		});

	private void OnWebProcessMetricsChanged(object? sender, EventArgs e) =>
		_uiTaskDispatcher.Post(() =>
		{
			if (!_disposed)
			{
				RefreshSelectedTabDetails();
			}
		});

	private void RefreshSelectedTabDetails()
	{
		if (ViewModel.SelectedSession is { } session)
		{
			_webProcessMetricsMonitor.SetTarget(pageId: null, enabled: false);
			_runtimeCoordinator.TryGetActiveController(
				session.Record.Id,
				out _,
				out var controller);
			_processMetricsMonitor.SetTarget(
				controller?.ProcessId,
				_externalProcessMetricsEnabled);
			ViewModel.TerminalTabStatuses.TryGetDiagnostics(
				session.Record.Id,
				out var diagnostics);
			PublishSelectedTabDetails(
				session,
				SelectedTabDetailsFactory.Create(
					session,
					diagnostics,
					_processMetricsMonitor.Current));
			return;
		}

		_processMetricsMonitor.SetTarget(rootProcessId: null, enabled: false);
		if (ViewModel.SelectedWebPage is { } webPage)
		{
			var isLoaded = _webPageCoordinator.TryGetHost(webPage.Record.Id, out _);
			_webProcessMetricsMonitor.SetTarget(
				isLoaded ? webPage.Record.Id : null,
				_externalProcessMetricsEnabled);
			_webMonitorCoordinator.TryGetLiveDiagnostics(
				webPage.Record.Id,
				out var diagnostics);
			PublishSelectedTabDetails(
				webPage,
				SelectedTabDetailsFactory.Create(
					webPage,
					diagnostics,
					_webProcessMetricsMonitor.Current,
					_externalProcessMetricsEnabled));
			return;
		}

		_webProcessMetricsMonitor.SetTarget(pageId: null, enabled: false);
		_selectedTabDetailsOwner = null;
		SelectedTabDetails = null;
	}

	private void PublishSelectedTabDetails(
		object owner,
		SelectedTabDetailsViewModel snapshot)
	{
		if (ReferenceEquals(_selectedTabDetailsOwner, owner)
			&& SelectedTabDetails is { } current)
		{
			current.UpdateFrom(snapshot);
			return;
		}

		_selectedTabDetailsOwner = owner;
		SelectedTabDetails = snapshot;
	}

	private void OnDocsAndNotesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(DocsAndNotesWorkspaceViewModel.ActiveDocument))
		{
			InvalidateSelectionIfSourceChanged();
		}
	}

	private async Task ApplyWebMonitorRulesAsync(
		IReadOnlyList<WebMonitorRule> rules,
		CancellationToken cancellationToken)
	{
		await _webMonitorCoordinator.SetRulesAsync(rules, cancellationToken);
		RecordDiagnostic(
			"web-monitor-rules-applied",
			$"count={rules.Count}");
		if (_webMonitorStartupRulesApplied)
		{
			return;
		}

		_webMonitorStartupRulesApplied = true;
		PublishWebMonitorPresentationFacts();
	}

	private void PublishWebMonitorPresentationFacts()
	{
		if (!_webMonitorStartupRulesApplied)
		{
			return;
		}

		RecordDiagnostic(
			"web-monitor-presentation-facts",
			$"selected={ViewModel.SelectedWebPage?.Record.Id ?? "<none>"};"
			+ $"visible={_webMonitorWindowVisible};active={_webMonitorWindowActive}");
		_webMonitorCoordinator.SetPresentationFacts(
			ViewModel.SelectedWebPage?.Record.Id,
			_webMonitorWindowVisible,
			_webMonitorWindowActive);
	}

	private void OnWebMonitorStableUrlChanged(
		object? sender,
		WebMonitorStableUrlChangedEventArgs e) =>
		RunEventOperation(
			"web-monitor-stable-url",
			() => ViewModel.UpdateWebPageResumeUrlAsync(
				e.WebPageId,
				e.DocumentUrl.AbsoluteUri,
				title: null,
				CancellationToken.None),
			$"Web page monitoring URL update failed for '{e.WebPageId}'");

	private void OnPasteRequested(object? sender, EventArgs e)
	{
		if (Interlocked.CompareExchange(ref _rightClickPasteInProgress, 1, 0) != 0)
		{
			return;
		}

		if (!RunEventOperation(
			"terminal-paste",
			PasteFromEventAsync,
			"Terminal paste failed"))
		{
			Volatile.Write(ref _rightClickPasteInProgress, 0);
		}
	}

	private async Task PasteFromEventAsync()
	{
		try
		{
			await PasteAsync();
		}
		finally
		{
			Volatile.Write(ref _rightClickPasteInProgress, 0);
		}
	}

	/// <summary>
	/// Shows <paramref name="next"/> in place of whatever was shown, keeping the notes
	/// subscription, the selection-action source and the loading event in step with it.
	/// </summary>
	private void Present(PresentedSurface next)
	{
		var previous = PresentedSurface;
		if (previous == next)
		{
			// The browser surface carries no page, so switching pages presents an equal value
			// while the page, and the project it belongs to, changed.
			UpdateCurrentChanges();
			return;
		}

		PresentedSurface = next;
		var previousDocs = (previous as PresentedSurface.Notes)?.Workspace;
		var nextDocs = (next as PresentedSurface.Notes)?.Workspace;
		if (!ReferenceEquals(previousDocs, nextDocs))
		{
			previousDocs?.PropertyChanged -= OnDocsAndNotesPropertyChanged;
			nextDocs?.PropertyChanged += OnDocsAndNotesPropertyChanged;
			InvalidateSelectionIfSourceChanged();
		}

		var wasLoading = previous is PresentedSurface.Terminal { Loading: true };
		var isLoading = next is PresentedSurface.Terminal { Loading: true };
		if (wasLoading != isLoading)
		{
			TerminalLoadingChanged?.Invoke(this, isLoading);
		}

		OnPropertyChanged(nameof(PresentedSurface));
		RaiseIfChanged(previous is PresentedSurface.Terminal, IsTerminalVisible, nameof(IsTerminalVisible));
		RaiseIfChanged(
			previous is PresentedSurface.PausedSession or PresentedSurface.PausedWebPage,
			IsPausedItemVisible,
			nameof(IsPausedItemVisible));
		RaiseIfChanged(previousDocs, nextDocs, nameof(CurrentDocsAndNotes));
		RaiseIfChanged((previous as PresentedSurface.Notes)?.Document, CurrentNoteDocument, nameof(CurrentNoteDocument));
		RaiseIfChanged((previous as PresentedSurface.Project)?.Screen, CurrentProjectScreen, nameof(CurrentProjectScreen));
		ViewModel.RefreshCurrentProjectState(CurrentProjectScreen?.Workspace);
		UpdateCurrentChanges();
	}

	private void RaiseIfChanged(bool previous, bool current, string propertyName)
	{
		if (previous != current)
		{
			OnPropertyChanged(propertyName);
		}
	}

	private void RaiseIfChanged(object? previous, object? current, string propertyName)
	{
		if (!ReferenceEquals(previous, current))
		{
			OnPropertyChanged(propertyName);
		}
	}

	/// <summary>Drops the notes surface when it is the one shown; anything else stays.</summary>
	private void StopPresentingNotes()
	{
		if (PresentedSurface is PresentedSurface.Notes)
		{
			Present(new PresentedSurface.None());
		}
	}

	private void ClearTerminalLoading(string sessionId)
	{
		if (PresentedSurface is PresentedSurface.Terminal { Loading: true } terminal
			&& string.Equals(terminal.Session.Record.Id, sessionId, StringComparison.Ordinal))
		{
			Present(terminal with { Loading = false });
		}
	}
	private void OnBusyOverlayActionRequested(object? sender, EventArgs e) =>
		BusyOverlayActionRequested?.Invoke(this, EventArgs.Empty);
	private void OnAgentMouseReleased(object? sender, TerminalMouseReleased e)
	{
		var source = CaptureTerminalSelectionSource(e.SessionId);
		if (source is null)
		{
			return;
		}

		var version = Interlocked.Increment(ref _selectionCaptureVersion);
		var baseline = Clipboard.ChangeSequence;
		RunEventOperation(
			"terminal-native-copy",
			() => WatchNativeCopyAsync(version, source, e.Anchor, baseline),
			"Terminal selection actions failed");
	}

	// Agents that own the mouse may copy their selection straight to the native clipboard
	// without telling the terminal. A clipboard change shortly after their mouse release is
	// that copy; an OSC 52 copy or any newer gesture advances the version and ends the watch.
	private async Task WatchNativeCopyAsync(
		int version,
		SelectionActionSourceIdentity source,
		TerminalSelectionAnchor anchor,
		uint baseline)
	{
		for (var waited = TimeSpan.Zero; waited < NativeCopyWindow; waited += NativeCopyPollInterval)
		{
			await Task.Delay(NativeCopyPollInterval, _timeProvider, CancellationToken.None);
			if (version != Volatile.Read(ref _selectionCaptureVersion))
			{
				return;
			}

			if (Clipboard.ChangeSequence == baseline)
			{
				continue;
			}

			var text = await Clipboard.GetTextAsync();
			if (version == Volatile.Read(ref _selectionCaptureVersion)
				&& IsSelectionSourceCurrent(source)
				&& !string.IsNullOrWhiteSpace(text))
			{
				ApplySelectionText(
					version,
					text,
					new SelectionActionAnchor(
						SelectionActionSourceKind.Terminal,
						anchor.X,
						anchor.Y,
						IsAvailable: true),
					source);
			}

			return;
		}
	}

	private void OnCopyRequested(object? sender, TerminalCopyRequest e)
	{
		var source = CaptureTerminalSelectionSource(e.SessionId);
		if (string.IsNullOrWhiteSpace(e.Text) || source is null)
		{
			return;
		}

		var version = Interlocked.Increment(ref _selectionCaptureVersion);
		RunEventOperation(
			"terminal-copy",
			() => CopyFromEventAsync(version, source, e),
			"Terminal copy failed");
	}

	private async Task CopyFromEventAsync(
		int version,
		SelectionActionSourceIdentity source,
		TerminalCopyRequest request)
	{
		if (Clipboard is not null)
		{
			if (!await Clipboard.TrySetTextAsync(request.Text))
			{
				await ReportStatusAsync("Could not copy the terminal selection to the clipboard.");
			}
		}

		if (version != Volatile.Read(ref _selectionCaptureVersion) ||
			!IsSelectionSourceCurrent(source))
		{
			return;
		}

		ApplySelectionText(
			version,
			request.Text,
			new SelectionActionAnchor(
				SelectionActionSourceKind.Terminal,
				request.Anchor?.X ?? 0,
				request.Anchor?.Y ?? 0,
				request.Anchor is not null),
			source);
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private enum SelectionActionOwnerKind
	{
		Root,
		Project
	}

	private sealed record SelectionActionOwnerIdentity(
		SelectionActionOwnerKind Kind,
		string? ProjectId,
		WorkspaceViewModel? Project);

	/// <remarks>
	/// <see cref="Owner"/> is absent for a source that belongs to no project or ROOT
	/// collection, such as the pinned orchestrator terminal. Identity stays valid without it:
	/// the source is established by <see cref="SourceId"/> and <see cref="SourceInstance"/>.
	/// </remarks>
	private sealed record SelectionActionSourceIdentity(
		SelectionActionSourceKind Kind,
		SelectionActionOwnerIdentity? Owner,
		string SourceId,
		object SourceInstance,
		object? ContentInstance);

	private sealed record SelectionActionSnapshot(
		int Version,
		SelectionActionSourceIdentity Source,
		string Text,
		SelectionActionAnchor? Anchor);
}
