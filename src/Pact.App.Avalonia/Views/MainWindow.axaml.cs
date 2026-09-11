using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Pact.App.Avalonia.Controllers;
using Pact.App.Avalonia.Diagnostics;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Platform;
using Pact.App.Avalonia.SelectionActions;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.App.Avalonia.Views.Settings;
using Pact.App.Avalonia.Web;
using Pact.Core.Platform;
using Pact.Core.Prompting;
using Pact.Core.Updates;
using Pact.Presentation.Settings;
using Pact.Presentation.Settings.ViewModels;
using Pact.Presentation.Updates;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class MainWindow : Window, IDisposable
{
	private readonly IFolderPicker? _folderPicker;
	private readonly SettingsFileStore? _settingsFileStore;
	private readonly IProjectSettingsEditor? _projectSettingsEditor;
	private readonly IExternalLauncher? _externalLauncher;
	private readonly WindowLayoutStore? _windowLayoutStore;
	private readonly IUserAttention? _userAttention;
	private readonly ObservedTaskGroup _eventTasks;
	private readonly IUiTaskDispatcher _uiTaskDispatcher;
	private readonly AvaloniaUpdateController? _updateController;
	private readonly List<Action> _eventDetachments = [];
	private bool _closeApproved;
	private bool _closePromptRunning;
	private bool _gracefulShutdownRunning;
	private bool _eventProducersDetached;
	private Task? _gracefulShutdownTask;
	private Flyout? _gitFlyout;
	private int _gitDialogDepth;
	private bool _showSelectedTabDetails = true;
	private bool _showExternalProcessMetrics;

	/// <summary>Exposes the real, already-initialized terminal host to <see cref="EngineProbeRunner"/>.</summary>
	internal AvaloniaTerminalWebViewHost EngineProbeTerminalHost => TerminalPane.WebViewControl.Host;

	/// <summary>Exposes the production shell controller to <see cref="EngineProbeRunner"/>.</summary>
	internal AvaloniaMainShellController EngineProbeController { get; }

	/// <summary>Test-only observability for pane visibility (see MainWindowPaneVisibilityHeadlessTests).</summary>
	internal bool IsBrowserPaneVisible => BrowserPane.IsVisible;
	internal bool IsBrowserLoadingSurfaceVisible => BrowserPane.IsLoadingSurfaceVisible;
	internal bool IsNotesPaneVisible => NotesPane.IsVisible;
	internal Task? ShellInitializationTask { get; private set; }
	internal bool IsTerminalPaneVisible => TerminalPane.IsVisible;
	internal bool IsPausedPaneVisible => PausedPane.IsVisible;
	internal bool IsEmptyPaneVisible => EmptyPane.IsVisible;
	internal Func<SettingsWindow, Task>? ShowSettingsWindowAsyncOverride { get; set; }
	internal Func<MessageDialogRequest, Task<MessageDialogResult>>? ShowMessageDialogAsyncOverride { get; set; }
	internal Func<UpdateRelease, Task<UpdateAvailableDialogResult>>? ShowUpdateAvailableDialogAsyncOverride { get; set; }
	internal Func<string, Task>? ShowUpdateInformationAsyncOverride { get; set; }
	internal Action? StartGracefulShutdownOverride { get; set; }
	internal Func<CancellationToken, Task<SoftRestartRequestResult>>? RequestSoftRestartAsyncOverride { get; set; }
	internal Func<CancellationToken, Task<SoftRestartRequestResult>>? RequestUpdateRestartAsyncOverride { get; set; }
	public MainWindow()
	{
		InitializeComponent();
		Title = AppProfileDefaults.ProductTitle;
		var factory = App.Services.GetRequiredService<AvaloniaShellControllerFactory>();
		var windowServices = factory.WindowServices;
		var appPaths = windowServices.AppPaths;
		var uiTaskDispatcher = windowServices.UiTaskDispatcher;
		var eventTasks = windowServices.EventTasks;
		_uiTaskDispatcher = uiTaskDispatcher;
		_eventTasks = eventTasks;
		var webViewEnvironment =
			AvaloniaWebViewEnvironmentLayout.Create(appPaths.WebViewDirectory);
		WebViewProfileHousekeeping webViewHousekeeping = new(
			new WindowsWebViewProfileDataCleaner(),
			reportFailureAsync: exception => AppLog.AppendAsync(
				appPaths.RootDirectory,
				"WebView cache cleanup failed",
				exception));
		TerminalPane.ConfigureEnvironment(
			webViewEnvironment.TerminalUserDataFolder,
			webViewEnvironment.TerminalProfileName);
		BrowserPane.ConfigureEnvironment(
			webViewEnvironment.BrowserUserDataFolder,
			webViewEnvironment.BrowserProfileName);
		BrowserPane.ConfigureDiagnosticSink(
			WebViewDiagnosticLog.CreateSink(appPaths.LogsDirectory));
		TerminalPane.ConfigureProfileHousekeeping(webViewHousekeeping);
		BrowserPane.ConfigureProfileHousekeeping(webViewHousekeeping);
		TerminalPane.WebViewControl.ConfigureLifecycle(eventTasks, uiTaskDispatcher);
		BrowserPane.ConfigureLifecycle(eventTasks, uiTaskDispatcher);
		EngineProbeController = factory.Create(
			TerminalPane.WebViewControl.Host,
			BrowserPane.Factory);
		_updateController = EngineProbeController.UpdateController;
		ConfigureUpdateController();
		NotesPane.ConfigureLifecycle(
			eventTasks,
			exception => EngineProbeController.ReportUiFailureAsync("Notes", exception),
			EngineProbeController.ReportDocumentSaveFailureAsync);
		_folderPicker = windowServices.FolderPicker;
		_settingsFileStore = windowServices.SettingsFileStore;
		_projectSettingsEditor = windowServices.ProjectSettingsEditor;
		_externalLauncher = windowServices.ExternalLauncher;
		_windowLayoutStore = windowServices.WindowLayoutStore;
		_showSelectedTabDetails = App.CurrentAppearance.ShowSelectedTabDetails;
		_showExternalProcessMetrics = App.CurrentAppearance.ShowExternalProcessMetrics;
		UpdateExternalProcessMetrics();
		ApplyWindowLayout(_windowLayoutStore.Load());
		_userAttention = new AvaloniaUserAttention(this);
		WireAttentionEvents();
		DataContext = EngineProbeController.ViewModel;
		ProjectTree.DataContext = EngineProbeController.ViewModel;
		RightActions.DataContext = EngineProbeController.ViewModel;
		WireProjectTreeEvents();
		WireRightActionsEvents();
		WireSelectionActionsEvents();
		WireScenarioJournalEvents();
		WireControllerEvents();
		RefreshSelectedTabDetails();
		App.Bootstrap.RegisterShellShutdown(
			BeginShellShutdown,
			EngineProbeController.ShutdownAsync);
		Opened += OnOpened;
		Closing += OnClosing;
		_eventDetachments.Add(() => Opened -= OnOpened);
		_eventDetachments.Add(() => Closing -= OnClosing);
	}

	internal MainWindow(
		AvaloniaMainShellController controller,
		SettingsFileStore? settingsFileStore = null,
		IProjectSettingsEditor? projectSettingsEditor = null,
		IExternalLauncher? externalLauncher = null,
		IFolderPicker? folderPicker = null,
		WindowLayoutStore? windowLayoutStore = null,
		IUserAttention? userAttention = null)
	{
		InitializeComponent();
		Title = AppProfileDefaults.ProductTitle;
		EngineProbeController = controller;
		_updateController = controller.UpdateController;
		ConfigureUpdateController();
		_eventTasks = controller.GetEventTasks();
		_uiTaskDispatcher = controller.GetUiTaskDispatcher();
		NotesPane.ConfigureLifecycle(
			_eventTasks,
			exception => EngineProbeController.ReportUiFailureAsync("Notes", exception),
			EngineProbeController.ReportDocumentSaveFailureAsync);
		_folderPicker = folderPicker;
		_settingsFileStore = settingsFileStore;
		_projectSettingsEditor = projectSettingsEditor;
		_externalLauncher = externalLauncher;
		_windowLayoutStore = windowLayoutStore;
		ApplyWindowLayout(_windowLayoutStore?.Load());
		_userAttention = userAttention;
		WireAttentionEvents();
		DataContext = controller.ViewModel;
		ProjectTree.DataContext = controller.ViewModel;
		RightActions.DataContext = controller.ViewModel;
		WireProjectTreeEvents();
		WireRightActionsEvents();
		WireSelectionActionsEvents();
		WireScenarioJournalEvents();
		WireControllerEvents();
		RefreshSelectedTabDetails();
	}

	private void WireControllerEvents()
	{
		EngineProbeController.PropertyChanged += OnControllerPropertyChanged;
		EngineProbeController.BusyOverlayActionRequested += OnControllerBusyOverlayActionRequested;
		EngineProbeController.StatusMessage += OnControllerStatusMessage;
		EngineProbeController.TerminalLoadingChanged += OnTerminalLoadingChanged;
		EngineProbeController.ViewModel.PropertyChanged += OnViewModelPropertyChanged;
		_eventDetachments.Add(() => EngineProbeController.PropertyChanged -= OnControllerPropertyChanged);
		_eventDetachments.Add(() =>
			EngineProbeController.BusyOverlayActionRequested -= OnControllerBusyOverlayActionRequested);
		_eventDetachments.Add(() => EngineProbeController.StatusMessage -= OnControllerStatusMessage);
		_eventDetachments.Add(() =>
			EngineProbeController.TerminalLoadingChanged -= OnTerminalLoadingChanged);
		_eventDetachments.Add(() =>
			EngineProbeController.ViewModel.PropertyChanged -= OnViewModelPropertyChanged);
	}

	private void WireProjectTreeEvents()
	{
		ObserveEvent(
			handler => ProjectTree.SelectOrchestratorRequested += handler,
			handler => ProjectTree.SelectOrchestratorRequested -= handler,
			"select-orchestrator",
			() => EngineProbeController.SelectOrchestratorAsync());
		ObserveEvent(
			handler => ProjectTree.StartOrchestratorRequested += handler,
			handler => ProjectTree.StartOrchestratorRequested -= handler,
			"start-orchestrator",
			() => EngineProbeController.StartOrchestratorAsync());
		ObserveEvent(
			handler => ProjectTree.StopOrchestratorRequested += handler,
			handler => ProjectTree.StopOrchestratorRequested -= handler,
			"stop-orchestrator",
			EngineProbeController.StopOrchestratorAsync);
		ObserveEvent<object?>(
			handler => ProjectTree.SelectedItemChanged += handler,
			handler => ProjectTree.SelectedItemChanged -= handler,
			"select-project-item",
			item => EngineProbeController.SelectItemAsync(item));
		ObserveEvent<WorkspaceViewModel>(
			handler => ProjectTree.PauseProjectRequested += handler,
			handler => ProjectTree.PauseProjectRequested -= handler,
			"pause-project",
			PauseWorkspaceWithConfirmationAsync);
		ObserveEvent<WorkspaceViewModel>(
			handler => ProjectTree.CloseProjectRequested += handler,
			handler => ProjectTree.CloseProjectRequested -= handler,
			"close-project",
			CloseWorkspaceWithConfirmationAsync);
		ObserveEvent(
			handler => ProjectTree.AddProjectRequested += handler,
			handler => ProjectTree.AddProjectRequested -= handler,
			"add-project",
			OpenDirectorySelectionAsync);
		ObserveEvent<WorkspaceViewModel>(
			handler => ProjectTree.ResumePausedProjectRequested += handler,
			handler => ProjectTree.ResumePausedProjectRequested -= handler,
			"resume-project",
			workspace => RunWorkspaceOperationWithBusyOverlayAsync(
				"Restoring project...",
				() => EngineProbeController.ResumeWorkspaceAsync(workspace)));
		ObserveEvent<GitFlyoutRequest>(
			handler => ProjectTree.GitRequested += handler,
			handler => ProjectTree.GitRequested -= handler,
			"open-git",
			OpenGitFlyoutAsync);
		ObserveEvent<WorkspaceActionFlyoutRequest>(
			handler => ProjectTree.AddSessionRequested += handler,
			handler => ProjectTree.AddSessionRequested -= handler,
			"open-shell-profiles",
			request =>
			{
				OpenShellProfileFlyout(request);
				return Task.CompletedTask;
			});
		ObserveEvent<RootActionFlyoutRequest>(
			handler => ProjectTree.AddRootSessionRequested += handler,
			handler => ProjectTree.AddRootSessionRequested -= handler,
			"open-root-shell-profiles",
			request =>
			{
				OpenRootShellProfileFlyout(request);
				return Task.CompletedTask;
			});
		ObserveEvent<RootActionFlyoutRequest>(
			handler => ProjectTree.AddRootWebPageRequested += handler,
			handler => ProjectTree.AddRootWebPageRequested -= handler,
			"open-root-web-link-templates",
			request =>
			{
				OpenRootWebLinkTemplateFlyout(request);
				return Task.CompletedTask;
			});
		ObserveEvent<WorkspaceActionFlyoutRequest>(
			handler => ProjectTree.AddWebPageRequested += handler,
			handler => ProjectTree.AddWebPageRequested -= handler,
			"open-web-link-templates",
			request =>
			{
				OpenWebLinkTemplateFlyout(request);
				return Task.CompletedTask;
			});
		ObserveEvent<WorkspaceViewModel>(
			handler => ProjectTree.NotesToggleRequested += handler,
			handler => ProjectTree.NotesToggleRequested -= handler,
			"toggle-notes",
			workspace => EngineProbeController.ToggleNotesAsync(workspace));
		ObserveEvent<(SessionViewModel Session, bool PreferResumeCommand)>(
			handler => ProjectTree.RestartSessionRequested += handler,
			handler => ProjectTree.RestartSessionRequested -= handler,
			"restart-session",
			request => EngineProbeController.RestartSessionAsync(
				request.Session,
				request.PreferResumeCommand));
		ObserveEvent<SessionViewModel>(
			handler => ProjectTree.CloseSessionRequested += handler,
			handler => ProjectTree.CloseSessionRequested -= handler,
			"close-session",
			CloseSessionWithConfirmationAsync);
		ObserveEvent<SessionViewModel>(
			handler => ProjectTree.PauseRootSessionRequested += handler,
			handler => ProjectTree.PauseRootSessionRequested -= handler,
			"pause-root-session",
			PauseRootSessionWithConfirmationAsync);
		ObserveEvent<SessionViewModel>(
			handler => ProjectTree.ResumeRootSessionRequested += handler,
			handler => ProjectTree.ResumeRootSessionRequested -= handler,
			"resume-root-session",
			session => EngineProbeController.ResumeRootSessionAsync(session));
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.ReloadWebPageRequested += handler,
			handler => ProjectTree.ReloadWebPageRequested -= handler,
			"reload-web-page",
			webPage => EngineProbeController.ReloadWebPageAsync(webPage));
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.CopyWebPageAddressRequested += handler,
			handler => ProjectTree.CopyWebPageAddressRequested -= handler,
			"copy-web-page-address",
			EngineProbeController.CopyWebPageAddressAsync);
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.CloseWebPageRequested += handler,
			handler => ProjectTree.CloseWebPageRequested -= handler,
			"close-web-page",
			webPage => EngineProbeController.CloseWebPageAsync(webPage));
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.PauseRootWebPageRequested += handler,
			handler => ProjectTree.PauseRootWebPageRequested -= handler,
			"pause-root-web-page",
			webPage => EngineProbeController.PauseRootWebPageAsync(webPage));
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.ResumeRootWebPageRequested += handler,
			handler => ProjectTree.ResumeRootWebPageRequested -= handler,
			"resume-root-web-page",
			webPage => EngineProbeController.ResumeRootWebPageAsync(webPage));
		ObserveEvent<ProjectNoteViewModel>(
			handler => ProjectTree.CloseNoteRequested += handler,
			handler => ProjectTree.CloseNoteRequested -= handler,
			"close-note",
			note => EngineProbeController.CloseNoteAsync(note));
		ObserveEvent<WorkspaceViewModel>(
			handler => ProjectTree.EditProjectRequested += handler,
			handler => ProjectTree.EditProjectRequested -= handler,
			"edit-project",
			workspace => OpenSettingsAsync(
				SettingsSection.Projects,
				workspace.Id,
				null));
		ObserveEvent<SessionViewModel>(
			handler => ProjectTree.EditSessionRequested += handler,
			handler => ProjectTree.EditSessionRequested -= handler,
			"edit-session",
			OpenSessionSettingsAsync);
		ObserveEvent<WebPageViewModel>(
			handler => ProjectTree.EditWebPageRequested += handler,
			handler => ProjectTree.EditWebPageRequested -= handler,
			"edit-web-page",
			webPage => webPage.IsRootItem
				? OpenSettingsAsync(SettingsSection.RootTabs, webPage.Record.Id, null)
				: Task.CompletedTask);
		ObserveEvent<TreeItemDropRequest>(
			handler => ProjectTree.TreeItemDropRequested += handler,
			handler => ProjectTree.TreeItemDropRequested -= handler,
			"reorder-tree-item",
			request => EngineProbeController.MoveTreeItemAsync(
				request.Source,
				request.Target,
				request.InsertAfter));
	}

	internal async Task PauseWorkspaceWithConfirmationAsync(WorkspaceViewModel workspace)
	{
		if (!await ConfirmStoppingSessionsAsync(
			"Pause project",
			$"Pause '{workspace.Name}'?",
			workspace.Sessions))
		{
			return;
		}

		await RunWorkspaceOperationWithBusyOverlayAsync(
			"Pausing project...",
			() => EngineProbeController.PauseWorkspaceAsync(workspace));
	}

	internal async Task CloseWorkspaceWithConfirmationAsync(WorkspaceViewModel workspace)
	{
		if (!await ConfirmStoppingSessionsAsync(
			"Close project",
			$"Close and remove '{workspace.Name}'?",
			workspace.Sessions))
		{
			return;
		}

		await EngineProbeController.CloseWorkspaceAsync(workspace);
	}

	internal async Task CloseSessionWithConfirmationAsync(SessionViewModel session)
	{
		if (!await ConfirmStoppingSessionsAsync(
			"Close session",
			$"Close '{session.Title}'?",
			[session]))
		{
			return;
		}

		await EngineProbeController.CloseSessionAsync(session);
	}

	internal async Task PauseRootSessionWithConfirmationAsync(SessionViewModel session)
	{
		if (!await ConfirmStoppingSessionsAsync(
			"Pause session",
			$"Pause '{session.Title}'?",
			[session]))
		{
			return;
		}

		await EngineProbeController.PauseRootSessionAsync(session);
	}

	internal async Task<bool> ConfirmStoppingSessionsAsync(
		string title,
		string actionMessage,
		IEnumerable<SessionViewModel> candidates)
	{
		var activeSessions = EngineProbeController.GetActiveSessions(candidates);
		if (activeSessions.Count == 0)
		{
			return true;
		}

		var processLabel = activeSessions.Count == 1 ? "process" : "processes";
		var sessionList = string.Join(
			Environment.NewLine,
			activeSessions.Select(session => $"• {session.Title}"));
		MessageDialogRequest request = new(
			title,
			$"{actionMessage}\n\n{activeSessions.Count} active terminal {processLabel} will be stopped:"
				+ $"{Environment.NewLine}{sessionList}",
			MessageDialogButtons.YesNo,
			MessageDialogResult.No);
		var result = ShowMessageDialogAsyncOverride is not null
			? await ShowMessageDialogAsyncOverride(request)
			: await MessageDialogWindow.ShowOwnedAsync(this, request);
		return result == MessageDialogResult.Yes;
	}

	private Task OpenSessionSettingsAsync(SessionViewModel session)
	{
		if (session.IsRootItem)
		{
			return OpenSettingsAsync(SettingsSection.RootTabs, session.Record.Id, null);
		}

		var owner = EngineProbeController.ViewModel.Workspaces
			.Concat(EngineProbeController.ViewModel.PausedWorkspaces)
			.FirstOrDefault(workspace => workspace.Sessions.Contains(session));
		if (owner is null)
		{
			return Task.CompletedTask;
		}

		var section = EngineProbeController.ViewModel.PausedWorkspaces.Contains(owner)
			? SettingsSection.PausedProjects
			: SettingsSection.Projects;
		return OpenSettingsAsync(section, owner.Id, session.Record.Id);
	}

	private void ObserveEvent<TEventArgs>(
		Action<EventHandler<TEventArgs>> subscribe,
		Action<EventHandler<TEventArgs>> unsubscribe,
		string operationName,
		Func<TEventArgs, Task> operation)
	{
		void Handler(object? sender, TEventArgs args)
		{
			RunUiEvent(operationName, () => operation(args));
		}

		subscribe(Handler);
		_eventDetachments.Add(() => unsubscribe(Handler));
	}

	private void ObserveEvent(
		Action<EventHandler> subscribe,
		Action<EventHandler> unsubscribe,
		string operationName,
		Func<Task> operation)
	{
		void Handler(object? sender, EventArgs args)
		{
			RunUiEvent(operationName, operation);
		}

		subscribe(Handler);
		_eventDetachments.Add(() => unsubscribe(Handler));
	}

	private void ObserveAction<TEventArgs>(
		Action<EventHandler<TEventArgs>> subscribe,
		Action<EventHandler<TEventArgs>> unsubscribe,
		Action<TEventArgs> action)
	{
		void Handler(object? sender, TEventArgs args)
		{
			action(args);
		}

		subscribe(Handler);
		_eventDetachments.Add(() => unsubscribe(Handler));
	}

	private void ObserveAction(
		Action<EventHandler> subscribe,
		Action<EventHandler> unsubscribe,
		Action action)
	{
		void Handler(object? sender, EventArgs args)
		{
			action();
		}

		subscribe(Handler);
		_eventDetachments.Add(() => unsubscribe(Handler));
	}

	private bool RunUiEvent(string operationName, Func<Task> operation) =>
		_eventTasks.TryRun(
			operationName,
			() => _uiTaskDispatcher.InvokeAsync(operation),
			exception => EngineProbeController.ReportUiFailureAsync("Window", exception));

	private async Task OpenDirectorySelectionAsync()
	{
		if (_folderPicker is null)
		{
			return;
		}

		var recentDirectories =
			await EngineProbeController.LoadRecentDirectoriesAsync();
		var defaultDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
		var initialDirectory = recentDirectories.FirstOrDefault(Directory.Exists)
			?? defaultDirectory;
		DirectorySelectionViewModel viewModel = new(
			recentDirectories,
			initialDirectory);
		DirectorySelectionWindow dialog = new(
			viewModel,
			_folderPicker,
			_eventTasks,
			exception => EngineProbeController.ReportUiFailureAsync("Directory picker", exception));
		var result =
			await dialog.ShowDialog<DirectorySelectionResult?>(this);
		if (result is not null)
		{
			await EngineProbeController.AddProjectFromDirectoryAsync(result.Directory);
		}
	}

	private void WireScenarioJournalEvents()
	{
		ObserveEvent<ScenarioRunViewModel>(
			handler => ScenarioJournal.CloseRunRequested += handler,
			handler => ScenarioJournal.CloseRunRequested -= handler,
			"close-scenario-run",
			run => EngineProbeController.CloseScenarioRunAsync(run));
		ObserveAction<ScenarioRunViewModel>(
			handler => ScenarioJournal.SoftStopRequested += handler,
			handler => ScenarioJournal.SoftStopRequested -= handler,
			EngineProbeController.RequestScenarioSoftStop);
		ObserveAction<ScenarioRunViewModel>(
			handler => ScenarioJournal.PauseRequested += handler,
			handler => ScenarioJournal.PauseRequested -= handler,
			EngineProbeController.PauseScenario);
		ObserveAction<ScenarioRunViewModel>(
			handler => ScenarioJournal.AbortRequested += handler,
			handler => ScenarioJournal.AbortRequested -= handler,
			EngineProbeController.AbortScenario);
		ObserveAction<ScenarioRunViewModel>(
			handler => ScenarioJournal.ResumeRequested += handler,
			handler => ScenarioJournal.ResumeRequested -= handler,
			EngineProbeController.ResumeScenario);
	}

	private void WireRightActionsEvents()
	{
		ObserveEvent<MarkdownTreeNodeViewModel>(
			handler => RightActions.TreeNodeSelected += handler,
			handler => RightActions.TreeNodeSelected -= handler,
			"select-document",
			SelectDocumentAsync);
		ObserveAction<MarkdownTreeNodeViewModel>(
			handler => RightActions.FolderToggleRequested += handler,
			handler => RightActions.FolderToggleRequested -= handler,
			node => EngineProbeController.CurrentDocsAndNotes?.ToggleFolder(node));
		ObserveEvent<PromptTemplateRecord>(
			handler => RightActions.QuickActionRequested += handler,
			handler => RightActions.QuickActionRequested -= handler,
			"quick-action",
			template => EngineProbeController.RunQuickActionAsync(template));
		ObserveEvent(
			handler => RightActions.SettingsRequested += handler,
			handler => RightActions.SettingsRequested -= handler,
			"open-settings",
			() => OpenSettingsAsync(SettingsSection.Projects, null, null));
		ObserveEvent(
			handler => RightActions.RestartAndUpdateRequested += handler,
			handler => RightActions.RestartAndUpdateRequested -= handler,
			"restart-and-update",
			() => RequestRestartAndUpdateAsync(CancellationToken.None));
		ObserveEvent<Core.Scenarios.ScenarioDefinition>(
			handler => RightActions.ScenarioRequested += handler,
			handler => RightActions.ScenarioRequested -= handler,
			"open-scenario",
			OpenScenarioSetupAsync);
	}

	private void WireSelectionActionsEvents()
	{
		ObserveEvent<SessionViewModel>(
			handler => SelectionActions.SendSelectionRequested += handler,
			handler => SelectionActions.SendSelectionRequested -= handler,
			"send-selection",
			SendSelectionToSessionAsync);
		ObserveEvent<ProjectNotesTargetViewModel>(
			handler => SelectionActions.SendSelectionToNotesRequested += handler,
			handler => SelectionActions.SendSelectionToNotesRequested -= handler,
			"send-selection-to-notes",
			SendSelectionToNotesAsync);
		ObserveAction(
			handler => SelectionActions.CloseRequested += handler,
			handler => SelectionActions.CloseRequested -= handler,
			EngineProbeController.CloseSelectionActions);
		ObserveAction<NotesSelectionCompletion>(
			handler => NotesPane.SelectionCompleted += handler,
			handler => NotesPane.SelectionCompleted -= handler,
			completion => EngineProbeController.CompleteNotesSelection(
				completion.Text,
				new SelectionActionAnchor(
					SelectionActionSourceKind.Notes,
					completion.X,
					completion.Y,
					completion.HasAnchor)));
	}

	private async Task SendSelectionToSessionAsync(SessionViewModel session)
	{
		var advancesNotesSource = EngineProbeController.IsNotesSelectionSource;
		if (await EngineProbeController.SendSelectionToSessionAsync(session) &&
			advancesNotesSource)
		{
			NotesPane.BeginSelectionSourceGeneration();
		}
	}

	private async Task SendSelectionToNotesAsync(ProjectNotesTargetViewModel target)
	{
		var advancesNotesSource = EngineProbeController.IsNotesSelectionSource;
		if (await EngineProbeController.SendSelectionToNotesAsync(target) &&
			advancesNotesSource)
		{
			NotesPane.BeginSelectionSourceGeneration();
		}
	}

	private async Task SelectDocumentAsync(MarkdownTreeNodeViewModel node)
	{
		if (EngineProbeController.CurrentDocsAndNotes is not { } workspace)
		{
			return;
		}

		await workspace.SelectDocumentAsync(node, CancellationToken.None);
		NotesPane.FocusEditor();
	}

	private async Task OpenScenarioSetupAsync(Core.Scenarios.ScenarioDefinition definition)
	{
		var workspace = EngineProbeController.ViewModel.SelectedWorkspace;
		if (workspace is null && EngineProbeController.ViewModel.SelectedSession is { } selectedSession)
		{
			workspace = EngineProbeController.ViewModel.Workspaces.FirstOrDefault(candidate =>
				candidate.Sessions.Contains(selectedSession));
		}

		workspace ??= EngineProbeController.ViewModel.Workspaces.FirstOrDefault();
		var setup = EngineProbeController.CreateScenarioSetup(definition, workspace);
		if (setup is null || workspace is null)
		{
			return;
		}

		ScenarioSetupWindow dialog = new(setup);
		var accepted = await dialog.ShowDialog<bool>(this);
		if (!accepted)
		{
			return;
		}

		await EngineProbeController.StartScenarioAsync(
			definition,
			workspace,
			setup,
			CancellationToken.None);
	}

	private async Task OpenSettingsAsync(
		SettingsSection section,
		string? itemId,
		string? subItemId)
	{
		if (_settingsFileStore is null
			|| _projectSettingsEditor is null
			|| _externalLauncher is null
			|| _folderPicker is null)
		{
			return;
		}

		Task<string?> pickDirectoryAsync()
		{
			return _folderPicker.PickFolderAsync(null, "Select project directory");
		}
		using UpdatesSectionViewModel? updatesSection = _updateController is null
			? null
			: new UpdatesSectionViewModel(
				_updateController.Coordinator,
				_uiTaskDispatcher.Post);
		SettingsWindowViewModel viewModel = new(
			_settingsFileStore,
			() => EngineProbeController.ViewModel.Workspaces,
			_projectSettingsEditor,
			pickDirectoryAsync,
			() => EngineProbeController.ViewModel.PausedWorkspaces,
			applyAppearance: preferences =>
			{
				App.ApplyAppearance(preferences);
				_showSelectedTabDetails = preferences.ShowSelectedTabDetails;
				_showExternalProcessMetrics = preferences.ShowExternalProcessMetrics;
				UpdateExternalProcessMetrics();
				RefreshSelectedTabDetails();
			},
			testCurrentWebTabAsync: (rule, cancellationToken) =>
				EngineProbeController.TestWebMonitorRuleOnCurrentTabAsync(
					rule,
					cancellationToken),
			rootTabsProvider: () => EngineProbeController.ViewModel.RootTabs,
			rootTabsEditor: _projectSettingsEditor as IRootTabsSettingsEditor,
			orchestratorSection: EngineProbeController.CreateOrchestratorSectionViewModel(),
			updatesSection: updatesSection);
		using SettingsWindow dialog = new(
			viewModel,
			_externalLauncher,
			pickDirectoryAsync: pickDirectoryAsync,
			eventTasks: _eventTasks,
			reportUserFailureAsync: exception =>
				EngineProbeController.ReportUiFailureAsync("Settings", exception),
			checkForUpdatesAsync: _updateController is null
				? null
				: _updateController.CheckNowAsync,
			openUpdateReleaseNotesAsync: _updateController is null
				? null
				: _updateController.OpenCurrentReleaseNotesAsync,
			openUpdateContainingFolderAsync: _updateController is null
				? null
				: _updateController.OpenContainingFolderAsync,
			softRestartAsync: RequestDiagnosticSoftRestartAsync,
			restartAndUpdateAsync: RequestRestartAndUpdateAsync)
		{
			InitialSection = section,
			InitialItemId = itemId,
			InitialSubItemId = subItemId
		};

		if (ShowSettingsWindowAsyncOverride is { } showOverride)
		{
			await showOverride(dialog);
		}
		else
		{
			await dialog.ShowDialog(this);
		}

		var loaded = await EngineProbeController.ReloadExternalSettingsAsync(
			CancellationToken.None);
		if (dialog.SavedAnyFile && loaded)
		{
			await RefreshSubscriptionUsageOnceAsync(CancellationToken.None);
		}
	}

	internal async Task RequestDiagnosticSoftRestartAsync(CancellationToken cancellationToken)
	{
		var coordinator = EngineProbeController.SoftRestartCoordinator;
		if (coordinator is null && RequestSoftRestartAsyncOverride is null)
		{
			return;
		}

		var request = new MessageDialogRequest(
			"Soft restart",
			"Restart Pact and restore the terminal and browser tabs that are currently live?",
			MessageDialogButtons.YesNo,
			MessageDialogResult.No);
		var confirmation = ShowMessageDialogAsyncOverride is { } showOverride
			? await showOverride(request)
			: await MessageDialogWindow.ShowOwnedAsync(this, request);
		if (confirmation != MessageDialogResult.Yes)
		{
			return;
		}

		var result = RequestSoftRestartAsyncOverride is { } requestOverride
			? await requestOverride(cancellationToken)
			: await coordinator!.RequestRestartOnlyAsync(cancellationToken);
		if (result.Started)
		{
			StartConfirmedSoftRestart();
			return;
		}

		var message = result.Blockers.Count > 0
			? string.Join(Environment.NewLine, result.Blockers.Select(blocker => blocker.Description))
			: "Pact could not start the soft restart helper.";
		var information = new MessageDialogRequest(
			"Soft restart unavailable",
			message,
			MessageDialogButtons.Ok,
			MessageDialogResult.Ok);
		if (ShowMessageDialogAsyncOverride is { } showInformation)
		{
			await showInformation(information);
		}
		else
		{
			await MessageDialogWindow.ShowOwnedAsync(this, information);
		}
	}

	internal async Task RequestRestartAndUpdateAsync(CancellationToken cancellationToken)
	{
		if (_updateController is null && RequestUpdateRestartAsyncOverride is null)
		{
			return;
		}

		var request = new MessageDialogRequest(
			"Restart and update Pact",
			"Restart Pact now, install the verified update, and restore the tabs that are currently live?",
			MessageDialogButtons.YesNo,
			MessageDialogResult.No);
		var confirmation = ShowMessageDialogAsyncOverride is { } showOverride
			? await showOverride(request)
			: await MessageDialogWindow.ShowOwnedAsync(this, request);
		if (confirmation != MessageDialogResult.Yes)
		{
			return;
		}

		var result = RequestUpdateRestartAsyncOverride is { } requestOverride
			? await requestOverride(cancellationToken)
			: await _updateController!.RequestRestartAndUpdateAsync(cancellationToken);
		if (result.Started)
		{
			StartConfirmedSoftRestart();
			return;
		}

		var message = result.Blockers.Count > 0
			? string.Join(Environment.NewLine, result.Blockers.Select(blocker => blocker.Description))
			: "Pact could not start the update helper.";
		var information = new MessageDialogRequest(
			"Restart and update unavailable",
			message,
			MessageDialogButtons.Ok,
			MessageDialogResult.Ok);
		if (ShowMessageDialogAsyncOverride is { } showInformation)
		{
			await showInformation(information);
		}
		else
		{
			await MessageDialogWindow.ShowOwnedAsync(this, information);
		}
	}

	private async Task ShowRestorationSummaryAsync(RestorationSummary summary)
	{
		const int maximumListedItems = 20;
		List<string> lines =
		[
			$"Restored terminal tabs: {summary.RestoredTerminalIds.Count}",
			$"Restored browser tabs: {summary.RestoredWebPageIds.Count}"
		];
		AppendBoundedSection(lines, "Cold starts", summary.ColdStartFallbacks, maximumListedItems);
		AppendBoundedSection(lines, "Could not restore", summary.Failures, maximumListedItems);
		if (!string.IsNullOrWhiteSpace(summary.UpdateFailureCategory))
		{
			lines.Add($"Update was not applied: {summary.UpdateFailureCategory}");
		}

		var request = new MessageDialogRequest(
			"Soft restart complete",
			string.Join(Environment.NewLine, lines),
			MessageDialogButtons.Ok,
			MessageDialogResult.Ok);
		if (ShowMessageDialogAsyncOverride is { } showOverride)
		{
			await showOverride(request);
		}
		else
		{
			await MessageDialogWindow.ShowOwnedAsync(this, request);
		}
	}

	private static void AppendBoundedSection(
		List<string> lines,
		string heading,
		IReadOnlyList<string> items,
		int maximumItems)
	{
		if (items.Count == 0)
		{
			return;
		}

		lines.Add($"{heading}:");
		lines.AddRange(items.Take(maximumItems).Select(item => $"- {item}"));
		if (items.Count > maximumItems)
		{
			lines.Add($"- ... (+{items.Count - maximumItems} more)");
		}
	}

	private async Task OpenGitFlyoutAsync(GitFlyoutRequest request)
	{
		_gitFlyout?.Hide();
		await EngineProbeController.SelectWorkspaceAsync(request.Workspace);
		if (EngineProbeController.CurrentGitPanel is not { } viewModel)
		{
			return;
		}

		GitPanelView panel = new()
		{
			DataContext = viewModel,
			ActionCoordinator = EngineProbeController.CreateGitActionCoordinator(
				ShowGitCommitDialogAsync,
				ShowGitPushDialogAsync,
				ShowGitBranchDialogAsync)
		};
		panel.ConfigureLifecycle(
			_eventTasks,
			exception => EngineProbeController.ReportUiFailureAsync("Git", exception));
		var flyout = CreateGitFlyout(panel, () => _gitDialogDepth > 0);
		flyout.Closed += (_, _) =>
		{
			panel.DetachEventProducers();
			if (ReferenceEquals(_gitFlyout, flyout))
			{
				_gitFlyout = null;
			}
		};
		_gitFlyout = flyout;
		flyout.ShowAt(request.Anchor);
		await panel.RefreshAsync();
	}

	/// <summary>
	/// Builds the Git panel flyout. A transient flyout is dismissed as soon as a modal dialog
	/// takes activation, which would hide the command log exactly when the command the dialog
	/// started writes to it, so <paramref name="keepOpen"/> suppresses dismissal while the panel
	/// itself is showing a dialog.
	/// </summary>
	internal static Flyout CreateGitFlyout(Control content, Func<bool> keepOpen)
	{
		ArgumentNullException.ThrowIfNull(keepOpen);

		Flyout flyout = new()
		{
			Content = content,
			Placement = PlacementMode.RightEdgeAlignedTop,
			ShowMode = FlyoutShowMode.Transient,
			VerticalOffset = -64,
			PlacementConstraintAdjustment =
				PopupPositionerConstraintAdjustment.SlideX |
				PopupPositionerConstraintAdjustment.SlideY
		};
		flyout.Closing += (_, e) => e.Cancel = keepOpen();
		return flyout;
	}

	private async Task<GitCommitDialogResult?> ShowGitCommitDialogAsync(GitCommitDialogViewModel viewModel)
	{
		GitCommitDialog dialog = new(viewModel);
		var accepted = await ShowGitDialogAsync(dialog);
		return accepted ? dialog.Result : null;
	}

	private async Task<GitPushDialogResult?> ShowGitPushDialogAsync(GitPushDialogViewModel viewModel)
	{
		GitPushDialog dialog = new(viewModel);
		var accepted = await ShowGitDialogAsync(dialog);
		return accepted ? dialog.Result : null;
	}

	private async Task<GitBranchPickDialogResult?> ShowGitBranchDialogAsync(GitBranchDialogRequest request)
	{
		GitBranchPickDialog dialog = new(
			request.ViewModel,
			request.Title,
			request.HelpText,
			request.AcceptText);
		var accepted = await ShowGitDialogAsync(dialog);
		return accepted ? dialog.Result : null;
	}

	private async Task<bool> ShowGitDialogAsync(Window dialog)
	{
		_gitDialogDepth++;
		try
		{
			return await dialog.ShowDialog<bool>(this);
		}
		finally
		{
			_gitDialogDepth--;
		}
	}

	private void OpenShellProfileFlyout(WorkspaceActionFlyoutRequest request)
	{
		MenuFlyout flyout = new();
		foreach (var profile in EngineProbeController.ViewModel.ShellProfiles)
		{
			MenuItem item = new() { Header = profile.DisplayName };
			item.Click += (_, _) => RunUiEvent(
				"add-session",
				() => EngineProbeController.AddSessionAsync(request.Workspace, profile));
			flyout.Items.Add(item);
		}
		if (flyout.Items.Count == 0)
		{
			flyout.Items.Add(new MenuItem { Header = "No shell profiles", IsEnabled = false });
		}

		flyout.ShowAt(request.Anchor);
	}

	private void OpenWebLinkTemplateFlyout(WorkspaceActionFlyoutRequest request)
	{
		MenuFlyout flyout = new();
		foreach (var template in EngineProbeController.WebLinkTemplates)
		{
			MenuItem item = new() { Header = template.Title };
			item.Click += (_, _) => RunUiEvent(
				"add-web-page",
				() => EngineProbeController.AddWebPageAsync(request.Workspace, template));
			flyout.Items.Add(item);
		}
		if (flyout.Items.Count == 0)
		{
			flyout.Items.Add(new MenuItem { Header = "No web link templates", IsEnabled = false });
		}
		else
		{
			flyout.Items.Add(new Separator());
		}
		MenuItem customUrl = new() { Header = "Custom URL..." };
		customUrl.Click += (_, _) => RunUiEvent(
			"add-custom-web-page",
			() => OpenCustomProjectUrlAsync(request.Workspace));
		flyout.Items.Add(customUrl);

		flyout.ShowAt(request.Anchor);
	}

	private void OpenRootShellProfileFlyout(RootActionFlyoutRequest request)
	{
		MenuFlyout flyout = new();
		foreach (var profile in EngineProbeController.ViewModel.ShellProfiles)
		{
			MenuItem item = new() { Header = profile.DisplayName };
			item.Click += (_, _) => RunUiEvent(
				"add-root-session",
				() => EngineProbeController.AddRootSessionAsync(profile));
			flyout.Items.Add(item);
		}
		if (flyout.Items.Count == 0)
		{
			flyout.Items.Add(new MenuItem { Header = "No shell profiles", IsEnabled = false });
		}

		flyout.ShowAt(request.Anchor);
	}

	private void OpenRootWebLinkTemplateFlyout(RootActionFlyoutRequest request)
	{
		MenuFlyout flyout = new();
		foreach (var template in EngineProbeController.WebLinkTemplates)
		{
			MenuItem item = new() { Header = template.Title };
			item.Click += (_, _) => RunUiEvent(
				"add-root-web-page",
				() => EngineProbeController.AddRootWebPageAsync(template));
			flyout.Items.Add(item);
		}
		if (flyout.Items.Count == 0)
		{
			flyout.Items.Add(new MenuItem { Header = "No web link templates", IsEnabled = false });
		}
		else
		{
			flyout.Items.Add(new Separator());
		}
		MenuItem customUrl = new() { Header = "Custom URL..." };
		customUrl.Click += (_, _) => RunUiEvent(
			"add-root-custom-web-page",
			OpenCustomRootUrlAsync);
		flyout.Items.Add(customUrl);

		flyout.ShowAt(request.Anchor);
	}

	private async Task OpenCustomProjectUrlAsync(WorkspaceViewModel workspace)
	{
		if (await CustomUrlDialog.ShowOwnedAsync(this) is { } uri)
		{
			await EngineProbeController.AddWebPageAsync(workspace, uri);
		}
	}

	private async Task OpenCustomRootUrlAsync()
	{
		if (await CustomUrlDialog.ShowOwnedAsync(this) is { } uri)
		{
			await EngineProbeController.AddRootWebPageAsync(uri);
		}
	}

	private void OnOpened(object? sender, EventArgs e)
	{
		try
		{
			ShellInitializationTask =
				App.Bootstrap.StartShellAsync(InitializeWindowAsync);
		}
		catch (Exception exception)
		{
			Title = AppProfileDefaults.StartupFailedWindowTitle(exception.Message);
		}
	}

	private async Task InitializeWindowAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();
		EngineProbeController.AttachWorkstationLockMonitor(
			TryGetPlatformHandle()?.Handle ?? IntPtr.Zero);
		PublishTerminalWindowFacts();
		ProjectTree.SetSelectionActivationEnabled(false);
		try
		{
			Uri terminalPage = new(Path.Combine(AppContext.BaseDirectory, "Web", "terminal.html"));
			await EngineProbeController.InitializeAsync(terminalPage, cancellationToken);
			if (_updateController is not null)
			{
				await _updateController.StartAsync(cancellationToken);
			}
			cancellationToken.ThrowIfCancellationRequested();
			ProjectTree.SetProjectActionsEnabled(true);
			StartSubscriptionUsagePolling();
			Title = AppProfileDefaults.ReadyWindowTitle;

			if (App.Bootstrap.LaunchOptions.SoftRestartProbeOutputPath is not null)
			{
				var exitCode = await new SoftRestartProbeRunner(
					App.Bootstrap.LaunchOptions).RunAsync(
						EngineProbeController,
						cancellationToken);
				if (exitCode is null)
				{
					StartConfirmedSoftRestart();
					return;
				}

				await App.Bootstrap.ShutdownAsync();
				_closeApproved = true;
				App.Shutdown(exitCode.Value);
				return;
			}

			if (EngineProbeController.LastRestorationSummary is { } restorationSummary)
			{
				await ShowRestorationSummaryAsync(restorationSummary);
			}

			if (App.Bootstrap.ProbeRunner is { } probeRunner)
			{
				await RunEngineProbeAndExitAsync(probeRunner);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Closing during startup is an expected lifetime outcome.
		}
		catch (Exception exception)
		{
			Title = AppProfileDefaults.StartupFailedWindowTitle(exception.Message);
		}
		finally
		{
			if (!cancellationToken.IsCancellationRequested)
			{
				ProjectTree.SetSelectionActivationEnabled(true);
			}
		}
	}

	private async Task RunEngineProbeAndExitAsync(EngineProbeRunner probeRunner)
	{
		var exitCode = await probeRunner.RunAsync(this, App.Bootstrap.ShutdownAsync);
		_closeApproved = true;
		App.Shutdown(exitCode);
	}

	private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(AvaloniaMainShellController.IsSelectionActionsOpen)
			or nameof(AvaloniaMainShellController.SelectionActionsAnchor))
		{
			RefreshSelectionActionsPopover();
		}

		if (e.PropertyName == nameof(AvaloniaMainShellController.StatusText))
		{
			RightActions.SetStatusText(EngineProbeController.StatusText);
		}

		if (e.PropertyName == nameof(AvaloniaMainShellController.SelectedTabDetails))
		{
			RefreshSelectedTabDetails();
		}

		if (e.PropertyName == nameof(AvaloniaMainShellController.CurrentDocsAndNotes))
		{
			NotesPane.Workspace = EngineProbeController.CurrentDocsAndNotes;
			RightActions.Workspace = EngineProbeController.CurrentDocsAndNotes;
		}

		if (e.PropertyName == nameof(AvaloniaMainShellController.SelectedScenarioRun))
		{
			ScenarioJournal.DataContext = EngineProbeController.SelectedScenarioRun;
		}

		if (e.PropertyName is not (nameof(AvaloniaMainShellController.IsTerminalVisible)
			or nameof(AvaloniaMainShellController.IsPausedItemVisible)
			or nameof(AvaloniaMainShellController.CurrentDocsAndNotes)
			or nameof(AvaloniaMainShellController.SelectedScenarioRun)))
		{
			return;
		}

		RefreshPaneVisibility();
		if (e.PropertyName == nameof(AvaloniaMainShellController.CurrentDocsAndNotes)
			&& EngineProbeController.CurrentDocsAndNotes is not null)
		{
			var workspace = EngineProbeController.CurrentDocsAndNotes;
			Dispatcher.UIThread.Post(
				() =>
				{
					if (ReferenceEquals(EngineProbeController.CurrentDocsAndNotes, workspace)
						&& NotesPane.IsVisible)
					{
						NotesPane.FocusEditor();
					}
				},
				DispatcherPriority.Loaded);
		}
	}

	private void RefreshSelectedTabDetails() => RightActions.SetSelectedTabDetails(
		EngineProbeController.SelectedTabDetails,
		_showSelectedTabDetails);

	private void UpdateExternalProcessMetrics() =>
		EngineProbeController.SetExternalProcessMetricsEnabled(
			_showSelectedTabDetails && _showExternalProcessMetrics);

	private void RefreshSelectionActionsPopover()
	{
		if (EngineProbeController.IsSelectionActionsOpen
			&& EngineProbeController.SelectionActionsAnchor is { } anchor)
		{
			var source = anchor.Source == SelectionActionSourceKind.Terminal
				? TerminalPane
				: NotesPane.SelectionAnchorSource;
			if (!SelectionActions.TryReposition(source, CenterPane, anchor))
			{
				SelectionActions.Open(source, CenterPane, anchor);
			}
		}
		else
		{
			SelectionActions.Close();
		}
	}

	private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(MainWindowViewModel.HasUnreadCompletions))
		{
			OnUnreadCompletionsChanged();
			return;
		}
		if (e.PropertyName != nameof(MainWindowViewModel.SelectedWebPage))
		{
			return;
		}

		RefreshPaneVisibility();
	}

	private void RefreshPaneVisibility()
	{
		BrowserPane.Page = EngineProbeController.ViewModel.SelectedWebPage;
		TerminalPane.IsVisible = EngineProbeController.IsTerminalVisible;
		NotesPane.IsVisible = EngineProbeController.CurrentDocsAndNotes is not null;
		ScenarioJournal.IsVisible = EngineProbeController.SelectedScenarioRun is not null;
		PausedPane.IsVisible = EngineProbeController.IsPausedItemVisible;
		BrowserPane.IsVisible = EngineProbeController.ViewModel.SelectedWebPage is not null
			&& !EngineProbeController.IsTerminalVisible
			&& !EngineProbeController.IsPausedItemVisible;
		EmptyPane.IsVisible = !EngineProbeController.IsTerminalVisible
			&& !EngineProbeController.IsPausedItemVisible
			&& EngineProbeController.CurrentDocsAndNotes is null
			&& EngineProbeController.SelectedScenarioRun is null
			&& EngineProbeController.ViewModel.SelectedWebPage is null;
		UpdateTerminalPresentationVisibility();
	}

	private void UpdateTerminalPresentationVisibility() =>
		RunUiEvent(
			"update-terminal-presentation",
			() => TerminalPane.WebViewControl.Host.SetPresentationVisibleAsync(
				TerminalPane.IsVisible
				&& IsTerminalWindowVisible(IsVisible, WindowState)
				&& WindowForegroundProbe.IsWindowForeground(this)));

	private async Task ShowBusyOverlayAsync(string message, string? actionLabel)
	{
		BusyOverlayText.Text = message;
		BusyOverlayActionButton.Content = actionLabel ?? string.Empty;
		BusyOverlayActionButton.IsVisible = !string.IsNullOrWhiteSpace(actionLabel);
		BusyOverlay.IsVisible = true;
		BusyOverlay.Focus();
		await EngineProbeController.SetBusyOverlayAsync(message, true, true, actionLabel);
	}

	private async Task RunWorkspaceOperationWithBusyOverlayAsync(string message, Func<Task> operation)
	{
		await ShowBusyOverlayAsync(message, actionLabel: null);
		try
		{
			await operation();
		}
		finally
		{
			await HideBusyOverlayAsync();
		}
	}

	private async Task HideBusyOverlayAsync()
	{
		BusyOverlay.IsVisible = false;
		BusyOverlayActionButton.IsVisible = false;
		await EngineProbeController.SetBusyOverlayAsync(string.Empty, false, false);
	}

	private void OnTerminalLoadingChanged(object? sender, bool isLoading) =>
		Dispatcher.UIThread.Post(() =>
		{
			TerminalPane.IsVisible = EngineProbeController.IsTerminalVisible && !isLoading;
			TerminalLoadingSurface.IsVisible = isLoading;
			UpdateTerminalPresentationVisibility();
		});

	public void Dispose()
	{
		DetachEventProducers();
		_usageRefreshCancellation.Dispose();
		_usageRefreshGate.Dispose();
	}

	private void ConfigureUpdateController()
	{
		if (_updateController is null)
		{
			return;
		}

		_updateController.ConfigureHost(
			release => ShowUpdateAvailableDialogAsyncOverride?.Invoke(release)
				?? UpdateAvailableDialogWindow.ShowOwnedAsync(this, release),
			message => ShowUpdateInformationAsyncOverride?.Invoke(message)
				?? ShowUpdateInformationAsync(message));
		_updateController.Coordinator.StatusChanged += OnUpdateStatusChanged;
		_updateController.RestartAndUpdateRequested += OnAutomaticRestartAndUpdateRequested;
		_eventDetachments.Add(() =>
			_updateController.Coordinator.StatusChanged -= OnUpdateStatusChanged);
		_eventDetachments.Add(() =>
			_updateController.RestartAndUpdateRequested -= OnAutomaticRestartAndUpdateRequested);
		RefreshUpdateRestartAction();
	}

	private void OnUpdateStatusChanged(object? sender, UpdateStatusChangedEventArgs e) =>
		_uiTaskDispatcher.Post(RefreshUpdateRestartAction);

	private void OnAutomaticRestartAndUpdateRequested(object? sender, EventArgs e) =>
		_eventTasks.TryRun(
			"offer-restart-and-update",
			() => RequestRestartAndUpdateAsync(CancellationToken.None),
			exception => EngineProbeController.ReportUiFailureAsync("Update restart", exception));

	private void RefreshUpdateRestartAction() =>
		RightActions.SetUpdateRestartAction(
			_updateController?.IsRestartActionVisible == true,
			_updateController?.RestartActionText ?? string.Empty);

	private async Task ShowUpdateInformationAsync(string message)
	{
		await MessageDialogWindow.ShowOwnedAsync(
			this,
			new MessageDialogRequest(
				"Pact updates",
				message,
				MessageDialogButtons.Ok,
				MessageDialogResult.Ok));
	}

	private void DetachEventProducers()
	{
		if (_eventProducersDetached)
		{
			return;
		}

		_eventProducersDetached = true;
		_updateController?.Stop();
		_usageRefreshCancellation.Cancel();
		SelectionActions.DetachEventProducers();
		NotesPane.DetachEventProducers();
		RightActions.Workspace = null;
		if (_gitFlyout?.Content is GitPanelView gitPanel)
		{
			gitPanel.DetachEventProducers();
		}
		_gitFlyout?.Hide();
		for (var index = _eventDetachments.Count - 1; index >= 0; index--)
		{
			_eventDetachments[index]();
		}
		_eventDetachments.Clear();
	}
}
