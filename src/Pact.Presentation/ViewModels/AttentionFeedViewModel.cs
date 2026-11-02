using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Pact.Core.Sessions;

namespace Pact.Presentation.ViewModels;

/// <summary>A current attention target, capturing its owning projection rather than a title or id alone.</summary>
/// <param name="Target">The original terminal or browser row.</param>
/// <param name="Workspace">Owning running project, null for ROOT or orchestrator.</param>
/// <param name="IsOrchestrator">Whether activation uses the pinned orchestrator selector.</param>
/// <param name="OwnerName">Project, ROOT or Orchestrator label.</param>
/// <param name="Title">Current tab label.</param>
/// <param name="Indicator">The most urgent attention reason.</param>
/// <param name="ToolTip">All current attention reasons, without acknowledgement side effects.</param>
public sealed record AttentionItem(object Target, WorkspaceViewModel? Workspace, bool IsOrchestrator,
	string OwnerName, string Title, TerminalTabIndicator Indicator, string ToolTip)
{
	/// <summary>Whether this card also carries an explicitly retained manual bookmark.</summary>
	public bool IsMarkedForLater => Target is SessionViewModel { IsMarkedForLater: true };
}

/// <summary>Projects current tab attention without an event history or a second source of tab state.</summary>
public sealed class AttentionFeedViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly ObservableCollection<WorkspaceViewModel> _workspaces;
	private readonly RootTabsViewModel _rootTabs;
	private readonly OrchestratorSlotViewModel _orchestratorSlot;
	private readonly HashSet<INotifyPropertyChanged> _properties = [];
	private readonly HashSet<INotifyCollectionChanged> _collections = [];
	private bool _disposed;

	/// <summary>Observes running project, ROOT and orchestrator membership and their current indicators.</summary>
	public AttentionFeedViewModel(ObservableCollection<WorkspaceViewModel> workspaces,
		RootTabsViewModel rootTabs, OrchestratorSlotViewModel orchestratorSlot)
	{
		_workspaces = workspaces ?? throw new ArgumentNullException(nameof(workspaces));
		_rootTabs = rootTabs ?? throw new ArgumentNullException(nameof(rootTabs));
		_orchestratorSlot = orchestratorSlot ?? throw new ArgumentNullException(nameof(orchestratorSlot));
		Refresh();
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>Current attention targets in priority then tree order.</summary>
	public IReadOnlyList<AttentionItem> Items { get; private set; } = [];
	/// <summary>Whether the feed has any current targets.</summary>
	public bool HasItems => Items.Count != 0;
	/// <summary>Rejects cards whose original target no longer belongs to a running owner or needs attention.</summary>
	public bool Contains(AttentionItem item) => Owns(item) && Items.Any(current =>
		ReferenceEquals(current.Target, item.Target) && ReferenceEquals(current.Workspace, item.Workspace)
		&& current.IsOrchestrator == item.IsOrchestrator);

	/// <summary>Checks owner membership even after normal activation has acknowledged the attention reason.</summary>
	public bool Owns(AttentionItem item)
	{
		ArgumentNullException.ThrowIfNull(item);
		if (_disposed)
		{
			return false;
		}
		if (item.IsOrchestrator)
		{
			return _orchestratorSlot.IsEnabled && ReferenceEquals(_orchestratorSlot.Session, item.Target);
		}
		if (item.Target is SessionViewModel { IsManuallyPaused: true } or WebPageViewModel { IsManuallyPaused: true })
		{
			return false;
		}
		return item.Workspace is { } workspace
			? _workspaces.Contains(workspace) && workspace.TreeItems.Contains(item.Target)
			: _rootTabs.TreeItems.Contains(item.Target);
	}

	/// <summary>Detaches all source subscriptions.</summary>
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		foreach (var source in _properties)
		{
			source.PropertyChanged -= OnPropertyChanged;
		}
		foreach (var source in _collections)
		{
			source.CollectionChanged -= OnCollectionChanged;
		}
		_properties.Clear();
		_collections.Clear();
		Publish([]);
	}

	private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => Refresh();

	private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
	{
		if (args.PropertyName is null or "" or "Indicator" or "Review" or "Title" or "Name" or "Record"
			or "IsManuallyPaused" or "StatusDescription" or "HasMonitorUnread" or "MonitorDiagnostic"
			or "Session" or "IsEnabled")
		{
			Refresh();
		}
	}

	private void Refresh()
	{
		if (_disposed)
		{
			return;
		}
		HashSet<INotifyPropertyChanged> properties = [_orchestratorSlot];
		HashSet<INotifyCollectionChanged> collections = [_workspaces, _rootTabs.TreeItems];
		List<AttentionItem> items = [];
		void Add(object target, WorkspaceViewModel? workspace, bool orchestrator, string owner)
		{
			if (target is INotifyPropertyChanged source)
			{
				properties.Add(source);
			}
			if (target is SessionViewModel { IsManuallyPaused: false } session)
			{
				var indicator = session.Indicator;
				var issue = session.Review?.DeliveryIssue;
				if (indicator != TerminalTabIndicator.InputRequested && issue is not null)
				{
					indicator = TerminalTabIndicator.Failed;
				}
				if (session.IsMarkedForLater || indicator is TerminalTabIndicator.InputRequested or TerminalTabIndicator.Failed or TerminalTabIndicator.Unread)
				{
					var description = session.Indicator is TerminalTabIndicator.InputRequested or TerminalTabIndicator.Failed or TerminalTabIndicator.Unread
						? $"{session.Indicator}: {session.StatusDescription}"
						: string.Empty;
					if (session.IsMarkedForLater)
					{
						description = $"{description}\nRead later (double-click the session to clear)".TrimStart();
					}
					if (issue is not null)
					{
						description = $"{description}\n{issue} {session.Review!.RecoveryInstruction}".TrimStart();
					}
					items.Add(new(session, workspace, orchestrator, owner, session.Title, indicator, description));
				}
			}
			else if (target is WebPageViewModel { IsManuallyPaused: false, HasMonitorUnread: true } page)
			{
				items.Add(new(page, workspace, false, owner, page.Title, TerminalTabIndicator.Unread,
					$"Monitored page has unseen changes\n{page.MonitorDiagnostic}".TrimEnd()));
			}
		}
		if (_orchestratorSlot.Session is { } slotSession && _orchestratorSlot.IsEnabled)
		{
			Add(slotSession, null, true, "Orchestrator");
		}
		foreach (var target in _rootTabs.TreeItems)
		{
			Add(target, null, false, "ROOT");
		}
		foreach (var workspace in _workspaces)
		{
			properties.Add(workspace);
			collections.Add(workspace.TreeItems);
			foreach (var target in workspace.TreeItems)
			{
				Add(target, workspace, false, workspace.Name);
			}
		}
		foreach (var source in _properties.Except(properties).ToArray())
		{
			source.PropertyChanged -= OnPropertyChanged;
			_properties.Remove(source);
		}
		foreach (var source in properties)
		{
			if (_properties.Add(source))
			{
				source.PropertyChanged += OnPropertyChanged;
			}
		}
		foreach (var source in _collections.Except(collections).ToArray())
		{
			source.CollectionChanged -= OnCollectionChanged;
			_collections.Remove(source);
		}
		foreach (var source in collections)
		{
			if (_collections.Add(source))
			{
				source.CollectionChanged += OnCollectionChanged;
			}
		}
		Publish(items.OrderBy(item => item.Indicator switch
		{
			TerminalTabIndicator.InputRequested => 0,
			TerminalTabIndicator.Failed => 1,
			_ => 2
		}).ToArray());
	}

	private void Publish(IReadOnlyList<AttentionItem> items)
	{
		if (Items.SequenceEqual(items))
		{
			return;
		}
		Items = items;
		PropertyChanged?.Invoke(this, new(nameof(Items)));
		PropertyChanged?.Invoke(this, new(nameof(HasItems)));
	}
}
