using System.Collections.Concurrent;
using System.Globalization;

namespace Pact.Presentation.ViewModels;

/// <summary>Whether the display follows a scope, a captured local base, or standalone history.</summary>
public enum ReviewSourceKind
{
	/// <summary>A live scope with an aggregate, local-only, or individual-commit filter.</summary>
	Scope,
	/// <summary>Current local content compared with a remark's fixed captured base.</summary>
	FixedLocalBase,
	/// <summary>An immutable historical comparison outside scope filters.</summary>
	HistoricalCommit
}

public sealed partial class ProjectChangesViewModel
{
	private readonly IGitComparisonReader? _comparisonReader;
	private readonly IGitReviewReader? _reviewReader;
	private GitRevisionChanges? _selectedComparison;
	private readonly ConcurrentDictionary<GitReviewScope, GitReviewChanges> _scopeSummaries = new();
	private readonly ConcurrentDictionary<GitReviewScope, string> _scopeErrors = new();
	private readonly ConcurrentDictionary<string, string?> _targetBranches = new(StringComparer.Ordinal);
	private readonly int[] _reviewReadVersions = new int[3];
	private readonly int[] _reviewAppliedVersions = new int[3];
	private GitReviewChanges? _displayedReview;
	private string? _fixedLocalBase;
	private int _targetEpoch;
	private int _historyEpoch;
	private int _interactionHolds;
	/// <summary>The source displayed in the file list; summary cards remain live independently.</summary>
	public ReviewSourceKind SourceKind { get; private set; }
	/// <summary>The surrounding scope, retained while looking at one of its commits.</summary>
	public GitReviewScope SelectedReviewScope { get; private set; }
	/// <summary>Whether the inner filter shows only current local edits.</summary>
	public bool IsUncommittedFilter { get; private set; }
	/// <summary>Whether this reader supports the complete review-scope contract.</summary>
	public bool CanChooseReviewScopes => _reviewReader is not null;
	/// <summary>Whether the latest observed local repository state permits explicit Pull (rebase).</summary>
	public bool CanPullRebase => PullRebaseBlockedReason is null;
	/// <summary>Explains a disabled Pull action; execution still performs a fresh check.</summary>
	public string? PullRebaseBlockedReason => _scopeErrors.TryGetValue(GitReviewScope.Uncommitted, out var error) ? "Repository state is stale: " + error
		: !_scopeSummaries.TryGetValue(GitReviewScope.Uncommitted, out var local) ? "Loading repository status…"
		: local.UnavailableReason ?? local.PullRebaseBlockedReason;
	/// <summary>Immutable target only for a historical display; local aggregates have no target SHA.</summary>
	public string? DisplayedTargetCommit => SelectedCommit?.Id;
	/// <summary>Whether scope-specific history and filter actions apply.</summary>
	public bool IsScopeHistoryVisible => SourceKind == ReviewSourceKind.Scope && SelectedReviewScope != GitReviewScope.Uncommitted && CanChooseReviewScopes;
	/// <summary>Bounded history for the surrounding scope, independent of the displayed file.</summary>
	public IReadOnlyList<GitCommitInfo> ScopeCommits { get; private set; } = [];
	/// <summary>Whether another bounded page may exist.</summary>
	public bool HasMoreScopeCommits { get; private set; } = true;
	/// <summary>Whether a scope-history request is pending.</summary>
	public bool IsScopeHistoryLoading { get; private set; }
	/// <summary>The selected inner filter, for the source control.</summary>
	public string ReviewFilterLabel => SelectedCommit is { } commit ? commit.ShortId : IsUncommittedFilter ? "Uncommitted" : "All changes";
	/// <summary>The manually chosen target, null for automatic default selection.</summary>
	public string? SelectedTargetBranch => TargetFor(Branch);
	/// <summary>The effective merge target and whether it was chosen automatically.</summary>
	public string TargetDescription => $"Target: {SelectedTargetBranch ?? "Auto · " + (_scopeSummaries.GetValueOrDefault(GitReviewScope.Unmerged)?.BaseRef ?? "select a branch")}";
	/// <summary>Unmerged file total; never becomes an individual commit's count.</summary>
	public string UnmergedEntryTitle => ScopeEntryTitle(GitReviewScope.Unmerged);
	/// <summary>Unmerged totals or an explicit unavailable state.</summary>
	public string UnmergedEntrySubtitle => ScopeEntrySubtitle(GitReviewScope.Unmerged);
	/// <summary>Unpushed file total; never becomes an individual commit's count.</summary>
	public string UnpushedEntryTitle => ScopeEntryTitle(GitReviewScope.Unpushed);
	/// <summary>Unpushed totals or an explicit unavailable state.</summary>
	public string UnpushedEntrySubtitle => ScopeEntrySubtitle(GitReviewScope.Unpushed);
	/// <summary>The exact server-comparison contract, fallback and divergence explanation.</summary>
	public string UnpushedDescription => _scopeSummaries.GetValueOrDefault(GitReviewScope.Unpushed) is not { } review ? "Remote refs reflect the last fetch/pull."
		: review.UnavailableReason ?? (review.IsFallback ? $"No tracking branch — using merge base with {review.BaseRef}."
		: $"Exact difference from {review.BaseRef} to local · ahead {review.AheadCount}, behind {review.BehindCount}."
			+ (review.BehindCount > 0 ? " Remote-only changes appear as local reversions." : string.Empty));
	/// <summary>Allows remark placement to inspect the captured immutable base rather than current HEAD.</summary>
	internal IGitCommitReader? CommitReader { get; }

