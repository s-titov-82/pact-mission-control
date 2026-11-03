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
		IsExpanded = !record.IsCollapsed;
		IsGitRepository = _isGitRepository(Record.RootPath);
		Sessions.CollectionChanged += OnTreeItemCollectionChanged;
		WebPages.CollectionChanged += OnTreeItemCollectionChanged;
		Notes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsNotesTabOpen));
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Persisted project state.</summary>
	public ProjectRecord Record { get; private set; }

	/// <summary>Current persisted project resources, reconciled with the project record.</summary>
	public IReadOnlyList<ProjectLink> Links => Record.Links;

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

	/// <summary>
	/// Whether the project is open in the tree. Starts from the saved state and is left alone by
	/// <see cref="UpdateRecord"/>, so a save that lands late cannot flip what the user sees.
	/// </summary>
	public bool IsExpanded { get; private set; }

	/// <summary>Terminal sessions in this project.</summary>
	public ObservableCollection<SessionViewModel> Sessions { get; } = [];

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

	/// <summary>Whether the project's own screen is what the shell shows.</summary>
	public bool IsCurrentProject { get; private set; }

	/// <summary>Recovery guidance for a review in this project, including while its sessions are collapsed.</summary>
	public string? ReviewAttentionToolTip { get; private set; }

	/// <summary>Whether at least one of this project's sessions has a review delivery warning.</summary>
	public bool HasReviewAttention => ReviewAttentionToolTip is not null;

	/// <summary>Refreshes the project warning after its sessions' review badges have been updated.</summary>
	public void RefreshReviewAttention()
	{
		var affected = Sessions.FirstOrDefault(session => session.Review is { HasDeliveryIssue: true, IsOwnTurn: true })
			?? Sessions.FirstOrDefault(session => session.Review?.HasDeliveryIssue == true);
		var toolTip = affected is null
			? null
			: $"{affected.Title}: {affected.Review!.DeliveryIssue} {affected.Review.RecoveryInstruction}";
		if (ReviewAttentionToolTip == toolTip)
		{
			return;
		}

		ReviewAttentionToolTip = toolTip;
		OnPropertyChanged(nameof(ReviewAttentionToolTip));
		OnPropertyChanged(nameof(HasReviewAttention));
	}

	/// <summary>Marks the project row as the shown item.</summary>
	public void SetCurrentProject(bool isCurrent)
	{
		if (IsCurrentProject == isCurrent)
		{
			return;
		}

		IsCurrentProject = isCurrent;
		OnPropertyChanged(nameof(IsCurrentProject));
	}

	/// <summary>Opens or closes the project in the tree.</summary>
	public void SetExpanded(bool isExpanded)
	{
		if (IsExpanded == isExpanded)
		{
			return;
		}

		IsExpanded = isExpanded;
		OnPropertyChanged(nameof(IsExpanded));
	}

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
		OnPropertyChanged(nameof(Links));
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

		if (ReferenceEquals(sender, WebPages))
		{
			return Sessions.Count + (groupIndex >= 0 ? groupIndex : CountTreeItems<WebPageViewModel>());
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

		foreach (var webPage in WebPages)
		{
			TreeItems.Add(webPage);
		}

	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
