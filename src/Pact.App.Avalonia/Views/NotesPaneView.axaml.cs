using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pact.App.Avalonia.Lifecycle;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Views;

/// <summary>
/// Describes a completed Notes editor selection and its optional editor-local anchor.
/// </summary>
internal sealed record NotesSelectionCompletion(
	string Text,
	double X,
	double Y,
	bool HasAnchor);

internal sealed partial class NotesPaneView : UserControl
{
	private readonly Dictionary<IMarkdownEditorDocument, EditorState> _editorStates = [];
	private readonly Dictionary<IMarkdownEditorDocument, DocumentMode> _documentModes = [];
	private readonly Dictionary<IMarkdownEditorDocument, string> _knownTexts = [];
	private readonly DispatcherTimer _externalChangeTimer;
	private ObservedTaskGroup _eventTasks = new(
		static (_, _) => Task.CompletedTask);
	private Func<Exception, Task> _reportUserFailureAsync =
		static _ => Task.CompletedTask;
	private Func<Exception, Task> _reportSaveFailureAsync =
		static _ => Task.CompletedTask;
	private IMarkdownEditorDocument? _document;
	private bool _updating;
	private bool _moveCaretToEndOnNextFocus;
	private int _externalCheckRunning;
	private bool _closing;
	private int _selectionSourceGeneration;
	private PublishedSelectionCompletion? _lastPublishedSelection;

	private enum DocumentMode
	{
		Preview,
		Editor
	}

	public NotesPaneView()
	{
		InitializeComponent();
		ConfirmDiscardAsync = ConfirmDiscardWithDialogAsync;
		_externalChangeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
		_externalChangeTimer.Tick += OnExternalChangeTimerTick;
		AttachedToVisualTree += (_, _) => _externalChangeTimer.Start();
		DetachedFromVisualTree += (_, _) => _externalChangeTimer.Stop();
		Editor.TextChanged += (_, _) =>
		{
			if (!_updating)
			{
				_document?.SetText(Editor.Text ?? string.Empty);
			}
		};
		Editor.PointerReleased += OnEditorPointerReleased;
		// TextBox deletes the selection from its own Delete class handler, which runs before any
		// bubbling handler, so the classic Shift+Delete cut has to be claimed while the key
		// tunnels down to the editor.
		Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
		Editor.KeyUp += OnEditorKeyUp;
		RefreshDocumentMode();
	}

	private void OnEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
	{
		if (e.GetCurrentPoint(Editor).Properties.PointerUpdateKind !=
			PointerUpdateKind.LeftButtonReleased)
		{
			return;
		}

		RaiseSelectionCompleted(e.GetPosition(Editor));
	}

	private void OnEditorKeyUp(object? sender, KeyEventArgs e)
	{
		if (e.Key is Key.LeftCtrl
			or Key.RightCtrl
			or Key.LeftShift
			or Key.RightShift
			or Key.LeftAlt
			or Key.RightAlt
			|| e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
		{
			return;
		}

		RaiseSelectionCompleted(anchor: null);
	}

	private void OnEditorKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Delete
			|| e.KeyModifiers != KeyModifiers.Shift
			|| Editor.IsReadOnly)
		{
			return;
		}

		var cutText = Editor.SelectedText ?? string.Empty;
		if (cutText.Length == 0)
		{
			return;
		}

		e.Handled = true;
		Editor.SelectedText = string.Empty;
		if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
		{
			RunEvent("notes-cut-selection", () => clipboard.SetTextAsync(cutText));
		}

