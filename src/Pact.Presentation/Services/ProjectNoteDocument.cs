using Pact.Core.AgentControl;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;

namespace Pact.Presentation.Services;

/// <summary>Owns the app-managed per-project notes buffer and debounce autosave.</summary>
public sealed class ProjectNoteDocument : IMarkdownEditorDocument
{
	private readonly IProjectNotesStore _store;
	private readonly string _projectRootPath;
	private readonly TimeSpan _debounceInterval;
	private readonly TimeProvider _timeProvider;
	private readonly Lock _gate = new();
	private readonly Lock _flushQueueGate = new();
	private Task _flushTail = Task.CompletedTask;
	private CancellationTokenSource? _pendingDebounce;
	private string _text = string.Empty;
	private int _version;
	private int _savedVersion;
	private DocumentSaveStatus _saveStatus = new(DocumentSaveState.Clean);

	/// <summary>
	/// Creates a notes document.
	/// </summary>
	/// <param name="store">Backing notes store.</param>
	/// <param name="projectRootPath">Project whose notes this document holds.</param>
	/// <param name="debounceInterval">
	/// Quiet period after the last edit before an autosave runs, so typing does not write on
	/// every keystroke.
	/// </param>
	/// <param name="timeProvider">
	/// Clock used only for debounce scheduling; production uses the system clock.
	/// </param>
	public ProjectNoteDocument(
		IProjectNotesStore store,
		string projectRootPath,
		TimeSpan debounceInterval,
		TimeProvider? timeProvider = null)
	{
		ArgumentNullException.ThrowIfNull(store);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
		_store = store;
		_projectRootPath = projectRootPath;
		_debounceInterval = debounceInterval;
		_timeProvider = timeProvider ?? TimeProvider.System;
	}

	/// <inheritdoc />
	public event EventHandler? TextReplaced;

	/// <inheritdoc />
	public event EventHandler<DocumentSaveStatus>? SaveStatusChanged;

	/// <inheritdoc />
	public string Text { get { lock (_gate) { return _text; } } }

	/// <inheritdoc />
	public bool IsLoaded { get; private set; }

	/// <inheritdoc />
	public DocumentSaveStatus SaveStatus { get { lock (_gate) { return _saveStatus; } } }

	/// <summary>Returns the exact current buffer and its opaque content revision.</summary>
	public ProjectNotesSnapshot GetSnapshot()
	{
		lock (_gate)
		{
			return ProjectNotesSnapshot.FromText(_text);
		}
	}

	/// <inheritdoc />
	/// <remarks>Subsequent calls are ignored, so the buffer is read from disk exactly once.</remarks>
	public async Task LoadAsync(CancellationToken cancellationToken)
	{
		if (IsLoaded)
		{
			return;
		}

		var loaded = await _store.LoadAsync(_projectRootPath, cancellationToken);
		lock (_gate)
		{ _text = loaded; _savedVersion = _version; }
		IsLoaded = true;
		TextReplaced?.Invoke(this, EventArgs.Empty);
	}

