using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Projects;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// A project node in the tree, owning its sessions, scenario runs, web pages, and notes tab.
/// </summary>
/// <remarks>
/// <see cref="TreeItems"/> is kept in sync with the four typed collections incrementally rather
/// than rebuilt on every change, so adding one item does not reset the tree's selection and
/// expansion state.
/// </remarks>
public sealed class WorkspaceViewModel : INotifyPropertyChanged
{
	private readonly Func<string?, bool> _isGitRepository;

	/// <summary>Creates a view model that detects git repositories from the filesystem.</summary>
	public WorkspaceViewModel(ProjectRecord record)
		: this(record, GitRepositoryDetector.IsGitRepository)
	{
	}

	/// <summary>
	/// Creates a view model with an injectable git-repository test, for tests that must not
	/// touch the filesystem.
	/// </summary>
	public WorkspaceViewModel(ProjectRecord record, Func<string?, bool> isGitRepository)
	{
		ArgumentNullException.ThrowIfNull(record);
		ArgumentNullException.ThrowIfNull(isGitRepository);

		Record = record;
		_isGitRepository = isGitRepository;
		IsGitRepository = _isGitRepository(Record.RootPath);
		Sessions.CollectionChanged += OnTreeItemCollectionChanged;
		ScenarioRuns.CollectionChanged += OnTreeItemCollectionChanged;
		WebPages.CollectionChanged += OnTreeItemCollectionChanged;
		Notes.CollectionChanged += OnTreeItemCollectionChanged;
		Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsNotesTabOpen));
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Persisted project state.</summary>
	public ProjectRecord Record { get; private set; }

	/// <summary>Project id.</summary>
	public string Id => Record.Id;

	/// <summary>Project display name.</summary>
	public string Name => Record.Name;

	/// <summary>Project root directory.</summary>
	public string RootPath => Record.RootPath;

	/// <summary>Project status as text, for binding.</summary>
	public string Status => Record.Status.ToString();

	/// <summary>
	/// Whether the root is a git repository, deciding if the git panel is offered. Re-evaluated
	/// on each <see cref="UpdateRecord"/>, since a directory can become a repository later.
	/// </summary>
	public bool IsGitRepository { get; private set; }

	/// <summary>Terminal sessions in this project.</summary>
	public ObservableCollection<SessionViewModel> Sessions { get; } = [];

	/// <summary>Scenario runs shown under this project.</summary>
	public ObservableCollection<ScenarioRunViewModel> ScenarioRuns { get; } = [];

	/// <summary>Web page tabs in this project.</summary>
	public ObservableCollection<WebPageViewModel> WebPages { get; } = [];

	/// <summary>Notes tab, holding at most one entry.</summary>
	public ObservableCollection<ProjectNoteViewModel> Notes { get; } = [];

	/// <summary>Whether the notes tab is currently shown.</summary>
	public bool IsNotesTabOpen => Notes.Count > 0;

	/// <summary>
	/// Flattened children in display order — sessions, scenario runs, web pages, then notes —
	/// as the single collection the tree binds to.
	/// </summary>
	public ObservableCollection<object> TreeItems { get; } = [];

	/// <summary>
	/// Replaces the persisted state and raises change notifications for the derived properties.
	/// </summary>
	public void UpdateRecord(ProjectRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);

		Record = record;
		var newIsGitRepository = _isGitRepository(Record.RootPath);
		if (IsGitRepository != newIsGitRepository)
		{
			IsGitRepository = newIsGitRepository;
			OnPropertyChanged(nameof(IsGitRepository));
		}

		OnPropertyChanged(nameof(Record));
		OnPropertyChanged(nameof(Name));
		OnPropertyChanged(nameof(RootPath));
		OnPropertyChanged(nameof(Status));
	}

	private void OnTreeItemCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		switch (e.Action)
		{
			case NotifyCollectionChangedAction.Add when e.NewItems is { Count: > 0 }:
				InsertTreeItems(sender, e);
				break;
			case NotifyCollectionChangedAction.Remove when e.OldItems is { Count: > 0 }:
				RemoveTreeItems(e);
				break;
			default:
				RebuildTreeItems();
				break;
		}
	}

	private void InsertTreeItems(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (e.NewItems is null)
		{
			RebuildTreeItems();
			return;
		}

		for (var i = 0; i < e.NewItems.Count; i++)
		{
			var groupIndex = e.NewStartingIndex >= 0 ? e.NewStartingIndex + i : -1;
			var treeIndex = GetTreeInsertIndex(sender, groupIndex);
			TreeItems.Insert(treeIndex, e.NewItems[i]!);
		}
	}

	private void RemoveTreeItems(NotifyCollectionChangedEventArgs e)
	{
		if (e.OldItems is null)
		{
			RebuildTreeItems();
			return;
		}

		foreach (var item in e.OldItems)
		{
			TreeItems.Remove(item);
		}
	}

	private int GetTreeInsertIndex(object? sender, int groupIndex)
	{
		if (ReferenceEquals(sender, Sessions))
		{
			return groupIndex >= 0 ? groupIndex : CountTreeItems<SessionViewModel>();
		}

		if (ReferenceEquals(sender, ScenarioRuns))
		{
			return Sessions.Count + (groupIndex >= 0 ? groupIndex : CountTreeItems<ScenarioRunViewModel>());
		}

		if (ReferenceEquals(sender, WebPages))
		{
			return Sessions.Count + ScenarioRuns.Count + (groupIndex >= 0 ? groupIndex : CountTreeItems<WebPageViewModel>());
		}

		if (ReferenceEquals(sender, Notes))
		{
			return Sessions.Count + ScenarioRuns.Count + WebPages.Count
				+ (groupIndex >= 0 ? groupIndex : CountTreeItems<ProjectNoteViewModel>());
		}

		return TreeItems.Count;
	}

	private int CountTreeItems<T>()
	{
		var count = 0;
		foreach (var item in TreeItems)
		{
			if (item is T)
			{
				count++;
			}
		}

		return count;
	}

	private void RebuildTreeItems()
	{
		TreeItems.Clear();
		foreach (var session in Sessions)
		{
			TreeItems.Add(session);
		}

		foreach (var run in ScenarioRuns)
		{
			TreeItems.Add(run);
		}

		foreach (var webPage in WebPages)
		{
			TreeItems.Add(webPage);
		}

		foreach (var note in Notes)
		{
			TreeItems.Add(note);
		}
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
