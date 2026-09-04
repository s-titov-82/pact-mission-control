using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Pact.Core.RootTabs;

namespace Pact.Presentation.ViewModels;

/// <summary>
/// Presentation state for project-independent terminal and browser tabs shown under ROOT.
/// </summary>
public sealed class RootTabsViewModel
{
	private bool _reconciling;

	/// <summary>Creates the projection and preserves its item identities across later updates.</summary>
	public RootTabsViewModel(RootTabsRecord record)
	{
		Sessions.CollectionChanged += OnTreeItemCollectionChanged;
		WebPages.CollectionChanged += OnTreeItemCollectionChanged;
		UpdateRecord(record);
	}

	/// <summary>Latest normalized persisted ROOT state.</summary>
	public RootTabsRecord Record { get; private set; } = RootTabsRecord.CreateDefault();

	/// <summary>ROOT terminal sessions in saved order.</summary>
	public ObservableCollection<SessionViewModel> Sessions { get; } = [];

	/// <summary>ROOT browser pages in saved order.</summary>
	public ObservableCollection<WebPageViewModel> WebPages { get; } = [];

	/// <summary>Flattened ROOT children in terminal-then-browser display order.</summary>
	public ObservableCollection<object> TreeItems { get; } = [];

	/// <summary>
	/// Reconciles the presentation projection with the persisted record without replacing
	/// surviving child view models, which keeps selection stable.
	/// </summary>
	public void UpdateRecord(RootTabsRecord record)
	{
		ArgumentNullException.ThrowIfNull(record);
		Record = record.Normalize();

		_reconciling = true;
		try
		{
			ReconcileSessions();
			ReconcileWebPages();
		}
		finally
		{
			_reconciling = false;
		}

		RebuildTreeItems();
	}

	/// <summary>Returns whether the identified child is explicitly paused.</summary>
	public bool IsPaused(string itemId) => Record.IsPaused(itemId);

	private void OnTreeItemCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		if (!_reconciling)
		{
			RebuildTreeItems();
		}
	}

	private void ReconcileSessions()
	{
		var existingById = Sessions.ToDictionary(item => item.Record.Id, StringComparer.Ordinal);
		Sessions.Clear();
		foreach (var record in Record.Sessions)
		{
			if (!existingById.TryGetValue(record.Id, out var viewModel))
			{
				viewModel = new SessionViewModel(record, isRootItem: true);
			}
			else
			{
				viewModel.UpdateRecord(record);
			}

			viewModel.SetManuallyPaused(Record.IsPaused(record.Id));
			Sessions.Add(viewModel);
		}
	}

	private void ReconcileWebPages()
	{
		var existingById = WebPages.ToDictionary(item => item.Record.Id, StringComparer.Ordinal);
		WebPages.Clear();
		foreach (var record in Record.WebPages)
		{
			if (!existingById.TryGetValue(record.Id, out var viewModel))
			{
				viewModel = new WebPageViewModel(record, isRootItem: true);
			}
			else
			{
				viewModel.UpdateRecord(record);
			}

			viewModel.SetManuallyPaused(Record.IsPaused(record.Id));
			WebPages.Add(viewModel);
		}
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
}
