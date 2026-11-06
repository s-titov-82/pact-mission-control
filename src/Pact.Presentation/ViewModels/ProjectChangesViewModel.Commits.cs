namespace Pact.Presentation.ViewModels;

public sealed partial class ProjectChangesViewModel
{
	private WorkingTreeChanges? _workingTreeChanges;
	private int _sourceVersion;
	private int _sourceIntentVersion;
	private int _workingReadVersion;
	private int _appliedWorkingReadVersion;
	private string? _expandedCommit;
	/// <summary>Reserves navigation before external asynchronous work; newer choices supersede it.</summary>
	public int ReserveSourceSelection()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return Interlocked.Increment(ref _sourceIntentVersion);
	}
	/// <summary>Whether this navigation is still the newest request for this live owner.</summary>
	public bool IsSourceSelectionCurrent(int intent) => !_disposed && intent == Volatile.Read(ref _sourceIntentVersion);
	/// <summary>Whether immutable history is supported.</summary>
	public bool CanChooseCommit => CommitReader is not null;
	/// <summary>Creates the standalone bounded history picker.</summary>
	public GitCommitPickerViewModel CreateCommitPicker() => new(ProjectRoot,
		CommitReader ?? throw new InvalidOperationException("Commit history is unavailable."));
	/// <summary>The displayed immutable commit, including a commit selected inside a scope.</summary>
	public GitCommitInfo? SelectedCommit { get; private set; }
	/// <summary>Whether the display is HEAD versus current local contents.</summary>
	public bool IsWorkingTree => SelectedCommit is null && SourceKind == ReviewSourceKind.Scope
		&& (SelectedReviewScope == GitReviewScope.Uncommitted || IsUncommittedFilter);
	/// <summary>Actual live HEAD, never a merge-base or tracking comparison checkpoint.</summary>
	public string? WorkingTreeHeadCommit { get; private set; }
	/// <summary>Live uncommitted paths, independent of the display.</summary>
	public int WorkingTreeFileCount => _workingTreeChanges?.Files.Count ?? 0;
	/// <summary>Live uncommitted additions, independent of the display.</summary>
	public int WorkingTreeAddedLines => _workingTreeChanges?.AddedLines ?? 0;
	/// <summary>Live uncommitted deletions, independent of the display.</summary>
	public int WorkingTreeRemovedLines => _workingTreeChanges?.RemovedLines ?? 0;
	/// <summary>The displayed source and scope, including captured-base navigation.</summary>
	public string SourceTitle => SourceKind == ReviewSourceKind.FixedLocalBase ? $"Fixed base {Short(_fixedLocalBase) ?? "empty tree"} vs local"
		: SelectedCommit is { } commit ? $"{(SourceKind == ReviewSourceKind.Scope ? SelectedReviewScope + " · " : string.Empty)}{commit.ShortId} · {commit.Subject}"
		: $"{SelectedReviewScope} · {Branch}";
	/// <summary>The actual checkpoint, filtering rule or first-parent comparison.</summary>
	public string SourceDescription => SelectedCommit is { } commit
		? _selectedComparison is { } comparison ? $"Captured comparison against {Short(comparison.BaseCommit) ?? "empty tree"}"
			: commit.BaseCommit is { } parent ? $"Compared with {(commit.IsMerge ? "first parent " : string.Empty)}{Short(parent)}" : "Initial commit · compared with empty tree"
		: SourceKind == ReviewSourceKind.FixedLocalBase ? "Captured base retained; local files remain live."
		: IsUncommittedFilter || SelectedReviewScope == GitReviewScope.Uncommitted ? "HEAD → local contents, including untracked files"
		: _displayedReview?.UnavailableReason ?? (SelectedReviewScope == GitReviewScope.Unpushed ? UnpushedDescription
			: $"Merge base with {_displayedReview?.BaseRef} · {Short(HeadCommit)} → local contents");
	/// <summary>Whether metadata selection is pending; the prior display remains readable.</summary>
	public bool IsSourceLoading { get; private set; }
	/// <summary>Selection or refresh failure; prior data is retained as stale.</summary>
	public string? SourceError { get; private set; }
	/// <summary>Shows standalone immutable history, or returns to the Uncommitted scope.</summary>
	public Task SelectCommitAsync(GitCommitInfo? commit, CancellationToken cancellationToken) => commit is null
		? SelectReviewScopeAsync(GitReviewScope.Uncommitted, cancellationToken)
		: SelectSourceCoreAsync(commit, null, ReserveSourceSelection(), false, cancellationToken);

	private async Task OpenRemarkSourceCoreAsync(string? targetCommit, string path, string? baseCommit, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var intent = ReserveSourceSelection();
		if (!IsSourceLoading && ShownDiff is { } shown && shown.Path == path && shown.TargetCommit == targetCommit && shown.HeadCommit == baseCommit)
		{
			// Editing an already-shown remark must keep its live control and editor lease.
			// A local source can adopt the captured base without rebuilding the identical diff.
			if (targetCommit is null && _reviewReader is not null)
			{
				Interlocked.Increment(ref _sourceVersion);
				SourceKind = ReviewSourceKind.FixedLocalBase;
				_fixedLocalBase = baseCommit;
				_displayedReview = null;
				IsUncommittedFilter = false;
				ResetScopeHistory();
				RaiseSource();
			}
			return;
		}
		try
		{
			GitCommitInfo? commit = null;
			GitRevisionChanges? comparison = null;
			if (targetCommit is not null)
			{
				commit = (CommitReader is null ? null : await CommitReader.ResolveCommitAsync(ProjectRoot, targetCommit, cancellationToken).ConfigureAwait(true))
					?? throw new IOException($"Commit is no longer available: {targetCommit}");
				if (!string.Equals(baseCommit, commit.BaseCommit, StringComparison.Ordinal))
				{
					comparison = _comparisonReader is null ? throw new IOException("Captured comparison is unavailable.")
						: await _comparisonReader.ReadComparisonChangesAsync(ProjectRoot, baseCommit, targetCommit, cancellationToken).ConfigureAwait(true);
				}
			}
			if (!IsSourceSelectionCurrent(intent))
			{ throw new OperationCanceledException("Source selection changed."); }
			if (targetCommit is null && _reviewReader is not null)
			{ await SelectLocalSourceAsync(SelectedReviewScope, false, true, baseCommit, intent, cancellationToken).ConfigureAwait(true); }
			else
			{ await SelectSourceCoreAsync(commit, comparison, intent, false, cancellationToken).ConfigureAwait(true); }
			if (!IsSourceSelectionCurrent(intent))
			{ throw new OperationCanceledException("Source selection changed."); }
			if (DisplayedTargetCommit != targetCommit || IsSourceLoading
				|| HeadCommit != baseCommit)
			{ throw new IOException(SourceError ?? "The remark source could not be opened."); }
			var file = Files.FirstOrDefault(file => file.Path == path) ?? throw new IOException($"File is unavailable in this source: {path}");
			await SelectFileAsync(file, cancellationToken).ConfigureAwait(true);
		}
		catch (IOException exception)
		{
			if (IsSourceSelectionCurrent(intent))
			{ SourceError = exception.Message; RaiseSource(); }
			throw;
		}
	}

	private async Task SelectSourceCoreAsync(GitCommitInfo? commit, GitRevisionChanges? comparison, int intent, bool keepScope, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var desiredBase = comparison is null ? commit?.BaseCommit : comparison.BaseCommit;
		var version = Interlocked.Increment(ref _sourceVersion);
		Interlocked.Increment(ref _selectionVersion);
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
		var token = lifetime.Token;
		IsSourceLoading = true;
		SourceError = null;
		RaiseSource();
		try
		{
			CommitChanges? historical = null;
			var working = _workingTreeChanges;
			if (commit is not null && comparison is null)
			{ historical = await (CommitReader ?? throw new IOException("Commit history unavailable.")).ReadCommitChangesAsync(ProjectRoot, commit, token).ConfigureAwait(false); }
			else if (commit is null && working is null)
			{ working = await _reader.ReadChangesAsync(ProjectRoot, token).ConfigureAwait(false); }
			token.ThrowIfCancellationRequested();
			Task first = Task.CompletedTask;
			await PublishSourceAsync(() =>
			{
				if (!IsSourceSelectionCurrent(intent) || version != _sourceVersion || token.IsCancellationRequested)
				{ return; }
				if (commit is null)
				{ UpdateWorkingFacts(working, Interlocked.Increment(ref _workingReadVersion)); }
				SelectedCommit = commit;
				_selectedComparison = comparison;
				IsUncommittedFilter = false;
				SourceKind = commit is null || keepScope ? ReviewSourceKind.Scope : ReviewSourceKind.HistoricalCommit;
				if (!keepScope)
				{ SelectedReviewScope = GitReviewScope.Uncommitted; _displayedReview = null; ResetScopeHistory(); }
				IsSourceLoading = false;
				IsAvailable = commit is not null || working is not null;
				HeadCommit = commit is null ? working?.HeadCommit : desiredBase;
				AddedLines = comparison?.AddedLines ?? historical?.AddedLines ?? working?.AddedLines ?? 0;
				RemovedLines = comparison?.RemovedLines ?? historical?.RemovedLines ?? working?.RemovedLines ?? 0;
				_files = (comparison?.Files ?? historical?.Files ?? working?.Files ?? []).Select(entry => new ChangedFileViewModel(entry)).ToArray();
				Files = _files;
				SelectedFile = null;
				_selectedPath = null;
				_showAll = false;
				ApplyDiff(null);
				Raise(nameof(Files), nameof(FileCount), nameof(SelectedFile), nameof(HeadCommit), nameof(AddedLines), nameof(RemovedLines), nameof(IsAvailable), nameof(TabHeader));
				RaiseSource();
				if (_autoSelectFirstFile && Files.Count > 0)
				{ first = SelectFileAsync(Files[0], token); }
			}, token).ConfigureAwait(false);
			await first.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
		{ await PublishSourceAsync(() => { if (IsSourceSelectionCurrent(intent)) { SourceError = exception.Message; RaiseSource(); } }, CancellationToken.None).ConfigureAwait(false); }
		finally
		{
			await PublishSourceAsync(() => { if (!_disposed && version == _sourceVersion) { IsSourceLoading = false; RaiseSource(); } }, CancellationToken.None).ConfigureAwait(false);
		}
	}
	private static string? Short(string? id) => id?[..Math.Min(8, id.Length)];
	private bool UpdateWorkingFacts(WorkingTreeChanges? changes, int readVersion)
	{
		if (_disposed || readVersion < _appliedWorkingReadVersion)
		{ return false; }
		_appliedWorkingReadVersion = readVersion;
		_workingTreeChanges = changes;
		WorkingTreeHeadCommit = changes?.HeadCommit;
		Branch = changes?.Branch ?? string.Empty;
		Raise(nameof(WorkingTreeHeadCommit), nameof(WorkingTreeFileCount), nameof(WorkingTreeAddedLines), nameof(WorkingTreeRemovedLines), nameof(Branch), nameof(EntryTitle), nameof(EntrySubtitle));
		return true;
	}
	private Task PublishSourceAsync(Action action, CancellationToken cancellationToken)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_postToUi(() =>
		{
			try
			{ if (!cancellationToken.IsCancellationRequested) { action(); } completion.TrySetResult(); }
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}
	private void RaiseSource() => Raise(nameof(SelectedCommit), nameof(IsWorkingTree), nameof(SourceTitle), nameof(SourceDescription), nameof(SourceKind),
		nameof(SelectedReviewScope), nameof(IsUncommittedFilter), nameof(ReviewFilterLabel), nameof(IsScopeHistoryVisible), nameof(DisplayedTargetCommit), nameof(IsSourceLoading), nameof(SourceError));
}
