namespace Pact.Presentation.ViewModels;

public sealed partial class ProjectChangesViewModel
{
	private readonly IGitCommitReader? _commitReader;
	private WorkingTreeChanges? _workingTreeChanges;
	private int _sourceVersion;
	private int _sourceIntentVersion;
	private int _workingReadVersion;
	private int _appliedWorkingReadVersion;
	private string? _expandedCommit;
	/// <summary>Reserves a source navigation before external asynchronous work; later selections supersede it.</summary>
	public int ReserveSourceSelection()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		return Interlocked.Increment(ref _sourceIntentVersion);
	}
	/// <summary>Whether a reserved source navigation is still the newest request for this live project.</summary>
	public bool IsSourceSelectionCurrent(int intent) => !_disposed && intent == Volatile.Read(ref _sourceIntentVersion);
	/// <summary>Whether the reader supports immutable commit selection.</summary>
	public bool CanChooseCommit => _commitReader is not null;
	/// <summary>Creates a dialog-owned picker using the same repository reader.</summary>
	public GitCommitPickerViewModel CreateCommitPicker() => new(ProjectRoot,
		_commitReader ?? throw new InvalidOperationException("Commit history is unavailable."));

	/// <summary>Opens a commit's first-parent source and file, preserving the prior display on failure.</summary>
	public Task OpenRemarkSourceAsync(string? targetCommit, string path, CancellationToken cancellationToken) =>
		OpenRemarkSourceCoreAsync(targetCommit, path, null, false, cancellationToken);

	private async Task OpenRemarkSourceCoreAsync(string? targetCommit, string path, string? baseCommit, bool capturedBase, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var intent = ReserveSourceSelection();
		try
		{
			GitCommitInfo? commit = null;
			GitRevisionChanges? comparison = null;
			if (targetCommit is not null)
			{
				commit = (_commitReader is null ? null : await _commitReader.ResolveCommitAsync(ProjectRoot, targetCommit, cancellationToken).ConfigureAwait(true))
					?? throw new IOException($"Commit is no longer available: {targetCommit}");
				if (capturedBase && !string.Equals(baseCommit, commit.BaseCommit, StringComparison.Ordinal))
				{
					comparison = _comparisonReader is null ? throw new IOException("Captured comparison is unavailable.")
						: await _comparisonReader.ReadComparisonChangesAsync(ProjectRoot, baseCommit, targetCommit, cancellationToken).ConfigureAwait(true);
				}
			}
			cancellationToken.ThrowIfCancellationRequested();
			if (_disposed || intent != Volatile.Read(ref _sourceIntentVersion))
			{ throw new OperationCanceledException("Source selection changed."); }
			await SelectSourceCoreAsync(commit, false, comparison, intent, cancellationToken).ConfigureAwait(true);
			if (_disposed || intent != Volatile.Read(ref _sourceIntentVersion))
			{ throw new OperationCanceledException("Source selection changed."); }
			var expectedBase = capturedBase ? baseCommit : commit?.BaseCommit;
			if (!string.Equals(DisplayedTargetCommit, targetCommit, StringComparison.Ordinal) || IsSourceLoading || IsCurrentBranch
				|| (targetCommit is not null && !string.Equals(HeadCommit, expectedBase, StringComparison.Ordinal)))
			{ throw new IOException(SourceError ?? "The remark source could not be opened."); }
			var file = Files.FirstOrDefault(file => string.Equals(file.Path, path, StringComparison.Ordinal))
				?? throw new IOException($"File is unavailable in this source: {path}");
			await SelectFileAsync(file, cancellationToken).ConfigureAwait(true);
		}
		catch (IOException exception)
		{
			if (!_disposed && intent == Volatile.Read(ref _sourceIntentVersion))
			{ SourceError = exception.Message; RaiseSource(); }
			throw;
		}
	}
	/// <summary>The individual historical commit, or null for a live source.</summary>
	public GitCommitInfo? SelectedCommit { get; private set; }
	/// <summary>Whether displayed files belong to the working tree.</summary>
	public bool IsWorkingTree => !IsCurrentBranch && SelectedCommit is null;
	/// <summary>Actual live HEAD, independent of a historical comparison base.</summary>
	public string? WorkingTreeHeadCommit { get; private set; }
	/// <summary>Live changed-file count, independent of the displayed source.</summary>
	public int WorkingTreeFileCount => _workingTreeChanges?.Files.Count ?? 0;
	/// <summary>Live added-line count for the right-column summary.</summary>
	public int WorkingTreeAddedLines => _workingTreeChanges?.AddedLines ?? 0;
	/// <summary>Live removed-line count for the right-column summary.</summary>
	public int WorkingTreeRemovedLines => _workingTreeChanges?.RemovedLines ?? 0;
	/// <summary>Source identity shown above the displayed file list.</summary>
	public string SourceTitle => IsCurrentBranch ? $"Current branch · {_displayedBranch?.Branch}"
		: SelectedCommit is { } commit ? $"{commit.ShortId} · {commit.Subject}" : "Working tree";
	/// <summary>Explicit comparison base, including the first-parent rule or captured upstream pair.</summary>
	public string SourceDescription => IsCurrentBranch
		? _displayedBranch?.UnavailableReason ?? $"HEAD {Short(DisplayedTargetCommit)} compared with {_displayedBranch?.Upstream} · {Short(HeadCommit)}"
		: SelectedCommit is not { } commit ? "Uncommitted changes compared with HEAD"
		: _selectedComparison is { } comparison ? $"Captured comparison against {Short(comparison.BaseCommit) ?? "empty tree"}"
		: commit.BaseCommit is not { } parent ? "Initial commit · compared with empty tree"
		: $"Compared with {(commit.IsMerge ? "first parent " : string.Empty)}{Short(parent)}";
	/// <summary>Whether a source is being read while the preceding display stays intact.</summary>
	public bool IsSourceLoading { get; private set; }
	/// <summary>Failure to select a source; the preceding valid display remains selected.</summary>
	public string? SourceError { get; private set; }
	/// <summary>Selects an immutable commit, or returns to Working tree.</summary>
	public Task SelectCommitAsync(GitCommitInfo? commit, CancellationToken cancellationToken) =>
		SelectSourceCoreAsync(commit, false, null, ReserveSourceSelection(), cancellationToken);

	private async Task SelectSourceCoreAsync(GitCommitInfo? commit, bool currentBranch, GitRevisionChanges? comparison, int intent, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		cancellationToken.ThrowIfCancellationRequested();
		if ((commit is not null && _commitReader is null) || (currentBranch && !CanChooseCurrentBranch))
		{ throw new InvalidOperationException("This comparison is unavailable."); }
		var desiredBase = comparison is not null ? comparison.BaseCommit : commit?.BaseCommit;
		if (!IsSourceLoading && !currentBranch && !IsCurrentBranch
			&& string.Equals(SelectedCommit?.Id, commit?.Id, StringComparison.Ordinal)
			&& (commit is null ? _workingTreeChanges is not null : string.Equals(HeadCommit, desiredBase, StringComparison.Ordinal)))
		{
			if (commit is null)
			{ RequestRefresh(); }
			return;
		}
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
			GitBranchChanges? branch = null;
			var working = _workingTreeChanges;
			var readVersion = 0;
			var branchVersion = 0;
			if (currentBranch)
			{
				branchVersion = Interlocked.Increment(ref _branchReadVersion);
				branch = await _comparisonReader!.ReadBranchChangesAsync(ProjectRoot, token).ConfigureAwait(false);
			}
			else if (commit is not null && comparison is null)
			{ historical = await _commitReader!.ReadCommitChangesAsync(ProjectRoot, commit, token).ConfigureAwait(false); }
			else if (commit is null && working is null)
			{
				readVersion = Interlocked.Increment(ref _workingReadVersion);
				working = await _reader.ReadChangesAsync(ProjectRoot, token).ConfigureAwait(false);
			}
			token.ThrowIfCancellationRequested();
			Task firstFile = Task.CompletedTask;
			await PublishSourceAsync(() =>
			{
				if (_disposed || version != Volatile.Read(ref _sourceVersion) || intent != Volatile.Read(ref _sourceIntentVersion) || token.IsCancellationRequested)
				{ return; }
				if (readVersion > 0)
				{ UpdateWorkingFacts(working, readVersion); }
				if (branch is not null)
				{ UpdateBranchFacts(branch, branchVersion); }
				if (currentBranch && IsCurrentBranch && SameBranch(_displayedBranch, branch))
				{
					IsSourceLoading = false;
					RaiseSource();
					if (ShownDiff is null && SelectedFile is { } pending)
					{ firstFile = SelectFileAsync(pending, token); }
					return;
				}
				var live = _workingTreeChanges ?? working;
				SelectedCommit = commit;
				IsCurrentBranch = currentBranch;
				_displayedBranch = currentBranch ? branch : null;
				_selectedComparison = comparison;
				IsSourceLoading = false;
				var revision = currentBranch ? branch?.Comparison : comparison;
				IsAvailable = currentBranch ? revision is not null : commit is not null || live is not null;
				HeadCommit = currentBranch ? revision?.BaseCommit : commit is null ? live?.HeadCommit : desiredBase;
				AddedLines = currentBranch ? revision?.AddedLines ?? 0 : revision?.AddedLines ?? historical?.AddedLines ?? live?.AddedLines ?? 0;
				RemovedLines = currentBranch ? revision?.RemovedLines ?? 0 : revision?.RemovedLines ?? historical?.RemovedLines ?? live?.RemovedLines ?? 0;
				var entries = currentBranch ? revision?.Files ?? [] : revision?.Files ?? historical?.Files ?? live?.Files ?? [];
				_files = entries.Select(entry => new ChangedFileViewModel(entry)).ToArray();
				Files = _files;
				_selectedPath = null;
				SelectedFile = null;
				_showAll = false;
				ApplyDiff(null);
				Raise(nameof(Files), nameof(FileCount), nameof(SelectedFile), nameof(HeadCommit), nameof(AddedLines), nameof(RemovedLines), nameof(IsAvailable), nameof(TabHeader));
				RaiseSource();
				if (_autoSelectFirstFile && Files.Count > 0)
				{ firstFile = SelectFileAsync(Files[0], token); }
			}, token).ConfigureAwait(false);
			await firstFile.ConfigureAwait(false);
			if (commit is null && version == Volatile.Read(ref _sourceVersion) && intent == Volatile.Read(ref _sourceIntentVersion))
			{ RequestRefresh(); }
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
		catch (Exception exception)
		{
			if (!_disposed && version == Volatile.Read(ref _sourceVersion) && intent == Volatile.Read(ref _sourceIntentVersion))
			{
				await PublishSourceAsync(() =>
				{
					if (_disposed || version != Volatile.Read(ref _sourceVersion) || intent != Volatile.Read(ref _sourceIntentVersion))
					{ return; }
					SourceError = exception.Message;
					RaiseSource();
				}, token).ConfigureAwait(false);
			}
		}
		finally
		{
			if (!_disposed && version == Volatile.Read(ref _sourceVersion))
			{
				await PublishSourceAsync(() =>
				{
					if (_disposed || version != Volatile.Read(ref _sourceVersion))
					{ return; }
					IsSourceLoading = false;
					RaiseSource();
				}, CancellationToken.None).ConfigureAwait(false);
			}
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
		Raise(nameof(WorkingTreeHeadCommit), nameof(WorkingTreeFileCount), nameof(WorkingTreeAddedLines), nameof(WorkingTreeRemovedLines),
			nameof(Branch), nameof(EntryTitle), nameof(EntrySubtitle));
		return true;
	}

	// A source transition completes its metadata publication before callers navigate to a row.
	private Task PublishSourceAsync(Action action, CancellationToken cancellationToken)
	{
		TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_postToUi(() =>
		{
			try
			{
				if (!cancellationToken.IsCancellationRequested)
				{ action(); }
				completion.TrySetResult();
			}
			catch (Exception exception) { completion.TrySetException(exception); }
		});
		return completion.Task.WaitAsync(cancellationToken);
	}
	private void RaiseSource() => Raise(nameof(SelectedCommit), nameof(IsWorkingTree), nameof(SourceTitle), nameof(SourceDescription),
		nameof(IsCurrentBranch), nameof(DisplayedTargetCommit), nameof(IsSourceLoading), nameof(SourceError));
}