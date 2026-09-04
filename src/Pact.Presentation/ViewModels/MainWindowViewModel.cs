using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Projects;
using Pact.Core.RootTabs;
using Pact.Core.Prompting;
using Pact.Core.Scenarios;
using Pact.Core.Sessions;
using Pact.Core.Web;
using Pact.Core.Workspaces;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services;
using Pact.Presentation.Settings;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// State behind the main window: the project tree, the current selection, and the persisted
/// mutations that follow from it.
/// </summary>
/// <remarks>
/// Every mutation is applied through <see cref="IProjectStore.UpdateAsync"/> as one
/// read-modify-write, then reflected onto the loaded view models, so persisted state and the
/// tree cannot drift apart. Selection is exclusive across sessions, web pages, scenario runs,
/// and notes: selecting one clears the others.
/// </remarks>
public sealed partial class MainWindowViewModel : INotifyPropertyChanged
{
	private readonly IProjectStore _projectStore;
	private readonly IRootTabsStore _rootTabsStore;
	private readonly RootTabsPersistenceCoordinator _rootPersistence;
	private readonly ProjectPersistenceCoordinator _persistence;
	private readonly ProjectStructurePersistenceCoordinator _structurePersistence;
	private readonly IProjectNotesStore _notesStore;
	private readonly IProjectMarkdownFileStore _markdownFileStore;
	private readonly HashSet<SessionViewModel> _terminalStatusSessions = [];
	private readonly Dictionary<string, DocsAndNotesWorkspaceViewModel> _docsAndNotesWorkspaces =
		new(StringComparer.OrdinalIgnoreCase);
	private static readonly TimeSpan NoteDebounceInterval = TimeSpan.FromMilliseconds(750);
	private SessionViewModel? _selectedSession;
	private ScenarioRunViewModel? _selectedScenarioRun;
	private WebPageViewModel? _selectedWebPage;
	private ProjectNoteViewModel? _selectedProjectNote;

	/// <summary>
	/// Creates the model with default Markdown persistence and a pass-through UI dispatcher.
	/// </summary>
	public MainWindowViewModel(IProjectStore projectStore, IProjectNotesStore notesStore)
		: this(
			projectStore,
			notesStore,
			new ProjectMarkdownFileStore(),
			new TerminalTabStatusCoordinator(action => action()))
	{
	}

	/// <summary>
	/// Creates the model with an explicit tab status coordinator, whose dispatcher decides which
	/// thread indicator updates are raised on.
	/// </summary>
	public MainWindowViewModel(
		IProjectStore projectStore,
		IProjectNotesStore notesStore,
		TerminalTabStatusCoordinator terminalTabStatuses)
		: this(projectStore, notesStore, terminalTabStatuses, null)
	{
	}

	/// <summary>
	/// Creates the model with explicit terminal status and optional ROOT persistence.
	/// </summary>
	public MainWindowViewModel(
		IProjectStore projectStore,
		IProjectNotesStore notesStore,
		TerminalTabStatusCoordinator terminalTabStatuses,
		IRootTabsStore? rootTabsStore)
		: this(
			projectStore,
			notesStore,
			new ProjectMarkdownFileStore(),
			terminalTabStatuses,
			rootTabsStore)
	{
	}

