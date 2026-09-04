using Pact.Core.Projects;

namespace Pact.Presentation.Services;

/// <summary>
/// Owns an editable project Markdown buffer with debounced, conflict-aware persistence.
/// </summary>
public sealed class ProjectMarkdownDocument : IMarkdownEditorDocument
{
	private readonly IProjectMarkdownFileStore _store;
	private readonly TimeSpan _debounceInterval;
	private readonly TimeProvider _timeProvider;
	private readonly Lock _gate = new();
	private CancellationTokenSource? _pendingDebounce;
	private string _text = string.Empty;
	private string _diskRevision = string.Empty;
	private int _version;
	private int _savedVersion;
	private DocumentSaveStatus _saveStatus = new(DocumentSaveState.Clean);

	/// <summary>Creates a document for one project Markdown file.</summary>
	/// <param name="store">Conflict-aware backing store.</param>
	/// <param name="path">Absolute or resolvable project Markdown path.</param>
	/// <param name="debounceInterval">Quiet period before autosave.</param>
	/// <param name="timeProvider">
	/// Clock used only for debounce scheduling; production uses the system clock.
	/// </param>
	public ProjectMarkdownDocument(
		IProjectMarkdownFileStore store,
		string path,
		TimeSpan debounceInterval,
		TimeProvider? timeProvider = null)
	{
		_store = store ?? throw new ArgumentNullException(nameof(store));
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Path = System.IO.Path.GetFullPath(path);
		_debounceInterval = debounceInterval;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <summary>Raised when the entire editor buffer must be replaced.</summary>
	public event EventHandler? TextReplaced;

	/// <summary>Raised after the canonical persistence state changes.</summary>
	public event EventHandler<DocumentSaveStatus>? SaveStatusChanged;

	/// <summary>Absolute path of the edited project file.</summary>
	public string Path { get; }

	/// <summary>Current editor buffer.</summary>
	public string Text { get { lock (_gate) { return _text; } } }

	/// <summary>Whether the first disk snapshot has been loaded.</summary>
	public bool IsLoaded { get; private set; }

	/// <summary>Current persistence state of the editor buffer.</summary>
	public DocumentSaveStatus SaveStatus { get { lock (_gate) { return _saveStatus; } } }

	/// <summary>Loads the initial disk snapshot once.</summary>
	public async Task LoadAsync(CancellationToken cancellationToken)
	{
		if (IsLoaded)
		{
			return;
		}

		var loaded = await _store.LoadAsync(Path, cancellationToken);
		lock (_gate)
		{
			_text = loaded.Text;
			_diskRevision = loaded.Revision;
			_savedVersion = _version;
		}
		IsLoaded = true;
		TextReplaced?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Replaces the editor buffer and schedules autosave.</summary>
	public void SetText(string text)
	{
		DocumentSaveStatus? status;
		lock (_gate)
		{
			if (string.Equals(_text, text, StringComparison.Ordinal))
			{
				return;
			}

			_text = text;
			_version++;
			status = SetSaveStatusUnderLock(
				_saveStatus.State == DocumentSaveState.Conflict
					? DocumentSaveState.Conflict
					: DocumentSaveState.Dirty);
		}
		PublishSaveStatus(status);
		ScheduleFlush();
	}

	/// <summary>Saves immediately when the disk revision still matches the loaded revision.</summary>
	public async Task FlushAsync(CancellationToken cancellationToken)
	{
		string snapshot;
		string expectedRevision;
		int snapshotVersion;
		DocumentSaveStatus? saving;
		lock (_gate)
		{
			_pendingDebounce?.Cancel();
			_pendingDebounce = null;
			if (_version == _savedVersion
				|| _saveStatus.State == DocumentSaveState.Conflict)
			{
				return;
			}

			snapshot = _text;
			expectedRevision = _diskRevision;
			snapshotVersion = _version;
			saving = SetSaveStatusUnderLock(DocumentSaveState.Saving);
		}
		PublishSaveStatus(saving);

		try
		{
			var result = await _store.TrySaveAsync(
				Path,
				snapshot,
				expectedRevision,
				cancellationToken);
			if (!result.Saved)
			{
				SetSaveStatus(DocumentSaveState.Conflict);
				return;
			}

			DocumentSaveStatus? completed;
			lock (_gate)
			{
				_diskRevision = result.Snapshot.Revision;
				if (_savedVersion < snapshotVersion)
				{
					_savedVersion = snapshotVersion;
				}
				completed = SetSaveStatusUnderLock(
					_version == _savedVersion
						? DocumentSaveState.Clean
						: DocumentSaveState.Dirty);
			}
			PublishSaveStatus(completed);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetSaveStatus(DocumentSaveState.Dirty);
			throw;
		}
		catch (Exception exception)
		{
			SetSaveStatus(DocumentSaveState.Failed, exception.Message, exception);
			throw;
		}
	}

	/// <summary>Reloads a clean buffer or marks a dirty buffer as conflicted.</summary>
	public async Task CheckForExternalChangeAsync(CancellationToken cancellationToken)
	{
		if (!IsLoaded)
		{
			return;
		}

		var disk = await _store.LoadAsync(Path, cancellationToken);
		DocumentSaveStatus? status = null;
		var textReplaced = false;
		lock (_gate)
		{
			if (string.Equals(disk.Revision, _diskRevision, StringComparison.Ordinal))
			{
				return;
			}

			if (_version != _savedVersion)
			{
				status = SetSaveStatusUnderLock(DocumentSaveState.Conflict);
			}
			else
			{
				_text = disk.Text;
				_diskRevision = disk.Revision;
				_version++;
				_savedVersion = _version;
				status = SetSaveStatusUnderLock(DocumentSaveState.Clean);
				textReplaced = true;
			}
		}
		PublishSaveStatus(status);

		if (textReplaced)
		{
			TextReplaced?.Invoke(this, EventArgs.Empty);
		}
	}

	/// <summary>Discards local edits and reloads the current disk contents.</summary>
	public async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
	{
		var disk = await _store.LoadAsync(Path, cancellationToken);
		DocumentSaveStatus? status;
		lock (_gate)
		{
			_pendingDebounce?.Cancel();
			_pendingDebounce = null;
			_text = disk.Text;
			_diskRevision = disk.Revision;
			_version++;
			_savedVersion = _version;
			status = SetSaveStatusUnderLock(DocumentSaveState.Clean);
		}
		PublishSaveStatus(status);

		TextReplaced?.Invoke(this, EventArgs.Empty);
	}

	/// <summary>Resolves a conflict by replacing disk contents with the local buffer.</summary>
	public async Task SaveMineAsync(CancellationToken cancellationToken)
	{
		string snapshot;
		int snapshotVersion;
		lock (_gate)
		{
			snapshot = _text;
			snapshotVersion = _version;
		}

		try
		{
			var saved = await _store.OverwriteAsync(
				Path,
				snapshot,
				cancellationToken);
			DocumentSaveStatus? status;
			lock (_gate)
			{
				_diskRevision = saved.Revision;
				if (_savedVersion < snapshotVersion)
				{
					_savedVersion = snapshotVersion;
				}

				status = SetSaveStatusUnderLock(
					_version == _savedVersion
						? DocumentSaveState.Clean
						: DocumentSaveState.Dirty);
			}
			PublishSaveStatus(status);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			SetSaveStatus(DocumentSaveState.Conflict);
			throw;
		}
		catch (Exception exception)
		{
			SetSaveStatus(DocumentSaveState.Failed, exception.Message, exception);
			throw;
		}
	}

	private void ScheduleFlush()
	{
		CancellationTokenSource cts = new();
		lock (_gate)
		{
			_pendingDebounce?.Cancel();
			_pendingDebounce = cts;
		}
		_ = DebouncedFlushAsync(cts);
	}

	private async Task DebouncedFlushAsync(CancellationTokenSource cts)
	{
		try
		{
			await Task.Delay(_debounceInterval, _timeProvider, cts.Token);
		}
		catch (OperationCanceledException)
		{
			return;
		}

		lock (_gate)
		{
			if (!ReferenceEquals(_pendingDebounce, cts))
			{
				return;
			}

			_pendingDebounce = null;
		}

		try
		{
			await FlushAsync(CancellationToken.None);
		}
		catch
		{
			// The next explicit flush or edit retries; background autosave must not crash the UI.
		}
	}

	private void SetSaveStatus(
		DocumentSaveState state,
		string? errorMessage = null,
		Exception? exception = null)
	{
		DocumentSaveStatus? status;
		lock (_gate)
		{
			status = SetSaveStatusUnderLock(state, errorMessage, exception);
		}
		PublishSaveStatus(status);
	}

	private DocumentSaveStatus? SetSaveStatusUnderLock(
		DocumentSaveState state,
		string? errorMessage = null,
		Exception? exception = null)
	{
		DocumentSaveStatus status = new(state, errorMessage, exception);
		if (_saveStatus == status)
		{
			return null;
		}

		_saveStatus = status;
		return status;
	}

	private void PublishSaveStatus(DocumentSaveStatus? status)
	{
		if (status is not null)
		{
			SaveStatusChanged?.Invoke(this, status);
		}
	}
}