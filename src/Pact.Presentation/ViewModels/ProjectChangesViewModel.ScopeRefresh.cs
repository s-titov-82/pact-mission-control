namespace Pact.Presentation.ViewModels;

public sealed partial class ProjectChangesViewModel
{
	private void ApplyReviewError(GitReviewScope scope, int version, string error)
	{
		if (_disposed || version < _reviewAppliedVersions[(int)scope])
		{ return; }
		_reviewAppliedVersions[(int)scope] = version;
		_scopeErrors[scope] = error;
		RaiseReviewFacts();
	}
	private async Task RefreshReviewAsync(CancellationToken cancellationToken)
	{
		var reader = _reviewReader!.CreateReadBatch();
		var sourceVersion = Volatile.Read(ref _sourceVersion);
		var selectionVersion = Volatile.Read(ref _selectionVersion);
		var targetEpoch = Volatile.Read(ref _targetEpoch);
		var scope = SelectedReviewScope;
		var kind = SourceKind;
		var fixedBase = _fixedLocalBase;
		var localOnly = IsUncommittedFilter;
		var displayLocal = SelectedCommit is null && !IsSourceLoading;
		var target = TargetFor(Branch);
		List<(GitReviewChanges Review, int Version)> results = [];
		List<(GitReviewScope Scope, int Version, string Error)> errors = [];
		foreach (var readScope in Enum.GetValues<GitReviewScope>())
		{
			var readVersion = Interlocked.Increment(ref _reviewReadVersions[(int)readScope]);
			try
			{
				var review = await reader.ReadReviewChangesAsync(ProjectRoot, readScope, target, cancellationToken).ConfigureAwait(false);
				results.Add((review, readVersion));
			}
			catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
			{ errors.Add((readScope, readVersion, exception.Message)); }
		}
		var effectiveScope = localOnly ? GitReviewScope.Uncommitted : scope;
		var displayed = results.FirstOrDefault(item => item.Review.Scope == effectiveScope).Review;
		var local = displayed?.Changes;
		string? displayFailure = null;
		if (displayLocal && kind == ReviewSourceKind.FixedLocalBase)
		{
			try
			{ local = await reader.ReadLocalChangesAsync(ProjectRoot, fixedBase, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
			{ displayFailure = exception.Message; }
		}
		var files = (local?.Files ?? []).Select(entry => new ChangedFileViewModel(entry)).ToArray();
		var selected = displayLocal ? Reselect(_files, files, _selectedPath) : null;
		selected ??= displayLocal && _autoSelectFirstFile && files.Length > 0 ? files[0] : null;
		LoadedDiff? diff = null;
		if (selected is not null && displayFailure is null)
		{
			try
			{ diff = await LoadDiffAsync(selected.Entry, local!.BaseCommit, cancellationToken).ConfigureAwait(false); }
			catch (Exception exception) when (exception is IOException or InvalidOperationException or UnauthorizedAccessException)
			{ displayFailure = exception.Message; }
		}
		Task reload = Task.CompletedTask;
		Task history = Task.CompletedTask;
		await PublishSourceAsync(() =>
		{
			if (_disposed || targetEpoch != _targetEpoch)
			{ return; }
			foreach (var (review, version) in results)
			{
				if (results.FirstOrDefault(item => item.Review.Scope == GitReviewScope.Uncommitted).Review is { } live && review.Branch != live.Branch)
				{ RequestRefresh(); continue; }
				if (review.Scope != GitReviewScope.Uncommitted && TargetFor(review.Branch) != target)
				{ RequestRefresh(); continue; }
				UpdateReviewFacts(review, version);
			}
			foreach (var (failedScope, version, error) in errors)
			{ ApplyReviewError(failedScope, version, error); }
			if (IsScopeHistoryVisible && ScopeCommits.Count == 0)
			{ history = LoadMoreScopeCommitsAsync(cancellationToken); }
			if (!displayLocal || sourceVersion != _sourceVersion || SelectedCommit is not null || IsSourceLoading || Volatile.Read(ref _interactionHolds) > 0)
			{ return; }
			if (displayFailure is not null)
			{ SourceError = "Stale · " + displayFailure; RaiseSource(); return; }
			if (kind != ReviewSourceKind.FixedLocalBase && errors.FirstOrDefault(item => item.Scope == effectiveScope).Error is { } failure)
			{ SourceError = "Stale · " + failure; RaiseSource(); return; }
			if (kind != ReviewSourceKind.FixedLocalBase && displayed is not null && TargetFor(displayed.Branch) != target)
			{ return; }
			if (kind != ReviewSourceKind.FixedLocalBase && displayed is not null && displayed.Branch != Branch)
			{ RequestRefresh(); return; }
			var picked = selectionVersion != Volatile.Read(ref _selectionVersion);
			var changedBase = HeadCommit != local?.BaseCommit;
			if (changedBase)
			{ Interlocked.Increment(ref _sourceVersion); }
			var latestPath = _selectedPath;
			if (picked)
			{ selected = Reselect(_files, files, latestPath); }
			_displayedReview = kind == ReviewSourceKind.FixedLocalBase ? null
				: localOnly ? _scopeSummaries.GetValueOrDefault(scope) : displayed;
			ApplyLocalSource(local, resetSelection: false);
			SelectedFile = selected;
			_selectedPath = selected?.Path;
			SourceError = null;
			Raise(nameof(SelectedFile));
			RaiseSource();
			if (!picked)
			{
				Interlocked.Increment(ref _selectionVersion);
				ApplyDiff(diff);
			}
			else if (changedBase)
			{
				Interlocked.Increment(ref _selectionVersion);
				ApplyDiff(null);
				if (selected is not null)
				{ reload = SelectFileAsync(selected, cancellationToken); }
			}
		}, cancellationToken).ConfigureAwait(false);
		await reload.ConfigureAwait(false);
		await history.ConfigureAwait(false);
	}
}