	/// <summary>Creates the main presentation model with explicit project Markdown persistence.</summary>
	public MainWindowViewModel(
		IProjectStore projectStore,
		IProjectNotesStore notesStore,
		IProjectMarkdownFileStore markdownFileStore,
		TerminalTabStatusCoordinator terminalTabStatuses,
		IRootTabsStore? rootTabsStore = null)
	{
		_projectStore = projectStore ?? throw new ArgumentNullException(nameof(projectStore));
		_rootTabsStore = rootTabsStore ?? new VolatileRootTabsStore();
		_rootPersistence = new RootTabsPersistenceCoordinator(_rootTabsStore);
		_persistence = new ProjectPersistenceCoordinator(_projectStore);
		_structurePersistence = new ProjectStructurePersistenceCoordinator(_projectStore);
		_notesStore = notesStore ?? throw new ArgumentNullException(nameof(notesStore));
		_markdownFileStore = markdownFileStore ?? throw new ArgumentNullException(nameof(markdownFileStore));
		TerminalTabStatuses = terminalTabStatuses
			?? throw new ArgumentNullException(nameof(terminalTabStatuses));
		Sessions.CollectionChanged += OnSessionsCollectionChanged;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Open projects, in display order.</summary>
	public ObservableCollection<WorkspaceViewModel> Workspaces { get; } = [];

	/// <summary>Parked projects, which can be restored.</summary>
	public ObservableCollection<WorkspaceViewModel> PausedWorkspaces { get; } = [];

	/// <summary>Project-independent terminal and browser tabs shown above PROJECTS.</summary>
	public RootTabsViewModel RootTabs { get; } =
		new(RootTabsRecord.CreateDefault());

	/// <summary>Singular orchestration tier shown above ROOT.</summary>
	public OrchestratorSlotViewModel OrchestratorSlot { get; } = new();

	/// <summary>Every session across open projects, flattened.</summary>
	public ObservableCollection<SessionViewModel> Sessions { get; } = [];

	/// <summary>Every web page across open projects, flattened.</summary>
	public ObservableCollection<WebPageViewModel> WebPages { get; } = [];

	/// <summary>Every scenario run across open projects, flattened.</summary>
	public ObservableCollection<ScenarioRunViewModel> ScenarioRuns { get; } = [];

	/// <summary>
	/// Sessions offered as "send selection" targets: the selected session's siblings, excluding
	/// any locked by a scenario.
	/// </summary>
	public ObservableCollection<SessionViewModel> SendSelectedTargets { get; } = [];

	/// <summary>
	/// Sessions a prompt template can target, including the selected session itself.
	/// </summary>
	public ObservableCollection<SessionViewModel> PromptTemplateTargets { get; } = [];

	/// <summary>
	/// Templates shown as quick-action buttons for the selected session's agent.
	/// </summary>
	public ObservableCollection<PromptTemplateRecord> VisibleQuickActions { get; } = [];

	/// <summary>Selection-action entries, including non-selectable group headers.</summary>
	public ObservableCollection<SelectionActionChoiceViewModel> SelectionActionChoices { get; } = [];

	/// <summary>Selection-action targets grouped by project.</summary>
	public ObservableCollection<SelectionActionTargetProjectViewModel> SelectionActionTargetProjects { get; } = [];

	/// <summary>
	/// Source-owner group from <see cref="SelectionActionTargetProjects"/>, or <see langword="null"/>
	/// when the selected source owner has no compatible targets.
	/// </summary>
	public SelectionActionTargetProjectViewModel? SelectionActionCompactTargetProject { get; private set; }

	/// <summary>
	/// Whether the selected source owner has no compatible compact targets, even when other
	/// owners remain available through the expanded target list.
	/// </summary>
	public bool HasNoCompactSelectionActionTargets =>
		SelectionActionCompactTargetProject is null;

	/// <summary>
	/// Whether <see cref="SelectionActionTargetProjects"/> contains a compatible target group outside the
	/// selected source owner.
	/// </summary>
	public bool HasAdditionalSelectionActionTargets { get; private set; }

	/// <summary>Configured launch profiles.</summary>
	public ObservableCollection<AgentProfileRecord> ShellProfiles { get; } = [];

	/// <summary>Subscription usage rows, one per profile that reports usage.</summary>
	public ObservableCollection<SubscriptionUsageRow> SubscriptionUsages { get; } = [];

	/// <summary>Coordinator deriving each tab's busy/unread indicator.</summary>
	public TerminalTabStatusCoordinator TerminalTabStatuses { get; }

	/// <summary>Configured prompt templates.</summary>
	public ObservableCollection<PromptTemplateRecord> PromptTemplates { get; } = [];

	/// <summary>Configured scenario definitions.</summary>
	public ObservableCollection<ScenarioDefinition> ScenarioDefinitions { get; } = [];
	/// <summary>Chosen selection action, or <see langword="null"/> when none is selected.</summary>
	public SelectionActionChoiceViewModel? SelectedSelectionAction
	{
		get; set
		{
			if (value is { IsSelectable: false } || ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			RefreshSelectionActionTargetProjects();
			OnPropertyChanged();
		}
	}

	/// <summary>Whether any target is available to receive a selection.</summary>
	public bool HasSelectionActionTargets => SelectionActionTargetProjects.Count > 0;

	/// <summary>Whether no target is available, used to show the empty-state message.</summary>
	public bool HasNoSelectionActionTargets => !HasSelectionActionTargets;

	/// <summary>Returns the selection-action choice to its default entry.</summary>
	public void ResetSelectionActionChoice() => SelectedSelectionAction = SelectionActionChoices.FirstOrDefault();

	/// <summary>Whether any web page is loading, which drives the shared spinner.</summary>
	public bool HasLoadingWebPages => WebPages.Any(page => page.IsLoading);

	/// <summary>
	/// Whether any session has an unseen completion, which drives taskbar attention.
	/// </summary>
	/// <remarks>
	/// A scenario consumes its own sessions' turns, so their completions are not waiting for
	/// anybody. Only a paused run hands its terminals back to the user and is worth interrupting
	/// for.
	/// </remarks>
	public bool HasUnreadCompletions => Sessions.Any(session =>
		session.Indicator == TerminalTabIndicator.Unread
		&& !IsDrivenByAdvancingScenario(session));

	/// <summary>
	/// Selected terminal session, or <see langword="null"/>. Setting this clears the web page,
	/// scenario run, and notes selections.
	/// </summary>
	public SessionViewModel? SelectedSession
	{
		get => _selectedSession;
		set
		{
			if (ReferenceEquals(_selectedSession, value))
			{
				return;
			}

			_selectedSession = value;
			if (value is not null)
			{
				if (_selectedScenarioRun is not null)
				{
					_selectedScenarioRun = null;
					OnPropertyChanged(nameof(SelectedScenarioRun));
				}

				if (_selectedWebPage is not null)
				{
					_selectedWebPage = null;
					OnPropertyChanged(nameof(SelectedWebPage));
				}

				if (_selectedProjectNote is not null)
				{
					_selectedProjectNote = null;
					OnPropertyChanged(nameof(SelectedProjectNote));
				}
			}

			RefreshCurrentTerminalState();
			RefreshCurrentBrowserState();
			RefreshCurrentNoteState();
			RefreshCurrentScenarioState();
			RefreshTerminalActionTargets();
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// Selected scenario run, or <see langword="null"/>. Setting this clears the other selections.
	/// </summary>
	public ScenarioRunViewModel? SelectedScenarioRun
	{
		get => _selectedScenarioRun;
		set
		{
			if (ReferenceEquals(_selectedScenarioRun, value))
			{
				return;
			}

			_selectedScenarioRun = value;
			if (value is not null)
			{
				if (_selectedSession is not null)
				{
					_selectedSession = null;
					OnPropertyChanged(nameof(SelectedSession));
				}

				if (_selectedWebPage is not null)
				{
					_selectedWebPage = null;
					OnPropertyChanged(nameof(SelectedWebPage));
				}

				if (_selectedProjectNote is not null)
				{
					_selectedProjectNote = null;
					OnPropertyChanged(nameof(SelectedProjectNote));
				}

				RefreshCurrentTerminalState();
				RefreshCurrentBrowserState();
				RefreshCurrentNoteState();
				RefreshCurrentScenarioState();
				RefreshTerminalActionTargets();
			}

			RefreshCurrentScenarioState();
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// Selected web page, or <see langword="null"/>. Setting this clears the other selections.
	/// </summary>
	public WebPageViewModel? SelectedWebPage
	{
		get => _selectedWebPage;
		set
		{
			if (ReferenceEquals(_selectedWebPage, value))
			{
				return;
			}

			_selectedWebPage = value;
			if (value is not null)
			{
				if (_selectedSession is not null)
				{
					_selectedSession = null;
					RefreshCurrentTerminalState();
					RefreshCurrentBrowserState();
					RefreshTerminalActionTargets();
					OnPropertyChanged(nameof(SelectedSession));
				}

				if (_selectedScenarioRun is not null)
				{
					_selectedScenarioRun = null;
					OnPropertyChanged(nameof(SelectedScenarioRun));
				}

				if (_selectedProjectNote is not null)
				{
					_selectedProjectNote = null;
					OnPropertyChanged(nameof(SelectedProjectNote));
				}
			}

			RefreshCurrentBrowserState();
			RefreshCurrentNoteState();
			RefreshCurrentScenarioState();
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// Selected notes tab, or <see langword="null"/>. Setting this clears the other selections.
	/// </summary>
	public ProjectNoteViewModel? SelectedProjectNote
	{
		get => _selectedProjectNote;
		set
		{
			if (ReferenceEquals(_selectedProjectNote, value))
			{
				return;
			}

			_selectedProjectNote = value;
			if (value is not null)
			{
				if (_selectedSession is not null)
				{ _selectedSession = null; OnPropertyChanged(nameof(SelectedSession)); }
				if (_selectedScenarioRun is not null)
				{ _selectedScenarioRun = null; OnPropertyChanged(nameof(SelectedScenarioRun)); }
				if (_selectedWebPage is not null)
				{ _selectedWebPage = null; OnPropertyChanged(nameof(SelectedWebPage)); }
			}
			RefreshCurrentTerminalState();
			RefreshCurrentBrowserState();
			RefreshTerminalActionTargets();
			RefreshCurrentNoteState();
			RefreshCurrentScenarioState();
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// Project owning the selected item, kept in sync with whichever item is selected.
	/// </summary>
	public WorkspaceViewModel? SelectedWorkspace
	{
		get; set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	/// <summary>
	/// Loads all projects and restores the previously active item. Sessions persisted as running
	/// are normalized to stopped first, since no process survives a restart.
	/// </summary>
	public async Task LoadAsync(CancellationToken cancellationToken)
	{
		var rootTabsRecord = await _rootTabsStore.LoadAsync(cancellationToken);
		var document = await _projectStore.LoadAsync(cancellationToken);
		document = await NormalizeTransientSessionStatusesAsync(document, cancellationToken);
		Workspaces.Clear();
		PausedWorkspaces.Clear();
		Sessions.Clear();
		WebPages.Clear();
		foreach (var run in ScenarioRuns)
		{
			run.PropertyChanged -= OnScenarioRunPropertyChanged;
		}

		ScenarioRuns.Clear();
		PromptTemplates.Clear();
		RootTabs.UpdateRecord(rootTabsRecord);
		foreach (var session in RootTabs.Sessions.Where(session => !session.IsManuallyPaused))
		{
			Sessions.Add(session);
		}

		foreach (var webPage in RootTabs.WebPages.Where(webPage => !webPage.IsManuallyPaused))
		{
			WebPages.Add(webPage);
		}

		foreach (var project in document.Projects)
		{
			var projectViewModel = CreateWorkspaceViewModel(project);
			if (project.Status == WorkspaceStatus.Paused)
			{
				PausedWorkspaces.Add(projectViewModel);
				continue;
			}

			Workspaces.Add(projectViewModel);
			foreach (var session in projectViewModel.Sessions)
			{
				Sessions.Add(session);
			}

			foreach (var webPage in projectViewModel.WebPages)
			{
				WebPages.Add(webPage);
			}
		}

		SelectedWorkspace = Workspaces.FirstOrDefault();
		if (!SelectRootStartupItem())
		{
			SelectStartupItem(SelectedWorkspace);
		}
		RefreshTerminalActionTargets();
	}

	private bool SelectRootStartupItem()
	{
		if (string.IsNullOrWhiteSpace(RootTabs.Record.ActiveItemId))
		{
			return false;
		}

		var session = RootTabs.Sessions.FirstOrDefault(item =>
			string.Equals(item.Record.Id, RootTabs.Record.ActiveItemId, StringComparison.Ordinal));
		if (session is not null)
		{
			SelectedWorkspace = null;
			SelectedSession = session;
			return true;
		}

		var webPage = RootTabs.WebPages.FirstOrDefault(item =>
			string.Equals(item.Record.Id, RootTabs.Record.ActiveItemId, StringComparison.Ordinal));
		if (webPage is null)
		{
			return false;
		}

		SelectedWorkspace = null;
		SelectedWebPage = webPage;
		return true;
	}

	private async Task<ProjectsDocument> NormalizeTransientSessionStatusesAsync(
		ProjectsDocument document,
		CancellationToken cancellationToken)
	{
		var changed = false;
		var normalized = document with
		{
			Projects = document.Projects
				.Select(project =>
				{
					var sessions = project.Sessions
						.Select(session =>
						{
							if (session.Status is not (SessionStatus.Starting or SessionStatus.Running))
							{
								return session;
							}

							changed = true;
							return session with { Status = SessionStatus.Stopped };
						})
						.ToArray();

					return changed && !project.Sessions.SequenceEqual(sessions)
						? project with { Sessions = sessions }
						: project;
				})
				.ToArray()
		};

		if (changed)
		{
			await _projectStore.SaveAsync(normalized, cancellationToken);
		}

		return normalized;
	}

	/// <summary>Finds a loaded session by id, or <see langword="null"/> when it is not loaded.</summary>
	public SessionViewModel? FindSession(string sessionId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		return FindSessionViewModel(sessionId);
	}

	/// <summary>
	/// Moves a terminal or browser tab relative to a peer of the same type and owner, persisting
	/// the new order before changing the observable projection.
	/// </summary>
	public async Task<bool> MoveTreeItemAsync(
		object source,
		object target,
		bool insertAfter,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(source);
		ArgumentNullException.ThrowIfNull(target);
		if (ReferenceEquals(source, target) || source.GetType() != target.GetType())
		{
			return false;
		}

		if (source is SessionViewModel sourceSession && target is SessionViewModel targetSession)
		{
			if (!WouldMove(
				sourceSession.IsRootItem
					? RootTabs.Sessions
					: Workspaces.Concat(PausedWorkspaces)
						.FirstOrDefault(workspace => workspace.Sessions.Contains(sourceSession))
						?.Sessions,
				sourceSession,
				targetSession,
				insertAfter))
			{
				return false;
			}

			return await MoveSessionAsync(sourceSession, targetSession, insertAfter, cancellationToken);
		}

		if (source is WebPageViewModel sourceWebPage && target is WebPageViewModel targetWebPage)
		{
			if (!WouldMove(
				sourceWebPage.IsRootItem
					? RootTabs.WebPages
					: Workspaces.Concat(PausedWorkspaces)
						.FirstOrDefault(workspace => workspace.WebPages.Contains(sourceWebPage))
						?.WebPages,
				sourceWebPage,
				targetWebPage,
				insertAfter))
			{
				return false;
			}

			return await MoveWebPageAsync(sourceWebPage, targetWebPage, insertAfter, cancellationToken);
		}

		return false;
	}

	/// <summary>Advances the spinner frame on every loading web page.</summary>
	public void SetLoadingWebPageGlyphs(string glyph)
	{
		foreach (var page in WebPages.Where(page => page.IsLoading))
		{
			page.SetLoadingGlyph(glyph);
		}
	}

	/// <summary>
	/// Locks or unlocks the listed sessions for a run. Unlocking only affects sessions locked by
	/// that same run, so one run cannot release another run's locks.
	/// </summary>
	public void SetScenarioLocks(string runId, IEnumerable<string> sessionIds, bool locked)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(runId);
		ArgumentNullException.ThrowIfNull(sessionIds);

		var changed = false;
		foreach (var sessionId in sessionIds)
		{
			var session = FindSessionViewModel(sessionId);
			if (session is null)
			{
				continue;
			}

			if (locked)
			{
				changed |= !session.IsLockedByScenario
					|| !string.Equals(session.LockedByScenarioRunId, runId, StringComparison.Ordinal);
				session.LockForScenario(runId);
				continue;
			}

			if (string.Equals(session.LockedByScenarioRunId, runId, StringComparison.Ordinal))
			{
				session.UnlockFromScenario();
				changed = true;
			}
		}

		if (changed)
		{
			RefreshTerminalActionTargets();
		}
	}

	/// <summary>Adds a run under its project, optionally preserving the current selection.</summary>
	public void AddScenarioRun(
		string workspaceId,
		ScenarioRunViewModel run,
		bool select = true)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		ArgumentNullException.ThrowIfNull(run);

		var workspace = FindWorkspaceViewModel(workspaceId)
			?? throw new InvalidOperationException($"Project '{workspaceId}' was not found.");
		if (!workspace.ScenarioRuns.Contains(run))
		{
			workspace.ScenarioRuns.Add(run);
		}

		if (!ScenarioRuns.Contains(run))
		{
			run.PropertyChanged += OnScenarioRunPropertyChanged;
			ScenarioRuns.Add(run);
			OnPropertyChanged(nameof(HasUnreadCompletions));
		}

		if (select)
		{
			SelectedWorkspace = workspace;
			SelectedScenarioRun = run;
		}
	}

	/// <summary>Removes a run from the window and its project.</summary>
	public void RemoveScenarioRun(ScenarioRunViewModel run)
	{
		ArgumentNullException.ThrowIfNull(run);

		foreach (var workspace in Workspaces.Concat(PausedWorkspaces))
		{
			workspace.ScenarioRuns.Remove(run);
		}

		if (ScenarioRuns.Remove(run))
		{
			run.PropertyChanged -= OnScenarioRunPropertyChanged;
			OnPropertyChanged(nameof(HasUnreadCompletions));
		}

		if (ReferenceEquals(SelectedScenarioRun, run))
		{
			SelectedScenarioRun = null;
		}

		run.Dispose();
	}

	/// <summary>
	/// Replaces the configured prompt templates and recomputes the quick actions and targets.
	/// </summary>
	public void ReplacePromptTemplates(IEnumerable<PromptTemplateRecord> promptTemplates)
	{
		ArgumentNullException.ThrowIfNull(promptTemplates);

		PromptTemplates.Clear();
		foreach (var promptTemplate in promptTemplates)
		{
			PromptTemplates.Add(promptTemplate);
		}

		RefreshPromptActionCollections();
	}

	/// <summary>Replaces the configured launch profiles and rebuilds the usage rows.</summary>
	public void ReplaceShellProfiles(IEnumerable<AgentProfileRecord> shellProfiles)
	{
		ArgumentNullException.ThrowIfNull(shellProfiles);

		ShellProfiles.Clear();
		foreach (var shellProfile in shellProfiles)
		{
			ShellProfiles.Add(shellProfile);
		}

		ReplaceSubscriptionUsageRows(shellProfiles);
	}

	/// <summary>Replaces the configured scenario definitions.</summary>
	public void ReplaceScenarioDefinitions(IEnumerable<ScenarioDefinition> scenarioDefinitions)
	{
		ArgumentNullException.ThrowIfNull(scenarioDefinitions);

		ScenarioDefinitions.Clear();
		foreach (var scenarioDefinition in scenarioDefinitions)
		{
			ScenarioDefinitions.Add(scenarioDefinition);
		}
	}

	/// <summary>
	/// Creates a session with a generated id and persists it under the project. Selection may be
	/// suppressed for automatic reviewer creation.
	/// </summary>
	public async Task<SessionViewModel> CreateSessionAsync(
		string projectId,
		AgentKind kind,
		string title,
		string workingDirectory,
		string launchCommand,
		string? resumeCommand,
		CancellationToken cancellationToken,
		string? workspaceId = null,
		bool select = true) => await CreateSessionAsync(
			Guid.NewGuid().ToString("N"),
			projectId,
			kind,
			title,
			workingDirectory,
			launchCommand,
			resumeCommand,
			cancellationToken,
			workspaceId,
			select);

	/// <summary>
	/// Creates a session with an explicit id and persists it under the project. Passing
	/// <c>select: false</c> keeps the current tab selected during automatic reviewer creation.
	/// </summary>
	public async Task<SessionViewModel> CreateSessionAsync(
		string sessionId,
		string projectId,
		AgentKind kind,
		string title,
		string workingDirectory,
		string launchCommand,
		string? resumeCommand,
		CancellationToken cancellationToken,
		string? workspaceId = null,
		bool select = true)
	{
		var targetProjectId = string.IsNullOrWhiteSpace(workspaceId)
			? projectId
			: workspaceId;
		var now = DateTimeOffset.UtcNow;
		SessionRecord session = new(
			sessionId,
			kind,
			title,
			workingDirectory,
			launchCommand,
			resumeCommand,
			SessionStatus.Stopped,
			now,
			now);

		var updatedProject = await _structurePersistence.AddSessionAsync(
			targetProjectId,
			session,
			cancellationToken)
			?? throw new InvalidOperationException($"Project '{targetProjectId}' was not found.");

		var workspace = FindWorkspaceViewModel(targetProjectId);
		SessionViewModel viewModel = new(session, workspace?.RootPath);
		workspace?.UpdateRecord(updatedProject);
		workspace?.Sessions.Add(viewModel);
		if (workspace is not null && Workspaces.Contains(workspace))
		{
			Sessions.Add(viewModel);
		}

		if (select)
		{
			SelectedSession = viewModel;
		}

		RefreshTerminalActionTargets();
		return viewModel;
	}

	/// <summary>
	/// Creates and selects a project-independent ROOT terminal session.
	/// </summary>
	public async Task<SessionViewModel> CreateRootSessionAsync(
		AgentKind kind,
		string title,
		string workingDirectory,
		string launchCommand,
		string? resumeCommand,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
		ArgumentException.ThrowIfNullOrWhiteSpace(launchCommand);
		var now = DateTimeOffset.UtcNow;
		SessionRecord session = new(
			Guid.NewGuid().ToString("N"),
			kind,
			string.IsNullOrWhiteSpace(title) ? kind.ToString() : title.Trim(),
			workingDirectory,
			launchCommand,
			resumeCommand,
			SessionStatus.Stopped,
			now,
			now);

		var record = await _rootPersistence.AddSessionAsync(session, cancellationToken);
		RootTabs.UpdateRecord(record);
		var viewModel = RootTabs.Sessions.Single(item =>
			string.Equals(item.Record.Id, session.Id, StringComparison.Ordinal));
		Sessions.Add(viewModel);
		SelectedWorkspace = null;
		SelectedSession = viewModel;
		RefreshTerminalActionTargets();
		return viewModel;
	}

	/// <summary>
	/// Returns the project for a directory, creating it when that root is not open yet. The root
	/// is normalized first, so the same directory never yields two projects.
	/// </summary>
	public async Task<WorkspaceViewModel> EnsureWorkspaceForDirectoryAsync(
		string workingDirectory,
		CancellationToken cancellationToken)
	{
		var rootPath = WorkspaceRootDetector.NormalizeRoot(workingDirectory);
		var existing = Workspaces
			.Concat(PausedWorkspaces)
			.FirstOrDefault(workspace => string.Equals(
				workspace.RootPath,
				rootPath,
				StringComparison.OrdinalIgnoreCase));
		if (existing is not null)
		{
			SelectedWorkspace = existing;
			return existing;
		}

		var now = DateTimeOffset.UtcNow;
		ProjectRecord record = new(
			Guid.NewGuid().ToString("N"),
			WorkspaceRootDetector.GetWorkspaceName(rootPath),
			rootPath,
			now,
			now,
			Notes: null);

		await _structurePersistence.AddProjectAsync(record, cancellationToken);

		WorkspaceViewModel viewModel = new(record);
		Workspaces.Add(viewModel);
		SelectedWorkspace = viewModel;
		return viewModel;
	}

	/// <summary>Persists a session's lifecycle status and forwards it to the status engine.</summary>
	public async Task UpdateSessionStatusAsync(
		string sessionId,
		SessionStatus status,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		var occurredAt = DateTimeOffset.UtcNow;
		var updatedSession = await UpdateSessionAsync(
			sessionId,
			session => session with
			{
				Status = status,
				LastActiveAt = occurredAt
			},
			cancellationToken);

		if (updatedSession is null)
		{
			return;
		}

		TerminalTabStatuses.OnLifecycleChanged(sessionId, status, occurredAt);
	}

	/// <summary>Persists a session's resume command after it is extracted from agent output.</summary>
	public async Task UpdateSessionResumeCommandAsync(
		string sessionId,
		string resumeCommand,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentException.ThrowIfNullOrWhiteSpace(resumeCommand);

		var occurredAt = DateTimeOffset.UtcNow;
		await UpdateSessionAsync(
			sessionId,
			session => session with
			{
				ResumeCommand = resumeCommand,
				LastActiveAt = occurredAt
			},
			cancellationToken);
	}

	/// <summary>
	/// Clears the stored conversation id so the next start begins fresh, preserving the user's own
	/// executable and flags in the command.
	/// </summary>
	public async Task ClearSessionResumeCommandAsync(
		string sessionId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		var occurredAt = DateTimeOffset.UtcNow;
		await UpdateSessionAsync(
			sessionId,
			session => session with
			{
				ResumeCommand = AgentResumeCommandExtractor.SetResumeCommandId(session.ResumeCommand, resumeId: null),
				LastActiveAt = occurredAt
			},
			cancellationToken);
	}

	/// <summary>Persists a session title, falling back to a default when blank.</summary>
	public async Task UpdateSessionTitleAsync(
		string sessionId,
		string? title,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		var normalizedTitle = string.IsNullOrWhiteSpace(title)
			? "Session"
			: title.Trim();
		var occurredAt = DateTimeOffset.UtcNow;
		await UpdateSessionAsync(
			sessionId,
			session => session with
			{
				Title = normalizedTitle,
				LastActiveAt = occurredAt
			},
			cancellationToken);
	}

	private async Task<SessionRecord?> UpdateSessionAsync(
		string sessionId,
		Func<SessionRecord, SessionRecord> mutate,
		CancellationToken cancellationToken)
	{
		if (RootTabs.Sessions.Any(session =>
				string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal)))
		{
			var rootResult = await _rootPersistence.UpdateSessionAsync(
				sessionId,
				mutate,
				cancellationToken);
			if (rootResult is null)
			{
				return null;
			}

			RootTabs.UpdateRecord(rootResult.Value.Record);
			return rootResult.Value.Session;
		}

		var result = await _persistence.UpdateSessionAsync(
			sessionId,
			mutate,
			cancellationToken);
		if (result is null)
		{
			return null;
		}

		var viewModel = FindSessionViewModel(sessionId);
		viewModel?.UpdateRecord(result.Session);
		return result.Session;
	}

	/// <summary>Applies a partial project edit and persists it.</summary>
	public async Task UpdateProjectSettingsAsync(
		string projectId,
		ProjectSettingsEdit edit,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
		ArgumentNullException.ThrowIfNull(edit);
		var modifiedAt = DateTimeOffset.UtcNow;

		var updatedProject = await _persistence.UpdateProjectAsync(
			projectId,
			project => edit.ApplyTo(project, modifiedAt),
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var viewModel = FindWorkspaceViewModel(projectId);
		viewModel?.UpdateRecord(updatedProject);
	}

	/// <summary>Applies a partial session edit and persists it.</summary>
	public async Task UpdateSessionSettingsAsync(
		string sessionId,
		SessionSettingsEdit edit,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(edit);
		var modifiedAt = DateTimeOffset.UtcNow;

		await UpdateSessionAsync(
			sessionId,
			session => edit.ApplyTo(session, modifiedAt),
			cancellationToken);
	}

	/// <summary>Creates a web page with a generated id, persists it, and selects it.</summary>
	public async Task<WebPageViewModel> CreateWebPageAsync(
		string workspaceId,
		string title,
		string startUrl,
		CancellationToken cancellationToken) => await CreateWebPageAsync(
			Guid.NewGuid().ToString("N"),
			workspaceId,
			title,
			startUrl,
			cancellationToken);

	/// <summary>Creates a web page with an explicit id, persists it, and selects it.</summary>
	public async Task<WebPageViewModel> CreateWebPageAsync(
		string webPageId,
		string workspaceId,
		string title,
		string startUrl,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);

		var normalizedTitle = string.IsNullOrWhiteSpace(title)
			? "Web page"
			: title.Trim();
		var now = DateTimeOffset.UtcNow;
		WebPageRecord webPage = new(
			webPageId,
			normalizedTitle,
			startUrl,
			startUrl,
			now,
			now);

		var updatedProject = await _structurePersistence.AddWebPageAsync(
			workspaceId,
			webPage,
			cancellationToken)
			?? throw new InvalidOperationException($"Project '{workspaceId}' was not found.");

		var workspace = FindWorkspaceViewModel(workspaceId);
		WebPageViewModel viewModel = new(webPage);
		workspace?.UpdateRecord(updatedProject);
		workspace?.WebPages.Add(viewModel);
		if (workspace is not null && Workspaces.Contains(workspace))
		{
			WebPages.Add(viewModel);
		}

		SelectedWorkspace = workspace;
		SelectedWebPage = viewModel;
		return viewModel;
	}

	/// <summary>Creates and selects a project-independent ROOT browser page.</summary>
	public async Task<WebPageViewModel> CreateRootWebPageAsync(
		string title,
		string startUrl,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);
		var now = DateTimeOffset.UtcNow;
		WebPageRecord webPage = new(
			Guid.NewGuid().ToString("N"),
			string.IsNullOrWhiteSpace(title) ? "Web page" : title.Trim(),
			startUrl,
			startUrl,
			now,
			now);

		var record = await _rootPersistence.AddWebPageAsync(webPage, cancellationToken);
		RootTabs.UpdateRecord(record);
		var viewModel = RootTabs.WebPages.Single(item =>
			string.Equals(item.Record.Id, webPage.Id, StringComparison.Ordinal));
		WebPages.Add(viewModel);
		SelectedWorkspace = null;
		SelectedWebPage = viewModel;
		return viewModel;
	}

	/// <summary>
	/// Persists the address a web page reopens at, and its title when the browser reported a
	/// non-blank one.
	/// </summary>
	public async Task UpdateWebPageResumeUrlAsync(
		string webPageId,
		string resumeUrl,
		string? title,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentException.ThrowIfNullOrWhiteSpace(resumeUrl);

		await UpdateWebPageAsync(
			webPageId,
			webPage =>
			{
				var now = DateTimeOffset.UtcNow;
				return webPage with
				{
					ResumeUrl = resumeUrl,
					Title = string.IsNullOrWhiteSpace(title)
						? webPage.Title
						: title.Trim(),
					LastActiveAt = now
				};
			},
			cancellationToken);
	}

	/// <summary>Persists a web page title, falling back to a default when blank.</summary>
	public async Task UpdateWebPageTitleAsync(
		string webPageId,
		string? title,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);

		var normalizedTitle = string.IsNullOrWhiteSpace(title)
			? "Web page"
			: title.Trim();
		await UpdateWebPageAsync(
			webPageId,
			webPage => webPage with
			{
				Title = normalizedTitle,
				LastActiveAt = DateTimeOffset.UtcNow
			},
			cancellationToken);
	}

	/// <summary>Applies editable title and URL fields to a ROOT browser page.</summary>
	public async Task UpdateRootWebPageSettingsAsync(
		string webPageId,
		RootWebPageSettingsEdit edit,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentNullException.ThrowIfNull(edit);
		await UpdateWebPageAsync(
			webPageId,
			webPage =>
			{
				var title = edit.Title is null ? webPage.Title : edit.Title.Trim();
				var url = edit.Url is null ? webPage.ResumeUrl : edit.Url.Trim();
				return webPage with
				{
					Title = title,
					StartUrl = url,
					ResumeUrl = url,
					LastActiveAt = DateTimeOffset.UtcNow
				};
			},
			cancellationToken);
	}

	private async Task<WebPageRecord?> UpdateWebPageAsync(
		string webPageId,
		Func<WebPageRecord, WebPageRecord> mutate,
		CancellationToken cancellationToken)
	{
		if (RootTabs.WebPages.Any(webPage =>
				string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal)))
		{
			var rootResult = await _rootPersistence.UpdateWebPageAsync(
				webPageId,
				mutate,
				cancellationToken);
			if (rootResult is null)
			{
				return null;
			}

			RootTabs.UpdateRecord(rootResult.Value.Record);
			return rootResult.Value.WebPage;
		}

		var result = await _persistence.UpdateWebPageAsync(
			webPageId,
			mutate,
			cancellationToken);
		if (result is null)
		{
			return null;
		}

		FindWebPageViewModel(webPageId)?.UpdateRecord(result.WebPage);
		return result.WebPage;
	}

	/// <summary>Persists which item its project reselects when reopened.</summary>
	public async Task SetActiveItemAsync(string itemId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemId);

		if (RootTabs.Sessions.Any(item =>
				string.Equals(item.Record.Id, itemId, StringComparison.Ordinal))
			|| RootTabs.WebPages.Any(item =>
				string.Equals(item.Record.Id, itemId, StringComparison.Ordinal)))
		{
			var rootRecord = await _rootPersistence.SetActiveItemAsync(itemId, cancellationToken);
			RootTabs.UpdateRecord(rootRecord);
			SelectedWorkspace = null;
			var rootSession = FindSessionViewModel(itemId);
			if (rootSession is not null)
			{
				SelectedSession = rootSession;
				return;
			}

			SelectedWebPage = FindWebPageViewModel(itemId);
			return;
		}

		var owningWorkspace = FindWorkspaceViewModelOwningItem(itemId);
		if (owningWorkspace is null)
		{
			return;
		}

		var updatedProject = await _persistence.UpdateProjectAsync(
			owningWorkspace.Id,
			project =>
			{
				return project with
				{
					ActiveItemId = itemId,
					LastActiveAt = DateTimeOffset.UtcNow
				};
			},
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var workspace = FindWorkspaceViewModel(updatedProject.Id);
		workspace?.UpdateRecord(updatedProject);
		SelectedWorkspace = workspace;

		var sessionViewModel = FindSessionViewModel(itemId);
		if (sessionViewModel is not null)
		{
			SelectedSession = sessionViewModel;
			return;
		}

		var webPageViewModel = FindWebPageViewModel(itemId);
		if (webPageViewModel is not null)
		{
			SelectedWebPage = webPageViewModel;
			return;
		}

		var noteViewModel = FindProjectNoteViewModel(itemId);
		if (noteViewModel is not null)
		{
			SelectedProjectNote = noteViewModel;
		}
	}

	/// <summary>Shows the project's notes tab, creating it if needed, and selects it.</summary>
	public Task<ProjectNoteViewModel> ShowNotesTabAsync(string workspaceId, CancellationToken cancellationToken) =>
		ShowNotesTabCoreAsync(workspaceId, true, cancellationToken);

	private async Task<ProjectNoteViewModel> ShowNotesTabCoreAsync(string workspaceId, bool select, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		var workspace = FindWorkspaceViewModel(workspaceId);
		var existing = workspace?.Notes.FirstOrDefault();
		if (workspace is not null && existing is not null)
		{
			if (select)
			{ SelectedWorkspace = workspace; SelectedProjectNote = existing; await SetActiveItemAsync(existing.Record.Id, cancellationToken); }
			return existing;
		}

		var now = DateTimeOffset.UtcNow;
		NotesTabRecord notesTab = new(Guid.NewGuid().ToString("N"), now, now);
		var updatedProject = await _structurePersistence.EnsureNotesTabAsync(
			workspaceId,
			notesTab,
			select,
			cancellationToken)
			?? throw new InvalidOperationException($"Project '{workspaceId}' was not found.");

		workspace = FindWorkspaceViewModel(workspaceId);
		var viewModel = workspace?.Notes.FirstOrDefault() ?? new ProjectNoteViewModel(updatedProject.NotesTab!, updatedProject.RootPath);
		if (workspace is not null)
		{
			workspace.UpdateRecord(updatedProject);
			if (!workspace.Notes.Contains(viewModel))
			{
				workspace.Notes.Add(viewModel);
			}
		}
		if (select)
		{ SelectedWorkspace = workspace; SelectedProjectNote = viewModel; }
		return viewModel;
	}

	/// <summary>Hides the notes tab. The notes file itself is left untouched.</summary>
	public async Task HideNotesTabAsync(string workspaceId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		var workspace = FindWorkspaceViewModel(workspaceId);
		var note = workspace?.Notes.FirstOrDefault();
		if (workspace is null || note is null)
		{
			return;
		}

		if (_docsAndNotesWorkspaces.TryGetValue(
				note.ProjectRootPath,
				out var docsAndNotesWorkspace))
		{
			await docsAndNotesWorkspace.NotesDocument.FlushAsync(cancellationToken);
		}

		var removedSelected = ReferenceEquals(SelectedProjectNote, note);
		var replacement = removedSelected ? workspace.Sessions.FirstOrDefault()?.Record.Id ?? workspace.WebPages.FirstOrDefault()?.Record.Id : null;
		var updated = await _structurePersistence.HideNotesTabAsync(
			workspaceId,
			note.Record.Id,
			replacement,
			cancellationToken);
		if (updated is not null)
		{
			workspace.UpdateRecord(updated);
		}

		workspace.Notes.Remove(note);
		if (removedSelected)
		{
			SelectedProjectNote = null;
			if (replacement is not null)
			{
				SelectedSession = FindSessionViewModel(replacement);
				if (SelectedSession is null)
				{
					SelectedWebPage = FindWebPageViewModel(replacement);
				}
			}
		}
	}

	/// <summary>Reads the retained project Notes buffer without exposing or selecting its pane.</summary>
	public async Task<ProjectNotesSnapshot> ReadProjectNotesAsync(
		string workspaceId,
		CancellationToken cancellationToken)
	{
		var document = await GetLoadedProjectNoteDocumentAsync(
			workspaceId,
			cancellationToken);
		return document.GetSnapshot();
	}

	/// <summary>Revision-safely replaces project Notes without changing shell selection.</summary>
	public async Task<ProjectNotesMutationResult> ReplaceProjectNotesAsync(
		string workspaceId,
		string text,
		string expectedRevision,
		CancellationToken cancellationToken)
	{
		var document = await GetLoadedProjectNoteDocumentAsync(
			workspaceId,
			cancellationToken);
		return await document.ReplaceAsync(
			text,
			expectedRevision,
			cancellationToken);
	}

	/// <summary>Appends and immediately persists project Notes without exposing its pane.</summary>
	public async Task<ProjectNotesMutationResult> AppendToProjectNotesAsync(
		string workspaceId,
		string text,
		CancellationToken cancellationToken)
	{
		var document = await GetLoadedProjectNoteDocumentAsync(
			workspaceId,
			cancellationToken);
		if (string.IsNullOrWhiteSpace(text))
		{
			return new ProjectNotesMutationResult(
				document.GetSnapshot(),
				ProjectNotesMutationStatus.Applied);
		}

		return await document.AppendAndFlushAsync(text, cancellationToken);
	}

	/// <summary>
	/// Returns the editor document for a notes tab, creating it on first use. One document per
	/// project keeps debounced autosaves from racing each other.
	/// </summary>
	public ProjectNoteDocument GetOrCreateNoteDocument(ProjectNoteViewModel note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return GetOrCreateDocsAndNotesWorkspace(note).NotesDocument;
	}

	/// <summary>Returns the retained Docs &amp; Notes workspace for a project note pseudo-tab.</summary>
	public DocsAndNotesWorkspaceViewModel GetOrCreateDocsAndNotesWorkspace(ProjectNoteViewModel note)
	{
		ArgumentNullException.ThrowIfNull(note);
		return GetOrCreateDocsAndNotesWorkspace(note.ProjectRootPath);
	}

	private DocsAndNotesWorkspaceViewModel GetOrCreateDocsAndNotesWorkspace(
		string projectRootPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		if (!_docsAndNotesWorkspaces.TryGetValue(
				projectRootPath,
				out var workspace))
		{
			workspace = new DocsAndNotesWorkspaceViewModel(
				projectRootPath,
				new ProjectNoteDocument(
					_notesStore,
					projectRootPath,
					NoteDebounceInterval),
				_markdownFileStore,
				NoteDebounceInterval);
			_docsAndNotesWorkspaces[projectRootPath] = workspace;
		}
		return workspace;
	}

	private async Task<ProjectNoteDocument> GetLoadedProjectNoteDocumentAsync(
		string workspaceId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		var workspace = FindWorkspaceViewModel(workspaceId)
			?? throw new InvalidOperationException($"Project '{workspaceId}' was not found.");
		var document = GetOrCreateDocsAndNotesWorkspace(workspace.RootPath).NotesDocument;
		await document.LoadAsync(cancellationToken);
		return document;
	}

	/// <summary>Persists every pending note edit immediately, used on shutdown.</summary>
	public async Task FlushAllNoteDocumentsAsync(CancellationToken cancellationToken)
	{
		List<Exception> failures = [];
		foreach (var workspace in _docsAndNotesWorkspaces.Values)
		{
			try
			{
				await workspace.FlushAsync(cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (AggregateException exception)
			{
				failures.AddRange(exception.InnerExceptions);
			}
			catch (Exception exception)
			{
				failures.Add(exception);
			}
		}

		if (failures.Count > 0)
		{
			throw new AggregateException(
				"One or more project workspaces could not save their documents.",
				failures);
		}
	}

	/// <summary>
	/// Removes a web page and persists the replacement selection, so closing a tab lands on a
	/// sensible neighbor rather than nothing.
	/// </summary>
	public async Task RemoveWebPageAsync(string webPageId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);

		var rootWebPage = RootTabs.WebPages.FirstOrDefault(
			webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));
		if (rootWebPage is not null)
		{
			await RemoveRootItemAsync(rootWebPage, webPageId, cancellationToken);
			return;
		}

		var removedIndex = -1;
		var webPageToRemove = WebPages.FirstOrDefault(
			webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));
		if (webPageToRemove is not null)
		{
			removedIndex = WebPages.IndexOf(webPageToRemove);
		}

		var removedSelectedWebPage = ReferenceEquals(SelectedWebPage, webPageToRemove);
		var replacementActiveItemId = removedSelectedWebPage
			? GetReplacementActiveItemIdAfterWebPageRemoval(webPageId, removedIndex)
			: null;

		var updatedProjects = await _structurePersistence.RemoveWebPageAsync(
			webPageId,
			replacementActiveItemId,
			cancellationToken);

		webPageToRemove ??= Workspaces
				.Concat(PausedWorkspaces)
				.SelectMany(workspace => workspace.WebPages)
				.FirstOrDefault(webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));

		if (webPageToRemove is null)
		{
			return;
		}

		WebPages.Remove(webPageToRemove);
		RemoveWebPageFromWorkspaceGroup(webPageToRemove);

		foreach (var updatedProject in updatedProjects)
		{
			FindWorkspaceViewModel(updatedProject.Id)?.UpdateRecord(updatedProject);
		}

		if (!removedSelectedWebPage)
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(replacementActiveItemId))
		{
			var replacementWorkspace = FindWorkspaceViewModelOwningItem(replacementActiveItemId);
			if (replacementWorkspace is not null)
			{
				SelectedWorkspace = replacementWorkspace;
			}

			var replacementWebPage = FindWebPageViewModel(replacementActiveItemId);
			if (replacementWebPage is not null)
			{
				SelectedWebPage = replacementWebPage;
				return;
			}

			SelectedWebPage = null;
			SelectedSession = FindSessionViewModel(replacementActiveItemId);
			return;
		}

		SelectedWebPage = null;
		SelectedSession = null;
	}

	/// <summary>
	/// Removes a session, unregisters it from the status engine, and persists the replacement
	/// selection.
	/// </summary>
	public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

		var rootSession = RootTabs.Sessions.FirstOrDefault(
			session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));
		if (rootSession is not null)
		{
			await RemoveRootItemAsync(rootSession, sessionId, cancellationToken);
			return;
		}

		var removedIndex = -1;
		var sessionToRemove = Sessions.FirstOrDefault(
			session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));
		if (sessionToRemove is not null)
		{
			removedIndex = Sessions.IndexOf(sessionToRemove);
		}

