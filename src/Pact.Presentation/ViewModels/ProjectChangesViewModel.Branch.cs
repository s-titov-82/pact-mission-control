using System.Globalization;

namespace Pact.Presentation.ViewModels;

public sealed partial class ProjectChangesViewModel
{
	private readonly IGitComparisonReader? _comparisonReader;
	private GitBranchChanges? _branchSummary;
	private GitBranchChanges? _displayedBranch;
	private GitRevisionChanges? _selectedComparison;
	private int _branchReadVersion;
	private int _appliedBranchReadVersion;
	private int _branchInteractionHolds;
	/// <summary>Keeps a branch comparison stable while its remark editor is open; live summary counters still refresh.</summary>
	public IDisposable RetainCurrentComparison()
	{
		if (!IsCurrentBranch || _disposed)
		{ return new ComparisonHold(null); }
		Interlocked.Increment(ref _branchInteractionHolds);
		return new ComparisonHold(() =>
		{
			Interlocked.Decrement(ref _branchInteractionHolds);
			_postToUi(() => { if (!_disposed) { RequestRefresh(); } });
		});
	}
	private sealed class ComparisonHold(Action? release) : IDisposable
	{
		private Action? _release = release;
		public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
	}
	/// <summary>Whether the repository reader supports tracking-upstream comparisons.</summary>
	public bool CanChooseCurrentBranch => _comparisonReader is not null && _commitReader is not null;
	/// <summary>Whether Changes follows committed HEAD versus tracking upstream.</summary>
	public bool IsCurrentBranch { get; private set; }
	/// <summary>Immutable target of the displayed historical comparison; null for Working tree.</summary>
	public string? DisplayedTargetCommit => IsCurrentBranch ? _displayedBranch?.Comparison?.TargetCommit : SelectedCommit?.Id;
	/// <summary>Current committed branch file count, independent of the displayed source.</summary>
	public int CurrentBranchFileCount => _branchSummary?.Comparison?.Files.Count ?? 0;
	/// <summary>Current branch additions.</summary>
	public int CurrentBranchAddedLines => _branchSummary?.Comparison?.AddedLines ?? 0;
	/// <summary>Current branch deletions.</summary>
	public int CurrentBranchRemovedLines => _branchSummary?.Comparison?.RemovedLines ?? 0;
	/// <summary>The current branch card's title.</summary>
	public string CurrentBranchEntryTitle => _branchSummary?.Comparison is null ? "Current branch"
		: string.Create(CultureInfo.InvariantCulture, $"Current branch · {CurrentBranchFileCount}");
	/// <summary>Counts or an explicit unavailable/loading state, never a false clean indication.</summary>
	public string CurrentBranchEntrySubtitle => _branchSummary?.Comparison is null
		? _branchSummary?.UnavailableReason ?? (CanChooseCurrentBranch ? "Loading upstream…" : "Branch comparison unavailable.")
		: string.Create(CultureInfo.InvariantCulture, $"+{CurrentBranchAddedLines} −{CurrentBranchRemovedLines}");
	/// <summary>The tracking ref supplying the current branch summary.</summary>
	public string? TrackingUpstream => _branchSummary?.Upstream;
	/// <summary>Selects the committed branch comparison against the configured tracking upstream.</summary>
	public Task SelectCurrentBranchAsync(CancellationToken cancellationToken) =>
		SelectSourceCoreAsync(null, true, null, ReserveSourceSelection(), cancellationToken);
	/// <summary>Reopens a remark's exact captured pair instead of substituting the first parent.</summary>
	public Task OpenRemarkSourceAsync(string? targetCommit, string path, string? baseCommit, CancellationToken cancellationToken) =>
		OpenRemarkSourceCoreAsync(targetCommit, path, baseCommit, true, cancellationToken);

	private void UpdateBranchFacts(GitBranchChanges branch, int readVersion)
	{
		if (_disposed || readVersion < _appliedBranchReadVersion)
		{ return; }
		_appliedBranchReadVersion = readVersion;
		_branchSummary = branch;
		Raise(nameof(CurrentBranchFileCount), nameof(CurrentBranchAddedLines), nameof(CurrentBranchRemovedLines),
			nameof(CurrentBranchEntryTitle), nameof(CurrentBranchEntrySubtitle), nameof(TrackingUpstream));
	}
	private static bool SameBranch(GitBranchChanges? first, GitBranchChanges? second) => first is not null && second is not null
		&& first.Branch == second.Branch && first.Upstream == second.Upstream && first.UnavailableReason == second.UnavailableReason
		&& first.Comparison?.BaseCommit == second.Comparison?.BaseCommit && first.Comparison?.TargetCommit == second.Comparison?.TargetCommit;

	private void ApplyBranchRefresh(GitBranchChanges branch, int sourceVersion)
	{
		if (_disposed || !IsCurrentBranch || IsSourceLoading || Volatile.Read(ref _branchInteractionHolds) > 0
			|| sourceVersion != Volatile.Read(ref _sourceVersion) || SameBranch(_displayedBranch, branch))
		{ return; }
		Interlocked.Increment(ref _sourceVersion);
		Interlocked.Increment(ref _selectionVersion);
		var files = (branch.Comparison?.Files ?? []).Select(entry => new ChangedFileViewModel(entry)).ToArray();
		var selected = Reselect(_files, files, _selectedPath);
		_displayedBranch = branch;
		_selectedComparison = null;
		_files = files;
		Files = files;
		HeadCommit = branch.Comparison?.BaseCommit;
		AddedLines = branch.Comparison?.AddedLines ?? 0;
		RemovedLines = branch.Comparison?.RemovedLines ?? 0;
		IsAvailable = branch.Comparison is not null;
		SelectedFile = null;
		_selectedPath = null;
		_showAll = false;
		ApplyDiff(null);
		Raise(nameof(Files), nameof(FileCount), nameof(SelectedFile), nameof(HeadCommit), nameof(AddedLines), nameof(RemovedLines), nameof(IsAvailable), nameof(TabHeader));
		RaiseSource();
		selected ??= _autoSelectFirstFile && files.Length > 0 ? files[0] : null;
		if (selected is not null)
		{ _ = SelectBranchFileAfterRefreshAsync(selected); }
	}
	private async Task SelectBranchFileAfterRefreshAsync(ChangedFileViewModel selected)
	{
		try
		{ await SelectFileAsync(selected, _disposal.Token).ConfigureAwait(true); }
		catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException or UnauthorizedAccessException)
		{ /* The next explicit selection can retry without changing the captured comparison. */ }
	}
}