	private string? TargetFor(string branch) => _targetBranches.GetValueOrDefault(branch);
	private string ScopeEntryTitle(GitReviewScope scope) => _scopeSummaries.GetValueOrDefault(scope)?.Changes is { } changes
		? string.Create(CultureInfo.InvariantCulture, $"{scope} · {changes.Files.Count}") : scope.ToString();
	private string ScopeEntrySubtitle(GitReviewScope scope) => _scopeErrors.TryGetValue(scope, out var error) ? "Stale · " + error
		: _scopeSummaries.GetValueOrDefault(scope) is not { } review ? "Loading…"
		: review.UnavailableReason ?? string.Create(CultureInfo.InvariantCulture, $"+{review.AddedLines} −{review.RemovedLines}");

	/// <summary>Chooses an aggregate scope, leaving fixed-base and standalone history sources.</summary>
	public Task SelectReviewScopeAsync(GitReviewScope scope, CancellationToken cancellationToken)
	{
		if (!Enum.IsDefined(scope))
		{ throw new ArgumentOutOfRangeException(nameof(scope)); }
		if (_reviewReader is null)
		{
			if (scope != GitReviewScope.Uncommitted)
			{ throw new InvalidOperationException("Review scopes are unavailable."); }
			return SelectSourceCoreAsync(null, null, ReserveSourceSelection(), false, cancellationToken);
		}
		return SelectLocalSourceAsync(scope, false, false, null, ReserveSourceSelection(), cancellationToken);
	}
	/// <summary>Shows all scope changes, local-only edits, or one commit's first-parent patch.</summary>
	public Task SelectReviewFilterAsync(GitCommitInfo? commit, bool uncommitted, CancellationToken cancellationToken)
	{
		if (commit is not null && uncommitted)
		{ throw new ArgumentException("Choose one filter.", nameof(uncommitted)); }
		return commit is not null ? SelectSourceCoreAsync(commit, null, ReserveSourceSelection(), true, cancellationToken)
			: SelectLocalSourceAsync(SelectedReviewScope, uncommitted, false, null, ReserveSourceSelection(), cancellationToken);
	}
	/// <summary>Retains a manual target for this live project's branch, or returns it to automatic selection.</summary>
	public async Task SelectTargetBranchAsync(string? branchRef, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		_targetBranches[Branch] = branchRef;
		Interlocked.Increment(ref _targetEpoch);
		if (SourceKind == ReviewSourceKind.Scope && SelectedCommit is null && !IsUncommittedFilter
			&& (SelectedReviewScope == GitReviewScope.Unmerged || SelectedReviewScope == GitReviewScope.Unpushed
				&& _scopeSummaries.GetValueOrDefault(GitReviewScope.Unpushed)?.IsFallback == true))
		{ await SelectReviewScopeAsync(SelectedReviewScope, cancellationToken).ConfigureAwait(true); }
		RequestRefresh();
	}
	/// <summary>Reads candidates for the target chooser; it never changes repository state.</summary>
	public Task<IReadOnlyList<string>> ReadTargetBranchesAsync(CancellationToken cancellationToken) =>
		_reviewReader?.ReadBranchesAsync(ProjectRoot, cancellationToken) ?? Task.FromResult<IReadOnlyList<string>>([]);
	/// <summary>Opens a remark's captured historical pair or its fixed base versus live local contents.</summary>
	public Task OpenRemarkSourceAsync(string? targetCommit, string path, string? baseCommit, CancellationToken cancellationToken) =>
		OpenRemarkSourceCoreAsync(targetCommit, path, baseCommit, cancellationToken);

