using System.ComponentModel;
using System.Collections.Specialized;
using Pact.Core.Boards;
using Pact.Presentation.Services;

namespace Pact.Presentation.ViewModels;

/// <summary>Retained board selection and commands; persistence is shared with agent requests.</summary>
public sealed class ProjectBoardViewModel : INotifyPropertyChanged, IDisposable
{
	private readonly Action<Action> _dispatch;
	private readonly Func<IReadOnlyList<SessionViewModel>> _sessions;
	private readonly TimeProvider _timeProvider;
	private readonly INotifyCollectionChanged? _sessionChanges;
	private readonly HashSet<SessionViewModel> _watchedSessions = [];

	/// <summary>Creates a project surface without loading or launching a terminal.</summary>
	public ProjectBoardViewModel(ProjectBoardWorkspace workspace, Action<Action> dispatch,
		Func<IReadOnlyList<SessionViewModel>> sessions, TimeProvider timeProvider, INotifyCollectionChanged? sessionChanges = null)
	{
		ArgumentNullException.ThrowIfNull(workspace);
		ArgumentNullException.ThrowIfNull(dispatch);
		ArgumentNullException.ThrowIfNull(sessions);
		ArgumentNullException.ThrowIfNull(timeProvider);
		Workspace = workspace;
		_dispatch = dispatch;
		_sessions = sessions;
		_timeProvider = timeProvider;
		_sessionChanges = sessionChanges;
		_sessionChanges?.CollectionChanged += OnSessionsChanged;
		WatchSessions();
		workspace.SnapshotChanged += OnSnapshot;
	}

