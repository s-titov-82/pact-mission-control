using System.ComponentModel;
using Pact.Infrastructure.Documents;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

// The project screen and the change list of the project whose item is shown.
internal sealed partial class AvaloniaMainShellController
{
	private readonly Dictionary<string, ProjectScreenViewModel> _projectScreens = new(StringComparer.Ordinal);
	private readonly Dictionary<string, ProjectChangesViewModel> _projectChanges = new(StringComparer.Ordinal);
	private readonly HashSet<WorkspaceViewModel> _watchedWorkspaces = [];
	private IDisposable? _changesObservation;
	private ProjectFileChangeSignal? _changesSignal;

	/// <summary>Reads working-tree changes; tests replace it before the first project is shown.</summary>
	internal IGitWorkingTreeReader WorkingTreeReader { get; set; }

	/// <summary>The project screen shown in the center pane, if any.</summary>
	public ProjectScreenViewModel? CurrentProjectScreen => (PresentedSurface as PresentedSurface.Project)?.Screen;

	/// <summary>
	/// The change list of the project owning what is shown, kept fresh while it is current;
	/// <see langword="null"/> when nothing of a git project is shown.
	/// </summary>
	public ProjectChangesViewModel? CurrentChanges { get; private set; }

	/// <summary>Shows a project's screen on the given tab.</summary>
	public async Task OpenProjectScreenAsync(
		WorkspaceViewModel workspace,
		ProjectScreenTab tab,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ObjectDisposedException.ThrowIf(_disposed, this);
		var screen = GetOrCreateProjectScreen(workspace);
		await PresentProjectScreenAsync(screen, cancellationToken);
		await screen.SelectTabAsync(tab, cancellationToken);
	}

	/// <summary>Opens the Changes tab of the project whose change list is current.</summary>
	public Task OpenCurrentChangesAsync(CancellationToken cancellationToken = default) =>
		CurrentChanges is not null && FindShownWorkspace() is { } workspace
			? OpenProjectScreenAsync(workspace, ProjectScreenTab.Changes, cancellationToken)
			: Task.CompletedTask;

	private async Task PresentProjectScreenAsync(ProjectScreenViewModel screen, CancellationToken cancellationToken)
	{
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		await HideActiveWebPageAsync(cancellationToken);
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
		var screen = GetOrCreateProjectScreen(workspace);
		if (!await screen.OpenReviewLogAsync(relativePath, cancellationToken))
		{
			return false;
		}

		await PresentProjectScreenAsync(screen, cancellationToken);
		return true;
	}

	/// <summary>Deletes a review log of the shown project screen, unless a running review writes it.</summary>
	public async Task DeleteReviewLogAsync(string relativePath, CancellationToken cancellationToken = default)
	{
		if (CurrentProjectScreen is not { } screen)
		{
			return;
		}

		var result = await screen.DeleteReviewLogAsync(relativePath, cancellationToken);
		if (result == ReviewLogDeletion.InUse)
		{
			await ReportStatusAsync("This review is still running; its log stays until it ends.");
		}
	}

	/// <summary>Deletes every review log of the shown project screen that no running review writes.</summary>
	public async Task DeleteAllReviewLogsAsync(CancellationToken cancellationToken = default)
	{
		if (CurrentProjectScreen is not { } screen)
		{
			return;
		}

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
			new ProjectMarkdownFileStore(),
			relativePath => IsReviewLogInUse(workspace, relativePath));
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
			WatchWorkspace(workspace);
		}

		return changes;
	}

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
		if (current)
		{
			return;
		}

		var shownTab = CurrentProjectScreen is { } shown && ReferenceEquals(shown.Workspace, workspace)
			? shown.SelectedTab
			: (ProjectScreenTab?)null;
		DropProject(workspace);
		if (shownTab is { } tab)
		{
			var screen = GetOrCreateProjectScreen(workspace);
			Present(new PresentedSurface.Project(screen));
			_eventTasks.TryRun("project-screen-rebuild", () => screen.SelectTabAsync(tab, CancellationToken.None));
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
	}

	private WorkspaceViewModel? FindShownWorkspace() => PresentedSurface switch
	{
		PresentedSurface.Project project => project.Screen.Workspace,
		PresentedSurface.Terminal terminal => FindWorkspace(workspace => workspace.Sessions.Contains(terminal.Session)),
		PresentedSurface.PausedSession paused => FindWorkspace(workspace => workspace.Sessions.Contains(paused.Session)),
		PresentedSurface.PausedWebPage paused => FindWorkspace(workspace => workspace.WebPages.Contains(paused.WebPage)),
		PresentedSurface.Notes notes => FindWorkspace(workspace => workspace.Notes.Contains(notes.Note)),
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
		if (PresentedSurface is PresentedSurface.Project project
			&& string.Equals(project.Screen.Workspace.Id, workspace.Id, StringComparison.Ordinal))
		{
			Present(new PresentedSurface.None());
		}

		DropProject(workspace);
		UnwatchWorkspace(workspace);
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

		_projectScreens.Remove(workspace.Id);
	}

	private void DisposeProjectScreens()
	{
		_changesObservation?.Dispose();
		_changesSignal?.Dispose();
		foreach (var changes in _projectChanges.Values)
		{
			changes.Dispose();
		}

		_projectChanges.Clear();
		_projectScreens.Clear();
		foreach (var workspace in _watchedWorkspaces.ToArray())
		{
			UnwatchWorkspace(workspace);
		}
	}
}
