using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Pact.App.Avalonia.Lifecycle;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>The project screen: overview, changes and review logs of one project.</summary>
internal sealed partial class ProjectScreenView : UserControl
{
	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);
	private ProjectScreenViewModel? _screen;
	private ProjectChangesViewModel? _changes;

	private static readonly TimeSpan LogReloadInterval = TimeSpan.FromSeconds(2);
	private readonly DispatcherTimer _logReloadTimer;

	public ProjectScreenView()
	{
		InitializeComponent();
		DataContextChanged += (_, _) => Attach(DataContext as ProjectScreenViewModel);
		// A review still running appends turns to the open log; re-read it while it is on screen.
		_logReloadTimer = new DispatcherTimer { Interval = LogReloadInterval };
		_logReloadTimer.Tick += OnLogReloadTick;
		AttachedToVisualTree += (_, _) => _logReloadTimer.Start();
		DetachedFromVisualTree += (_, _) => _logReloadTimer.Stop();
	}

	private void OnLogReloadTick(object? sender, EventArgs e)
	{
		if (IsEffectivelyVisible && _screen is { SelectedTab: ProjectScreenTab.ReviewLogs } screen)
		{
			_eventTasks.TryRun("project-screen-log-reload", () => screen.ReloadReviewLogAsync(CancellationToken.None), static _ => Task.CompletedTask);
		}
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
	}

	private void OnScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(ProjectScreenViewModel.SelectedTab))
		{
			RefreshTab();
		}
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.DiffPlaceholder)
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
		DiffPlaceholderText.IsVisible = _changes?.DiffPlaceholder is not null;
		ShowAllButton.IsVisible = _changes?.HiddenLineCount > 0;
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

	private void OnShowAllClicked(object? sender, RoutedEventArgs e) => _changes?.ShowAllLines();
}
