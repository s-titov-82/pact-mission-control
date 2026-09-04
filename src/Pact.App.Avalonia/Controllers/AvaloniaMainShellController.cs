using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Avalonia.Threading;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Web;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Git;
using Pact.Core.Orchestrator;
using Pact.Core.Platform;
using Pact.Core.Presentation;
using Pact.Core.Projects;
using Pact.Core.Prompting;
using Pact.Core.Scenarios;
using Pact.Core.Sessions;
using Pact.Core.Terminal;
using Pact.Core.Web;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.AgentControl;
using Pact.Infrastructure.Orchestrator;
using Pact.Infrastructure.Storage;
using Pact.Infrastructure.Diagnostics;
using Pact.Presentation.Services;
using Pact.Presentation.Services.AgentControl;
using Pact.Presentation.Services.Orchestrator;
using Pact.Presentation.Services.WebMonitoring;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed class AvaloniaMainShellController : INotifyPropertyChanged, IAsyncDisposable
{
	private static readonly TimeSpan GracefulAgentExitTimeout = TimeSpan.FromSeconds(20);
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
	private readonly AvaloniaScenarioCoordinator _scenarioCoordinator;
	private readonly ReviewProfileProvider _reviewProfileProvider;
	private readonly AgentControlTokenRegistry _agentControlTokens;
	private readonly AgentControlEndpoint _agentControlEndpoint;
	private readonly bool _agentControlEnabled;
	private PactSkillPublication _pactSkillPublication = PactSkillPublication.Empty;
	private readonly OrchestratorStore _orchestratorStore;
	private readonly OrchestratorDispatcher _orchestratorDispatcher;
	private const string OrchestratorSessionId = "pact-orchestrator";
	private OrchestratorRecord _orchestratorRecord = OrchestratorRecord.CreateDefault();
	private SessionViewModel? _orchestratorSession;
	private WorkstationLockMonitor? _workstationLockMonitor;
	private IntPtr _mainWindowHandle;
	private bool _orchestratorIntentionalStop;
	private int _orchestratorConsecutiveFailures;
	private DateTimeOffset _orchestratorStartedAt;
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
	private string? _loadingTerminalSessionId;
	private bool _webMonitorStartupRulesApplied;
	private bool _webMonitorWindowVisible;
	private bool _webMonitorWindowActive;
	private bool _terminalHostInitialized;
	private INotifyPropertyChanged? _selectedDetailsSource;
	private object? _selectedTabDetailsOwner;
	private readonly SelectedProcessMetricsMonitor _processMetricsMonitor;
	private readonly SelectedWebProcessMetricsMonitor _webProcessMetricsMonitor;
	private bool _externalProcessMetricsEnabled;

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
		ScenarioDefinitionStore scenarioDefinitionStore,
		IClipboardService clipboard,
		TimeProvider timeProvider,
		int? agentControlPort = null,
		IProcessTreeSnapshotReader? processTreeSnapshotReader = null,
		IWebProcessMetricsSnapshotReader? webProcessMetricsSnapshotReader = null)
	{
		ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
		_settingsFileStore = settingsFileStore ?? throw new ArgumentNullException(nameof(settingsFileStore));
		_appPaths = appPaths ?? throw new ArgumentNullException(nameof(appPaths));
		_reviewProfileProvider = new ReviewProfileProvider(_appPaths.ReviewProfilesPath);
		_terminalHost = terminalHost ?? throw new ArgumentNullException(nameof(terminalHost));
		ArgumentNullException.ThrowIfNull(backendFactory);
		_resolveCommandAsync = resolveCommandAsync ?? throw new ArgumentNullException(nameof(resolveCommandAsync));
		_runtimeCoordinator = new SessionRuntimeCoordinator(
			terminalHost,
			() => new TerminalController(backendFactory()));
		_gitCliRunner = gitCliRunner ?? throw new ArgumentNullException(nameof(gitCliRunner));
		_executableLocator = executableLocator ?? throw new ArgumentNullException(nameof(executableLocator));
		_uiTaskDispatcher = uiTaskDispatcher ?? throw new ArgumentNullException(nameof(uiTaskDispatcher));
		_timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
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
		ArgumentNullException.ThrowIfNull(scenarioDefinitionStore);
		_scenarioCoordinator = new AvaloniaScenarioCoordinator(
			ViewModel,
			_appPaths,
			scenarioDefinitionStore,
			SendScenarioPromptAndSubmitAsync,
			FindScenarioSession,
			SendScenarioEscapeAsync,
			IsScenarioSessionActive,
			_uiTaskDispatcher,
			SelectScenarioRunAsync,
			ReportStatusAsync,
			_eventTasks);
		_agentControlTokens = new AgentControlTokenRegistry();
		_orchestratorStore = new OrchestratorStore(_appPaths.OrchestratorPath);
		AgentControlHost agentControlHost = new(this, ViewModel, _uiTaskDispatcher);
		AgentControlDispatcher agentControlDispatcher = new(agentControlHost);
		OrchestratorHost orchestratorHost = new(
			ViewModel,
			_uiTaskDispatcher,
			IsLiveSession,
			SendOrchestratorMessageAsync,
			_webPageCoordinator.ResumeInBackgroundAsync,
			_webPageCoordinator.ReadDocumentHtmlAsync,
			() => _orchestratorSession is null ? null : OrchestratorSessionId);
		_orchestratorDispatcher = new OrchestratorDispatcher(orchestratorHost);
		AgentControlJsonRpc agentControlRpc = new(
			caller => caller.IsOrchestrator
				? OrchestratorToolCatalog.BuildToolsListResult()
				: BuildAgentControlToolsList(),
			(call, cancellationToken) => InvokeAgentControlToolAsync(
				agentControlDispatcher,
				call,
				cancellationToken));
		_agentControlEndpoint = new AgentControlEndpoint(_agentControlTokens, agentControlRpc);
		AgentControlAddress = _agentControlEndpoint.Start(
			agentControlPort ?? _settingsFileStore.ReadAgentControlPort());
		_agentControlEnabled = _settingsFileStore.ReadAgentControlEnabled();
		_terminalHost.InputReceived += OnInputReceived;
		_terminalHost.ResizeReceived += OnResizeReceived;
		_terminalHost.ScreenSnapshotReceived += OnScreenSnapshotReceived;
		_terminalHost.SelectionChanged += OnSelectionChanged;
		_terminalHost.SelectionCompleted += OnSelectionCompleted;
		_terminalHost.SelectionDismissed += OnSelectionDismissed;
		_terminalHost.LinkRequested += OnTerminalLinkRequested;
		_terminalHost.BusyOverlayActionRequested += OnBusyOverlayActionRequested;
		_terminalHost.PasteRequested += OnPasteRequested;
		_terminalHost.CopyRequested += OnCopyRequested;
		ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		ViewModel.TerminalTabStatuses.DiagnosticsChanged += OnTerminalDiagnosticsChanged;
		RebindSelectedDetailsSource();
		Clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
	}

	public event PropertyChangedEventHandler? PropertyChanged;
	public event EventHandler<string>? StatusMessage;
	public event EventHandler? BusyOverlayActionRequested;
	/// <summary>Reports whether the selected terminal must remain covered until its first output reaches xterm.</summary>
	public event EventHandler<bool>? TerminalLoadingChanged;

	public MainWindowViewModel ViewModel { get; }
	public IReadOnlyDictionary<string, SessionRuntime> Runtimes => _runtimeCoordinator.Runtimes;
	internal Uri AgentControlAddress { get; }
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
	public ProjectNoteDocument? CurrentNoteDocument { get; private set { if (ReferenceEquals(field, value)) { return; } field = value; OnPropertyChanged(); } }
	/// <summary>Current project documentation workspace displayed in the center pane.</summary>
	public DocsAndNotesWorkspaceViewModel? CurrentDocsAndNotes
	{
		get; private set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.PropertyChanged -= OnDocsAndNotesPropertyChanged;
			field = value;
			field?.PropertyChanged += OnDocsAndNotesPropertyChanged;
			InvalidateSelectionIfSourceChanged();
			OnPropertyChanged();
		}
	}
	public ScenarioRunViewModel? SelectedScenarioRun { get; private set { if (ReferenceEquals(field, value)) { return; } field = value; OnPropertyChanged(); } }
	public GitPanelViewModel? CurrentGitPanel { get; private set { if (ReferenceEquals(field, value)) { return; } field = value; OnPropertyChanged(); } }
	public bool IsTerminalVisible
	{
		get; private set
		{
			if (!value)
			{
				ClearTerminalLoading();
			}

			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}
	/// <summary>
	/// Whether the selected ROOT item is intentionally paused and should show the paused
	/// placeholder instead of starting or loading its surface.
	/// </summary>
	public bool IsPausedItemVisible
	{
		get; private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}
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

	private JsonNode BuildAgentControlToolsList()
	{
		ScenarioDefinition[] scenarios = [];
		_uiTaskDispatcher.Post(() => scenarios = ViewModel.ScenarioDefinitions.ToArray());
		return AgentControlToolCatalog.BuildToolsListResult(
			scenarios,
			_reviewProfileProvider.Current);
	}

	private async Task<AgentControlResultData> InvokeAgentControlToolAsync(
		AgentControlDispatcher dispatcher,
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		if (call.IsOrchestrator)
		{
			return await InvokeOrchestratorToolAsync(call, cancellationToken);
		}

		AgentControlResult result;
		string successText;
		string statusText;
		var sessionId = call.SessionId ?? string.Empty;
		switch (call.ToolName)
		{
			case "pact_get_notes":
				result = await dispatcher.GetNotesAsync(
					sessionId,
					cancellationToken);
				successText = "Notes read.";
				statusText = "Agent action: notes read";
				break;

			case "pact_replace_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var replacementText,
						out var replacementError))
				{
					return replacementError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"expectedRevision",
						out var expectedRevision,
						out var revisionError))
				{
					return revisionError;
				}

				result = await dispatcher.ReplaceNotesAsync(
					sessionId,
					new ReplaceNoteRequest(replacementText, expectedRevision),
					cancellationToken);
				successText = "Notes replaced.";
				statusText = "Agent action: notes replaced";
				break;

			case "pact_append_note":
				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var noteText,
						out var noteError))
				{
					return noteError;
				}

				result = await dispatcher.AppendNoteAsync(
					sessionId,
					new AppendNoteRequest(noteText),
					cancellationToken);
				successText = "Notes updated.";
				statusText = "Agent action: notes updated";
				break;

			case "pact_open_web_tab":
				if (!TryGetRequiredString(
						call.Arguments,
						"url",
						out var url,
						out var urlError))
				{
					return urlError;
				}

				if (!TryGetOptionalString(
						call.Arguments,
						"title",
						out var title,
						out var titleError))
				{
					return titleError;
				}

				result = await dispatcher.OpenWebTabAsync(
					sessionId,
					new OpenWebTabRequest(url, title),
					cancellationToken);
				successText = "Browser tab opened.";
				statusText = "Agent action: browser tab opened";
				break;

			case "pact_request_review":
				if (!TryGetRequiredString(
						call.Arguments,
						"scenarioId",
						out var scenarioId,
						out var scenarioError))
				{
					return scenarioError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"reviewProfileId",
						out var reviewProfileId,
						out var profileError))
				{
					return profileError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"target",
						out var target,
						out var targetError))
				{
					return targetError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"maxIterations",
						out var maxIterations,
						out var iterationsError))
				{
					return iterationsError;
				}

				result = await dispatcher.RequestReviewAsync(
					sessionId,
					new RequestReviewRequest(
						scenarioId,
						reviewProfileId,
						target,
						maxIterations),
					cancellationToken);
				successText = result.Payload ?? "Review run started.";
				statusText = "Agent action: review run started";
				break;

			default:
				return new AgentControlResultData(
					$"Unknown tool '{call.ToolName}'.",
					IsError: true);
		}

		if (!result.Succeeded)
		{
			var failure = result.Failure;
			return new AgentControlResultData(
				failure is null
					? "The agent action was refused."
					: $"{failure.Code}: {failure.Message}",
				IsError: true);
		}

		await _uiTaskDispatcher.InvokeAsync(() => ReportStatusAsync(statusText));
		return new AgentControlResultData(
			result.Payload ?? successText,
			IsError: false);
	}

	private async Task<AgentControlResultData> InvokeOrchestratorToolAsync(
		AgentControlToolCall call,
		CancellationToken cancellationToken)
	{
		AgentControlResult result;
		switch (call.ToolName)
		{
			case "pact_list_workspaces":
				result = _orchestratorDispatcher.ListWorkspaces();
				break;
			case "pact_get_session":
				if (!TryGetRequiredString(
						call.Arguments,
						"sessionId",
						out var sessionId,
						out var sessionError))
				{
					return sessionError;
				}

				if (!TryGetOptionalString(
						call.Arguments,
						"content",
						out var content,
						out var contentError))
				{
					return contentError;
				}

				result = _orchestratorDispatcher.GetSession(
					sessionId,
					content ?? "message");
				break;
			case "pact_send_message":
				if (!TryGetRequiredString(
						call.Arguments,
						"sessionId",
						out var targetSessionId,
						out var targetError))
				{
					return targetError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"message",
						out var text,
						out var textError))
				{
					return textError;
				}

				result = await _orchestratorDispatcher.SendMessageAsync(
					targetSessionId,
					text,
					cancellationToken);
				break;
			case "pact_get_subscription_usage":
				result = _orchestratorDispatcher.GetSubscriptionUsage();
				break;
			case "pact_list_active_runs":
				result = _orchestratorDispatcher.ListActiveRuns();
				break;
			case "pact_get_review_run":
			case "pact_pause_review":
			case "pact_resume_review":
				if (!TryGetRequiredString(
						call.Arguments,
						"runId",
						out var reviewRunId,
						out var reviewRunError))
				{
					return reviewRunError;
				}

				result = call.ToolName switch
				{
					"pact_get_review_run" => _orchestratorDispatcher.GetReviewRun(reviewRunId),
					"pact_pause_review" => _orchestratorDispatcher.PauseReview(reviewRunId),
					_ => _orchestratorDispatcher.ResumeReview(reviewRunId)
				};
				break;
			case "pact_get_project_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var notesWorkspaceId,
						out var notesWorkspaceError))
				{
					return notesWorkspaceError;
				}

				result = await _orchestratorDispatcher.GetProjectNotesAsync(
					notesWorkspaceId,
					cancellationToken);
				break;
			case "pact_replace_project_notes":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var replaceWorkspaceId,
						out var replaceWorkspaceError))
				{
					return replaceWorkspaceError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var replacementText,
						out var replacementTextError))
				{
					return replacementTextError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"expectedRevision",
						out var expectedRevision,
						out var expectedRevisionError))
				{
					return expectedRevisionError;
				}

				result = await _orchestratorDispatcher.ReplaceProjectNotesAsync(
					replaceWorkspaceId,
					new ReplaceNoteRequest(replacementText, expectedRevision),
					cancellationToken);
				break;
			case "pact_append_project_note":
				if (!TryGetRequiredString(
						call.Arguments,
						"workspaceId",
						out var appendWorkspaceId,
						out var appendWorkspaceError))
				{
					return appendWorkspaceError;
				}

				if (!TryGetRequiredString(
						call.Arguments,
						"text",
						out var appendText,
						out var appendTextError))
				{
					return appendTextError;
				}

				result = await _orchestratorDispatcher.AppendProjectNoteAsync(
					appendWorkspaceId,
					appendText,
					cancellationToken);
				break;
			case "pact_list_web_tabs":
				result = _orchestratorDispatcher.ListWebTabs();
				break;
			case "pact_resume_web_tab":
				if (!TryGetRequiredString(
						call.Arguments,
						"pageId",
						out var resumePageId,
						out var resumePageError))
				{
					return resumePageError;
				}

				result = await _orchestratorDispatcher.ResumeWebTabAsync(
					resumePageId,
					cancellationToken);
				break;
			case "pact_get_web_tab_html":
				if (!TryGetRequiredString(
						call.Arguments,
						"pageId",
						out var htmlPageId,
						out var htmlPageError))
				{
					return htmlPageError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"offset",
						out var offset,
						out var offsetError))
				{
					return offsetError;
				}

				if (!TryGetOptionalInt32(
						call.Arguments,
						"maxChars",
						out var maxChars,
						out var maxCharsError))
				{
					return maxCharsError;
				}

				result = await _orchestratorDispatcher.GetWebTabHtmlAsync(
					htmlPageId,
					offset ?? 0,
					maxChars ?? WebPageDocumentRange.DefaultMaxChars,
					cancellationToken);
				break;
			default:
				return new AgentControlResultData(
					$"Unknown tool '{call.ToolName}'.",
					IsError: true);
		}

		return result.Succeeded
			? new AgentControlResultData(result.Payload ?? "Done.", IsError: false)
			: new AgentControlResultData(
				result.Failure is { } failure
					? $"{failure.Code}: {failure.Message}"
					: "The orchestrator action was refused.",
				IsError: true);
	}

	private static bool TryGetRequiredString(
		JsonNode arguments,
		string name,
		out string value,
		out AgentControlResultData error)
	{
		value = string.Empty;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = ArgumentError(name, "is required");
			return false;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value!))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	private static bool TryGetOptionalString(
		JsonNode arguments,
		string name,
		out string? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<string>(out value))
		{
			error = ArgumentError(name, "must be a string");
			return false;
		}

		error = null!;
		return true;
	}

	private static bool TryGetOptionalInt32(
		JsonNode arguments,
		string name,
		out int? value,
		out AgentControlResultData error)
	{
		value = null;
		if (arguments is not JsonObject argumentObject)
		{
			error = new AgentControlResultData(
				"Tool arguments must be a JSON object.",
				IsError: true);
			return false;
		}

		if (argumentObject[name] is null)
		{
			error = null!;
			return true;
		}

		if (argumentObject[name] is not JsonValue node
			|| !node.TryGetValue<int>(out var parsed))
		{
			error = ArgumentError(name, "must be an integer");
			return false;
		}

		value = parsed;
		error = null!;
		return true;
	}

	private static AgentControlResultData ArgumentError(string name, string reason) =>
		new($"Argument '{name}' {reason}.", IsError: true);

	public async Task InitializeAsync(Uri terminalPage, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		await _settingsFileStore.EnsureDefaultFilesAsync(cancellationToken);
		try
		{
			_pactSkillPublication =
				await new PactSkillPublisher(_appPaths).PublishAsync(cancellationToken);
		}
		catch (Exception exception)
		{
			_pactSkillPublication = PactSkillPublication.Empty;
			RecordDiagnostic(
				"pact-skill-publication-failed",
				$"category={exception.GetType().Name}");
		}

		await ViewModel.LoadAsync(cancellationToken);
		await RestoreRetainedWebMonitorStateAsync(cancellationToken);
		await _scenarioCoordinator.CleanupAbandonedExchangesAsync(cancellationToken);
		await ReloadExternalSettingsAsync(cancellationToken);
		await _terminalHost.InitializeAsync(terminalPage, cancellationToken);
		_terminalHostInitialized = true;
		if (_orchestratorRecord.Enabled
			&& _orchestratorRecord.IsProvisioned
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
		ScenarioRunViewModel run => SelectScenarioRunAsync(run),
		WorkspaceViewModel workspace => SelectWorkspaceAsync(workspace),
		_ => Task.CompletedTask
	};

	public async Task SelectSessionAsync(
		SessionViewModel session,
		bool startIfNeeded,
		bool preferResumeCommand = true,
		CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ThrowIfRejectingInput();
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		await HideActiveWebPageAsync(cancellationToken);
		CurrentDocsAndNotes = null;
		CurrentNoteDocument = null;
		SelectedScenarioRun = null;
		if (session.IsManuallyPaused)
		{
			ViewModel.SelectedSession = session;
			await ViewModel.SetActiveItemAsync(session.Record.Id, cancellationToken);
			IsTerminalVisible = false;
			IsPausedItemVisible = true;
			return;
		}

		IsPausedItemVisible = false;
		IsTerminalVisible = true;
		var startsRuntime = startIfNeeded
			&& !_runtimeCoordinator.TryGetActiveController(
				session.Record.Id,
				out _,
				out _);
		if (startsRuntime)
		{
			SetTerminalLoading(session.Record.Id, true);
		}
		else
		{
			ClearTerminalLoading();
		}

		await _runtimeCoordinator.ActivateSessionAsync(
			session,
			PrepareSessionAsync,
			static (_, _) => Task.FromResult<IDisposable?>(null),
			(target, ct) => ActivateRuntimeAsync(target, startIfNeeded, preferResumeCommand, ct),
			_ => _terminalHost.FocusAsync(),
			(target, ct) => ViewModel.UpdateSessionStatusAsync(target.Record.Id, SessionStatus.Failed, ct),
			cancellationToken);
		if (startsRuntime
			&& !_runtimeCoordinator.TryGetActiveController(
				session.Record.Id,
				out _,
				out _))
		{
			SetTerminalLoading(session.Record.Id, false);
		}
	}

	public async Task SelectWebPageAsync(WebPageViewModel webPage, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(webPage);
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		CurrentDocsAndNotes = null;
		CurrentNoteDocument = null;
		SelectedScenarioRun = null;
		ViewModel.SelectedWebPage = webPage;
		await ViewModel.SetActiveItemAsync(webPage.Record.Id, cancellationToken);
		if (webPage.IsManuallyPaused)
		{
			IsTerminalVisible = false;
			IsPausedItemVisible = true;
			return;
		}

		IsPausedItemVisible = false;
		// NativeWebView must enter the visual tree at a non-zero visible size.
		// Showing the pane only after creating/navigating the native child can
		// leave its first frame black until a later visibility cycle.
		IsTerminalVisible = false;
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
		CurrentNoteDocument = document;
		CurrentDocsAndNotes = workspace;
		SelectedScenarioRun = null;
		IsPausedItemVisible = false;
		IsTerminalVisible = false;
	}

	public async Task SelectScenarioRunAsync(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(CancellationToken.None);
		ViewModel.SelectedScenarioRun = run;
		SelectedScenarioRun = run;
		CurrentDocsAndNotes = null;
		CurrentNoteDocument = null;
		IsPausedItemVisible = false;
		IsTerminalVisible = false;
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

	public async Task ApplyOrchestratorSettingsAsync(
		OrchestratorRecord record,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(record);
		var restartRequired = _orchestratorSession is not null
			&& (_orchestratorRecord.Credential != record.Credential
				|| _orchestratorRecord.LaunchCommand != record.LaunchCommand
				|| _orchestratorRecord.WorkingDirectory != record.WorkingDirectory);
		if (restartRequired || !record.Enabled || !record.IsProvisioned)
		{
			await StopOrchestratorAsync();
		}

		_orchestratorRecord = record;
		_agentControlTokens.SetOrchestratorCredential(
			record.Enabled && record.IsProvisioned ? record.Credential : null);
		RefreshWorkstationLockMonitor();
		if (!record.IsProvisioned)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: false,
				isEnabled: record.Enabled,
				isRunning: false,
				stateText: "Not provisioned");
			return;
		}

		if (!record.Enabled)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: false,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		if (!_terminalHostInitialized)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: "Starting");
		}
		else if (!IsOrchestratorRunning)
		{
			await StartOrchestratorAsync(cancellationToken);
		}
	}

	internal OrchestratorSectionViewModel CreateOrchestratorSectionViewModel() =>
		new(
			_orchestratorStore,
			new HermesProvisioner(new HermesCli(_executableLocator)),
			HermesHome.ResolveRoot(),
			AgentControlAddress.AbsoluteUri);

	internal void AttachWorkstationLockMonitor(IntPtr windowHandle)
	{
		if (!OperatingSystem.IsWindows() || windowHandle == IntPtr.Zero)
		{
			return;
		}

		_mainWindowHandle = windowHandle;
		RefreshWorkstationLockMonitor();
	}

	private void RefreshWorkstationLockMonitor()
	{
		var shouldSubscribe = OperatingSystem.IsWindows()
			&& _mainWindowHandle != IntPtr.Zero
			&& _orchestratorRecord.Enabled
			&& _orchestratorRecord.LockDetectionEnabled;
		if (shouldSubscribe && _workstationLockMonitor is null)
		{
			_workstationLockMonitor = new WorkstationLockMonitor(_mainWindowHandle);
			_workstationLockMonitor.LockStateChanged += OnWorkstationLockStateChanged;
			return;
		}

		if (shouldSubscribe || _workstationLockMonitor is null)
		{
			return;
		}

		_workstationLockMonitor.LockStateChanged -= OnWorkstationLockStateChanged;
		_workstationLockMonitor.Dispose();
		_workstationLockMonitor = null;
	}

	internal bool IsOrchestratorRunning =>
		_runtimeCoordinator.TryGetActiveController(
			OrchestratorSessionId,
			out _,
			out _);

	internal async Task StartOrchestratorAsync(CancellationToken cancellationToken = default)
	{
		if (!_orchestratorRecord.Enabled)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: _orchestratorRecord.IsProvisioned,
				isEnabled: false,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		if (!_orchestratorRecord.IsProvisioned)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: false,
				isEnabled: true,
				isRunning: false,
				stateText: "Not provisioned");
			return;
		}

		if (IsOrchestratorRunning)
		{
			return;
		}

		try
		{
			var commandLine = await _resolveCommandAsync(_orchestratorRecord.LaunchCommand, []);
			if (string.IsNullOrWhiteSpace(commandLine))
			{
				throw new InvalidOperationException(
					"The launch command could not be resolved.");
			}

			var now = DateTimeOffset.UtcNow;
			SessionRecord record = new(
				OrchestratorSessionId,
				AgentKind.Hermes,
				"Orchestrator",
				_orchestratorRecord.WorkingDirectory,
				_orchestratorRecord.LaunchCommand,
				ResumeCommand: null,
				SessionStatus.Starting,
				now,
				now);
			_orchestratorSession ??= new SessionViewModel(record);
			_orchestratorSession.UpdateRecord(record);
			ViewModel.OrchestratorSlot.AttachSession(_orchestratorSession);
			ViewModel.TerminalTabStatuses.RegisterSession(_orchestratorSession);
			await _terminalHost.CreateTerminalAsync(OrchestratorSessionId);

			void OutputHandler(object? _, string text)
			{
				OnOutputReceived(OrchestratorSessionId, text);
			}

			Task InputWritingHandler(string input)
			{
				return input.Contains('\r', StringComparison.Ordinal)
								|| input.Contains(Win32InputEncoder.EnterKey, StringComparison.Ordinal)
								? ResetSnapshotBaselineSafeAsync(OrchestratorSessionId)
								: Task.CompletedTask;
			}

			void InputWrittenHandler(object? _, string input)
			{
				ViewModel.TerminalTabStatuses.OnUserInput(
								OrchestratorSessionId,
								input,
								DateTimeOffset.UtcNow);
			}

			void ViewportChangedHandler(object? _, TerminalViewportChangedEventArgs args)
			{
				ViewModel.TerminalTabStatuses.OnViewportChanged(
								OrchestratorSessionId,
								args.Columns,
								args.Rows,
								DateTimeOffset.UtcNow);
			}

			(var columns, var rows) = _terminalHost.GetCurrentSize(OrchestratorSessionId);
			Dictionary<string, string> environment = new(StringComparer.Ordinal)
			{
				["PACT_MCP_URL"] = AgentControlAddress.AbsoluteUri,
				["PACT_MCP_TOKEN"] = _orchestratorRecord.Credential
			};
			_orchestratorIntentionalStop = false;
			_orchestratorStartedAt = _timeProvider.GetUtcNow();
			await _runtimeCoordinator.StartAsync(
				OrchestratorSessionId,
				new TerminalStartOptions(
					commandLine,
					_orchestratorRecord.WorkingDirectory,
					columns,
					rows,
					environment),
				OnOrchestratorExited,
				OutputHandler,
				InputWritingHandler,
				InputWrittenHandler,
				ViewportChangedHandler,
				cancellationToken);
			_orchestratorSession.UpdateRecord(record with { Status = SessionStatus.Running });
			ViewModel.TerminalTabStatuses.OnSessionStarted(
				OrchestratorSessionId,
				TerminalStartMode.Normal,
				DateTimeOffset.UtcNow);
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: true,
				stateText: "Running");
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			var failure = exception;
			try
			{
				await _runtimeCoordinator.StopAsync(OrchestratorSessionId);
			}
			catch (Exception cleanupException)
			{
				failure = new AggregateException(exception, cleanupException);
			}

			ViewModel.TerminalTabStatuses.RemoveSession(OrchestratorSessionId);
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: $"Failed: {failure.Message}");
		}
	}

	internal async Task StopOrchestratorAsync()
	{
		_orchestratorIntentionalStop = true;
		await _runtimeCoordinator.StopAsync(OrchestratorSessionId);
		ViewModel.TerminalTabStatuses.RemoveSession(OrchestratorSessionId);
		_orchestratorSession?.UpdateRecord(
			_orchestratorSession.Record with { Status = SessionStatus.Stopped });

		ViewModel.OrchestratorSlot.Apply(
			isProvisioned: _orchestratorRecord.IsProvisioned,
			isEnabled: _orchestratorRecord.Enabled,
			isRunning: false,
			stateText: _orchestratorRecord.IsProvisioned ? "Stopped" : "Not provisioned");
	}

	internal Task SelectOrchestratorAsync(CancellationToken cancellationToken = default) =>
		_orchestratorSession is not null && IsOrchestratorRunning
			? SelectSessionAsync(
				_orchestratorSession,
				startIfNeeded: false,
				cancellationToken: cancellationToken)
			: Task.CompletedTask;

	private void OnOrchestratorExited(string sessionId, TerminalController controller) =>
		_eventTasks.TryRun(
			"orchestrator-exit",
			() => HandleOrchestratorExitedAsync(controller),
			exception => ReportStatusAsync(
				$"Orchestrator exit cleanup failed: {exception.Message}"));

	private async Task HandleOrchestratorExitedAsync(TerminalController controller)
	{
		if (!await _runtimeCoordinator.HandleControllerExitedAsync(
				OrchestratorSessionId,
				controller))
		{
			return;
		}

		ViewModel.TerminalTabStatuses.RemoveSession(OrchestratorSessionId);
		var intentional = _orchestratorIntentionalStop;
		_orchestratorIntentionalStop = false;
		if (intentional || _shutdownBegun || !_orchestratorRecord.Enabled)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: _orchestratorRecord.Enabled,
				isRunning: false,
				stateText: "Stopped");
			return;
		}

		var ranFor = _timeProvider.GetUtcNow() - _orchestratorStartedAt;
		_orchestratorConsecutiveFailures = ranFor >= TimeSpan.FromMinutes(1)
			? 0
			: _orchestratorConsecutiveFailures + 1;
		var delay = OrchestratorRestartPolicy.NextDelay(
			_orchestratorConsecutiveFailures,
			ranFor);
		if (delay is null)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: true,
				isEnabled: true,
				isRunning: false,
				stateText: "Failed: restart budget exhausted");
			return;
		}

		ViewModel.OrchestratorSlot.Apply(
			isProvisioned: true,
			isEnabled: true,
			isRunning: false,
			stateText: delay == TimeSpan.Zero
				? "Restarting"
				: $"Restarting in {delay.Value.TotalSeconds:0}s");
		await Task.Delay(delay.Value, _timeProvider, CancellationToken.None);
		if (!_shutdownBegun && _orchestratorRecord.Enabled)
		{
			await StartOrchestratorAsync(CancellationToken.None);
		}
	}

	private void OnWorkstationLockStateChanged(object? sender, bool locked)
	{
		_eventTasks.TryRun(
			"orchestrator-lock-state",
			() => DeliverWorkstationLockPromptAsync(locked),
			exception => ReportStatusAsync(
				$"Orchestrator lock prompt failed: {exception.Message}"));
	}

	internal async Task DeliverWorkstationLockPromptAsync(bool locked)
	{
		if (!WorkstationLockPolicy.TryBuildPrompt(
				_orchestratorRecord,
				locked,
				out var prompt))
		{
			return;
		}

		if (_orchestratorSession is null || !IsOrchestratorRunning)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: _orchestratorRecord.IsProvisioned,
				isEnabled: _orchestratorRecord.Enabled,
				isRunning: false,
				stateText: locked
					? "Stopped — lock prompt was not delivered"
					: "Stopped — unlock prompt was not delivered");
			return;
		}

		var screen = ReadScreenState(_orchestratorSession.Record.Id);
		if (screen?.InputRequested == true)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: _orchestratorRecord.IsProvisioned,
				isEnabled: _orchestratorRecord.Enabled,
				isRunning: true,
				stateText: $"Waiting for an answer ({screen.StatusLine}) — prompt not delivered");
			return;
		}

		await _runtimeCoordinator.SendPromptAsync(
			_orchestratorSession,
			prompt,
			submit: true,
			startIfNeeded: false,
			enforceScenarioLock: false,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);
	}

	private bool IsLiveSession(string sessionId) =>
		_runtimeCoordinator.TryGetActiveController(sessionId, out _, out _);

	private Task SendOrchestratorMessageAsync(
		SessionViewModel target,
		string text,
		CancellationToken cancellationToken) =>
		_runtimeCoordinator.SendPromptAsync(
			target,
			text,
			submit: true,
			startIfNeeded: false,
			enforceScenarioLock: true,
			static (_, _) => Task.CompletedTask,
			static _ => Task.CompletedTask);

	public async Task PauseWorkspaceAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		InvalidateSelectionOwner(workspace);
		await AvaloniaScenarioCoordinator.AbortWorkspaceRunsAsync(workspace, cancellationToken);
		await UnloadWorkspaceWebPagesAsync(
			workspace,
			deleteSnapshots: false,
			cancellationToken);
		await CaptureResumeCommandsBeforeStopAsync(workspace.Sessions.ToArray(), cancellationToken);
		foreach (var session in workspace.Sessions.ToArray())
		{
			await StopSessionAsync(session, SessionStatus.Stopped);
		}
		_gitPanelViewModels.Remove(workspace.Id);
		await ViewModel.PauseWorkspaceAsync(workspace.Id, ViewModel.SelectedSession?.Record.Id, cancellationToken);
	}

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
	/// Atomically creates a hidden reviewer session and starts its reserved review run.
	/// </summary>
	[SuppressMessage(
		"Reliability",
		"CA2000:Dispose objects before losing scope",
		Justification = "The registered run is owned and later disposed by MainWindowViewModel.")]
	internal async Task<ReviewStartOutcome> StartAgentRequestedReviewAsync(
		string projectId,
		string authorSessionId,
		RequestReviewRequest request,
		CancellationToken cancellationToken)
	{
		ThrowIfRejectingInput();
		using var reservation = _scenarioCoordinator.TryReserveProjectSlot(
			projectId,
			out var conflict);
		if (reservation is null)
		{
			return new ReviewStartOutcome(null, conflict, FailureMessage: null);
		}

		var workspace = ViewModel.Workspaces
			.Concat(ViewModel.PausedWorkspaces)
			.FirstOrDefault(candidate => string.Equals(
				candidate.Id,
				projectId,
				StringComparison.Ordinal));
		if (workspace is null)
		{
			return FailedReviewStart($"Project '{projectId}' was not found.");
		}

		if (!workspace.Sessions.Any(session => string.Equals(
				session.Record.Id,
				authorSessionId,
				StringComparison.Ordinal)))
		{
			return FailedReviewStart("The author session no longer belongs to this project.");
		}

		var definition = ViewModel.ScenarioDefinitions.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ScenarioId, StringComparison.Ordinal));
		if (definition is null)
		{
			return FailedReviewStart($"Scenario '{request.ScenarioId}' was not found.");
		}

		var profile = _reviewProfileProvider.Current.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ReviewProfileId, StringComparison.Ordinal));
		if (profile is null)
		{
			return FailedReviewStart($"Review profile '{request.ReviewProfileId}' was not found.");
		}

		var reviewerSessionId = Guid.NewGuid().ToString("N");
		var sessionCreated = false;
		var terminalCreated = false;
		try
		{
			var reviewer = await ViewModel.CreateSessionAsync(
				sessionId: reviewerSessionId,
				projectId: projectId,
				kind: profile.Kind,
				title: $"Review · {profile.DisplayName}",
				workingDirectory: workspace.RootPath,
				launchCommand: profile.CommandTemplate,
				resumeCommand: null,
				cancellationToken: cancellationToken,
				workspaceId: workspace.Id,
				select: false);
			sessionCreated = true;
			await _terminalHost.CreateTerminalAsync(reviewerSessionId);
			terminalCreated = true;
			await ActivateRuntimeAsync(
				reviewer,
				startIfNeeded: true,
				preferResumeCommand: false,
				cancellationToken);
			var readiness = await _runtimeCoordinator.WaitForSessionReadyAsync(
				reviewerSessionId,
				reviewer.Record.Kind == AgentKind.Codex,
				() => ReadScreenState(reviewerSessionId),
				cancellationToken);
			if (!readiness.IsReady)
			{
				var rollbackFailure = await RollBackAgentReviewerAsync(
					reviewerSessionId,
					sessionCreated,
					terminalCreated);
				return FailedReviewStart(
					$"The reviewer session could not accept the review ({readiness.StatusLine}); "
						+ "the review was not started."
						+ (rollbackFailure is null ? string.Empty : $" {rollbackFailure}"));
			}

			var reviewerInstructions = definition.ReviewerInstructions
				.FirstOrDefault(instruction => string.Equals(
					instruction.Id,
					definition.DefaultReviewerInstructionId,
					StringComparison.Ordinal))
				?.Text
				?? definition.ReviewerInstructions.FirstOrDefault()?.Text
				?? string.Empty;
			var run = _scenarioCoordinator.StartReservedRun(
				reservation,
				definition,
				workspace,
				new Dictionary<string, string>(StringComparer.Ordinal)
				{
					["author"] = authorSessionId,
					["reviewer"] = reviewerSessionId
				},
				request.Target,
				request.MaxIterations ?? definition.MaxIterations,
				reviewerInstructions);
			return new ReviewStartOutcome(run.RunId, Conflict: null, FailureMessage: null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await RollBackAgentReviewerAsync(
				reviewerSessionId,
				sessionCreated,
				terminalCreated);
			throw;
		}
		catch (Exception exception)
		{
			var rollbackFailure = await RollBackAgentReviewerAsync(
				reviewerSessionId,
				sessionCreated,
				terminalCreated);
			var message = rollbackFailure is null
				? $"Review start failed: {exception.Message}"
				: $"Review start failed: {exception.Message}. Rollback: {rollbackFailure}";
			return FailedReviewStart(message);
		}
	}

	private async Task<string?> RollBackAgentReviewerAsync(
		string sessionId,
		bool sessionCreated,
		bool terminalCreated)
	{
		List<string> failures = [];
		try
		{
			await _runtimeCoordinator.StopAsync(sessionId);
		}
		catch (Exception exception)
		{
			failures.Add($"process stop failed: {exception.Message}");
		}

		if (terminalCreated)
		{
			try
			{
				await _terminalHost.DisposeTerminalAsync(sessionId);
			}
			catch (Exception exception)
			{
				failures.Add($"terminal disposal failed: {exception.Message}");
			}
		}

		if (sessionCreated)
		{
			try
			{
				await ViewModel.RemoveSessionAsync(sessionId, CancellationToken.None);
			}
			catch (Exception exception)
			{
				failures.Add($"session removal failed: {exception.Message}");
			}
		}

		_agentControlTokens.Revoke(sessionId);
		return failures.Count == 0 ? null : string.Join("; ", failures);
	}

	private static ReviewStartOutcome FailedReviewStart(string message) =>
		new(RunId: null, Conflict: null, FailureMessage: message);

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
			CurrentDocsAndNotes = null;
			CurrentNoteDocument = null;

			if (ViewModel.SelectedSession is { } session)
			{
				await SelectSessionAsync(session, startIfNeeded: true, cancellationToken: cancellationToken);
			}
			else if (ViewModel.SelectedWebPage is { } webPage)
			{
				await SelectWebPageAsync(webPage, cancellationToken);
			}
			else
			{
				IsTerminalVisible = false;
			}

			return;
		}

		var note = await ViewModel.ShowNotesTabAsync(workspace.Id, cancellationToken);
		await SelectNoteAsync(note, cancellationToken);
	}

	public async Task CloseWorkspaceAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		InvalidateSelectionOwner(workspace);
		await AvaloniaScenarioCoordinator.AbortWorkspaceRunsAsync(workspace, cancellationToken);
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
		await RestoreSelectedHostAsync(cancellationToken);
	}

	public async Task CloseSessionAsync(SessionViewModel session, CancellationToken cancellationToken = default)
	{
		_scenarioCoordinator.NotifySessionDied(session.Record.Id);
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

		await CaptureResumeCommandsBeforeStopAsync([session], cancellationToken);
		await StopSessionAsync(session, SessionStatus.Stopped);
		await ViewModel.SetRootItemPausedAsync(session.Record.Id, true, cancellationToken);
		if (ReferenceEquals(ViewModel.SelectedSession, session))
		{
			IsTerminalVisible = false;
			IsPausedItemVisible = true;
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
		if (!AgentResetCommands.TryGetResetCommand(session.Record.Kind, out _) || session.IsLockedByScenario)
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
			IsTerminalVisible = false;
			IsPausedItemVisible = true;
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
		CurrentDocsAndNotes = null;
		CurrentNoteDocument = null;
		await RestoreSelectedHostAsync(cancellationToken);
	}

	public void RequestScenarioSoftStop(ScenarioRunViewModel run) =>
		_scenarioCoordinator.RequestSoftStop(run);

	public void PauseScenario(ScenarioRunViewModel run) =>
		_scenarioCoordinator.Pause(run);

	public ScenarioSetupViewModel? CreateScenarioSetup(
		ScenarioDefinition definition,
		WorkspaceViewModel? workspace) =>
		_scenarioCoordinator.CreateSetup(definition, workspace);

	public Task<ScenarioRunViewModel?> StartScenarioAsync(
		ScenarioDefinition definition,
		WorkspaceViewModel workspace,
		ScenarioSetupViewModel setup,
		CancellationToken cancellationToken = default) =>
		_scenarioCoordinator.StartAsync(
			definition,
			workspace,
			setup,
			cancellationToken);

	public void AbortScenario(ScenarioRunViewModel run) =>
		_scenarioCoordinator.Abort(run);

	public void ResumeScenario(ScenarioRunViewModel run) =>
		_scenarioCoordinator.Resume(run);

	public async Task CloseScenarioRunAsync(ScenarioRunViewModel run, CancellationToken cancellationToken = default)
	{
		if (!await _scenarioCoordinator.CloseRunAsync(run, cancellationToken))
		{
			return;
		}

		if (ReferenceEquals(SelectedScenarioRun, run))
		{
			SelectedScenarioRun = null;
		}

		await RestoreSelectedHostAsync(cancellationToken);
	}

	private SessionViewModel? FindScenarioSession(string sessionId)
	{
		SessionViewModel? session = null;
		_uiTaskDispatcher.Post(() => session = ViewModel.FindSession(sessionId));
		return session;
	}

	private Task<PromptDeliveryResult> SendScenarioPromptAndSubmitAsync(
		string sessionId,
		string prompt,
		bool confirmDelivery,
		CancellationToken cancellationToken)
	{
		SessionViewModel? targetSession = null;
		var isCodex = false;
		_uiTaskDispatcher.Post(() =>
		{
			targetSession = ViewModel.FindSession(sessionId)
				?? throw new InvalidOperationException("Target scenario session is not running.");
			if (!_runtimeCoordinator.TryGetActiveController(sessionId, out _, out _))
			{
				throw new InvalidOperationException("Target scenario session is not running.");
			}

			isCodex = targetSession.Record.Kind == AgentKind.Codex;
		});

		return _runtimeCoordinator.WriteScenarioPromptAndSubmitAsync(
			sessionId,
			prompt,
			isCodex,
			confirmDelivery,
			() => ReadScreenState(sessionId),
			cancellationToken);
	}

	private SessionScreenState? ReadScreenState(string sessionId) =>
		ViewModel.TerminalTabStatuses.TryGetScreenState(sessionId, out var state) ? state : null;

	private async Task SendScenarioEscapeAsync(string sessionId)
	{
		SessionRuntime? runtime = null;
		TerminalController? controller = null;
		var isCodex = false;
		_uiTaskDispatcher.Post(() =>
		{
			if (!_runtimeCoordinator.TryGetActiveController(
				sessionId,
				out runtime,
				out controller))
			{
				throw new InvalidOperationException($"Session '{sessionId}' is not running.");
			}

			isCodex = ViewModel.FindSession(sessionId)?.Record.Kind == AgentKind.Codex;
		});

		var input = runtime!.Win32InputMode.IsActive && isCodex
			? Win32InputEncoder.EscapeKey
			: "\u001b";
		if (!await controller!.WriteInputAsync(input))
		{
			throw new InvalidOperationException("Target session escape write failed.");
		}
	}

	private bool IsScenarioSessionActive(string sessionId)
	{
		var active = false;
		_uiTaskDispatcher.Post(() =>
			active = _runtimeCoordinator.TryGetActiveController(sessionId, out _, out _));
		return active;
	}

	public Task WriteInputAsync(string sessionId, string input)
	{
		ThrowIfRejectingInput();
		return _runtimeCoordinator.WriteInputAsync(
			sessionId,
			input,
			id => ViewModel.Sessions.FirstOrDefault(session => session.Record.Id == id)?.IsLockedByScenario == true,
			id => ViewModel.Sessions.FirstOrDefault(session => session.Record.Id == id)?.Record.Kind == AgentKind.Codex,
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
			await _runtimeCoordinator.SendPromptAsync(
				selectedSession,
				text,
				submit: false,
				startIfNeeded: false,
				enforceScenarioLock: true,
				static (_, _) => Task.CompletedTask,
				static _ => Task.CompletedTask);
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

		await RunCleanupStepAsync(StopOrchestratorAsync, failures);
		await RunCleanupStepAsync(() => eventDrain, failures);
		await RunCleanupStepAsync(
			() => _scenarioCoordinator.AbortAllRunsAsync(CancellationToken.None),
			failures);
		await RunCleanupStepAsync(() => ViewModel.FlushAllNoteDocumentsAsync(CancellationToken.None), failures);
		await RunCleanupStepAsync(
			() => CaptureResumeCommandsBeforeStopAsync(ViewModel.Sessions.ToArray(), CancellationToken.None),
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
			() => InvokeUiActionAsync(() => IsTerminalVisible = false),
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
			_processMetricsMonitor.MetricsChanged -= OnProcessMetricsChanged;
			_processMetricsMonitor.Dispose();
			_webProcessMetricsMonitor.MetricsChanged -= OnWebProcessMetricsChanged;
			await _webProcessMetricsMonitor.DisposeAsync();
			if (_workstationLockMonitor is not null)
			{
				_workstationLockMonitor.LockStateChanged -= OnWorkstationLockStateChanged;
				_workstationLockMonitor.Dispose();
			}
			_agentControlEndpoint.Dispose();
			_webPageCoordinator.Dispose();
			_runtimeCoordinator.Dispose();
		}
	}

	private async Task PrepareSessionAsync(SessionViewModel session, CancellationToken cancellationToken)
	{
		ViewModel.SelectedSession = session;
		await ViewModel.SetActiveItemAsync(session.Record.Id, cancellationToken);
	}

	private async Task ActivateRuntimeAsync(
		SessionViewModel session,
		bool startIfNeeded,
		bool preferResumeCommand,
		CancellationToken cancellationToken)
	{
		var runtime = _runtimeCoordinator.GetOrCreateRuntime(session.Record.Id);
		if (runtime.TryGetController(out var existingController)
			&& existingController.IsActive)
		{
			(var columns, var rows) = _terminalHost.GetCurrentSize(session.Record.Id);
			await existingController.ResizeAsync(columns, rows);
			return;
		}
		if (!startIfNeeded)
		{
			return;
		}

		var command = ShellProfileCommandPlanner.GetStartCommand(session.Record, preferResumeCommand);
		var injection = CreateAgentLaunchInjection(session.Record);
		var commandLine = await _resolveCommandAsync(command, injection.Arguments);
		if (string.IsNullOrWhiteSpace(commandLine))
		{
			_agentControlTokens.Revoke(session.Record.Id);
			throw new InvalidOperationException("Session has no launch command.");
		}

		void OutputHandler(object? _, string text)
		{
			OnOutputReceived(session.Record.Id, text);
		}

		Task InputWritingHandler(string input)
		{
			if (input.Contains('\r', StringComparison.Ordinal)
				|| input.Contains(Win32InputEncoder.EnterKey, StringComparison.Ordinal))
			{
				return ResetSnapshotBaselineSafeAsync(session.Record.Id);
			}

			return Task.CompletedTask;
		}

		void InputWrittenHandler(object? _, string input)
		{
			ViewModel.TerminalTabStatuses.OnUserInput(
				session.Record.Id,
				input,
				DateTimeOffset.UtcNow);
		}

		void ViewportChangedHandler(object? _, TerminalViewportChangedEventArgs args)
		{
			ViewModel.TerminalTabStatuses.OnViewportChanged(
				session.Record.Id,
				args.Columns,
				args.Rows,
				DateTimeOffset.UtcNow);
		}

		(var startColumns, var startRows) = _terminalHost.GetCurrentSize(session.Record.Id);
		TerminalStartOptions options = new(
			commandLine,
			session.Record.WorkingDirectory,
			startColumns,
			startRows,
			injection.EnvironmentVariables);
		try
		{
			await _runtimeCoordinator.StartAsync(
				session.Record.Id,
				options,
				OnControllerExited,
				OutputHandler,
				InputWritingHandler,
				InputWrittenHandler,
				ViewportChangedHandler,
				cancellationToken);
			await ViewModel.UpdateSessionStatusAsync(session.Record.Id, SessionStatus.Running, cancellationToken);
			var startMode =
				preferResumeCommand && !string.IsNullOrWhiteSpace(session.Record.ResumeCommand)
					? TerminalStartMode.Resume
					: TerminalStartMode.Normal;
			ViewModel.TerminalTabStatuses.OnSessionStarted(
				session.Record.Id,
				startMode,
				DateTimeOffset.UtcNow);
		}
		catch
		{
			await _runtimeCoordinator.StopAsync(session.Record.Id);
			_agentControlTokens.Revoke(session.Record.Id);
			throw;
		}
	}

	private LaunchInjection CreateAgentLaunchInjection(SessionRecord session)
	{
		_agentControlTokens.Revoke(session.Id);
		var argumentTemplate = _agentControlEnabled
			? AgentControlArgumentTemplates.For(session.Kind)
			: null;
		IReadOnlyList<string> instructionArguments =
			PactInstructionComposer.BuildArguments(
				session.Kind,
				_agentControlEnabled,
				_pactSkillPublication);
		if (argumentTemplate is not { Count: > 0 })
		{
			return new LaunchInjection(
				instructionArguments,
				new Dictionary<string, string>(StringComparer.Ordinal));
		}

		var token = _agentControlTokens.Issue(session.Id);
		try
		{
			return AgentControlLaunchInjection.Create(
				argumentTemplate,
				instructionArguments,
				_appPaths.AgentControlDirectory,
				session.Id,
				AgentControlAddress,
				token);
		}
		catch
		{
			_agentControlTokens.Revoke(session.Id);
			throw;
		}
	}

	private Task SendTextToSessionAsync(SessionViewModel target, string text, bool submit, CancellationToken cancellationToken) =>
		_runtimeCoordinator.SendPromptAsync(
			target,
			text,
			submit,
			startIfNeeded: true,
			enforceScenarioLock: true,
			(session, _) => StartPromptRuntimeAsync(session, cancellationToken),
			static _ => Task.CompletedTask);

	private async Task StartPromptRuntimeAsync(SessionViewModel session, CancellationToken cancellationToken)
	{
		await _terminalHost.CreateTerminalAsync(session.Record.Id);
		await ActivateRuntimeAsync(session, startIfNeeded: true, preferResumeCommand: true, cancellationToken);
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
		else
		{
			CurrentDocsAndNotes = null;
			CurrentNoteDocument = null;
			SelectedScenarioRun = null;
			IsTerminalVisible = false;
			IsPausedItemVisible = false;
		}
	}

	private Task HandleBrowserFailureAsync(string message) =>
		ReportStatusAsync($"Web page navigation failed: {message}");

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

		runtime.Win32InputMode.Scan(displayText);
		runtime.AppendRecentOutput(displayText);
		RunEventOperation(
			"terminal-output",
			() => WriteOutputAsync(sessionId, displayText, runtime),
			"Terminal output failed");
	}

	private void OnScreenSnapshotReceived(object? sender, (string SessionId, string Text, bool Stable) e)
	{
		RefreshWindowFacts?.Invoke();
		ViewModel.TerminalTabStatuses.OnScreenSnapshot(e.SessionId, e.Text, DateTimeOffset.UtcNow, e.Stable);
		if (e.Stable && !string.IsNullOrWhiteSpace(e.Text))
		{
			SetTerminalLoading(e.SessionId, false);
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

	private async Task CaptureResumeCommandsBeforeStopAsync(
		IEnumerable<SessionViewModel> sessions,
		CancellationToken cancellationToken)
	{
		var eligible = sessions
			.Where(IsResumeCaptureEligible)
			.ToArray();
		using CancellationTokenSource deadline =
			new(GracefulAgentExitTimeout, _timeProvider);
		using var linkedCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				deadline.Token);
		var attempts = eligible
			.Select(session => CaptureResumeCommandUnderDeadlineAsync(
				session,
				new ResumeCaptureCancellation(
					deadline.Token,
					cancellationToken),
				linkedCancellation.Token))
			.ToArray();
		var timedOutSessionIds = await Task.WhenAll(attempts);
		foreach (var sessionId in timedOutSessionIds.OfType<string>())
		{
			await ReportStatusAsync($"Resume capture timed out: {sessionId}");
		}
	}

	private bool IsResumeCaptureEligible(SessionViewModel session) =>
		_runtimeCoordinator.TryGetActiveController(
			session.Record.Id,
			out _,
			out _)
		&& session.Record.Kind is AgentKind.Codex or AgentKind.Claude
		&& !AgentResumeCommandExtractor.IsConcreteResumeCommand(session.Record.ResumeCommand);

	private async Task<string?> CaptureResumeCommandUnderDeadlineAsync(
		SessionViewModel session,
		ResumeCaptureCancellation cancellation,
		CancellationToken cancellationToken)
	{
		try
		{
			if (!_runtimeCoordinator.TryGetActiveController(
					session.Record.Id,
					out var runtime,
					out var controller))
			{
				return null;
			}

			await ShowSessionTerminalUnderBusyAsync(session);
			if (await TrySaveResumeCommandFromRecentOutputAsync(session, runtime, cancellationToken))
			{
				return null;
			}

			await ReportStatusAsync($"Capturing resume session id: {session.Title}");
			if (!await controller.WriteInputAsync("/exit"))
			{
				return null;
			}

			await Task.Delay(
				TimeSpan.FromMilliseconds(75),
				_timeProvider,
				cancellationToken);
			var enterInput = runtime.Win32InputMode.IsActive
				&& session.Record.Kind == AgentKind.Codex
					? Win32InputEncoder.EnterKey
					: "\r";
			if (!await controller.WriteInputAsync(enterInput)
				&& !string.Equals(enterInput, "\r", StringComparison.Ordinal))
			{
				await controller.WriteInputAsync("\r");
			}

			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				if (await TrySaveResumeCommandFromRecentOutputAsync(session, runtime, cancellationToken))
				{
					return null;
				}

				await Task.Delay(
					TimeSpan.FromMilliseconds(100),
					_timeProvider,
					cancellationToken);
			}
		}
		catch (OperationCanceledException) when (
			cancellation.Deadline.IsCancellationRequested
			&& !cancellation.Caller.IsCancellationRequested)
		{
			return session.Record.Id;
		}
	}

	private readonly record struct ResumeCaptureCancellation(
		CancellationToken Deadline,
		CancellationToken Caller);

	private async Task ShowSessionTerminalUnderBusyAsync(SessionViewModel session)
	{
		await InvokeUiActionAsync(() =>
		{
			ViewModel.SelectedSession = session;
			IsTerminalVisible = true;
		});
		await _terminalHost.ShowTerminalAsync(session.Record.Id);
	}

	private async Task<bool> TrySaveResumeCommandFromRecentOutputAsync(
		SessionViewModel session,
		SessionRuntime runtime,
		CancellationToken cancellationToken)
	{
		var extracted = AgentResumeCommandExtractor.TryExtract(runtime.GetRecentOutput(), session.Record.Kind);
		var resumeId = AgentResumeCommandExtractor.TryGetResumeId(extracted);
		if (string.IsNullOrWhiteSpace(resumeId))
		{
			return false;
		}

		var resumeCommand = string.IsNullOrWhiteSpace(session.Record.ResumeCommand)
			? extracted
			: AgentResumeCommandExtractor.SetResumeCommandId(
				session.Record.ResumeCommand,
				resumeId);
		if (string.IsNullOrWhiteSpace(resumeCommand)
			|| !AgentResumeCommandExtractor.IsConcreteResumeCommand(resumeCommand))
		{
			return false;
		}

		if (!string.Equals(session.Record.ResumeCommand, resumeCommand, StringComparison.Ordinal))
		{
			await ViewModel.UpdateSessionResumeCommandAsync(session.Record.Id, resumeCommand, cancellationToken);
		}
		return true;
	}

	private void OnControllerExited(string sessionId, TerminalController controller) =>
		_eventTasks.TryRun(
			"terminal-exit",
			() => HandleControllerExitedAsync(sessionId, controller),
			exception => ReportStatusAsync(
				$"Terminal exit cleanup failed: {exception.Message}"));

	private async Task HandleControllerExitedAsync(
		string sessionId,
		TerminalController controller)
	{
		if (!await _runtimeCoordinator.HandleControllerExitedAsync(
				sessionId,
				controller))
		{
			return;
		}

		_agentControlTokens.Revoke(sessionId);
		_scenarioCoordinator.NotifySessionDied(sessionId);
		await _uiTaskDispatcher.InvokeAsync(
			() => ViewModel.UpdateSessionStatusAsync(
				sessionId,
				SessionStatus.Exited,
				CancellationToken.None));
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
		if (e.PropertyName == nameof(MainWindowViewModel.SelectedWebPage))
		{
			PublishWebMonitorPresentationFacts();
		}

		if (e.PropertyName is nameof(MainWindowViewModel.SelectedSession)
			or nameof(MainWindowViewModel.SelectedProjectNote)
			or nameof(MainWindowViewModel.SelectedWebPage)
			or nameof(MainWindowViewModel.SelectedScenarioRun))
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

	private void SetTerminalLoading(string sessionId, bool isLoading)
	{
		if (isLoading)
		{
			var changed = _loadingTerminalSessionId is null;
			_loadingTerminalSessionId = sessionId;
			if (changed)
			{
				TerminalLoadingChanged?.Invoke(this, true);
			}

			return;
		}

		if (!string.Equals(_loadingTerminalSessionId, sessionId, StringComparison.Ordinal))
		{
			return;
		}

		ClearTerminalLoading();
	}

	private void ClearTerminalLoading()
	{
		if (_loadingTerminalSessionId is null)
		{
			return;
		}

		_loadingTerminalSessionId = null;
		TerminalLoadingChanged?.Invoke(this, false);
	}
	private void OnBusyOverlayActionRequested(object? sender, EventArgs e) =>
		BusyOverlayActionRequested?.Invoke(this, EventArgs.Empty);
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