		RaiseSelectionCompleted(anchor: null);
	}

	public event EventHandler<NotesSelectionCompletion>? SelectionCompleted;
	internal Control SelectionAnchorSource => Editor;
	internal Func<Task<bool>> ConfirmDiscardAsync { get; set; }

	internal void BeginSelectionSourceGeneration() => _selectionSourceGeneration++;

	internal void ConfigureLifecycle(
		ObservedTaskGroup eventTasks,
		Func<Exception, Task>? reportUserFailureAsync = null,
		Func<Exception, Task>? reportSaveFailureAsync = null)
	{
		_eventTasks = eventTasks ?? throw new ArgumentNullException(nameof(eventTasks));
		_reportUserFailureAsync = reportUserFailureAsync ?? _reportUserFailureAsync;
		_reportSaveFailureAsync = reportSaveFailureAsync ?? _reportSaveFailureAsync;
	}

	internal void DetachEventProducers()
	{
		_closing = true;
		_externalChangeTimer.Stop();
		Workspace = null;
		SetDocument(null);
	}

	/// <summary>Workspace whose documents and tabs are rendered by this pane.</summary>
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

			RefreshWorkspace();
		}
	}

	/// <summary>
	/// Compatibility surface for focused document tests; production uses <see cref="Workspace"/>.
	/// </summary>
	public ProjectNoteDocument? Document
	{
		get => _document as ProjectNoteDocument;
		set
		{
			SetDocument(value);
			RefreshDocumentMode();
		}
	}

	public void FocusEditor()
	{
		if (!Editor.IsVisible)
		{
			return;
		}

		if (_moveCaretToEndOnNextFocus)
		{
			Editor.CaretIndex = Editor.Text?.Length ?? 0;
			_moveCaretToEndOnNextFocus = false;
		}
		Editor.Focus();
	}

	private void OnNotesTabClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("notes-select-notes", () => SelectSectionAsync(DocsAndNotesSection.Notes));

	private void OnCommonTabClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("notes-select-common", () => SelectSectionAsync(DocsAndNotesSection.Common));

	private void OnDocsTabClicked(object? sender, RoutedEventArgs e) =>
		RunEvent("notes-select-docs", () => SelectSectionAsync(DocsAndNotesSection.Docs));

	private void OnPreviewModeClicked(object? sender, RoutedEventArgs e) =>
		SetDocumentMode(DocumentMode.Preview);

	private void OnEditorModeClicked(object? sender, RoutedEventArgs e) =>
		SetDocumentMode(DocumentMode.Editor);

	private void SetDocumentMode(DocumentMode mode)
	{
		if (_document is null)
		{
			return;
		}

		_documentModes[_document] = mode;
		RefreshDocumentMode();
		if (mode == DocumentMode.Editor)
		{
			FocusEditor();
		}
	}

	private void RefreshDocumentMode()
	{
		var hasDocument = _document is not null;
		var mode = _document is null
			? DocumentMode.Preview
			: _documentModes.GetValueOrDefault(
				_document,
				_document is ProjectNoteDocument ? DocumentMode.Editor : DocumentMode.Preview);

		PreviewModeButton.IsEnabled = hasDocument;
		EditorModeButton.IsEnabled = hasDocument;
		PreviewModeButton.IsChecked = hasDocument && mode == DocumentMode.Preview;
		EditorModeButton.IsChecked = hasDocument && mode == DocumentMode.Editor;
		Editor.IsVisible = hasDocument && mode == DocumentMode.Editor;
		Preview.IsVisible = hasDocument && mode == DocumentMode.Preview;
		if (Preview.IsVisible)
		{
			Preview.Markdown = _document?.Text ?? string.Empty;
		}
	}

	private void OnReloadFromDiskClicked(object? sender, RoutedEventArgs e)
	{
		if (_document is null)
		{
			return;
		}

		RunEvent("notes-reload-from-disk", ReloadFromDiskAsync);
	}

	private async Task ReloadFromDiskAsync()
	{
		if (!await ConfirmDiscardAsync())
		{
			return;
		}

		await _document!.ReloadFromDiskAsync(CancellationToken.None);
		RefreshSaveStatus();
	}

	private async Task<bool> ConfirmDiscardWithDialogAsync()
	{
		if (TopLevel.GetTopLevel(this) is not Window owner)
		{
			return false;
		}

		var result = await MessageDialogWindow.ShowOwnedAsync(
			owner,
			new MessageDialogRequest(
				"Discard local changes",
				"Discard unsaved changes and reload this document from disk?",
				MessageDialogButtons.YesNo,
				MessageDialogResult.No));
		return result == MessageDialogResult.Yes;
	}

	private void OnSaveMineClicked(object? sender, RoutedEventArgs e)
	{
		if (_document is null)
		{
			return;
		}

		RunEvent("notes-save-mine", SaveMineAsync);
	}

	private async Task SaveMineAsync()
	{
		await _document!.SaveMineAsync(CancellationToken.None);
		RefreshSaveStatus();
	}

	private void OnRetrySaveClicked(object? sender, RoutedEventArgs e)
	{
		if (_document is not null)
		{
			RunEvent("notes-retry-save", RetrySaveAsync);
		}
	}

	private async Task RetrySaveAsync()
	{
		if (Workspace is { } workspace
			&& ReferenceEquals(workspace.ActiveDocument, _document))
		{
			await workspace.RetrySaveAsync(CancellationToken.None);
		}
		else
		{
			await _document!.FlushAsync(CancellationToken.None);
		}
		RefreshSaveStatus();
	}

	private void OnExternalChangeTimerTick(object? sender, EventArgs e)
	{
		if (_closing
			|| _document is not ProjectMarkdownDocument
			|| Interlocked.CompareExchange(ref _externalCheckRunning, 1, 0) != 0)
		{
			return;
		}

		if (!_eventTasks.TryRun(
			"notes-external-change",
			CheckForExternalChangeAsync,
			_reportUserFailureAsync))
		{
			Volatile.Write(ref _externalCheckRunning, 0);
		}
	}

	private async Task CheckForExternalChangeAsync()
	{
		try
		{
			await _document!.CheckForExternalChangeAsync(CancellationToken.None);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			// External probes are best-effort; explicit save or the next tick retries.
		}
		finally
		{
			Volatile.Write(ref _externalCheckRunning, 0);
		}
	}

	private void RunEvent(string operationName, Func<Task> operation)
	{
		if (!_closing)
		{
			_eventTasks.TryRun(operationName, operation, _reportUserFailureAsync);
		}
	}

	private async Task SelectSectionAsync(DocsAndNotesSection section)
	{
		if (Workspace is null)
		{
			return;
		}

		await Workspace.SelectSectionAsync(section, CancellationToken.None);
		RefreshWorkspace();
		if (Editor.IsVisible)
		{
			FocusEditor();
		}
	}

	private void OnWorkspacePropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(DocsAndNotesWorkspaceViewModel.ActiveDocument)
			or nameof(DocsAndNotesWorkspaceViewModel.SelectedSection)
			or nameof(DocsAndNotesWorkspaceViewModel.VisibleTree))
		{
			RefreshWorkspace();
		}
	}

	private void RefreshWorkspace()
	{
		NotesTab.IsChecked = Workspace?.SelectedSection == DocsAndNotesSection.Notes;
		CommonTab.IsChecked = Workspace?.SelectedSection == DocsAndNotesSection.Common;
		DocsTab.IsChecked = Workspace?.SelectedSection == DocsAndNotesSection.Docs;
		SetDocument(Workspace?.ActiveDocument);
		EmptyGroupMessage.IsVisible = Workspace is not null && _document is null;
		RefreshDocumentMode();
	}

	private void SetDocument(IMarkdownEditorDocument? document)
	{
		if (ReferenceEquals(_document, document))
		{
			RefreshSaveStatus();
			return;
		}

		CaptureEditorState();
		if (_document is not null)
		{
			_document.TextReplaced -= OnTextReplaced;
			_document.SaveStatusChanged -= OnSaveStatusChanged;
		}

		_document = document;
		if (_document is not null)
		{
			_document.TextReplaced += OnTextReplaced;
			_document.SaveStatusChanged += OnSaveStatusChanged;
		}

		var noteChangedWhileInactive = document is ProjectNoteDocument
			&& _knownTexts.TryGetValue(document, out var known)
			&& !string.Equals(known, document.Text, StringComparison.Ordinal);
		BeginSelectionSourceGeneration();
		RefreshText(noteChangedWhileInactive);
		var selection = CaptureCurrentSelectionState();
		if (string.IsNullOrEmpty(selection.Text))
		{
			_lastPublishedSelection = new PublishedSelectionCompletion(
				_selectionSourceGeneration,
				selection,
				SelectionAnchorProvenance.Fallback);
		}
		RefreshSaveStatus();
	}

	private void OnTextReplaced(object? sender, EventArgs e) =>
		RefreshText(sender is ProjectNoteDocument);

	private void OnSaveStatusChanged(object? sender, DocumentSaveStatus status)
	{
		RefreshSaveStatus();
		if (status.State == DocumentSaveState.Failed
			&& status.Exception is { } exception)
		{
			RunEvent(
				"notes-autosave-failed",
				() => _reportSaveFailureAsync(exception));
		}
	}

	private void RefreshText(bool moveCaretToEnd)
	{
		var text = _document?.Text ?? string.Empty;
		if (_document is not null)
		{
			_knownTexts[_document] = text;
		}

		var state = _document is not null && _editorStates.TryGetValue(_document, out var saved)
			? saved
			: null;
		_updating = true;
		try
		{
			Editor.Text = text;
			if (moveCaretToEnd)
			{
				_moveCaretToEndOnNextFocus = true;
			}
			else if (state is not null)
			{
				RestoreEditorState(state);
			}
			else
			{
				Editor.CaretIndex = 0;
				Editor.SelectionStart = 0;
				Editor.SelectionEnd = 0;
			}
		}
		finally
		{
			_updating = false;
		}

		if (Preview.IsVisible)
		{
			Preview.Markdown = text;
		}
	}

	private void CaptureEditorState()
	{
		if (_document is null)
		{
			return;
		}

		var scroll = Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
		_editorStates[_document] = new EditorState(
			Editor.CaretIndex,
			Editor.SelectionStart,
			Editor.SelectionEnd,
			scroll?.Offset ?? default);
		_knownTexts[_document] = Editor.Text ?? string.Empty;
	}

	private void RestoreEditorState(EditorState state)
	{
		var length = Editor.Text?.Length ?? 0;
		Editor.CaretIndex = Math.Clamp(state.CaretIndex, 0, length);
		Editor.SelectionStart = Math.Clamp(state.SelectionStart, 0, length);
		Editor.SelectionEnd = Math.Clamp(state.SelectionEnd, 0, length);
		Dispatcher.UIThread.Post(() =>
		{
			var scroll = Editor.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
			scroll?.Offset = state.ScrollOffset;
		}, DispatcherPriority.Loaded);
	}

	private void RefreshSaveStatus()
	{
		var status = _document?.SaveStatus;
		ConflictBanner.IsVisible =
			status?.State == DocumentSaveState.Conflict;
		SaveFailureBanner.IsVisible =
			status?.State == DocumentSaveState.Failed;
		SaveFailureMessage.Text = status?.ErrorMessage ?? string.Empty;
	}

	private void RaiseSelectionCompleted(Point? anchor)
	{
		var selection = CaptureCurrentSelectionState();
		var anchorProvenance = anchor is null
			? SelectionAnchorProvenance.Fallback
			: SelectionAnchorProvenance.LeftPointer;
		if (_lastPublishedSelection is { } last
			&& last.SourceGeneration == _selectionSourceGeneration
			&& last.Selection == selection
			&& (anchorProvenance == SelectionAnchorProvenance.Fallback
				|| last.AnchorProvenance == SelectionAnchorProvenance.LeftPointer))
		{
			return;
		}

		_lastPublishedSelection = new PublishedSelectionCompletion(
			_selectionSourceGeneration,
			selection,
			anchorProvenance);
		SelectionCompleted?.Invoke(
			this,
			new NotesSelectionCompletion(
				selection.Text,
				anchor?.X ?? 0,
				anchor?.Y ?? 0,
				anchor is not null));
	}

	private EditorSelectionState CaptureCurrentSelectionState() =>
		new(
			Editor.SelectionStart,
			Editor.SelectionEnd,
			Editor.SelectedText ?? string.Empty);

	private sealed record EditorState(
		int CaretIndex,
		int SelectionStart,
		int SelectionEnd,
		Vector ScrollOffset);

	private sealed record EditorSelectionState(
		int SelectionStart,
		int SelectionEnd,
		string Text);

	private sealed record PublishedSelectionCompletion(
		int SourceGeneration,
		EditorSelectionState Selection,
		SelectionAnchorProvenance AnchorProvenance);

	private enum SelectionAnchorProvenance
	{
		Fallback,
		LeftPointer
	}
}
