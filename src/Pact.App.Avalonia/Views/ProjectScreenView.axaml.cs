using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Lifecycle;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>The project screen: overview, changes and review logs of one project.</summary>
internal sealed partial class ProjectScreenView : UserControl
{
	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);
	private ProjectScreenViewModel? _screen;
	private ProjectChangesViewModel? _changes;
	private bool _syncing;

	public ProjectScreenView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) => Attach(DataContext as ProjectScreenViewModel);
	}

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks) =>
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

	private void Attach(ProjectScreenViewModel? screen)
	{
		if (ReferenceEquals(screen, _screen))
		{
			return;
		}

		_screen?.PropertyChanged -= OnScreenPropertyChanged;
		_changes?.PropertyChanged -= OnChangesPropertyChanged;
		_screen = screen;
		_changes = screen?.Changes;
		_screen?.PropertyChanged += OnScreenPropertyChanged;
		_changes?.PropertyChanged += OnChangesPropertyChanged;
		RefreshTab();
		RefreshChanges();
		RefreshReviewLogSelection();
	}

	private void OnScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(ProjectScreenViewModel.SelectedTab):
				RefreshTab();
				break;
			case nameof(ProjectScreenViewModel.SelectedReviewLog):
			case nameof(ProjectScreenViewModel.ReviewLogs):
				RefreshReviewLogSelection();
				break;
		}
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.SelectedFile)
			or nameof(ProjectChangesViewModel.Files)
			or nameof(ProjectChangesViewModel.DiffPlaceholder)
			or nameof(ProjectChangesViewModel.HiddenLineCount))
		{
			RefreshChanges();
		}
	}

	private void RefreshTab()
	{
		var tab = _screen?.SelectedTab ?? ProjectScreenTab.Overview;
		OverviewTab.IsChecked = tab == ProjectScreenTab.Overview;
		ChangesTab.IsChecked = tab == ProjectScreenTab.Changes;
		ReviewLogsTab.IsChecked = tab == ProjectScreenTab.ReviewLogs;
		OverviewPanel.IsVisible = tab == ProjectScreenTab.Overview;
		ChangesPanel.IsVisible = tab == ProjectScreenTab.Changes;
		ReviewLogsPanel.IsVisible = tab == ProjectScreenTab.ReviewLogs;
	}

	private void RefreshChanges()
	{
		_syncing = true;
		try
		{
			ChangedFilesList.SelectedItem = _changes?.SelectedFile;
		}
		finally
		{
			_syncing = false;
		}

		DiffPlaceholderText.IsVisible = _changes?.DiffPlaceholder is not null;
		ShowAllButton.IsVisible = _changes?.HiddenLineCount > 0;
	}

	private void RefreshReviewLogSelection()
	{
		_syncing = true;
		try
		{
			ReviewLogList.SelectedItem = _screen?.SelectedReviewLog;
		}
		finally
		{
			_syncing = false;
		}
	}

	private void OnOverviewTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.Overview);

	private void OnChangesTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.Changes);

	private void OnReviewLogsTabClicked(object? sender, RoutedEventArgs e) => SelectTab(ProjectScreenTab.ReviewLogs);

	private void SelectTab(ProjectScreenTab tab)
	{
		// The toggle flips itself on click; the view model decides what is checked.
		RefreshTab();
		if (_screen is { } screen)
		{
			_eventTasks.TryRun("project-screen-tab", () => screen.SelectTabAsync(tab, CancellationToken.None), static _ => Task.CompletedTask);
		}
	}

	private void OnChangedFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncing || _changes is not { } changes
			|| ChangedFilesList.SelectedItem is not ChangedFileViewModel file
			|| ReferenceEquals(file, changes.SelectedFile))
		{
			return;
		}

		_eventTasks.TryRun("project-screen-file", () => changes.SelectFileAsync(file, CancellationToken.None), static _ => Task.CompletedTask);
	}

	private void OnReviewLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncing || _screen is not { } screen
			|| ReviewLogList.SelectedItem is not string log
			|| string.Equals(log, screen.SelectedReviewLog, StringComparison.Ordinal))
		{
			return;
		}

		_eventTasks.TryRun("project-screen-log", () => screen.SelectReviewLogAsync(log, CancellationToken.None), static _ => Task.CompletedTask);
	}

	private void OnShowAllClicked(object? sender, RoutedEventArgs e) => _changes?.ShowAllLines();
}
