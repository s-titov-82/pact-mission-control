using System.ComponentModel;
using System.Runtime.CompilerServices;
using Pact.Core.Projects;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>Identifies one of the primary tabs in the documentation pane.</summary>
public enum DocsAndNotesSection
{
	/// <summary>App-owned project notes, which default to the editor.</summary>
	Notes,

	/// <summary>Project Markdown outside the <c>docs</c> directory.</summary>
	Common,

	/// <summary>Markdown under <c>docs</c>, including <c>docs/superpowers</c>.</summary>
	Docs
}

/// <summary>Folder or document node rendered by the documentation tree.</summary>
public sealed class MarkdownTreeNodeViewModel : INotifyPropertyChanged
{
	/// <summary>Creates a folder node.</summary>
	public MarkdownTreeNodeViewModel(
		string title,
		string relativePath,
		IReadOnlyList<MarkdownTreeNodeViewModel> children)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		Title = title;
		RelativePath = relativePath;
		Children = children ?? throw new ArgumentNullException(nameof(children));
	}

	/// <summary>Creates a document node bound to an editable document.</summary>
	public MarkdownTreeNodeViewModel(
		string title,
		string relativePath,
		IMarkdownEditorDocument document)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		Title = title;
		RelativePath = relativePath;
		Document = document ?? throw new ArgumentNullException(nameof(document));
		Children = [];
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>File or folder name shown in the tree.</summary>
	public string Title { get; }

	/// <summary>Project-relative path, used as tooltip and as expansion-state key.</summary>
	public string RelativePath { get; }

	/// <summary>Child nodes; always empty for document nodes.</summary>
	public IReadOnlyList<MarkdownTreeNodeViewModel> Children { get; }

	/// <summary>Editable document, or <see langword="null"/> for folder nodes.</summary>
	public IMarkdownEditorDocument? Document { get; }

	/// <summary>Whether this node groups other nodes instead of addressing a file.</summary>
	public bool IsFolder => Document is null;

	/// <summary>Whether the folder is expanded in the tree.</summary>
	public bool IsExpanded
	{
		get;
		set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
		}
	}
}

/// <summary>
/// Owns document discovery, tree selection, and persistence for one project pane.
/// </summary>
public sealed class DocsAndNotesWorkspaceViewModel : INotifyPropertyChanged
{
	private static readonly DocumentSaveStatus CleanSaveStatus =
		new(DocumentSaveState.Clean);
	private readonly string _projectRootPath;
	private readonly IProjectMarkdownFileStore _fileStore;
	private readonly TimeSpan _debounceInterval;
	private readonly Dictionary<string, IMarkdownEditorDocument> _documents =
		new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<DocsAndNotesSection, string> _lastSelectedPaths = [];
	private readonly HashSet<string> _expandedFolders = new(StringComparer.OrdinalIgnoreCase);
	private readonly HashSet<DocsAndNotesSection> _visitedSections = [];

	/// <summary>Creates a Docs &amp; Notes workspace for one project root.</summary>
	public DocsAndNotesWorkspaceViewModel(
		string projectRootPath,
		ProjectNoteDocument notesDocument,
		IProjectMarkdownFileStore fileStore,
		TimeSpan debounceInterval)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		_projectRootPath = Path.GetFullPath(projectRootPath);
		NotesDocument = notesDocument ?? throw new ArgumentNullException(nameof(notesDocument));
		_fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
		_debounceInterval = debounceInterval;
		ActiveDocument = notesDocument;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;

	/// <summary>Currently selected primary tab.</summary>
	public DocsAndNotesSection SelectedSection
	{
		get;
		private set
		{
			if (field == value)
			{
				return;
			}

			field = value;
			OnPropertyChanged();
			OnPropertyChanged(nameof(VisibleTree));
			OnPropertyChanged(nameof(ShowsDocumentTree));
		}
	} = DocsAndNotesSection.Notes;

