using System.ComponentModel;
using Pact.Core.Boards;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>Retains the user's text independently of concurrent agent edits.</summary>
public sealed class BoardCardEditorViewModel : IDisposable, INotifyPropertyChanged
{
	private readonly ProjectBoardWorkspace _workspace;
	private readonly Action<Action> _dispatch;
	private readonly TimeProvider _timeProvider;
	private readonly TimeSpan _debounce;
	private readonly SemaphoreSlim _saveGate = new(1, 1);
	private BoardCard _baseline;
	private string _title;
	private string _description;
	private CancellationTokenSource? _pending;
	private bool _saving;
	private bool _disposed;
	private int _editVersion;
	private int _saveCalls;
	/// <summary>Opens one card from a loaded shared workspace.</summary>
	public BoardCardEditorViewModel(ProjectBoardWorkspace workspace, string cardId, Action<Action> dispatch, TimeProvider timeProvider, TimeSpan debounce)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(dispatch);
		ArgumentNullException.ThrowIfNull(timeProvider);
		_workspace = workspace;
		_dispatch = dispatch;
		_timeProvider = timeProvider;
		_debounce = debounce;
		_baseline = workspace.Snapshot?.State.Cards.FirstOrDefault(c => c.Id == cardId)
			?? throw new ArgumentException("The card has not been loaded.", nameof(cardId));
		_title = _baseline.Title;
		_description = _baseline.Description;
		workspace.SnapshotChanged += OnSnapshot;
		workspace.Register(this);
	}
	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>Stable card identity.</summary>
	public string CardId => _baseline.Id;
	/// <summary>Local title buffer.</summary>
	public string Title { get => _title; set { if (_title != value) { _title = value; Edited(); } } }
	/// <summary>Local Markdown buffer.</summary>
	public string Description { get => _description; set { if (_description != value) { _description = value; Edited(); } } }
	/// <summary>Persistence status of the local fields.</summary>
	public DocumentSaveStatus SaveStatus { get; private set; } = new(DocumentSaveState.Clean);
	/// <summary>Saves only unchanged-baseline fields.</summary>
	public Task FlushAsync(CancellationToken cancellationToken) => SaveAsync(false, cancellationToken);
	/// <summary>Explicitly applies local edits to the latest version.</summary>
	public Task SaveMineAsync(CancellationToken cancellationToken) => SaveAsync(true, cancellationToken);
	/// <summary>Discards local fields in favor of the latest disk-confirmed card.</summary>
	public void Reload()
	{
		if (_workspace.Snapshot?.State.Cards.FirstOrDefault(c => c.Id == CardId) is { } card)
		{
			_baseline = card;
			_title = card.Title;
			_description = card.Description;
			_editVersion++;
			SetStatus(DocumentSaveState.Clean);
		}
	}

	private void Edited()
	{
		_editVersion++;
		SetStatus(DocumentSaveState.Dirty);
		_pending?.Cancel();
		_pending?.Dispose();
		_pending = new();
		_ = SaveLaterAsync(_pending.Token);
	}

	private async Task SaveLaterAsync(CancellationToken token)
	{
		try
		{
			await Task.Delay(_debounce, _timeProvider, token);
			_dispatch(() => { if (!_disposed) { _ = FlushAsync(CancellationToken.None); } });
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { }
	}

	private async Task SaveAsync(bool overwrite, CancellationToken cancellationToken)
	{
		if (_disposed)
		{
			return;
		}
		_saveCalls++;
		var entered = false;
		try
		{
			await _saveGate.WaitAsync(cancellationToken);
			entered = true;
			if (_disposed || (SaveStatus.State == DocumentSaveState.Clean && !overwrite))
			{ return; }
			_pending?.Cancel();
			var titleChanged = _title.Trim() != _baseline.Title;
			var descriptionChanged = _description != _baseline.Description;
			var original = _baseline;
			var title = _title;
			var description = _description;
			var version = _editVersion;
			_saving = true;
			SetStatus(DocumentSaveState.Saving);
			var snapshot = await _workspace.LoadAsync(cancellationToken);
			for (var attempt = 0; attempt < 2; attempt++)
			{
				var latest = snapshot.State.Cards.FirstOrDefault(c => c.Id == CardId);
				if (latest is null || latest.IsArchived || (!overwrite
					&& ((titleChanged && latest.Title != original.Title)
						|| (descriptionChanged && latest.Description != original.Description))))
				{
					SetStatus(DocumentSaveState.Conflict, "The edited fields changed. Reload or save your fields explicitly.");
					return;
				}
				var result = await _workspace.ExecuteAsync(new UpdateCard(CardId, BoardRevisions.ForCard(latest), new()
				{
					Title = titleChanged ? new(title) : null,
					Description = descriptionChanged ? new(description) : null
				}), new(null, "You"), cancellationToken);
				if (result.Outcome == BoardMutationOutcome.Conflict && attempt == 0)
				{
					snapshot = result.Snapshot;
					// Explicit overwrite authorizes only the version inspected above, not a new racing edit.
					overwrite = false;
					continue;
				}
				if (result.Outcome != BoardMutationOutcome.Applied)
				{
					SetStatus(result.Outcome == BoardMutationOutcome.Conflict ? DocumentSaveState.Conflict : DocumentSaveState.Failed,
						$"Cannot save card: {result.Outcome}.");
					return;
				}
				_baseline = result.Snapshot.State.Cards.Single(c => c.Id == CardId);
				break;
			}
			// A normalized saved title must not remove the separator the user just typed.
			if (_title == title && _title.Trim() != _baseline.Title)
			{
				_title = _baseline.Title;
			}
			if (_description == description)
			{
				_description = _baseline.Description;
			}
			SetStatus(version == _editVersion ? DocumentSaveState.Clean : DocumentSaveState.Dirty);
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException)
		{
			SetStatus(DocumentSaveState.Failed, exception.Message, exception);
		}
		finally
		{
			if (entered)
			{
				_saving = false;
				_saveGate.Release();
			}
			_saveCalls--;
			if (_disposed && _saveCalls == 0)
			{
				_saveGate.Dispose();
			}
		}
	}

	private void OnSnapshot(object? sender, BoardSnapshot snapshot) => _dispatch(() =>
	{
		if (_disposed || _saving)
		{ return; }
		var card = snapshot.State.Cards.FirstOrDefault(c => c.Id == CardId);
		if (card is null)
		{ SetStatus(DocumentSaveState.Conflict, "The card no longer exists."); return; }
		if (SaveStatus.State == DocumentSaveState.Clean)
		{
			if (_title.Trim() != card.Title)
			{
				_title = card.Title;
			}
			_description = card.Description;
			_baseline = card;
			PropertyChanged?.Invoke(this, new(null));
			return;
		}
		if (card.IsArchived || (_title.Trim() != _baseline.Title && card.Title != _baseline.Title)
			|| (_description != _baseline.Description && card.Description != _baseline.Description))
		{
			SetStatus(DocumentSaveState.Conflict, "The edited fields changed. Reload or save your fields explicitly.");
			return;
		}
		if (_title.Trim() == _baseline.Title && card.Title != _baseline.Title)
		{ _title = card.Title; }
		if (_description == _baseline.Description)
		{ _description = card.Description; }
		_baseline = card;
		PropertyChanged?.Invoke(this, new(null));
	});

	private void SetStatus(DocumentSaveState state, string? message = null, Exception? exception = null)
	{
		SaveStatus = new(state, message, exception);
		PropertyChanged?.Invoke(this, new(null));
	}
	/// <inheritdoc />
	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_pending?.Cancel();
		_pending?.Dispose();
		_workspace.SnapshotChanged -= OnSnapshot;
		_workspace.Unregister(this);
		if (_saveCalls == 0)
		{
			_saveGate.Dispose();
		}
	}
}
