using System.Collections.Concurrent;
using Pact.Core.Agents;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed partial class AvaloniaMainShellController
{
	private RootDashboardViewModel? _dashboard;
	private bool _openingRecentProject;
	private long _navigationIntent;
	// Persistence guards may run off the UI thread. Retain each release epoch so an old
	// selection cannot become current again after teardown or a failed mutation completes.
	private readonly ConcurrentDictionary<string, (long Epoch, int Releases)> _ownerNavigation = new();

	private bool OwnerNavigationCurrent(string? ownerId, long epoch) => ownerId is null
		|| (_ownerNavigation.GetValueOrDefault(ownerId) is var state && state.Epoch == epoch && state.Releases == 0);
	private long OwnerNavigationEpoch(string? ownerId) => ownerId is null ? 0 : _ownerNavigation.GetValueOrDefault(ownerId).Epoch;

	public RootDashboardViewModel Dashboard => _dashboard ??= new(ViewModel.Workspaces, WorkingTreeReader,
		session => session.Record.Kind is not (AgentKind.Pwsh or AgentKind.Custom) && HasActiveTerminalProcess(session),
		_uiTaskDispatcher.Post, _timeProvider, ViewModel.PausedWorkspaces, LoadRecentDirectoriesAsync);
	public bool IsDashboardVisible => PresentedSurface is PresentedSurface.Dashboard;

	public async Task OpenRecentProjectAsync(string directory, CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ThrowIfRejectingInput();
		cancellationToken.ThrowIfCancellationRequested();
		if (_openingRecentProject)
		{ return; }
		_openingRecentProject = true;
		var intent = ++_navigationIntent;
		_projectNavigationVersion++;
		try
		{
			await AddProjectFromDirectoryCoreAsync(directory, selectWorkspace: false, cancellationToken);
			if (_disposed || _shutdownBegun || intent != _navigationIntent)
			{ return; }
			var root = WorkspaceRootDetector.NormalizeRoot(directory.Trim());
			var paused = ViewModel.PausedWorkspaces.FirstOrDefault(owner =>
				string.Equals(WorkspaceRootDetector.NormalizeRoot(owner.RootPath), root, StringComparison.OrdinalIgnoreCase));
			if (paused is not null)
			{ await ResumeWorkspaceAsync(paused, cancellationToken); return; }
			var project = ViewModel.Workspaces.FirstOrDefault(owner =>
				string.Equals(WorkspaceRootDetector.NormalizeRoot(owner.RootPath), root, StringComparison.OrdinalIgnoreCase));
			if (project is not null)
			{ await OpenProjectScreenAsync(project, ProjectScreenTab.Overview, cancellationToken); }
		}
		finally { _openingRecentProject = false; }
	}

	public async Task OpenDashboardAsync(CancellationToken cancellationToken = default)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		ThrowIfRejectingInput();
		cancellationToken.ThrowIfCancellationRequested();
		var intent = ++_navigationIntent;
		_projectNavigationVersion++;
		_runtimeCoordinator.CancelActivation();
		ClearSelectionActions(restoreTerminalFocus: false);
		await FlushCurrentNoteAsync(cancellationToken);
		if (intent != _navigationIntent)
		{ return; }
		await HideActiveWebPageAsync(cancellationToken);
		if (intent != _navigationIntent)
		{ return; }
		cancellationToken.ThrowIfCancellationRequested();
		ObjectDisposedException.ThrowIf(_disposed, this);
		ViewModel.SelectDashboard();
		CurrentGitPanel = null;
		Present(new PresentedSurface.Dashboard());
		// Git refresh must not hold up navigation or capture an async UI event indefinitely.
		_ = Dashboard.ActivateAsync(CancellationToken.None);
	}

	private NavigationReturn CaptureNavigationReturn(WorkspaceViewModel? affectedOwner, object? affectedItem = null)
	{
		var current = affectedItem is not null
			? ReferenceEquals(affectedItem, ViewModel.SelectedSession) || ReferenceEquals(affectedItem, ViewModel.SelectedWebPage)
				|| ReferenceEquals(affectedItem, ViewModel.SelectedProjectNote)
			: affectedOwner is not null && ReferenceEquals(FindShownWorkspace(), affectedOwner);
		return new(_navigationIntent, current, affectedOwner, PresentedSurface,
			ViewModel.SelectedSession, ViewModel.SelectedWebPage);
	}

	private async Task ReturnAfterRemovalAsync(NavigationReturn previous, bool ownerRemoved, CancellationToken cancellationToken)
	{
		if (previous.Intent != _navigationIntent || _shutdownBegun)
		{ return; }
		if (previous.WasCurrent)
		{
			if (!ownerRemoved && previous.Owner is { } owner && ViewModel.Workspaces.Contains(owner))
			{ await OpenProjectScreenAsync(owner, ProjectScreenTab.Overview, cancellationToken); }
			else
			{ await OpenDashboardAsync(cancellationToken); }
			return;
		}
		// Background operations never present capture terminals, so no replacement is needed.
		// Internal cleanup may still clear a surface; restore the captured context without cold starts.
		if (previous.Surface == PresentedSurface)
		{ return; }
		if (previous.Session is { } session && ViewModel.Sessions.Contains(session))
		{ await SelectSessionAsync(session, startIfNeeded: false, cancellationToken: cancellationToken); }
		else if (previous.WebPage is { } page && ViewModel.WebPages.Contains(page))
		{ await SelectWebPageAsync(page, cancellationToken); }
		else if (previous.Surface is PresentedSurface.Project project && ViewModel.Workspaces.Contains(project.Screen.Workspace))
		{ await OpenProjectScreenAsync(project.Screen.Workspace, project.Screen.SelectedTab, cancellationToken); }
		else if (previous.Surface is PresentedSurface.Dashboard)
		{ await OpenDashboardAsync(cancellationToken); }
	}

	private sealed record NavigationReturn(long Intent, bool WasCurrent, WorkspaceViewModel? Owner,
		PresentedSurface Surface, SessionViewModel? Session, WebPageViewModel? WebPage);

	private async Task RunNavigationMutationAsync(NavigationReturn previous, Func<Task> mutate, bool releasingOwner = false)
	{
		var ownerId = releasingOwner ? previous.Owner?.Id : null;
		if (ownerId is not null)
		{ _ownerNavigation.AddOrUpdate(ownerId, (1, 1), (_, state) => (state.Epoch + 1, state.Releases + 1)); }
		try
		{
			try
			{ await mutate(); }
			finally
			{
				if (ownerId is not null)
				{ _ownerNavigation.AddOrUpdate(ownerId, (1, 0), (_, state) => (state.Epoch, state.Releases - 1)); }
			}
		}
		catch
		{
			if (previous.Intent == _navigationIntent && !_shutdownBegun)
			{
				try
				{
					if (previous.Surface is PresentedSurface.Project project && ViewModel.Workspaces.Contains(project.Screen.Workspace))
					{ await OpenProjectScreenAsync(project.Screen.Workspace, project.Screen.SelectedTab, CancellationToken.None); }
					else if (previous.Session is { } session && ViewModel.Sessions.Contains(session) && HasActiveTerminalProcess(session))
					{ await SelectSessionAsync(session, startIfNeeded: false, cancellationToken: CancellationToken.None); }
					else if (previous.WebPage is { } page && ViewModel.WebPages.Contains(page))
					{ await SelectWebPageAsync(page, CancellationToken.None); }
					else if (previous.Owner is { } owner && ViewModel.Workspaces.Contains(owner))
					{ await OpenProjectScreenAsync(owner, ProjectScreenTab.Overview, CancellationToken.None); }
					else
					{ await OpenDashboardAsync(CancellationToken.None); }
				}
				catch (Exception exception) { RecordDiagnostic("navigation-return-failed", exception.GetType().Name); }
			}
			throw;
		}
	}
}