		var removedSelectedSession = ReferenceEquals(SelectedSession, sessionToRemove);
		var replacementActiveItemId = removedSelectedSession
			? GetReplacementActiveItemIdAfterSessionRemoval(sessionId, removedIndex)
			: null;

		var updatedProjects = await _structurePersistence.RemoveSessionAsync(
			sessionId,
			replacementActiveItemId,
			cancellationToken);

		sessionToRemove ??= Workspaces
				.Concat(PausedWorkspaces)
				.SelectMany(workspace => workspace.Sessions)
				.FirstOrDefault(session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));

		if (sessionToRemove is null)
		{
			return;
		}

		Sessions.Remove(sessionToRemove);
		RemoveSessionFromWorkspaceGroup(sessionToRemove);

		foreach (var project in updatedProjects)
		{
			FindWorkspaceViewModel(project.Id)?.UpdateRecord(project);
		}

		if (!removedSelectedSession)
		{
			RefreshTerminalActionTargets();
			return;
		}

		if (!string.IsNullOrWhiteSpace(replacementActiveItemId))
		{
			var replacementWorkspace = FindWorkspaceViewModelOwningItem(replacementActiveItemId);
			if (replacementWorkspace is not null)
			{
				SelectedWorkspace = replacementWorkspace;
			}
		}

		if (Sessions.Count == 0)
		{
			SelectFirstWebPageOrSession();
			return;
		}

		var nextIndex = Math.Clamp(removedIndex, 0, Sessions.Count - 1);
		SelectedSession = Sessions[nextIndex];
		RefreshTerminalActionTargets();
	}

	/// <summary>
	/// Persists an individual ROOT pause state. Pausing never changes selection; the shell owns
	/// stopping or unloading the live surface before calling this method.
	/// </summary>
	public async Task SetRootItemPausedAsync(
		string itemId,
		bool paused,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
		var session = RootTabs.Sessions.FirstOrDefault(item =>
			string.Equals(item.Record.Id, itemId, StringComparison.Ordinal));
		var webPage = RootTabs.WebPages.FirstOrDefault(item =>
			string.Equals(item.Record.Id, itemId, StringComparison.Ordinal));
		if (session is null && webPage is null)
		{
			return;
		}

		var record = await _rootPersistence.SetPausedAsync(itemId, paused, cancellationToken);
		RootTabs.UpdateRecord(record);
		session = RootTabs.Sessions.FirstOrDefault(item =>
			string.Equals(item.Record.Id, itemId, StringComparison.Ordinal));
		webPage = RootTabs.WebPages.FirstOrDefault(item =>
			string.Equals(item.Record.Id, itemId, StringComparison.Ordinal));
		if (session is not null)
		{
			if (paused)
			{
				Sessions.Remove(session);
			}
			else if (!Sessions.Contains(session))
			{
				Sessions.Insert(GetRootSessionInsertIndex(session), session);
			}
		}
		else if (webPage is not null)
		{
			if (paused)
			{
				WebPages.Remove(webPage);
			}
			else if (!WebPages.Contains(webPage))
			{
				WebPages.Insert(GetRootWebPageInsertIndex(webPage), webPage);
			}
		}

		RefreshCurrentTerminalState();
		RefreshCurrentBrowserState();
		RefreshTerminalActionTargets();
		OnPropertyChanged(nameof(HasLoadingWebPages));
	}

	/// <summary>Returns whether the identified ROOT item is explicitly paused.</summary>
	public bool IsRootItemPaused(string itemId) =>
		RootTabs.IsPaused(itemId);

	private async Task RemoveRootItemAsync(
		object item,
		string itemId,
		CancellationToken cancellationToken)
	{
		var removedSelected = ReferenceEquals(item, SelectedSession)
			|| ReferenceEquals(item, SelectedWebPage);
		var replacementId = removedSelected ? GetRootReplacementItemId(itemId) : null;
		var record = await _rootPersistence.RemoveItemAsync(
			itemId,
			replacementId,
			cancellationToken);
		if (item is SessionViewModel session)
		{
			Sessions.Remove(session);
		}
		else if (item is WebPageViewModel webPage)
		{
			WebPages.Remove(webPage);
		}

		RootTabs.UpdateRecord(record);
		if (!removedSelected)
		{
			RefreshTerminalActionTargets();
			return;
		}

		SelectedSession = null;
		SelectedWebPage = null;
		if (!string.IsNullOrWhiteSpace(replacementId))
		{
			await SetActiveItemAsync(replacementId, cancellationToken);
			return;
		}

		SelectFirstSessionOrWebPage();
	}

	private string? GetRootReplacementItemId(string removedItemId)
	{
		var items = RootTabs.TreeItems;
		var removedIndex = items
			.Select((item, index) => (item, index))
			.FirstOrDefault(pair => GetItemId(pair.item) == removedItemId)
			.index;
		var remaining = items.Where(item =>
			!string.Equals(GetItemId(item), removedItemId, StringComparison.Ordinal)).ToArray();
		if (remaining.Length == 0)
		{
			return null;
		}

		return GetItemId(remaining[Math.Clamp(removedIndex, 0, remaining.Length - 1)]);
	}

	private static string GetItemId(object item) => item switch
	{
		SessionViewModel session => session.Record.Id,
		WebPageViewModel webPage => webPage.Record.Id,
		_ => string.Empty
	};

	private int GetRootSessionInsertIndex(SessionViewModel session) =>
		RootTabs.Sessions.TakeWhile(item => !ReferenceEquals(item, session)).Count(item =>
			!item.IsManuallyPaused);

	private int GetRootWebPageInsertIndex(WebPageViewModel webPage) =>
		RootTabs.WebPages.TakeWhile(item => !ReferenceEquals(item, webPage)).Count(item =>
			!item.IsManuallyPaused);

	/// <summary>
	/// Closes a project and everything nested under it, then selects a replacement item from the
	/// remaining projects.
	/// </summary>
	public async Task RemoveWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

		var workspaceToRemove = FindWorkspaceViewModel(workspaceId);
		var sessionsToRemove = workspaceToRemove?.Sessions.ToArray() ?? [];
		var webPagesToRemove = workspaceToRemove?.WebPages.ToArray() ?? [];
		var runsToRemove = workspaceToRemove?.ScenarioRuns.ToArray() ?? [];

		await _structurePersistence.RemoveProjectAsync(workspaceId, cancellationToken);

		foreach (var session in sessionsToRemove)
		{
			Sessions.Remove(session);
		}

		foreach (var webPage in webPagesToRemove)
		{
			WebPages.Remove(webPage);
		}

		foreach (var run in runsToRemove)
		{
			RemoveScenarioRun(run);
		}

		if (workspaceToRemove is not null)
		{
			Workspaces.Remove(workspaceToRemove);
			PausedWorkspaces.Remove(workspaceToRemove);
		}

		var removedSelectedSession = SelectedSession is not null
			&& sessionsToRemove.Any(session => ReferenceEquals(session, SelectedSession));
		if (removedSelectedSession)
		{
			SelectFirstSessionOrWebPage();
		}

		var removedSelectedWebPage = SelectedWebPage is not null
			&& webPagesToRemove.Any(webPage => ReferenceEquals(webPage, SelectedWebPage));
		if (removedSelectedWebPage)
		{
			SelectFirstWebPageOrSession();
		}

		if (ReferenceEquals(SelectedWorkspace, workspaceToRemove)
			&& SelectedWebPage is null
			&& SelectedSession is null)
		{
			SelectedWorkspace = Workspaces.FirstOrDefault();
		}

		RefreshTerminalActionTargets();
	}

	/// <summary>
	/// Parks a project, retaining its nested sessions and pages so the layout can be restored,
	/// and selects a replacement item.
	/// </summary>
	public async Task PauseWorkspaceAsync(
		string workspaceId,
		string? activeItemId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

		var workspaceToPause = Workspaces.FirstOrDefault(
			workspace => string.Equals(workspace.Id, workspaceId, StringComparison.Ordinal));
		var sessionsToPause = workspaceToPause?.Sessions.ToArray() ?? [];
		var webPagesToPause = workspaceToPause?.WebPages.ToArray() ?? [];
		var runsToRemove = workspaceToPause?.ScenarioRuns.ToArray() ?? [];

		var updatedProject = await _structurePersistence.PauseProjectAsync(
			workspaceId,
			activeItemId,
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var viewModel = workspaceToPause
			?? FindWorkspaceViewModel(workspaceId);
		if (viewModel is null)
		{
			return;
		}

		viewModel.UpdateRecord(updatedProject);
		Workspaces.Remove(viewModel);
		if (!PausedWorkspaces.Contains(viewModel))
		{
			PausedWorkspaces.Add(viewModel);
		}

		foreach (var session in sessionsToPause)
		{
			Sessions.Remove(session);
		}

		foreach (var webPage in webPagesToPause)
		{
			WebPages.Remove(webPage);
		}

		foreach (var run in runsToRemove)
		{
			RemoveScenarioRun(run);
		}

		if (SelectedSession is not null
			&& sessionsToPause.Any(session => ReferenceEquals(session, SelectedSession)))
		{
			SelectFirstSessionOrWebPage();
		}

		if (SelectedWebPage is not null
			&& webPagesToPause.Any(webPage => ReferenceEquals(webPage, SelectedWebPage)))
		{
			SelectFirstWebPageOrSession();
		}

		if (ReferenceEquals(SelectedWorkspace, viewModel)
			&& SelectedWebPage is null
			&& SelectedSession is null)
		{
			SelectedWorkspace = Workspaces.FirstOrDefault();
		}

		RefreshTerminalActionTargets();
	}

	/// <summary>Reopens a parked project and reselects the item it was parked on.</summary>
	public async Task RestoreWorkspaceAsync(string workspaceId, CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

		var updatedProject = await _structurePersistence.RestoreProjectAsync(
			workspaceId,
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var viewModel = FindWorkspaceViewModel(workspaceId)
			?? CreateWorkspaceViewModel(updatedProject);
		viewModel.UpdateRecord(updatedProject);
		ReplaceWorkspaceSessions(viewModel, updatedProject.Sessions);
		ReplaceWorkspaceWebPages(viewModel, updatedProject.WebPages);
		ReplaceWorkspaceNotes(viewModel, updatedProject);

		PausedWorkspaces.Remove(viewModel);
		if (!Workspaces.Contains(viewModel))
		{
			Workspaces.Add(viewModel);
		}

		foreach (var session in viewModel.Sessions)
		{
			if (!Sessions.Any(item => string.Equals(item.Record.Id, session.Record.Id, StringComparison.Ordinal)))
			{
				Sessions.Add(session);
			}
		}

		foreach (var webPage in viewModel.WebPages)
		{
			if (!WebPages.Any(item => string.Equals(item.Record.Id, webPage.Record.Id, StringComparison.Ordinal)))
			{
				WebPages.Add(webPage);
			}
		}

		SelectedWorkspace = viewModel;
		SelectStartupItem(viewModel, allowGlobalFallback: false);
		RefreshTerminalActionTargets();
	}

	/// <summary>Persists the project's notes text.</summary>
	public async Task SaveWorkspaceNotesAsync(
		string workspaceId,
		string notes,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);

		var updatedProject = await _persistence.UpdateProjectAsync(
			workspaceId,
			project =>
			{
				return project with
				{
					Notes = notes,
					LastActiveAt = DateTimeOffset.UtcNow
				};
			},
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var viewModel = FindWorkspaceViewModel(workspaceId);
		viewModel?.UpdateRecord(updatedProject);
	}

	/// <summary>Persists the project's GitLab id, used to fill web link templates.</summary>
	public async Task SaveWorkspaceGitLabRepoIdAsync(
		string workspaceId,
		string gitLabRepoId,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
		ArgumentException.ThrowIfNullOrWhiteSpace(gitLabRepoId);

		var normalizedGitLabRepoId = gitLabRepoId.Trim();
		var updatedProject = await _persistence.UpdateProjectAsync(
			workspaceId,
			project =>
			{
				return project with
				{
					GitLabRepoId = normalizedGitLabRepoId,
					LastActiveAt = DateTimeOffset.UtcNow
				};
			},
			cancellationToken);

		if (updatedProject is null)
		{
			return;
		}

		var viewModel = FindWorkspaceViewModel(workspaceId);
		viewModel?.UpdateRecord(updatedProject);
	}

	private void RefreshTerminalActionTargets()
	{
		RefreshSendSelectedTargets();
		RefreshPromptTemplateTargets();
		RefreshPromptActionCollections();
		RefreshSelectionActionTargetProjects();
	}

	private void RefreshPromptActionCollections()
	{
		VisibleQuickActions.Clear();
		SelectionActionChoices.Clear();
		SelectionActionChoices.Add(SelectionActionChoiceViewModel.Raw);
		SelectionActionChoices.Add(SelectionActionChoiceViewModel.Header("-- Prompts --"));
		foreach (var template in PromptTemplates.Where(template =>
					 template.UsesSelectedText && template.EffectiveType == PromptActionType.Prompt))
		{
			SelectionActionChoices.Add(SelectionActionChoiceViewModel.ForTemplate(template));
		}

		SelectionActionChoices.Add(SelectionActionChoiceViewModel.Header("-- Shell commands --"));
		foreach (var template in PromptTemplates.Where(template =>
					 template.UsesSelectedText && template.EffectiveType == PromptActionType.TerminalCommand))
		{
			SelectionActionChoices.Add(SelectionActionChoiceViewModel.ForTemplate(template));
		}

		if (SelectedSession is not null
			&& !SelectedSession.IsLockedByScenario
			&& !SelectedSession.IsManuallyPaused)
		{
			foreach (var template in PromptTemplates.Where(template => !template.UsesSelectedText))
			{
				if (PromptActionPolicy.CanTarget(template.EffectiveType, SelectedSession.Record.Kind))
				{
					VisibleQuickActions.Add(template);
				}
			}
		}

		ResetSelectionActionChoice();
	}
	private void RefreshSelectionActionTargetProjects()
	{
		SelectionActionTargetProjects.Clear();
		SelectionActionCompactTargetProject = null;
		HasAdditionalSelectionActionTargets = false;
		var sourceIsNote = SelectedProjectNote is not null;
		if (SelectedSession is null && !sourceIsNote)
		{
			OnPropertyChanged(nameof(HasSelectionActionTargets));
			OnPropertyChanged(nameof(HasNoSelectionActionTargets));
			OnPropertyChanged(nameof(SelectionActionCompactTargetProject));
			OnPropertyChanged(nameof(HasNoCompactSelectionActionTargets));
			OnPropertyChanged(nameof(HasAdditionalSelectionActionTargets));
			return;
		}

		var sourceWorkspace = SelectedSession is { } selectedSession
			? Workspaces.FirstOrDefault(workspace => workspace.Sessions.Any(session => ReferenceEquals(session, selectedSession)))
			: Workspaces.FirstOrDefault(workspace => workspace.Notes.Any(note => ReferenceEquals(note, SelectedProjectNote)));
		var sourceIsRoot = SelectedSession is { IsRootItem: true };
		var sourceOwnerId = sourceIsRoot ? "root" : sourceWorkspace?.Id;
		var rootSessions = RootTabs.Sessions
			.Where(session => !session.IsManuallyPaused)
			.Where(session => !ReferenceEquals(session, SelectedSession))
			.Where(session => !session.IsLockedByScenario)
			.Where(session => SelectedSelectionAction is { IsRaw: true }
				|| SelectedSelectionAction?.Template is { } rootTemplate
				&& PromptActionPolicy.CanTarget(rootTemplate.EffectiveType, session.Record.Kind))
			.ToArray();
		if (sourceIsRoot && rootSessions.Length > 0)
		{
			SelectionActionTargetProjects.Add(
				SelectionActionTargetProjectViewModel.CreateRoot(rootSessions, isExpanded: true));
		}

		foreach (var workspace in Workspaces.OrderByDescending(
					 workspace => ReferenceEquals(workspace, sourceWorkspace)))
		{
			var sessions = workspace.Sessions
				.Where(session => !ReferenceEquals(session, SelectedSession))
				.Where(session => !session.IsLockedByScenario)
				.Where(session => SelectedSelectionAction is { IsRaw: true } || SelectedSelectionAction?.Template is { } template && PromptActionPolicy.CanTarget(template.EffectiveType, session.Record.Kind))
				.ToArray();
			var isSourceNoteProject = sourceIsNote && workspace.Notes.Any(note => ReferenceEquals(note, SelectedProjectNote));
			var choiceRendersForNotes = SelectedSelectionAction is { IsRaw: true }
				|| SelectedSelectionAction?.Template is { } notesTemplate && CanRenderTemplateForNotes(notesTemplate);
			var notesTarget = isSourceNoteProject || !choiceRendersForNotes
				? null : new ProjectNotesTargetViewModel(workspace.Id, workspace.Name);
			if (sessions.Length > 0 || notesTarget is not null)
			{
				SelectionActionTargetProjects.Add(new SelectionActionTargetProjectViewModel(
					workspace, sessions, notesTarget, isExpanded: SelectionActionTargetProjects.Count == 0));
			}
		}
		if (!sourceIsRoot && rootSessions.Length > 0)
		{
			SelectionActionTargetProjects.Add(
				SelectionActionTargetProjectViewModel.CreateRoot(
					rootSessions,
					isExpanded: SelectionActionTargetProjects.Count == 0));
		}
		SelectionActionCompactTargetProject = SelectionActionTargetProjects.FirstOrDefault(
			group => string.Equals(group.Id, sourceOwnerId, StringComparison.Ordinal));
		HasAdditionalSelectionActionTargets = SelectionActionTargetProjects.Any(
			group => !string.Equals(group.Id, sourceOwnerId, StringComparison.Ordinal));
		OnPropertyChanged(nameof(HasSelectionActionTargets));
		OnPropertyChanged(nameof(HasNoSelectionActionTargets));
		OnPropertyChanged(nameof(SelectionActionCompactTargetProject));
		OnPropertyChanged(nameof(HasNoCompactSelectionActionTargets));
		OnPropertyChanged(nameof(HasAdditionalSelectionActionTargets));
	}

	private static bool CanRenderTemplateForNotes(PromptTemplateRecord template)
	{
		var rendered = template.Body.Replace("{selectedText}", string.Empty, StringComparison.Ordinal);
		return !MyRegex().IsMatch(rendered);
	}

	private void RefreshSendSelectedTargets()
	{
		SendSelectedTargets.Clear();

		IEnumerable<SessionViewModel> targets =
			SelectedTextRouter.GetTargetSessions(SelectedSession, Workspaces);
		if (SelectedSession is { IsRootItem: true })
		{
			targets = RootTabs.Sessions.Where(session =>
				!session.IsManuallyPaused && !ReferenceEquals(session, SelectedSession));
		}
		else
		{
			targets = targets.Concat(RootTabs.Sessions.Where(session => !session.IsManuallyPaused));
		}

		foreach (var target in targets.Distinct())
		{
			if (!target.IsLockedByScenario)
			{
				SendSelectedTargets.Add(target);
			}
		}
	}

	private void RefreshPromptTemplateTargets()
	{
		PromptTemplateTargets.Clear();

		if (SelectedSession is null)
		{
			return;
		}

		var workspace = Workspaces.FirstOrDefault(item =>
			item.Sessions.Any(session => string.Equals(
				session.Record.Id,
				SelectedSession.Record.Id,
				StringComparison.Ordinal)));
		if (workspace is null)
		{
			if (SelectedSession.IsRootItem)
			{
				foreach (var rootSession in RootTabs.Sessions.Where(session =>
							 !session.IsManuallyPaused && !session.IsLockedByScenario))
				{
					PromptTemplateTargets.Add(rootSession);
				}
			}

			return;
		}

		foreach (var target in workspace.Sessions)
		{
			if (!target.IsLockedByScenario)
			{
				PromptTemplateTargets.Add(target);
			}
		}

		foreach (var rootSession in RootTabs.Sessions.Where(session =>
					 !session.IsManuallyPaused && !session.IsLockedByScenario))
		{
			PromptTemplateTargets.Add(rootSession);
		}
	}

	private void RefreshCurrentTerminalState()
	{
		var terminalSessions = Sessions.Concat(RootTabs.Sessions);
		if (OrchestratorSlot.Session is { } orchestrator)
		{
			terminalSessions = terminalSessions.Append(orchestrator);
		}

		foreach (var session in terminalSessions.Distinct())
		{
			session.SetCurrentTerminal(ReferenceEquals(session, SelectedSession));
		}

		TerminalTabStatuses.SetSelectedSession(
			SelectedSession?.Record.Id,
			DateTimeOffset.UtcNow);
	}

	private void OnSessionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		if (args.Action == NotifyCollectionChangedAction.Reset)
		{
			foreach (var session in _terminalStatusSessions
						 .Where(session => !Sessions.Contains(session))
						 .ToArray())
			{
				UnregisterTerminalStatus(session);
			}
		}

		if (args.OldItems is not null)
		{
			foreach (var session in args.OldItems.OfType<SessionViewModel>())
			{
				UnregisterTerminalStatus(session);
			}
		}

		if (args.NewItems is not null)
		{
			foreach (var session in args.NewItems.OfType<SessionViewModel>())
			{
				RegisterTerminalStatus(session);
			}
		}
	}

	private void RegisterTerminalStatus(SessionViewModel session)
	{
		if (!_terminalStatusSessions.Add(session))
		{
			return;
		}

		session.PropertyChanged += OnTerminalStatusSessionPropertyChanged;
		TerminalTabStatuses.RegisterSession(session);
	}

	private void UnregisterTerminalStatus(SessionViewModel session)
	{
		if (!_terminalStatusSessions.Remove(session))
		{
			return;
		}

		session.PropertyChanged -= OnTerminalStatusSessionPropertyChanged;
		TerminalTabStatuses.RemoveSession(session.Record.Id);
	}

	private void OnTerminalStatusSessionPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is nameof(SessionViewModel.Indicator)
			or nameof(SessionViewModel.LockedByScenarioRunId))
		{
			OnPropertyChanged(nameof(HasUnreadCompletions));
		}
	}

	private void OnScenarioRunPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		// Pausing hands the run's terminals back, so the same unread completion starts asking
		// for the user at that moment and stops asking again on resume.
		if (args.PropertyName == nameof(ScenarioRunViewModel.State))
		{
			OnPropertyChanged(nameof(HasUnreadCompletions));
		}
	}

	private bool IsDrivenByAdvancingScenario(SessionViewModel session) =>
		session.LockedByScenarioRunId is { } runId
		&& ScenarioRuns.Any(run =>
			string.Equals(run.RunId, runId, StringComparison.Ordinal)
			&& run.State != ScenarioRunState.Paused);

	private void RefreshCurrentBrowserState()
	{
		foreach (var webPage in WebPages.Concat(RootTabs.WebPages).Distinct())
		{
			webPage.SetCurrentBrowser(ReferenceEquals(webPage, SelectedWebPage));
		}
	}

	private SessionViewModel? FindSessionViewModel(string sessionId)
	{
		var activeSession = Sessions.FirstOrDefault(
			session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));
		if (activeSession is not null)
		{
			return activeSession;
		}

		var rootSession = RootTabs.Sessions.FirstOrDefault(
			session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));
		if (rootSession is not null)
		{
			return rootSession;
		}

		return Workspaces
			.Concat(PausedWorkspaces)
			.SelectMany(workspace => workspace.Sessions)
			.FirstOrDefault(session => string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal));
	}

	private WebPageViewModel? FindWebPageViewModel(string webPageId)
	{
		var activeWebPage = WebPages.FirstOrDefault(
			webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));
		if (activeWebPage is not null)
		{
			return activeWebPage;
		}

		var rootWebPage = RootTabs.WebPages.FirstOrDefault(
			webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));
		if (rootWebPage is not null)
		{
			return rootWebPage;
		}

		return Workspaces
			.Concat(PausedWorkspaces)
			.SelectMany(workspace => workspace.WebPages)
			.FirstOrDefault(webPage => string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal));
	}

	private async Task<bool> MoveSessionAsync(
		SessionViewModel source,
		SessionViewModel target,
		bool insertAfter,
		CancellationToken cancellationToken)
	{
		if (source.IsRootItem || target.IsRootItem)
		{
			if (!source.IsRootItem || !target.IsRootItem)
			{
				return false;
			}

			var rootPreviousOrder = RootTabs.Record.Sessions.Select(session => session.Id);
			var record = await _rootPersistence.MoveSessionAsync(
				source.Record.Id,
				target.Record.Id,
				insertAfter,
				cancellationToken);
			if (rootPreviousOrder.SequenceEqual(record.Sessions.Select(session => session.Id)))
			{
				return false;
			}

			RootTabs.UpdateRecord(record);
			MoveObservableItem(Sessions, source, target, insertAfter);
			return true;
		}

		var workspace = Workspaces
			.Concat(PausedWorkspaces)
			.FirstOrDefault(candidate =>
				candidate.Sessions.Contains(source) && candidate.Sessions.Contains(target));
		if (workspace is null)
		{
			return false;
		}

		var projectPreviousOrder = workspace.Record.Sessions.Select(session => session.Id);
		var updated = await _structurePersistence.MoveSessionAsync(
			workspace.Id,
			source.Record.Id,
			target.Record.Id,
			insertAfter,
			cancellationToken);
		if (updated is null)
		{
			return false;
		}
		if (projectPreviousOrder.SequenceEqual(updated.Sessions.Select(session => session.Id)))
		{
			return false;
		}

		workspace.UpdateRecord(updated);
		MoveObservableItem(workspace.Sessions, source, target, insertAfter);
		MoveObservableItem(Sessions, source, target, insertAfter);
		return true;
	}

	private async Task<bool> MoveWebPageAsync(
		WebPageViewModel source,
		WebPageViewModel target,
		bool insertAfter,
		CancellationToken cancellationToken)
	{
		if (source.IsRootItem || target.IsRootItem)
		{
			if (!source.IsRootItem || !target.IsRootItem)
			{
				return false;
			}

			var rootPreviousOrder = RootTabs.Record.WebPages.Select(webPage => webPage.Id);
			var record = await _rootPersistence.MoveWebPageAsync(
				source.Record.Id,
				target.Record.Id,
				insertAfter,
				cancellationToken);
			if (rootPreviousOrder.SequenceEqual(record.WebPages.Select(webPage => webPage.Id)))
			{
				return false;
			}

			RootTabs.UpdateRecord(record);
			MoveObservableItem(WebPages, source, target, insertAfter);
			return true;
		}

		var workspace = Workspaces
			.Concat(PausedWorkspaces)
			.FirstOrDefault(candidate =>
				candidate.WebPages.Contains(source) && candidate.WebPages.Contains(target));
		if (workspace is null)
		{
			return false;
		}

		var projectPreviousOrder = workspace.Record.WebPages.Select(webPage => webPage.Id);
		var updated = await _structurePersistence.MoveWebPageAsync(
			workspace.Id,
			source.Record.Id,
			target.Record.Id,
			insertAfter,
			cancellationToken);
		if (updated is null)
		{
			return false;
		}
		if (projectPreviousOrder.SequenceEqual(updated.WebPages.Select(webPage => webPage.Id)))
		{
			return false;
		}

		workspace.UpdateRecord(updated);
		MoveObservableItem(workspace.WebPages, source, target, insertAfter);
		MoveObservableItem(WebPages, source, target, insertAfter);
		return true;
	}

	private static void MoveObservableItem<T>(
		ObservableCollection<T> items,
		T source,
		T target,
		bool insertAfter)
	{
		var sourceIndex = items.IndexOf(source);
		var targetIndex = items.IndexOf(target);
		if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
		{
			return;
		}

		items.RemoveAt(sourceIndex);
		targetIndex = items.IndexOf(target);
		items.Insert(insertAfter ? targetIndex + 1 : targetIndex, source);
	}

	private static bool WouldMove<T>(
		IList<T>? items,
		T source,
		T target,
		bool insertAfter)
	{
		if (items is null)
		{
			return false;
		}

		var sourceIndex = items.IndexOf(source);
		var targetIndex = items.IndexOf(target);
		if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex)
		{
			return false;
		}

		if (sourceIndex < targetIndex)
		{
			targetIndex--;
		}

		var insertionIndex = insertAfter ? targetIndex + 1 : targetIndex;
		return insertionIndex != sourceIndex;
	}

	private WorkspaceViewModel? FindWorkspaceViewModel(string workspaceId) => Workspaces
			.Concat(PausedWorkspaces)
			.FirstOrDefault(workspace => string.Equals(workspace.Id, workspaceId, StringComparison.Ordinal));

	private static WorkspaceViewModel CreateWorkspaceViewModel(ProjectRecord project)
	{
		WorkspaceViewModel viewModel = new(project);
		ReplaceWorkspaceSessions(viewModel, project.Sessions);
		ReplaceWorkspaceWebPages(viewModel, project.WebPages);
		ReplaceWorkspaceNotes(viewModel, project);
		return viewModel;
	}

	private static void ReplaceWorkspaceSessions(
		WorkspaceViewModel workspace,
		IEnumerable<SessionRecord> sessionRecords)
	{
		workspace.Sessions.Clear();
		foreach (var session in sessionRecords)
		{
			workspace.Sessions.Add(new SessionViewModel(session, workspace.RootPath));
		}
	}

	private static void ReplaceWorkspaceWebPages(
		WorkspaceViewModel workspace,
		IEnumerable<WebPageRecord> webPageRecords)
	{
		workspace.WebPages.Clear();
		foreach (var webPage in webPageRecords)
		{
			workspace.WebPages.Add(new WebPageViewModel(webPage));
		}
	}

	private static void ReplaceWorkspaceNotes(WorkspaceViewModel workspace, ProjectRecord project)
	{
		workspace.Notes.Clear();
		if (project.NotesTab is not null)
		{
			workspace.Notes.Add(new ProjectNoteViewModel(project.NotesTab, project.RootPath));
		}
	}

	private void SelectStartupItem(WorkspaceViewModel? workspace, bool allowGlobalFallback = true)
	{
		if (workspace is null)
		{
			if (allowGlobalFallback)
			{
				SelectedSession = Sessions.FirstOrDefault();
				if (SelectedSession is null)
				{
					SelectedWebPage = WebPages.FirstOrDefault();
				}
			}
			else
			{
				SelectedSession = null;
				SelectedWebPage = null;
			}

			return;
		}

		if (!string.IsNullOrWhiteSpace(workspace.Record.ActiveItemId))
		{
			var activeSession = workspace.Sessions.FirstOrDefault(
				session => string.Equals(session.Record.Id, workspace.Record.ActiveItemId, StringComparison.Ordinal));
			if (activeSession is not null)
			{
				SelectedSession = activeSession;
				return;
			}

			var activeWebPage = workspace.WebPages.FirstOrDefault(
				webPage => string.Equals(webPage.Record.Id, workspace.Record.ActiveItemId, StringComparison.Ordinal));
			if (activeWebPage is not null)
			{
				SelectedWebPage = activeWebPage;
				return;
			}

			var activeNote = workspace.Notes.FirstOrDefault(
				note => string.Equals(note.Record.Id, workspace.Record.ActiveItemId, StringComparison.Ordinal));
			if (activeNote is not null)
			{
				SelectedProjectNote = activeNote;
				return;
			}
		}

		SelectedSession = workspace.Sessions.FirstOrDefault();
		if (SelectedSession is null && allowGlobalFallback)
		{
			SelectedSession = Sessions.FirstOrDefault();
		}

		if (SelectedSession is null)
		{
			SelectedWebPage = workspace.WebPages.FirstOrDefault();
			if (SelectedWebPage is null && allowGlobalFallback)
			{
				SelectedWebPage = WebPages.FirstOrDefault();
				if (SelectedWebPage is not null)
				{
					SelectedWorkspace = FindWorkspaceViewModelOwningItem(SelectedWebPage.Record.Id);
				}
			}

			if (SelectedWebPage is null)
			{
				SelectedSession = null;
				SelectedWebPage = null;
			}
		}
		else if (!workspace.Sessions.Contains(SelectedSession))
		{
			SelectedWorkspace = FindWorkspaceViewModelOwningItem(SelectedSession.Record.Id);
		}
	}

	private void RemoveSessionFromWorkspaceGroup(SessionViewModel session)
	{
		foreach (var workspace in Workspaces.Concat(PausedWorkspaces))
		{
			workspace.Sessions.Remove(session);
		}
	}

	private void RemoveWebPageFromWorkspaceGroup(WebPageViewModel webPage)
	{
		foreach (var workspace in Workspaces.Concat(PausedWorkspaces))
		{
			workspace.WebPages.Remove(webPage);
		}
	}

	private WorkspaceViewModel? FindWorkspaceViewModelOwningItem(string itemId) => Workspaces
			.Concat(PausedWorkspaces)
			.FirstOrDefault(workspace =>
				workspace.Sessions.Any(session => string.Equals(session.Record.Id, itemId, StringComparison.Ordinal))
				|| workspace.WebPages.Any(webPage => string.Equals(webPage.Record.Id, itemId, StringComparison.Ordinal))
				|| workspace.Notes.Any(note => string.Equals(note.Record.Id, itemId, StringComparison.Ordinal)));

	private ProjectNoteViewModel? FindProjectNoteViewModel(string itemId) =>
		Workspaces.Concat(PausedWorkspaces).SelectMany(workspace => workspace.Notes)
			.FirstOrDefault(note => string.Equals(note.Record.Id, itemId, StringComparison.Ordinal));

	private void RefreshCurrentNoteState()
	{
		foreach (var workspace in Workspaces.Concat(PausedWorkspaces))
		{
			foreach (var note in workspace.Notes)
			{
				note.SetCurrentNote(ReferenceEquals(note, SelectedProjectNote));
			}
		}
	}

	private void RefreshCurrentScenarioState()
	{
		foreach (var run in ScenarioRuns)
		{
			run.SetCurrentScenario(ReferenceEquals(run, SelectedScenarioRun));
		}
	}

	private string? GetReplacementActiveItemIdAfterWebPageRemoval(string webPageId, int removedIndex)
	{
		var remainingWebPages = WebPages
			.Where(webPage => !string.Equals(webPage.Record.Id, webPageId, StringComparison.Ordinal))
			.ToArray();
		if (remainingWebPages.Length > 0)
		{
			var nextIndex = Math.Clamp(removedIndex, 0, remainingWebPages.Length - 1);
			return remainingWebPages[nextIndex].Record.Id;
		}

		return Sessions.FirstOrDefault()?.Record.Id;
	}

	private string? GetReplacementActiveItemIdAfterSessionRemoval(string sessionId, int removedIndex)
	{
		var remainingSessions = Sessions
			.Where(session => !string.Equals(session.Record.Id, sessionId, StringComparison.Ordinal))
			.ToArray();
		if (remainingSessions.Length > 0)
		{
			var nextIndex = Math.Clamp(removedIndex, 0, remainingSessions.Length - 1);
			return remainingSessions[nextIndex].Record.Id;
		}

		return WebPages.FirstOrDefault()?.Record.Id;
	}

	private void SelectFirstWebPageOrSession()
	{
		var webPage = WebPages.FirstOrDefault();
		if (webPage is not null)
		{
			SelectedWebPage = webPage;
			SelectedWorkspace = FindWorkspaceViewModelOwningItem(webPage.Record.Id);
			return;
		}

		SelectedWebPage = null;
		SelectedSession = Sessions.FirstOrDefault();
		if (SelectedSession is not null)
		{
			SelectedWorkspace = FindWorkspaceViewModelOwningItem(SelectedSession.Record.Id);
		}
	}

	private void SelectFirstSessionOrWebPage()
	{
		var session = Sessions.FirstOrDefault();
		if (session is not null)
		{
			SelectedSession = session;
			SelectedWorkspace = FindWorkspaceViewModelOwningItem(session.Record.Id);
			return;
		}

		SelectFirstWebPageOrSession();
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

	private void ReplaceSubscriptionUsageRows(IEnumerable<AgentProfileRecord> shellProfiles)
	{
		SubscriptionUsages.Clear();
		foreach (var row in SubscriptionUsageRows.CreatePendingRows(shellProfiles))
		{
			SubscriptionUsages.Add(row);
		}
	}

	[System.Text.RegularExpressions.GeneratedRegex(@"\{[A-Za-z][A-Za-z0-9]*\}")]
	private static partial System.Text.RegularExpressions.Regex MyRegex();
}
