using System.ComponentModel;
using Pact.Infrastructure.Documents;
using Pact.Core.Sessions;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

// The project screen and the change list of the project whose item is shown.
internal sealed partial class AvaloniaMainShellController
{
	private readonly Dictionary<string, ProjectScreenViewModel> _projectScreens = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ProjectChangesViewModel> _projectChanges = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ChangesRemarksViewModel> _projectRemarks = new(StringComparer.Ordinal);
	private readonly HashSet<WorkspaceViewModel> _watchedWorkspaces = [];
	private IDisposable? _changesObservation;
	private ProjectFileChangeSignal? _changesSignal;
	private int _projectNavigationVersion;

	/// <summary>Reads working-tree changes; tests replace it before the first project is shown.</summary>
	internal IGitWorkingTreeReader WorkingTreeReader { get; set; }

	/// <summary>Reads review logs; replace before opening a project to control asynchronous log I/O.</summary>
	internal IProjectMarkdownFileStore ReviewLogFileStore { get; set; } = new ProjectMarkdownFileStore();

	/// <summary>The project screen shown in the center pane, if any.</summary>
	public ProjectScreenViewModel? CurrentProjectScreen => (PresentedSurface as PresentedSurface.Project)?.Screen;

	/// <summary>
	/// The change list of the project owning what is shown, kept fresh while it is current;
	/// <see langword="null"/> when nothing of a git project is shown.
	/// </summary>
	public ProjectChangesViewModel? CurrentChanges { get; private set; }

	/// <summary>The remark threads of the project <see cref="CurrentChanges"/> belongs to.</summary>
	public ChangesRemarksViewModel? CurrentRemarks { get; private set; }

	/// <summary>Delivers a remark prompt; tests replace it, otherwise the shared prompt delivery runs.</summary>
	internal Func<string, string, CancellationToken, Task<PromptDeliveryResult>>? RemarkDelivery { get; set; }

	/// <summary>Shows a project's screen on the given tab.</summary>
	public Task OpenProjectScreenAsync(
		WorkspaceViewModel workspace,
		ProjectScreenTab tab,
		CancellationToken cancellationToken = default) => OpenProjectScreenCoreAsync(workspace, tab, cancellationToken);

	private async Task<bool> OpenProjectScreenCoreAsync(
		WorkspaceViewModel workspace, ProjectScreenTab tab, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (!OwnerNavigationCurrent(workspace.Id, OwnerNavigationEpoch(workspace.Id)))
		{ return false; }
		var version = ++_projectNavigationVersion;
		var screen = GetOrCreateProjectScreen(workspace);
		_navigationIntent++;
		await PresentProjectScreenAsync(screen, cancellationToken, version);
		if (version != _projectNavigationVersion || !ReferenceEquals(CurrentProjectScreen, screen))
		{
			return false;
		}
		await screen.Documents!.RefreshAsync(cancellationToken);
		if (version != _projectNavigationVersion || !ReferenceEquals(CurrentProjectScreen, screen))
		{
			return false;
		}
		var alreadyOverview = screen.SelectedTab == ProjectScreenTab.Overview;
		// SelectTabAsync announces its intent synchronously, before any document I/O.
		// Adopt that notification, then retain exactly this version across its awaits.
		var selection = screen.SelectTabAsync(tab, cancellationToken);
		var tabVersion = _projectNavigationVersion;
		if (!await selection || tabVersion != _projectNavigationVersion || !ReferenceEquals(CurrentProjectScreen, screen))
		{
			return false;
		}
		if (alreadyOverview && tab == ProjectScreenTab.Overview && ReferenceEquals(CurrentProjectScreen, screen))
		{
			RefreshOverview(screen);
		}
		return true;
	}

	/// <summary>Opens the Changes tab of the project whose change list is current.</summary>
	public Task OpenCurrentChangesAsync(CancellationToken cancellationToken = default) =>
		CurrentChanges is not null && FindShownWorkspace() is { } workspace
			? OpenWorkingTreeChangesAsync(workspace, cancellationToken)
			: Task.CompletedTask;

	/// <summary>Opens the shown project's live review scope, preserving newer navigation intents.</summary>
	public async Task OpenReviewScopeAsync(GitReviewScope scope, CancellationToken cancellationToken = default)
	{
		if (CurrentChanges is not { } changes || FindShownWorkspace() is not { } workspace)
		{ return; }
		var intent = changes.ReserveSourceSelection();
		if (!await OpenProjectScreenCoreAsync(workspace, ProjectScreenTab.Changes, cancellationToken))
		{ return; }
		if (changes.IsSourceSelectionCurrent(intent) && CurrentProjectScreen is { } screen && ReferenceEquals(screen.Workspace, workspace)
			&& ReferenceEquals(screen.Changes, changes))
		{ await changes.SelectReviewScopeAsync(scope, cancellationToken); }
	}