	/// <summary>Retains the local diff while its editor is open; live summary counters continue to refresh.</summary>
	public IDisposable RetainCurrentComparison()
	{
		if (SelectedCommit is not null || _disposed)
		{ return new ComparisonHold(null); }
		Interlocked.Increment(ref _interactionHolds);
		return new ComparisonHold(() =>
		{
			Interlocked.Decrement(ref _interactionHolds);
			_postToUi(() => { if (!_disposed) { RequestRefresh(); } });
		});
	}
	private sealed class ComparisonHold(Action? release) : IDisposable
	{
		private Action? _release = release;
		public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
	}

	private void UpdateReviewFacts(GitReviewChanges review, int version)
	{
		var index = (int)review.Scope;
		if (_disposed || version < _reviewAppliedVersions[index])
		{ return; }
		_reviewAppliedVersions[index] = version;
		_scopeErrors.TryRemove(review.Scope, out _);
		var prior = _scopeSummaries.GetValueOrDefault(review.Scope);
		_scopeSummaries[review.Scope] = review;
		if (review.Scope == GitReviewScope.Uncommitted)
		{
			var local = review.Changes;
			UpdateWorkingFacts(local is null ? null : new(local.HeadCommit, review.Branch, local.Files, local.AddedLines, local.RemovedLines),
				Interlocked.Increment(ref _workingReadVersion));
		}
		if (review.Scope == SelectedReviewScope && (prior?.BaseCommit != review.BaseCommit || prior?.HeadCommit != review.HeadCommit || prior?.BaseRef != review.BaseRef))
		{ ResetScopeHistory(); }
		RaiseReviewFacts();
	}
	private void RaiseReviewFacts() => Raise(nameof(UnmergedEntryTitle), nameof(UnmergedEntrySubtitle), nameof(UnpushedEntryTitle), nameof(UnpushedEntrySubtitle),
		nameof(UnpushedDescription), nameof(SelectedTargetBranch), nameof(TargetDescription), nameof(EntrySubtitle), nameof(CanPullRebase), nameof(PullRebaseBlockedReason));
	private void ResetScopeHistory()
	{
		_historyEpoch++;
		ScopeCommits = [];
		HasMoreScopeCommits = true;
		IsScopeHistoryLoading = false;
		Raise(nameof(ScopeCommits), nameof(HasMoreScopeCommits), nameof(IsScopeHistoryLoading));
	}
	/// <summary>Appends up to fifty commits from the immutable surrounding scope endpoints.</summary>
	public async Task LoadMoreScopeCommitsAsync(CancellationToken cancellationToken)
	{
		if (_reviewReader is null || !IsScopeHistoryVisible || IsScopeHistoryLoading || !HasMoreScopeCommits
			|| _scopeSummaries.GetValueOrDefault(SelectedReviewScope) is not { Changes: not null, HeadCommit: { } head } review)
		{ return; }
		var epoch = _historyEpoch;
		IsScopeHistoryLoading = true;
		Raise(nameof(IsScopeHistoryLoading));
		try
		{
			var page = await _reviewReader.ReadScopeCommitsAsync(ProjectRoot, review.BaseCommit, head, ScopeCommits.Count, 50, cancellationToken).ConfigureAwait(false);
			await PublishSourceAsync(() =>
			{
				if (_disposed || epoch != _historyEpoch)
				{ return; }
				ScopeCommits = [.. ScopeCommits, .. page];
				HasMoreScopeCommits = page.Count == 50;
				Raise(nameof(ScopeCommits), nameof(HasMoreScopeCommits));
			}, cancellationToken).ConfigureAwait(false);
		}
		catch (IOException exception)
		{ await PublishSourceAsync(() => { if (!_disposed && epoch == _historyEpoch) { SourceError = exception.Message; RaiseSource(); } }, CancellationToken.None).ConfigureAwait(false); }
		finally
		{
			await PublishSourceAsync(() =>
			{
				if (!_disposed && epoch == _historyEpoch)
				{ IsScopeHistoryLoading = false; Raise(nameof(IsScopeHistoryLoading)); }
			}, CancellationToken.None).ConfigureAwait(false);
		}
	}