	/// <inheritdoc />
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
			status = SetSaveStatusUnderLock(DocumentSaveState.Dirty);
		}
		PublishSaveStatus(status);
		ScheduleFlush();
	}

	/// <summary>
	/// Appends text to the buffer and schedules a save, keeping existing notes intact.
	/// </summary>
	public void Append(string text)
	{
		DocumentSaveStatus? status;
		lock (_gate)
		{
			_text = ProjectNotesStore.AppendWithSeparation(_text, text);
			_version++;
			status = SetSaveStatusUnderLock(DocumentSaveState.Dirty);
		}
		PublishSaveStatus(status);
		TextReplaced?.Invoke(this, EventArgs.Empty);
		ScheduleFlush();
	}

	/// <summary>
	/// Replaces the live buffer only when its current content matches the expected revision,
	/// then attempts to persist the replacement before returning.
	/// </summary>
	/// <param name="text">Complete replacement text; an empty string deletes all content.</param>
	/// <param name="expectedRevision">Revision returned by a preceding snapshot read.</param>
	/// <param name="cancellationToken">Cancels the immediate persistence attempt.</param>
	/// <returns>The current snapshot and mutation outcome.</returns>
	public async Task<ProjectNotesMutationResult> ReplaceAsync(
		string text,
		string expectedRevision,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);

		DocumentSaveStatus? status = null;
		var replaced = false;
		lock (_gate)
		{
			var current = ProjectNotesSnapshot.FromText(_text);
			if (!string.Equals(current.Revision, expectedRevision, StringComparison.Ordinal))
			{
				return new ProjectNotesMutationResult(
					current,
					ProjectNotesMutationStatus.Conflict);
			}

			if (!string.Equals(_text, text, StringComparison.Ordinal))
			{
				_pendingDebounce?.Cancel();
				_pendingDebounce = null;
				_text = text;
				_version++;
				status = SetSaveStatusUnderLock(DocumentSaveState.Dirty);
				replaced = true;
			}
		}

		PublishSaveStatus(status);
		if (replaced)
		{
			TextReplaced?.Invoke(this, EventArgs.Empty);
		}

		return await FlushMutationAsync(cancellationToken);
	}

	/// <summary>
	/// Appends to the live buffer and attempts to persist the combined content before returning.
	/// </summary>
	/// <param name="text">Text to append using the normal Notes separation rules.</param>
	/// <param name="cancellationToken">Cancels the immediate persistence attempt.</param>
	/// <returns>The current snapshot and mutation outcome.</returns>
	public async Task<ProjectNotesMutationResult> AppendAndFlushAsync(
		string text,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(text);
		Append(text);
		return await FlushMutationAsync(cancellationToken);
	}

	/// <inheritdoc />
	public Task FlushAsync(CancellationToken cancellationToken)
	{
		lock (_flushQueueGate)
		{
			var flush = FlushAfterAsync(_flushTail, cancellationToken);
			_flushTail = flush;
			return flush;
		}
	}

	private async Task FlushAfterAsync(
		Task previousFlush,
		CancellationToken cancellationToken)
	{
		try
		{
			await previousFlush.ConfigureAwait(false);
		}
		catch
		{
			// Every caller observes its own failure. A later queued flush must still retry
			// the current buffer instead of inheriting the earlier task's terminal state.
		}

		cancellationToken.ThrowIfCancellationRequested();
		await FlushCoreAsync(cancellationToken);
	}

	private async Task FlushCoreAsync(CancellationToken cancellationToken)
	{
		string snapshot;
		int snapshotVersion;
		DocumentSaveStatus? saving;
		lock (_gate)
		{
			_pendingDebounce?.Cancel();
			_pendingDebounce = null;
			if (_version == _savedVersion)
			{
				return;
			}

			snapshot = _text;
			snapshotVersion = _version;
			saving = SetSaveStatusUnderLock(DocumentSaveState.Saving);
		}
		PublishSaveStatus(saving);
		try
		{
			await _store.SaveAsync(_projectRootPath, snapshot, cancellationToken);
			DocumentSaveStatus? completed;
			lock (_gate)
			{
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
			DocumentSaveStatus? canceled;
			lock (_gate)
			{
				canceled = SetSaveStatusUnderLock(DocumentSaveState.Dirty);
			}
			PublishSaveStatus(canceled);
			throw;
		}
		catch (Exception exception)
		{
			DocumentSaveStatus? failed;
			lock (_gate)
			{
				failed = SetSaveStatusUnderLock(
					DocumentSaveState.Failed,
					exception.Message,
					exception);
			}
			PublishSaveStatus(failed);
			throw;
		}
	}

	/// <inheritdoc />
	public Task CheckForExternalChangeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	/// <inheritdoc />
	public async Task ReloadFromDiskAsync(CancellationToken cancellationToken)
	{
		var loaded = await _store.LoadAsync(_projectRootPath, cancellationToken);
		DocumentSaveStatus? status;
		lock (_gate)
		{
			_pendingDebounce?.Cancel();
			_pendingDebounce = null;
			_text = loaded;
			_version++;
			_savedVersion = _version;
			status = SetSaveStatusUnderLock(DocumentSaveState.Clean);
		}
		PublishSaveStatus(status);
		TextReplaced?.Invoke(this, EventArgs.Empty);
	}

	/// <inheritdoc />
	public Task SaveMineAsync(CancellationToken cancellationToken) => FlushAsync(cancellationToken);

	private void ScheduleFlush()
	{
		CancellationTokenSource cts = new();
		lock (_gate)
		{ _pendingDebounce?.Cancel(); _pendingDebounce = cts; }
		_ = DebouncedFlushAsync(cts);
	}

	private async Task DebouncedFlushAsync(CancellationTokenSource cts)
	{
		try
		{ await Task.Delay(_debounceInterval, _timeProvider, cts.Token); }
		catch (OperationCanceledException) { return; }
		lock (_gate)
		{
			if (!ReferenceEquals(_pendingDebounce, cts))
			{
				return;
			}

			_pendingDebounce = null;
		}
		try
		{ await FlushAsync(CancellationToken.None); }
		catch
		{
			// Flush publishes the original failure through SaveStatusChanged; the debounce
			// adapter must not surface it again as an unobserved task exception.
		}
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

	private async Task<ProjectNotesMutationResult> FlushMutationAsync(
		CancellationToken cancellationToken)
	{
		try
		{
			await FlushAsync(cancellationToken);
			return new ProjectNotesMutationResult(
				GetSnapshot(),
				ProjectNotesMutationStatus.Applied);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch
		{
			return new ProjectNotesMutationResult(
				GetSnapshot(),
				ProjectNotesMutationStatus.AppliedButNotPersisted);
		}
	}
}