	/// <inheritdoc />
	public event PropertyChangedEventHandler? PropertyChanged;
	/// <summary>The single project owner used by both surfaces.</summary>
	public ProjectBoardWorkspace Workspace { get; }
	/// <summary>The currently open card editor, retained through snapshot updates.</summary>
	public BoardCardEditorViewModel? Editor { get; private set; }
	/// <summary>Latest saved fields and comments of the selected card.</summary>
	public BoardCard? SelectedCard => Workspace.Snapshot?.State.Cards.FirstOrDefault(card => card.Id == Editor?.CardId);
	/// <summary>Active cards in manual order, or archived cards by number.</summary>
	public IReadOnlyList<BoardCard> Cards => Workspace.Snapshot?.State.Cards.Where(card => card.IsArchived == ShowArchive).ToArray() ?? [];
	/// <summary>Whether the archive is shown.</summary>
	public bool ShowArchive { get; private set; }
	/// <summary>Latest actionable failure; never implies that failed changes were saved.</summary>
	public string? Error { get; private set; }
	/// <summary>Unsubmitted comment text, cleared only after a confirmed write.</summary>
	public string CommentText { get; set; } = string.Empty;
	/// <summary>Current saved project sessions available for assignment.</summary>
	public IReadOnlyList<SessionViewModel> Sessions => _sessions();
	/// <summary>Returns the current display name, or the retained name if the session is gone.</summary>
	public string? AssigneeName(BoardCard card)
	{
		ArgumentNullException.ThrowIfNull(card);
		return Sessions.FirstOrDefault(session => session.Record.Id == card.AssignedSessionId)?.Title ?? card.AssignedSessionName;
	}
	/// <summary>A removed session is never recreated by board navigation.</summary>
	public bool CanNavigateToAssignee(BoardCard card) => Sessions.Any(session => session.Record.Id == card.AssignedSessionId);
	/// <summary>Refreshes saved data on entry.</summary>
	public Task<bool> LoadAsync(CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await Workspace.LoadAsync(cancellationToken);
	});
	/// <summary>Opens a card after flushing the preceding editor.</summary>
	public Task<bool> OpenAsync(string? cardId, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await Workspace.FlushAsync(cancellationToken);
		var next = cardId is null ? null : new BoardCardEditorViewModel(Workspace, cardId, _dispatch, _timeProvider, TimeSpan.FromMilliseconds(750));
		Editor?.Dispose();
		Editor = next;
		CommentText = string.Empty;
	});
	/// <summary>Switches active/archive views only after pending text is saved.</summary>
	public async Task<bool> SetArchiveAsync(bool archive, CancellationToken cancellationToken)
	{
		if (!await OpenAsync(null, cancellationToken))
		{
			return false;
		}
		ShowArchive = archive;
		Raise();
		return true;
	}
	/// <summary>The single Create action appends a Backlog card and opens it.</summary>
	public Task<bool> CreateAsync(string title, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await Workspace.FlushAsync(cancellationToken);
		var result = await ApplyAsync(new CreateCard(title, string.Empty, null), cancellationToken);
		ShowArchive = false;
		Editor?.Dispose();
		Editor = new(Workspace, result.CardId!, _dispatch, _timeProvider, TimeSpan.FromMilliseconds(750));
	});
	/// <summary>Applies metadata against the displayed card revision after saving local text.</summary>
	public Task<bool> UpdateAsync(BoardCardPatch patch, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await Workspace.FlushAsync(cancellationToken);
		var card = SelectedCard ?? throw new InvalidOperationException("Select a card first.");
		await ApplyAsync(new UpdateCard(card.Id, BoardRevisions.ForCard(card), patch), cancellationToken);
	});
	/// <summary>Moves a card before a same-column anchor, or to the column's end.</summary>
	public Task<bool> MoveAsync(string cardId, BoardCardStatus status, string? beforeCardId, CancellationToken cancellationToken,
		string? expectedOrderRevision = null)
	{
		var order = expectedOrderRevision ?? Workspace.Snapshot?.OrderRevision;
		return RunAsync(async () =>
		{
			var snapshot = Workspace.Snapshot ?? throw new InvalidOperationException("Load the board first.");
			var card = snapshot.State.Cards.Single(card => card.Id == cardId);
			if (status != card.Status)
			{
				await Workspace.FlushAsync(cancellationToken);
				card = Workspace.Snapshot.State.Cards.Single(card => card.Id == cardId);
			}
			await ApplyAsync(new UpdateCard(cardId, BoardRevisions.ForCard(card), new() { Status = new(status) }, beforeCardId, order, Move: true), cancellationToken);
		});
	}
	/// <summary>Archives a Done card, or restores it at the end of Done.</summary>
	public Task<bool> ArchiveAsync(CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await Workspace.FlushAsync(cancellationToken);
		var card = SelectedCard ?? throw new InvalidOperationException("Select a card first.");
		await ApplyAsync(new SetArchived(card.Id, !card.IsArchived, BoardRevisions.ForCard(card)), cancellationToken);
		Editor?.Dispose();
		Editor = null;
	});
	/// <summary>Archives the confirmed Done column and closes its card editor after saving.</summary>
	public Task<bool> ArchiveAllDoneAsync(string expectedOrderRevision, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		if (SelectedCard is { Status: BoardCardStatus.Done, IsArchived: false })
		{
			await Workspace.FlushAsync(cancellationToken);
		}

		var result = await ApplyAsync(new ArchiveAllDone(expectedOrderRevision), cancellationToken);
		if (Editor is { } editor && result.Snapshot.State.Cards.Any(card => card.Id == editor.CardId && card.IsArchived))
		{
			editor.Dispose();
			Editor = null;
			CommentText = string.Empty;
		}
	});
	/// <summary>Flushes the selected editor and captures the saved card and its confirmation requirement.</summary>
	public async Task<(BoardCard Card, bool RequiresConfirmation)?> PrepareDeleteAsync(CancellationToken cancellationToken)
	{
		(BoardCard, bool)? prepared = null;
		await RunAsync(async () =>
		{
			if (SelectedCard is not { } displayed || Editor is not { } editor)
			{
				throw new InvalidOperationException("Select a card first.");
			}

			var hadDescription = !string.IsNullOrWhiteSpace(displayed.Description) || !string.IsNullOrWhiteSpace(editor.Description);
			await Workspace.FlushAsync(cancellationToken);
			if (ReferenceEquals(Editor, editor) && SelectedCard is { } saved)
			{
				prepared = (saved, hadDescription || !string.IsNullOrWhiteSpace(saved.Description));
			}
		});
		return prepared;
	}

	/// <summary>Deletes the displayed revision and closes its editor after the saved result.</summary>
	public Task<bool> DeleteAsync(BoardCard card, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(card);
		return RemoveAsync(new DeleteCard(card.Id, BoardRevisions.ForDeletion(card)), cancellationToken);
	}
	/// <summary>Removes exactly the archive contents covered by the given revision.</summary>
	public Task<bool> CleanArchiveAsync(string expectedArchiveRevision, CancellationToken cancellationToken) =>
		RemoveAsync(new CleanArchive(expectedArchiveRevision), cancellationToken);

	private Task<bool> RemoveAsync(BoardCommand command, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		var result = await ApplyAsync(command, cancellationToken);
		if (Editor is { } editor && !result.Snapshot.State.Cards.Any(card => card.Id == editor.CardId))
		{
			editor.Dispose();
			Editor = null;
			CommentText = string.Empty;
		}
	});

	/// <summary>Appends the composer text once; failed writes retain it for inspection and retry.</summary>
	public Task<bool> AddCommentAsync(CancellationToken cancellationToken) => RunAsync(async () =>
	{
		var cardId = Editor?.CardId ?? throw new InvalidOperationException("Select a card first.");
		var text = CommentText;
		await ApplyAsync(new AddComment(cardId, text), cancellationToken);
		if (Editor?.CardId == cardId && CommentText == text)
		{
			CommentText = string.Empty;
		}
	});
	/// <summary>Deletes a comment explicitly; authorship does not restrict project edits.</summary>
	public Task<bool> DeleteCommentAsync(string commentId, CancellationToken cancellationToken) => RunAsync(async () =>
	{
		await ApplyAsync(new DeleteComment(Editor?.CardId ?? throw new InvalidOperationException("Select a card first."), commentId), cancellationToken);
	});

	private async Task<BoardMutationResult> ApplyAsync(BoardCommand command, CancellationToken cancellationToken)
	{
		var result = await Workspace.ExecuteAsync(command, new(null, "You"), cancellationToken);
		if (result.Outcome != BoardMutationOutcome.Applied)
		{
			var message = command switch
			{
				ArchiveAllDone => "The Done column changed. Review the board before trying again.",
				CleanArchive => "The archive changed. Review its contents before trying again.",
				_ => $"Card was not changed: {result.Outcome}. Refresh and check the latest card."
			};
			throw new InvalidOperationException(message);
		}
		return result;
	}
	private async Task<bool> RunAsync(Func<Task> action)
	{
		try
		{
			await action();
			Error = null;
			return true;
		}
		catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			Error = exception.Message;
			return false;
		}
		finally
		{
			Raise();
		}
	}
	private void OnSnapshot(object? sender, BoardSnapshot snapshot) => _dispatch(Raise);
	private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
	{
		WatchSessions();
		_dispatch(RaiseSessions);
	}
	private void WatchSessions()
	{
		foreach (var session in _watchedSessions)
		{
			session.PropertyChanged -= OnSessionChanged;
		}
		_watchedSessions.Clear();
		foreach (var session in Sessions)
		{
			_watchedSessions.Add(session);
			session.PropertyChanged += OnSessionChanged;
		}
	}
	private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (e.PropertyName is nameof(SessionViewModel.Title) or null)
		{
			_dispatch(RaiseSessions);
		}
	}
	private void RaiseSessions() => PropertyChanged?.Invoke(this, new(nameof(Sessions)));
	private void Raise() => PropertyChanged?.Invoke(this, new(null));
	/// <summary>Releases view subscriptions after the workspace has been flushed.</summary>
	public void Dispose()
	{
		_sessionChanges?.CollectionChanged -= OnSessionsChanged;
		foreach (var session in _watchedSessions)
		{
			session.PropertyChanged -= OnSessionChanged;
		}
		_watchedSessions.Clear();
		Workspace.SnapshotChanged -= OnSnapshot;
		Editor?.Dispose();
	}
}
