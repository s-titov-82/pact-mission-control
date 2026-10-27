using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pact.App.Avalonia.Lifecycle;
using Pact.Core.Prompting;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

internal sealed partial class RightActionsPanel : UserControl
{
	private bool _syncingSelection;

	public RightActionsPanel()
	{
		InitializeComponent();
	}

	/// <summary>Raised when the tree's selection moves to a node, folder or document.</summary>
	public event EventHandler<MarkdownTreeNodeViewModel>? TreeNodeSelected;

	/// <summary>Raised when a folder row is tapped, including an already-selected one.</summary>
	public event EventHandler<MarkdownTreeNodeViewModel>? FolderToggleRequested;

	public event EventHandler<PromptTemplateRecord>? QuickActionRequested;
	public event EventHandler? SettingsRequested;
	public event EventHandler? RestartAndUpdateRequested;

	/// <summary>Raised when the user clicks the Changes entry.</summary>
	public event EventHandler? ChangesEntryRequested;

	/// <summary>Change list shown by the Changes entry; <see langword="null"/> hides the entry.</summary>
	public ProjectChangesViewModel? Changes
	{
		get;
		set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.PropertyChanged -= OnChangesPropertyChanged;
			field = value;
			field?.PropertyChanged += OnChangesPropertyChanged;
			RefreshChangesEntry();
		}
	}

	private void OnChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.EntryTitle)
			or nameof(ProjectChangesViewModel.EntrySubtitle)
			or nameof(ProjectChangesViewModel.IsAvailable)
			or nameof(ProjectChangesViewModel.FileCount))
		{
			RefreshChangesEntry();
		}
	}

	private void RefreshChangesEntry()
	{
		var changes = Changes;
		ChangesSection.IsVisible = changes is { IsAvailable: true };
		var dirty = changes?.FileCount > 0;
		ChangesEntry.IsVisible = dirty;
		NoChangesText.IsVisible = !dirty;
		ChangesEntryTitle.Text = changes?.EntryTitle;
		ChangesEntrySubtitle.Text = changes?.EntrySubtitle;
	}

	private void OnChangesEntryClicked(object? sender, RoutedEventArgs e) =>
		ChangesEntryRequested?.Invoke(this, EventArgs.Empty);

	/// <summary>Shows a review log path as its file name.</summary>
	public static FuncValueConverter<string?, string?> FileNameOnly { get; } =
		new(path => path is null ? null : Path.GetFileNameWithoutExtension(path));

	/// <summary>Project screen whose tab list this panel shows: changed files or review logs.</summary>
	public ProjectScreenViewModel? ProjectScreen
	{
		get;
		set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.PropertyChanged -= OnProjectScreenPropertyChanged;
			_screenChanges?.PropertyChanged -= OnScreenChangesPropertyChanged;
			field = value;
			_screenChanges = value?.Changes;
			field?.PropertyChanged += OnProjectScreenPropertyChanged;
			_screenChanges?.PropertyChanged += OnScreenChangesPropertyChanged;
			RefreshProjectList();
		}
	}

	private ProjectChangesViewModel? _screenChanges;

	private void OnProjectScreenPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectScreenViewModel.SelectedTab)
			or nameof(ProjectScreenViewModel.ReviewLogs)
			or nameof(ProjectScreenViewModel.SelectedReviewLog))
		{
			RefreshProjectList();
		}
	}

	private void OnScreenChangesPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(ProjectChangesViewModel.Files)
			or nameof(ProjectChangesViewModel.SelectedFile))
		{
			RefreshProjectList();
		}
	}

	private void RefreshProjectList()
	{
		var tab = ProjectScreen?.SelectedTab;
		var showsFiles = tab == ProjectScreenTab.Changes && _screenChanges is not null;
		var showsLogs = tab == ProjectScreenTab.ReviewLogs;
		ProjectListSection.IsVisible = showsFiles || showsLogs;
		ProjectListTitle.Text = showsFiles ? "Changed files" : "Review logs";
		ChangedFilesList.IsVisible = showsFiles;
		ReviewLogList.IsVisible = showsLogs;
		_syncingSelection = true;
		try
		{
			ChangedFilesList.ItemsSource = showsFiles ? _screenChanges!.Files : null;
			ChangedFilesList.SelectedItem = showsFiles ? _screenChanges!.SelectedFile : null;
			ReviewLogList.ItemsSource = showsLogs ? ProjectScreen!.ReviewLogs : null;
			ReviewLogList.SelectedItem = showsLogs ? ProjectScreen!.SelectedReviewLog : null;
		}
		finally
		{
			_syncingSelection = false;
		}

		RefreshListRows();
	}

	private void OnChangedFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncingSelection || _screenChanges is not { } changes
			|| ChangedFilesList.SelectedItem is not ChangedFileViewModel file
			|| ReferenceEquals(file, changes.SelectedFile))
		{
			return;
		}

		RunSelection(() => changes.SelectFileAsync(file, CancellationToken.None));
	}

	private void OnReviewLogSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncingSelection || ProjectScreen is not { } screen
			|| ReviewLogList.SelectedItem is not string log
			|| string.Equals(log, screen.SelectedReviewLog, StringComparison.Ordinal))
		{
			return;
		}

		RunSelection(() => screen.SelectReviewLogAsync(log, CancellationToken.None));
	}

	/// <summary>Raised with a log's project-relative path when its delete button is clicked.</summary>
	public event EventHandler<string>? DeleteReviewLogRequested;

	private void OnDeleteReviewLogClicked(object? sender, RoutedEventArgs e)
	{
		e.Handled = true;
		if (sender is Control { DataContext: string log })
		{
			DeleteReviewLogRequested?.Invoke(this, log);
		}
	}

	internal void ConfigureLifecycle(ObservedTaskGroup eventTasks) =>
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));

	private ObservedTaskGroup _eventTasks = new(static (_, _) => Task.CompletedTask);

	private void RunSelection(Func<Task> selection) =>
		_eventTasks.TryRun("right-panel-project-list", selection, static _ => Task.CompletedTask);

	/// <summary>Documentation workspace whose tree this panel renders, if any.</summary>
	public DocsAndNotesWorkspaceViewModel? Workspace
	{
		get;
		set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.PropertyChanged -= OnWorkspacePropertyChanged;
			field = value;
			field?.PropertyChanged += OnWorkspacePropertyChanged;
			RefreshDocumentTree();
		}
	}

	/// <summary>Shows or hides metadata-only facts for the selected tab.</summary>
	public void SetSelectedTabDetails(SelectedTabDetailsViewModel? details, bool visible)
	{
		SelectedTabDetailsSection.DataContext = details;
		SelectedTabDetailsSection.IsVisible = visible && details is not null;
	}

	/// <summary>Displays transient action or error text separately from selected-tab facts.</summary>
	public void SetStatusText(string? text)
	{
		StatusText.Text = text;
		StatusText.IsVisible = !string.IsNullOrWhiteSpace(text);
	}

	public void SetUpdateRestartAction(bool visible, string text)
	{
		RestartAndUpdateButton.Content = text;
		RestartAndUpdateButton.IsVisible = visible;
	}

	private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(DocsAndNotesWorkspaceViewModel.VisibleTree)
			or nameof(DocsAndNotesWorkspaceViewModel.SelectedSection)
			or nameof(DocsAndNotesWorkspaceViewModel.SelectedNode)
			or nameof(DocsAndNotesWorkspaceViewModel.ShowsDocumentTree))
		{
			RefreshDocumentTree();
		}
	}

	private void RefreshDocumentTree()
	{
		DocumentsSection.IsVisible = Workspace?.ShowsDocumentTree == true;
		RefreshListRows();
		_syncingSelection = true;
		try
		{
			DocumentTree.ItemsSource = Workspace?.VisibleTree;
			DocumentTree.SelectedItem = Workspace?.SelectedNode;
		}
		finally
		{
			_syncingSelection = false;
		}
	}

	// The docs tree and the project list share one row. A hidden child still leaves a star row
	// occupying the remaining height, so the row itself has to collapse; without a list, the
	// free space sits between the Changes entry and the tab details.
	private void RefreshListRows()
	{
		var showsList = DocumentsSection.IsVisible || ProjectListSection.IsVisible;
		DefaultActionsPanel.RowDefinitions[1].Height = showsList
			? new GridLength(1, GridUnitType.Star)
			: new GridLength(0);
		DefaultActionsPanel.RowDefinitions[4].Height = showsList
			? new GridLength(0)
			: new GridLength(1, GridUnitType.Star);
	}

	private void OnDocumentTreeSelectionChanged(object? sender, SelectionChangedEventArgs e)
	{
		if (_syncingSelection)
		{
			return;
		}

		if (DocumentTree.SelectedItem is MarkdownTreeNodeViewModel node)
		{
			TreeNodeSelected?.Invoke(this, node);
		}
	}

	private void OnDocumentRowTapped(object? sender, TappedEventArgs e)
	{
		// Tapped fires for a repeat click on an already-selected row, which
		// SelectionChanged cannot report, so it is the collapse signal.
		if (sender is Control { DataContext: MarkdownTreeNodeViewModel { IsFolder: true } node })
		{
			FolderToggleRequested?.Invoke(this, node);
		}
	}

	private void OnQuickActionClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Control { DataContext: PromptTemplateRecord template })
		{
			QuickActionRequested?.Invoke(this, template);
		}
	}

	private void OnSettingsClicked(object? sender, RoutedEventArgs e) =>
		SettingsRequested?.Invoke(this, EventArgs.Empty);

	private void OnRestartAndUpdateClicked(object? sender, RoutedEventArgs e) =>
		RestartAndUpdateRequested?.Invoke(this, EventArgs.Empty);

}