	/// <summary>Runs the explicit review Pull on its captured owner and refreshes that owner's scopes.</summary>
	public async Task PullReviewBranchAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		if (!ViewModel.Workspaces.Contains(workspace) || !workspace.IsGitRepository)
		{ return; }
		var panel = GetOrCreateGitPanel(workspace);
		var changes = GetOrCreateProjectChanges(workspace);
		try
		{ await panel.RunPullRebaseAsync(cancellationToken); }
		finally
		{
			if (ViewModel.Workspaces.Contains(workspace) && changes is not null)
			{ changes.RequestRefresh(); }
		}
		var outcome = panel.LogText.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
		await ReportStatusAsync($"{workspace.Name}: {outcome ?? "Pull (rebase) finished; see Git log."}");
	}

	/// <summary>Opens the live changes named by a working-tree summary.</summary>
	public async Task OpenWorkingTreeChangesAsync(WorkspaceViewModel workspace, CancellationToken cancellationToken = default)
	{
		var changes = GetOrCreateProjectChanges(workspace);
		var intent = changes?.ReserveSourceSelection();
		if (!await OpenProjectScreenCoreAsync(workspace, ProjectScreenTab.Changes, cancellationToken))
		{ return; }
		if (changes is not null && intent is { } reserved && changes.IsSourceSelectionCurrent(reserved)
			&& CurrentProjectScreen is { } screen && ReferenceEquals(screen.Workspace, workspace) && ReferenceEquals(screen.Changes, changes))
		{ await changes.SelectReviewScopeAsync(GitReviewScope.Uncommitted, cancellationToken); }
	}

	private async Task PresentProjectScreenAsync(ProjectScreenViewModel screen, CancellationToken cancellationToken, int? navigationVersion = null)
	{
		var ownerId = screen.Workspace.Id;
		var ownerEpoch = OwnerNavigationEpoch(ownerId);
		if (!OwnerNavigationCurrent(ownerId, ownerEpoch))
		{ return; }
		var version = navigationVersion ?? ++_projectNavigationVersion;
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		if (version != _projectNavigationVersion)
		{
			return;
		}
		await HideActiveWebPageAsync(cancellationToken);
		if (version != _projectNavigationVersion || !OwnerNavigationCurrent(ownerId, ownerEpoch)
			|| !ViewModel.Workspaces.Contains(screen.Workspace))
		{
			return;
		}
		ViewModel.SelectProject(screen.Workspace);
		Present(new PresentedSurface.Project(screen));
	}

	/// <summary>Opens a review log on its project's screen.</summary>
	/// <returns><see langword="false"/> when the log is not in the project's review folder.</returns>
	private async Task<bool> OpenReviewLogOnScreenAsync(
		WorkspaceViewModel workspace,
		string relativePath,
		CancellationToken cancellationToken)
	{
		if (!OwnerNavigationCurrent(workspace.Id, OwnerNavigationEpoch(workspace.Id)))
		{ return false; }
		var version = ++_projectNavigationVersion;
		var screen = GetOrCreateProjectScreen(workspace);
		if (!screen.ContainsReviewLog(relativePath))
		{
			return false;
		}

		_navigationIntent++;
		// Present the validated intent before reading: a late read must never re-present an old owner.
		await PresentProjectScreenAsync(screen, cancellationToken, version);
		if (version != _projectNavigationVersion || !ReferenceEquals(CurrentProjectScreen, screen))
		{
			return false;
		}
		return await screen.OpenReviewLogAsync(relativePath, cancellationToken)
			&& ReferenceEquals(CurrentProjectScreen, screen);
	}

	// Deletion takes the screen the user confirmed it on: while the dialog waits, a review finishing
	// elsewhere can bring up another project's screen.

	/// <summary>Deletes a review log of a project screen, unless a running review writes it.</summary>
	public async Task DeleteReviewLogAsync(
		ProjectScreenViewModel screen,
		string relativePath,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(screen);
		var result = await screen.DeleteReviewLogAsync(relativePath, cancellationToken);
		if (result == ReviewLogDeletion.InUse)
		{
			await ReportStatusAsync("This review is still running; its log stays until it ends.");
		}
	}

	/// <summary>Deletes every review log of a project screen that no running review writes.</summary>
	public async Task DeleteAllReviewLogsAsync(
		ProjectScreenViewModel screen,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(screen);
		var result = await screen.DeleteAllReviewLogsAsync(cancellationToken);
		if (result.Kept > 0)
		{
			await ReportStatusAsync(result.Kept == 1
				? "1 review is still running; its log stays until it ends."
				: $"{result.Kept} reviews are still running; their logs stay until they end.");
		}
	}

	// A review appends to its log until its footer is in; a delete before that would bring it
	// back half-written.
	private bool IsReviewLogInUse(WorkspaceViewModel workspace, string relativePath) =>
		_reviewRelay.IsLogInUse(Path.Combine(workspace.RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

	private static bool IsReviewLogPath(string relativePath) =>
		relativePath.StartsWith(ReviewLogFiles.Folder + "/", StringComparison.OrdinalIgnoreCase);

	private ProjectScreenViewModel GetOrCreateProjectScreen(WorkspaceViewModel workspace)
	{
		if (_projectScreens.TryGetValue(workspace.Id, out var screen)
			&& ReferenceEquals(screen.Workspace, workspace))
		{
			return screen;
		}

		screen = new ProjectScreenViewModel(
			workspace,
			GetOrCreateProjectChanges(workspace),
			ReviewLogFileStore,
			relativePath => IsReviewLogInUse(workspace, relativePath),
			_projectRemarks.GetValueOrDefault(workspace.Id),
			(relativePath, heading, cancellationToken) => OpenProjectDocumentAsync(workspace.Id, relativePath, heading, cancellationToken),
			ViewModel.GetOrCreateDocsAndNotesWorkspace(workspace.RootPath),
			workspace.IsGitRepository ? GetOrCreateGitPanel(workspace) : null);
		_projectScreens[workspace.Id] = screen;
		WatchWorkspace(workspace);
		return screen;
	}

	private ProjectChangesViewModel? GetOrCreateProjectChanges(WorkspaceViewModel workspace)
	{
		if (!workspace.IsGitRepository)
		{
			return null;
		}

		if (!_projectChanges.TryGetValue(workspace.Id, out var changes))
		{
			changes = new ProjectChangesViewModel(
				workspace.RootPath,
				WorkingTreeReader,
				_timeProvider,
				_uiTaskDispatcher.Post);
			_projectChanges.Add(workspace.Id, changes);
			_projectRemarks[workspace.Id] = CreateRemarks(workspace, changes);
			WatchWorkspace(workspace);
		}

		return changes;
	}

	// The remarks of a project's Changes tab, sent to one of the project's sessions through the shared delivery.
	private ChangesRemarksViewModel CreateRemarks(WorkspaceViewModel workspace, ProjectChangesViewModel changes) =>
		new(
			changes,
			RemarkBooks.For(workspace.RootPath),
			() => workspace.Sessions.Select(session => new SessionTarget(session.Record.Id, session.Title)).ToArray(),
			(sessionId, text, cancellationToken) => RemarkDelivery?.Invoke(sessionId, text, cancellationToken)
				?? _runtimeCoordinator.DeliverPromptAsync(
					sessionId,
					text,
					submit: true,
					PromptSender.Outsider,
					startIfNotRunning: true,
					cancellationToken),
			_uiTaskDispatcher.Post,
			cancellationToken => OpenProjectScreenAsync(workspace, ProjectScreenTab.Changes, cancellationToken));

	private void WatchWorkspace(WorkspaceViewModel workspace)
	{
		if (_watchedWorkspaces.Add(workspace))
		{
			workspace.PropertyChanged += OnWatchedWorkspacePropertyChanged;
		}
	}

	private void UnwatchWorkspace(WorkspaceViewModel workspace)
	{
		if (_watchedWorkspaces.Remove(workspace))
		{
			workspace.PropertyChanged -= OnWatchedWorkspacePropertyChanged;
		}
	}

	// A settings edit can move the project to another folder or in and out of git: the change
	// list reads a fixed root, so it is rebuilt, and a shown screen is shown again with it.
	private void OnWatchedWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_disposed
			|| sender is not WorkspaceViewModel workspace
			|| e.PropertyName is not (nameof(WorkspaceViewModel.RootPath) or nameof(WorkspaceViewModel.IsGitRepository)))
		{
			return;
		}

		var hasChanges = _projectChanges.TryGetValue(workspace.Id, out var changes);
		var current = hasChanges
			? workspace.IsGitRepository
				&& string.Equals(changes!.ProjectRoot, workspace.RootPath, StringComparison.OrdinalIgnoreCase)
			: !workspace.IsGitRepository;
		if (current && (!_projectScreens.TryGetValue(workspace.Id, out var existingScreen)
			|| string.Equals(existingScreen.Documents?.ProjectRootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase)))
		{
			return;
		}

		var shownTab = CurrentProjectScreen is { } shown && ReferenceEquals(shown.Workspace, workspace)
			? shown.SelectedTab
			: (ProjectScreenTab?)null;
		DropProject(workspace);
		_gitPanelViewModels.Remove(workspace.Id);
		if (shownTab is { } tab)
		{
			var screen = GetOrCreateProjectScreen(workspace);
			Present(new PresentedSurface.Project(screen));
			_eventTasks.TryRun("project-screen-rebuild", () => OpenProjectScreenAsync(workspace, tab));
		}
		else
		{
			UpdateCurrentChanges();
		}
	}

	// Called after every surface change: the change list follows the project of what is shown.
	private void UpdateCurrentChanges()
	{
		if (_disposed)
		{
			return;
		}

		var workspace = FindShownWorkspace();
		var next = workspace is null ? null : GetOrCreateProjectChanges(workspace);
		if (ReferenceEquals(next, CurrentChanges))
		{
			return;
		}

		_changesObservation?.Dispose();
		_changesObservation = null;
		_changesSignal?.Dispose();
		_changesSignal = null;
		CurrentChanges = next;
		if (next is not null)
		{
			_changesObservation = next.Observe();
			_changesSignal = new ProjectFileChangeSignal(workspace!.RootPath, next.RequestRefresh);
		}

		OnPropertyChanged(nameof(CurrentChanges));
		CurrentRemarks = workspace is null ? null : _projectRemarks.GetValueOrDefault(workspace.Id);
		OnPropertyChanged(nameof(CurrentRemarks));
	}

	private WorkspaceViewModel? FindShownWorkspace() => PresentedSurface switch
	{
		PresentedSurface.Project project => project.Screen.Workspace,
		PresentedSurface.Terminal terminal => FindWorkspace(workspace => workspace.Sessions.Contains(terminal.Session)),
		PresentedSurface.PausedSession paused => FindWorkspace(workspace => workspace.Sessions.Contains(paused.Session)),
		PresentedSurface.PausedWebPage paused => FindWorkspace(workspace => workspace.WebPages.Contains(paused.WebPage)),
		PresentedSurface.Web when ViewModel.SelectedWebPage is { } page =>
			FindWorkspace(workspace => workspace.WebPages.Contains(page)),
		_ => null
	};

	private WorkspaceViewModel? FindWorkspace(Func<WorkspaceViewModel, bool> owns) =>
		ViewModel.Workspaces.FirstOrDefault(owns);

	/// <summary>
	/// Leaves the project's screen if it is shown and drops the project's screen and change list,
	/// for a project being paused or closed.
	/// </summary>
	private void ReleaseProjectScreen(WorkspaceViewModel workspace)
	{
		// Lifecycle cleanup is not a new navigation request. In particular it must not
		// invalidate a newer navigation still awaiting the old owner's document flush.
		if (_projectScreens.TryGetValue(workspace.Id, out var retained))
		{
			retained.Deactivate();
		}
		if (PresentedSurface is PresentedSurface.Project project
			&& string.Equals(project.Screen.Workspace.Id, workspace.Id, StringComparison.Ordinal))
		{
			Present(new PresentedSurface.None(), preserveNavigation: true);
		}

		DropProject(workspace);
		UnwatchWorkspace(workspace);
		ReleaseAgentFiles(workspace);
	}

	// Disposes the project's change list and forgets its screen; the current list lets go first.
	private void DropProject(WorkspaceViewModel workspace)
	{
		if (_projectChanges.TryGetValue(workspace.Id, out var changes))
		{
			if (ReferenceEquals(changes, CurrentChanges))
			{
				_changesObservation?.Dispose();
				_changesObservation = null;
				_changesSignal?.Dispose();
				_changesSignal = null;
				CurrentChanges = null;
				OnPropertyChanged(nameof(CurrentChanges));
			}

			changes.Dispose();
			_projectChanges.Remove(workspace.Id);
		}

		if (_projectRemarks.Remove(workspace.Id, out var remarks))
		{
			if (ReferenceEquals(remarks, CurrentRemarks))
			{
				CurrentRemarks = null;
				OnPropertyChanged(nameof(CurrentRemarks));
			}

			remarks.Dispose();
		}

		if (_projectScreens.Remove(workspace.Id, out var screen))
		{
			screen.Deactivate();
		}
	}

	private void DisposeProjectScreens()
	{
		_changesObservation?.Dispose();
		_changesSignal?.Dispose();
		foreach (var changes in _projectChanges.Values)
		{
			changes.Dispose();
		}

		foreach (var remarks in _projectRemarks.Values)
		{
			remarks.Dispose();
		}

		_projectRemarks.Clear();
		_projectChanges.Clear();
		foreach (var screen in _projectScreens.Values)
		{
			screen.Deactivate();
		}
		_projectScreens.Clear();
		foreach (var workspace in _watchedWorkspaces.ToArray())
		{
			UnwatchWorkspace(workspace);
		}
	}
}