using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>Visibility-owned overview of open projects, with bounded working-tree checks.</summary>
public sealed class RootDashboardViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly ObservableCollection<WorkspaceViewModel> _projects;
	private readonly IGitWorkingTreeReader _reader;
	private readonly Func<SessionViewModel, bool> _isLiveAgent;
	private readonly Action<Action> _post;
	private readonly ITimer _timer;
	private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _loadRecentDirectories;
	private IReadOnlyList<string> _recentRoots = [];
	private int _recentLoadVersion;
	private readonly HashSet<WorkspaceViewModel> _watched = [];
	private CancellationTokenSource _epoch = new();
	private Task _refreshTask = Task.CompletedTask;
	private bool _active;
	private bool _disposed;
	private bool _inFlight;
	private bool _pending;
	private int _generation;

	/// <summary>Creates the dashboard over existing project and session ownership.</summary>
	public RootDashboardViewModel(ObservableCollection<WorkspaceViewModel> projects, IGitWorkingTreeReader reader,
		Func<SessionViewModel, bool> isLiveAgent, Action<Action> postToUi, TimeProvider time,
		ObservableCollection<WorkspaceViewModel>? pausedProjects = null,
		Func<CancellationToken, Task<IReadOnlyList<string>>>? loadRecentDirectories = null)
	{
		ArgumentNullException.ThrowIfNull(projects);
		ArgumentNullException.ThrowIfNull(reader);
		ArgumentNullException.ThrowIfNull(isLiveAgent);
		ArgumentNullException.ThrowIfNull(postToUi);
		ArgumentNullException.ThrowIfNull(time);
		_projects = projects;
		PausedProjects = pausedProjects ?? [];
		_loadRecentDirectories = loadRecentDirectories ?? (_ => Task.FromResult<IReadOnlyList<string>>([]));
		_reader = reader;
		_isLiveAgent = isLiveAgent;
		_post = postToUi;
		_timer = time.CreateTimer(_ => _post(() => { if (_active) { _ = RequestRefreshAsync(); } }),
			null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		_projects.CollectionChanged += OnProjectsChanged;
		PausedProjects.CollectionChanged += OnProjectsChanged;
		Reconcile();
	}
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>Cards in project tree order.</summary>
	public ObservableCollection<ProjectDashboardCardViewModel> Cards { get; } = [];
	/// <summary>Paused owners from the existing project state.</summary>
	public ObservableCollection<WorkspaceViewModel> PausedProjects { get; }
	/// <summary>Recent paths not owned by an active or paused project, newest first.</summary>
	public ObservableCollection<string> RecentDirectories { get; } = [];
	/// <summary>Empty or failed history state shown below the Recent heading.</summary>
	public string RecentEmptyText { get; private set; } = "No recent folders";
	/// <summary>Whether the paused-project list needs its empty placeholder.</summary>
	public bool IsPausedEmpty => PausedProjects.Count == 0;
	/// <summary>Whether the recent-folders list needs its empty or failed placeholder.</summary>
	public bool IsRecentEmpty => RecentDirectories.Count == 0;
	/// <summary>Whether there are no open projects.</summary>
	public bool IsEmpty => Cards.Count == 0;
	/// <summary>Starts visible-only reconciliation.</summary>
	public Task ActivateAsync(CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		_active = true;
		Reconcile();
		_timer.Change(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
		return Task.WhenAll(RequestRefreshAsync(), RefreshRecentDirectoriesAsync());
	}
	/// <summary>Stops refresh scheduling and invalidates pending publications.</summary>
	public void Deactivate()
	{
		if (_disposed)
		{ return; }
		_active = false;
		_generation++;
		_pending = false;
		_timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
		_epoch.Cancel();
		_epoch.Dispose();
		_epoch = new();
	}
	/// <summary>Reconciles actual runtime membership without changing session indicators.</summary>
	public void RefreshAgents()
	{
		foreach (var card in Cards)
		{ card.RefreshAgents(_isLiveAgent); }
	}
	private void OnProjectsChanged(object? sender, NotifyCollectionChangedEventArgs args)
	{
		Changed();
		if (_active)
		{ _ = RefreshRecentDirectoriesAsync(); }
	}
	private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is nameof(WorkspaceViewModel.RootPath) or nameof(WorkspaceViewModel.IsGitRepository)
			or nameof(WorkspaceViewModel.Name))
		{ Changed(); }
	}
	private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshAgents();
	private void Changed()
	{
		if (_disposed)
		{ return; }
		_generation++;
		Reconcile();
		if (_active)
		{ _ = RequestRefreshAsync(); }
	}
	private void Reconcile()
	{
		var owners = _projects.Concat(PausedProjects).ToArray();
		foreach (var removed in _watched.Except(owners).ToArray())
		{
			removed.PropertyChanged -= OnWorkspaceChanged;
			removed.Sessions.CollectionChanged -= OnSessionsChanged;
			_watched.Remove(removed);
		}
		foreach (var project in owners)
		{
			if (_watched.Add(project))
			{
				project.PropertyChanged += OnWorkspaceChanged;
				project.Sessions.CollectionChanged += OnSessionsChanged;
			}
		}
		var desired = _projects.Select(project => Cards.FirstOrDefault(card =>
			ReferenceEquals(card.Workspace, project) && card.RootPath == project.RootPath
			&& card.HasGitRepository == project.IsGitRepository) ?? new ProjectDashboardCardViewModel(project)).ToArray();
		for (var index = 0; index < desired.Length; index++)
		{
			var existing = Cards.IndexOf(desired[index]);
			if (existing < 0)
			{ Cards.Insert(index, desired[index]); }
			else if (existing != index)
			{ Cards.Move(existing, index); }
		}
		while (Cards.Count > desired.Length)
		{ Cards.RemoveAt(Cards.Count - 1); }
		RefreshAgents();
		FilterRecentDirectories();
		PropertyChanged?.Invoke(this, new(nameof(IsPausedEmpty)));
		PropertyChanged?.Invoke(this, new(nameof(IsEmpty)));
	}
	private async Task RefreshRecentDirectoriesAsync()
	{
		var version = ++_recentLoadVersion;
		var token = _epoch.Token;
		try
		{
			var roots = await _loadRecentDirectories(token).ConfigureAwait(false);
			_post(() =>
			{
				if (!_active || _disposed || token.IsCancellationRequested || version != _recentLoadVersion)
				{ return; }
				_recentRoots = roots;
				RecentEmptyText = "No recent folders";
				FilterRecentDirectories();
				PropertyChanged?.Invoke(this, new(nameof(RecentEmptyText)));
			});
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
		catch (Exception)
		{
			_post(() =>
			{
				if (!_active || _disposed || token.IsCancellationRequested || version != _recentLoadVersion)
				{ return; }
				RecentEmptyText = "Could not load recent folders";
				PropertyChanged?.Invoke(this, new(nameof(RecentEmptyText)));
			});
		}
	}
	private void FilterRecentDirectories()
	{
		var seen = _projects.Concat(PausedProjects).Select(project => Normalize(project.RootPath))
			.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
		var desired = _recentRoots.Where(root => Normalize(root) is { } normalized && seen.Add(normalized)).ToArray();
		for (var index = 0; index < desired.Length; index++)
		{
			var existing = RecentDirectories.IndexOf(desired[index]);
			if (existing < 0)
			{ RecentDirectories.Insert(index, desired[index]); }
			else if (existing != index)
			{ RecentDirectories.Move(existing, index); }
		}
		while (RecentDirectories.Count > desired.Length)
		{ RecentDirectories.RemoveAt(RecentDirectories.Count - 1); }
		PropertyChanged?.Invoke(this, new(nameof(IsRecentEmpty)));

		static string? Normalize(string path)
		{
			try
			{ return WorkspaceRootDetector.NormalizeRoot(path); }
			catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
		}
	}
	private Task RequestRefreshAsync()
	{
		if (!_active || _disposed)
		{ return Task.CompletedTask; }
		_pending = true;
		if (_inFlight)
		{ return _refreshTask; }
		_inFlight = true;
		return _refreshTask = RefreshCoreAsync();
	}
	private async Task RefreshCoreAsync()
	{
		try
		{
			while (_pending && _active && !_disposed)
			{
				_pending = false;
				var generation = _generation;
				var token = _epoch.Token;
				var cards = Cards.Where(card => card.HasGitRepository).ToArray();
				// One sweep at a time, at most two readers, including across hide/reopen.
				using SemaphoreSlim limit = new(2);
				await Task.WhenAll(cards.GroupBy(card => card.RootPath, StringComparer.OrdinalIgnoreCase).Select(async group =>
				{
					try
					{
						await limit.WaitAsync(token).ConfigureAwait(false);
						int? count;
						try
						{ count = await _reader.ReadChangedFileCountAsync(group.Key, token).ConfigureAwait(false); }
						finally { limit.Release(); }
						_post(() =>
						{
							foreach (var card in group)
							{
								if (_active && !_disposed && generation == _generation && Cards.Contains(card)
									&& card.Workspace.RootPath == card.RootPath)
								{ card.SetCount(count); }
							}
						});
					}
					catch (OperationCanceledException) when (token.IsCancellationRequested) { }
					catch (Exception)
					{
						_post(() =>
						{
							foreach (var card in group)
							{
								if (_active && !_disposed && generation == _generation && Cards.Contains(card))
								{ card.SetCount(null); }
							}
						});
					}
				}));
			}
		}
		finally { _inFlight = false; }
	}
	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{ return; }
		Deactivate();
		_disposed = true;
		_timer.Dispose();
		_epoch.Dispose();
		_projects.CollectionChanged -= OnProjectsChanged;
		PausedProjects.CollectionChanged -= OnProjectsChanged;
		foreach (var project in _watched)
		{
			project.PropertyChanged -= OnWorkspaceChanged;
			project.Sessions.CollectionChanged -= OnSessionsChanged;
		}
		_watched.Clear();
	}
}