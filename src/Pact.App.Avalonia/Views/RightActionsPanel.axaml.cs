using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Pact.Core.Prompting;
using Pact.Core.Scenarios;
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
	public event EventHandler<ScenarioDefinition>? ScenarioRequested;

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
		var showsTree = Workspace?.ShowsDocumentTree == true;
		DocumentsSection.IsVisible = showsTree;
		// A hidden child still leaves a star row occupying the remaining height,
		// so the row itself has to collapse.
		DefaultActionsPanel.RowDefinitions[1].Height = showsTree
			? new GridLength(1, GridUnitType.Star)
			: new GridLength(0);
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

	private void OnScenarioClicked(object? sender, RoutedEventArgs e)
	{
		if (sender is Control { DataContext: ScenarioDefinition definition })
		{
			ScenarioRequested?.Invoke(this, definition);
		}
	}
}