	/// <summary>Node currently selected in the documentation tree, if any.</summary>
	public MarkdownTreeNodeViewModel? SelectedNode
	{
		get;
		private set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field = value;
			OnPropertyChanged();
		}
	}

	/// <summary>Document currently displayed by the editor or preview.</summary>
	public IMarkdownEditorDocument? ActiveDocument
	{
		get;
		private set
		{
			if (ReferenceEquals(field, value))
			{
				return;
			}

			field?.SaveStatusChanged -= OnActiveDocumentSaveStatusChanged;

			field = value;
			field?.SaveStatusChanged += OnActiveDocumentSaveStatusChanged;
			OnPropertyChanged();
			PublishSaveStatusProperties();
		}
	}

	/// <summary>
	/// Gets the single notes document owned by this project workspace.
	/// </summary>
	public ProjectNoteDocument NotesDocument { get; }

	/// <summary>Persistence state of the active editor document.</summary>
	public DocumentSaveStatus SaveStatus => ActiveDocument?.SaveStatus ?? CleanSaveStatus;

	/// <summary>User-facing message for the last failed save.</summary>
	public string? SaveErrorMessage => SaveStatus.ErrorMessage;

	/// <summary>Whether the active document has a persistence failure requiring user action.</summary>
	public bool HasSaveError => SaveStatus.State == DocumentSaveState.Failed;

	/// <summary>Retries persistence of the active document without discarding its buffer.</summary>
	public Task RetrySaveAsync(CancellationToken cancellationToken = default) =>
		ActiveDocument?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

	/// <summary>Tree of Markdown outside <c>docs</c>.</summary>
	public IReadOnlyList<MarkdownTreeNodeViewModel> CommonTree { get; private set; } = [];

	/// <summary>Tree of Markdown under <c>docs</c>, rooted at the <c>docs</c> children.</summary>
	public IReadOnlyList<MarkdownTreeNodeViewModel> DocsTree { get; private set; } = [];

	/// <summary>Tree shown for the selected section; empty for Notes.</summary>
	public IReadOnlyList<MarkdownTreeNodeViewModel> VisibleTree => SelectedSection switch
	{
		DocsAndNotesSection.Common => CommonTree,
		DocsAndNotesSection.Docs => DocsTree,
		_ => []
	};

	/// <summary>Whether the right panel must show the documentation tree.</summary>
	public bool ShowsDocumentTree => SelectedSection is DocsAndNotesSection.Common
		or DocsAndNotesSection.Docs;

	/// <summary>Refreshes discovery while retaining document buffers, expansion, and selection.</summary>
	public async Task RefreshAsync(CancellationToken cancellationToken)
	{
		var catalog = ProjectMarkdownCatalog.Scan(_projectRootPath);
		CommonTree = BuildNodes(MarkdownTreeNode.Build(catalog.Common));
		DocsTree = BuildNodes(MarkdownTreeNode.Build(catalog.Docs, "docs/"));
		await NotesDocument.LoadAsync(cancellationToken);
		await ActivateSelectedSectionAsync(cancellationToken);
		OnPropertyChanged(nameof(CommonTree));
		OnPropertyChanged(nameof(DocsTree));
		OnPropertyChanged(nameof(VisibleTree));
		OnPropertyChanged(nameof(ShowsDocumentTree));
	}

	/// <summary>Selects a primary tab and activates its remembered or default document.</summary>
	public async Task SelectSectionAsync(
		DocsAndNotesSection section,
		CancellationToken cancellationToken)
	{
		await FlushActiveAsync(cancellationToken);
		SelectedSection = section;
		await ActivateSelectedSectionAsync(cancellationToken);
	}

	/// <summary>
	/// Records the tree selection and, for a document node, opens that document.
	/// Selecting a folder changes neither the active document nor the remembered path.
	/// A failed flush of the previous document leaves the selection on the chosen
	/// node with the previous document still active and retryable.
	/// </summary>
	public async Task SelectDocumentAsync(
		MarkdownTreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(node);
		if (!ContainsNode(VisibleTree, node))
		{
			// A queued selection from the previous section or from a pre-refresh
			// tree must never activate a document in the current section.
			return;
		}

		// The tree has already moved its selection, so record it before any awaited
		// work: a slow flush must not leave the two divergent, and a failing flush
		// must not leave them divergent permanently.
		SelectedNode = node;
		if (node.IsFolder)
		{
			return;
		}

		await FlushActiveAsync(cancellationToken);
		await ActivateAsync(node, cancellationToken);
	}

	/// <summary>Flips a folder's expansion; document nodes and stale nodes are ignored.</summary>
	public void ToggleFolder(MarkdownTreeNodeViewModel node)
	{
		ArgumentNullException.ThrowIfNull(node);
		if (node.IsFolder && ContainsNode(VisibleTree, node))
		{
			node.IsExpanded = !node.IsExpanded;
		}
	}

	/// <summary>Flushes all loaded documents owned by the pane.</summary>
	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		List<Exception> failures = [];
		await TryFlushAsync(NotesDocument, failures, cancellationToken);
		foreach (var document in _documents.Values)
		{
			if (document.IsLoaded)
			{
				await TryFlushAsync(document, failures, cancellationToken);
			}
		}

		if (failures.Count > 0)
		{
			throw new AggregateException(
				"One or more project documents could not be saved.",
				failures);
		}
	}

	private static async Task TryFlushAsync(
		IMarkdownEditorDocument document,
		List<Exception> failures,
		CancellationToken cancellationToken)
	{
		try
		{
			await document.FlushAsync(cancellationToken);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception exception)
		{
			failures.Add(exception);
		}
	}

	private IReadOnlyList<MarkdownTreeNodeViewModel> BuildNodes(
		IReadOnlyList<MarkdownTreeNode> nodes) =>
		[.. nodes.Select(node => node.IsFolder
			? CreateFolder(node)
			: new MarkdownTreeNodeViewModel(
				node.Name,
				node.RelativePath,
				GetOrCreateDocument(node.FullPath!)))];

	private MarkdownTreeNodeViewModel CreateFolder(MarkdownTreeNode node)
	{
		MarkdownTreeNodeViewModel folder = new(
			node.Name,
			node.RelativePath,
			BuildNodes(node.Children))
		{
			IsExpanded = _expandedFolders.Contains(node.RelativePath)
		};
		folder.PropertyChanged += OnFolderExpansionChanged;
		return folder;
	}

	private void OnFolderExpansionChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(MarkdownTreeNodeViewModel.IsExpanded)
			|| sender is not MarkdownTreeNodeViewModel folder)
		{
			return;
		}

		if (folder.IsExpanded)
		{
			_expandedFolders.Add(folder.RelativePath);
		}
		else
		{
			_expandedFolders.Remove(folder.RelativePath);
		}
	}

	private IMarkdownEditorDocument GetOrCreateDocument(string fullPath)
	{
		if (_documents.TryGetValue(fullPath, out var existing))
		{
			return existing;
		}

		ProjectMarkdownDocument created = new(_fileStore, fullPath, _debounceInterval);
		_documents[fullPath] = created;
		return created;
	}

	private async Task ActivateSelectedSectionAsync(CancellationToken cancellationToken)
	{
		switch (SelectedSection)
		{
			case DocsAndNotesSection.Notes:
				ClearTreeSelection();
				ActiveDocument = NotesDocument;
				break;
			case DocsAndNotesSection.Common:
				await ActivateSectionAsync(CommonTree, "README.md", cancellationToken);
				break;
			case DocsAndNotesSection.Docs:
				await ActivateSectionAsync(DocsTree, null, cancellationToken);
				break;
		}
	}

	private async Task ActivateSectionAsync(
		IReadOnlyList<MarkdownTreeNodeViewModel> tree,
		string? defaultRelativePath,
		CancellationToken cancellationToken)
	{
		var firstVisit = _visitedSections.Add(SelectedSection);
		var remembered = _lastSelectedPaths.GetValueOrDefault(SelectedSection)
			?? (firstVisit ? defaultRelativePath : null);
		var target = remembered is null ? null : FindDocument(tree, remembered);
		if (target is null)
		{
			ClearTreeSelection();
			ActiveDocument = null;
			return;
		}

		await ActivateAsync(target, cancellationToken);
	}

	private static bool ContainsNode(
		IReadOnlyList<MarkdownTreeNodeViewModel> nodes,
		MarkdownTreeNodeViewModel node) =>
		nodes.Any(candidate => ReferenceEquals(candidate, node)
			|| ContainsNode(candidate.Children, node));

	private static MarkdownTreeNodeViewModel? FindDocument(
		IReadOnlyList<MarkdownTreeNodeViewModel> nodes,
		string relativePath)
	{
		foreach (var node in nodes)
		{
			if (!node.IsFolder
				&& string.Equals(node.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase))
			{
				return node;
			}

			if (FindDocument(node.Children, relativePath) is { } found)
			{
				return found;
			}
		}

		return null;
	}

	private async Task ActivateAsync(
		MarkdownTreeNodeViewModel node,
		CancellationToken cancellationToken)
	{
		SelectedNode = node;
		_lastSelectedPaths[SelectedSection] = node.RelativePath;
		ExpandAncestors(VisibleTree, node.RelativePath);
		await node.Document!.LoadAsync(cancellationToken);
		ActiveDocument = node.Document;
	}

	private static void ExpandAncestors(
		IReadOnlyList<MarkdownTreeNodeViewModel> nodes,
		string relativePath)
	{
		foreach (var node in nodes.Where(node => node.IsFolder))
		{
			if (relativePath.StartsWith(
				$"{node.RelativePath}/",
				StringComparison.OrdinalIgnoreCase))
			{
				node.IsExpanded = true;
				ExpandAncestors(node.Children, relativePath);
			}
		}
	}

	private async Task FlushActiveAsync(CancellationToken cancellationToken)
	{
		if (ActiveDocument is not null)
		{
			await ActiveDocument.FlushAsync(cancellationToken);
		}
	}

	private void ClearTreeSelection() => SelectedNode = null;

	private void OnActiveDocumentSaveStatusChanged(
		object? sender,
		DocumentSaveStatus status) =>
		PublishSaveStatusProperties();

	private void PublishSaveStatusProperties()
	{
		OnPropertyChanged(nameof(SaveStatus));
		OnPropertyChanged(nameof(SaveErrorMessage));
		OnPropertyChanged(nameof(HasSaveError));
	}

	private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