	private async Task SelectLocalSourceAsync(GitReviewScope scope, bool uncommitted, bool fixedBase, string? baseline, int intent, CancellationToken cancellationToken)
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		var reader = _reviewReader ?? throw new InvalidOperationException("Local comparisons are unavailable.");
		var version = Interlocked.Increment(ref _sourceVersion);
		var expectedBranch = Branch;
		var usedTarget = TargetFor(expectedBranch);
		var targetEpoch = Volatile.Read(ref _targetEpoch);
		Interlocked.Increment(ref _selectionVersion);
		using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
		var token = lifetime.Token;
		IsSourceLoading = true;
		SourceError = null;
		RaiseSource();
		try
		{
			GitReviewChanges? review = null;
			GitLocalChanges? local;
			var readVersion = 0;
			if (fixedBase)
			{ local = await reader.ReadLocalChangesAsync(ProjectRoot, baseline, token).ConfigureAwait(false); }
			else
			{
				var readScope = uncommitted ? GitReviewScope.Uncommitted : scope;
				readVersion = Interlocked.Increment(ref _reviewReadVersions[(int)readScope]);
				review = await reader.ReadReviewChangesAsync(ProjectRoot, readScope, usedTarget, token).ConfigureAwait(false);
				local = review.Changes;
			}
			token.ThrowIfCancellationRequested();
			if (review is { Scope: not GitReviewScope.Uncommitted } && expectedBranch.Length > 0 && review.Branch != expectedBranch)
			{
				RequestRefresh();
				throw new IOException("The branch changed during source selection. Refresh before choosing its review scope.");
			}
			Task first = Task.CompletedTask;
			await PublishSourceAsync(() =>
			{
				if (!IsSourceSelectionCurrent(intent) || version != _sourceVersion || targetEpoch != _targetEpoch || token.IsCancellationRequested)
				{ return; }
				if (review is { Scope: not GitReviewScope.Uncommitted } && TargetFor(review.Branch) != usedTarget)
				{ RequestRefresh(); SourceError = "The branch target changed; refresh before selecting this scope."; RaiseSource(); return; }
				if (review is not null)
				{ UpdateReviewFacts(review, readVersion); }
				SelectedReviewScope = scope;
				IsUncommittedFilter = uncommitted;
				SelectedCommit = null;
				SourceKind = fixedBase ? ReviewSourceKind.FixedLocalBase : ReviewSourceKind.Scope;
				_fixedLocalBase = baseline;
				_selectedComparison = null;
				_displayedReview = uncommitted ? _scopeSummaries.GetValueOrDefault(scope) : review;
				ResetScopeHistory();
				IsSourceLoading = false;
				ApplyLocalSource(local, resetSelection: true);
				RaiseSource();
				if (_autoSelectFirstFile && Files.Count > 0)
				{ first = SelectFileAsync(Files[0], token); }
			}, token).ConfigureAwait(false);
			await first.ConfigureAwait(false);
			Task history = Task.CompletedTask;
			await PublishSourceAsync(() => { if (IsSourceSelectionCurrent(intent)) { history = LoadMoreScopeCommitsAsync(token); } }, token).ConfigureAwait(false);
			await history.ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
		catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
		{ await PublishSourceAsync(() => { if (IsSourceSelectionCurrent(intent)) { SourceError = exception.Message; RaiseSource(); } }, CancellationToken.None).ConfigureAwait(false); }
		finally
		{
			await PublishSourceAsync(() => { if (!_disposed && version == _sourceVersion) { IsSourceLoading = false; RaiseSource(); } }, CancellationToken.None).ConfigureAwait(false);
		}
	}
	private void ApplyLocalSource(GitLocalChanges? local, bool resetSelection)
	{
		IsAvailable = local is not null;
		HeadCommit = local?.BaseCommit;
		AddedLines = local?.AddedLines ?? 0;
		RemovedLines = local?.RemovedLines ?? 0;
		_files = (local?.Files ?? []).Select(entry => new ChangedFileViewModel(entry)).ToArray();
		Files = _files;
		if (resetSelection)
		{ SelectedFile = null; _selectedPath = null; _showAll = false; ApplyDiff(null); }
		Raise(nameof(Files), nameof(FileCount), nameof(SelectedFile), nameof(HeadCommit), nameof(AddedLines), nameof(RemovedLines), nameof(IsAvailable), nameof(TabHeader));
	}
}