using Pact.Core.Git;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectReviewScopesViewModelTests
{
	[Test]
	public async Task Reopening_an_already_shown_local_remark_preserves_its_diff_and_adopts_the_fixed_base()
	{
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		var shown = changes.ShownDiff!;
		await changes.OpenRemarkSourceAsync(null, shown.Path, shown.HeadCommit, CancellationToken.None);
		changes.SourceKind.ShouldBe(ReviewSourceKind.FixedLocalBase);
		changes.ShownDiff.ShouldBeSameAs(shown);
	}
	[TestCase(false)]
	[TestCase(true)]
	public async Task Uncommitted_selection_accepts_the_current_branch_before_the_next_observation_tick(bool innerFilter)
	{
		using var changes = Create();
		await changes.SelectReviewScopeAsync(GitReviewScope.Uncommitted, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		_reader.BranchName = "other";
		if (innerFilter)
		{ await changes.SelectReviewFilterAsync(null, true, CancellationToken.None); }
		else
		{ await changes.SelectReviewScopeAsync(GitReviewScope.Uncommitted, CancellationToken.None); }
		changes.Branch.ShouldBe("other");
		changes.IsWorkingTree.ShouldBeTrue();
		changes.SourceError.ShouldBeNull();
	}
	[Test]
	public async Task Explicit_scope_rejects_a_result_computed_after_the_live_branch_switched()
	{
		using var changes = Create();
		await changes.SelectReviewScopeAsync(GitReviewScope.Uncommitted, CancellationToken.None);
		await changes.SelectTargetBranchAsync("refs/heads/release", CancellationToken.None);
		TaskCompletionSource<GitReviewChanges> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_reader.NextScope = gate;
		var pending = changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		_reader.BranchName = "other";
		gate.SetResult(_reader.Scope(GitReviewScope.Unmerged));
		await pending;
		changes.SelectedReviewScope.ShouldBe(GitReviewScope.Uncommitted);
		changes.SourceError.ShouldNotBeNull().ShouldContain("branch changed");
	}
	[Test]
	public async Task Sending_an_old_side_scope_remark_uses_its_captured_base_instead_of_head()
	{
		using var folder = TemporaryDirectory.Create();
		using ProjectChangesViewModel changes = new(folder.Path, _reader, _time, action => action());
		using var observer = changes.Observe();
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		using RemarkBook book = new(folder.Path, new RemarkFileStore(folder.Path), _time);
		string? prompt = null;
		using ChangesRemarksViewModel remarks = new(changes, book, () => [new("s1", "Agent")], (_, text, _) =>
		{
			prompt = text;
			return Task.FromResult(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true));
		}, action => action());
		await book.AddAsync(new("a", RemarkSide.Old, 1, "historical", [], [], _reader.Base, null), null, "why removed?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		prompt.ShouldNotBeNull().ShouldNotContain("outdated");
		prompt.ShouldContain("of base aaaaaaa");
		prompt.ShouldNotContain("of HEAD");
	}
	private readonly ManualTimeProvider _time = new();
	private readonly ReviewRepository _reader = new();
	private ProjectChangesViewModel Create() => new("C:/repo", _reader, _time, action => action());

	[TestCase(false)]
	[TestCase(true)]
	public async Task Display_read_failure_keeps_prior_diff_stale_but_publishes_live_scope_summaries(bool fixedBase)
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		if (fixedBase)
		{ await changes.OpenRemarkSourceAsync(null, "a", _reader.Base, CancellationToken.None); }
		var shown = changes.ShownDiff;
		_reader.FailFixedRead = fixedBase;
		_reader.FailDiff = !fixedBase;
		_reader.UnmergedFiles = 8;
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.UnmergedEntryTitle.ShouldContain("8");
		changes.SourceError.ShouldNotBeNull().ShouldStartWith("Stale");
		changes.ShownDiff.ShouldBeSameAs(shown);
	}

	[Test]
	public async Task New_side_remark_stays_visible_when_rename_pairing_changes()
	{
		using var folder = TemporaryDirectory.Create();
		_reader.OriginalPath = "before";
		using ProjectChangesViewModel changes = new(folder.Path, _reader, _time, action => action());
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		using RemarkBook book = new(folder.Path, new RemarkFileStore(folder.Path), _time);
		using ChangesRemarksViewModel remarks = new(changes, book, () => [], (_, _, _) => throw new InvalidOperationException(), action => action());
		var shown = changes.ShownDiff!;
		var row = shown.Rows.ToList().FindIndex(item => item.Kind == DiffRowKind.Added);
		await remarks.AddRemarkAsync(shown, row, "keep this", CancellationToken.None);
		_reader.OriginalPath = null;
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		await WaitUntilAsync(() => remarks.ThreadsByRow is { } rows && rows.ForDiff == changes.ShownDiff && rows.ByRow.Count > 0);
		remarks.ThreadsByRow!.ByRow.Values.SelectMany(items => items).ShouldHaveSingleItem().Id.ShouldBe("C1");
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task Selecting_a_merge_target_does_not_leave_a_frozen_source(bool fixedBase)
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		if (fixedBase)
		{ await changes.OpenRemarkSourceAsync(null, "a", _reader.Base, CancellationToken.None); }
		else
		{ await changes.SelectCommitAsync(ReviewRepository.Commit, CancellationToken.None); }
		var shown = changes.ShownDiff;
		await changes.SelectTargetBranchAsync("refs/heads/release", CancellationToken.None);
		changes.SourceKind.ShouldBe(fixedBase ? ReviewSourceKind.FixedLocalBase : ReviewSourceKind.HistoricalCommit);
		changes.ShownDiff.ShouldBeSameAs(shown);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
		while (!condition() && DateTimeOffset.UtcNow < deadline)
		{ await Task.Yield(); }
		condition().ShouldBeTrue();
	}

	[Test]
	public async Task Same_local_text_on_another_base_does_not_reuse_row_editor_or_file_count()
	{
		using var folder = TemporaryDirectory.Create();
		using ProjectChangesViewModel changes = new(folder.Path, _reader, _time, action => action());
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		using RemarkBook book = new(folder.Path, new RemarkFileStore(folder.Path), _time);
		using ChangesRemarksViewModel remarks = new(changes, book, () => [], (_, _, _) => throw new InvalidOperationException(), action => action());
		var shown = changes.ShownDiff.ShouldNotBeNull();
		var row = shown.Rows.ToList().FindIndex(item => item.Kind == DiffRowKind.Added);
		await remarks.AddRemarkAsync(shown, row, "later", CancellationToken.None);
		changes.Files[0].OpenRemarks.ShouldBe(1);
		var capturedBase = _reader.Base;
		_reader.Base = new string('d', 40);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		changes.ShownDiff.ShouldNotBeNull().NewLines.ShouldBe(shown.NewLines);
		remarks.CanRemarkOn(shown, row).ShouldBeFalse();
		changes.Files[0].OpenRemarks.ShouldBe(0);
		remarks.OpenCount.ShouldBe(1);
		await changes.OpenRemarkSourceAsync(null, "a", capturedBase, CancellationToken.None);
		changes.Files[0].OpenRemarks.ShouldBe(1);
	}

	[Test]
	public async Task Manual_target_is_retained_per_live_branch()
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		await changes.SelectTargetBranchAsync("refs/heads/release", CancellationToken.None);
		changes.SelectedTargetBranch.ShouldBe("refs/heads/release");
		_reader.BranchName = "other";
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedTargetBranch.ShouldBeNull();
		await changes.SelectTargetBranchAsync("refs/heads/main", CancellationToken.None);
		_reader.BranchName = "feature";
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedTargetBranch.ShouldBe("refs/heads/release");
	}

	[Test]
	public async Task Live_summaries_refresh_while_a_selected_commit_remains_immutable()
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		await changes.SelectReviewFilterAsync(ReviewRepository.Commit, false, CancellationToken.None);
		var shown = changes.ShownDiff;
		_reader.LocalText = "changed\n";
		_reader.UnmergedFiles = 7;
		_reader.ObservedHead = new string('d', 40);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.UnmergedEntryTitle.ShouldContain("7");
		changes.ShownDiff.ShouldBeSameAs(shown);
		changes.ShownDiff.ShouldNotBeNull().NewLines.ShouldBe(["historical"]);
		changes.ScopeCommits.ShouldHaveSingleItem().Id.ShouldBe(_reader.ObservedHead);
	}

	[Test]
	public async Task Pending_local_diff_cannot_replace_a_newer_historical_filter()
	{
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		TaskCompletionSource<FileDiff> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_reader.NextDiff = gate;
		var pending = changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		await changes.SelectReviewFilterAsync(ReviewRepository.Commit, false, CancellationToken.None);
		gate.SetResult(FileDiff.AllAdded("stale local\n"));
		await pending;
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(ReviewRepository.Commit.Id);
		changes.ShownDiff.NewLines.ShouldBe(["historical"]);
	}

	[Test]
	public async Task Aggregate_scope_includes_local_text_and_keeps_three_summary_counts_independent_of_commit_filter()
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		changes.SourceKind.ShouldBe(ReviewSourceKind.Scope);
		changes.SelectedReviewScope.ShouldBe(GitReviewScope.Unmerged);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBeNull();
		changes.ShownDiff.HeadCommit.ShouldBe(new string('a', 40));
		changes.ShownDiff.NewLines.ShouldBe(["local"]);
		changes.UnmergedEntryTitle.ShouldContain("3");
		changes.UnpushedEntryTitle.ShouldContain("2");
		changes.EntryTitle.ShouldContain("1");
		await changes.LoadMoreScopeCommitsAsync(CancellationToken.None);
		await changes.SelectReviewFilterAsync(changes.ScopeCommits.Single(), false, CancellationToken.None);
		changes.SelectedReviewScope.ShouldBe(GitReviewScope.Unmerged);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(ReviewRepository.Commit.Id);
		changes.ShownDiff.NewLines.ShouldBe(["historical"]);
		changes.UnmergedEntryTitle.ShouldContain("3");
		await changes.SelectReviewFilterAsync(null, true, CancellationToken.None);
		changes.HeadCommit.ShouldBe(ReviewRepository.Commit.Id);
		changes.SelectedReviewScope.ShouldBe(GitReviewScope.Unmerged);
		await changes.SelectReviewFilterAsync(null, false, CancellationToken.None);
		changes.ShownDiff.ShouldNotBeNull().HeadCommit.ShouldBe(new string('a', 40));
	}

	[Test]
	public async Task Local_remark_reopens_a_fixed_base_and_refresh_does_not_substitute_the_new_scope_base()
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		var captured = changes.HeadCommit;
		_reader.Base = new string('d', 40);
		await changes.OpenRemarkSourceAsync(null, "a", captured, CancellationToken.None);
		changes.SourceKind.ShouldBe(ReviewSourceKind.FixedLocalBase);
		changes.SourceTitle.ShouldContain("Fixed base");
		_reader.LocalText = "changed local\n";
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeNull().HeadCommit.ShouldBe(captured);
		changes.ShownDiff.NewLines.ShouldBe(["changed local"]);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		changes.SourceKind.ShouldBe(ReviewSourceKind.Scope);
		changes.HeadCommit.ShouldBe(_reader.Base);
	}

	[Test]
	public async Task Pending_scope_or_target_metadata_cannot_override_a_newer_explicit_choice()
	{
		using var changes = Create();
		await changes.SelectReviewScopeAsync(GitReviewScope.Uncommitted, CancellationToken.None);
		TaskCompletionSource<GitReviewChanges> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_reader.NextScope = gate;
		var pending = changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unpushed, CancellationToken.None);
		gate.SetResult(_reader.Scope(GitReviewScope.Unmerged));
		await pending;
		changes.SelectedReviewScope.ShouldBe(GitReviewScope.Unpushed);
		changes.SourceKind.ShouldBe(ReviewSourceKind.Scope);
	}

	[Test]
	public async Task Editing_a_local_scope_keeps_the_captured_diff_until_release_but_updates_its_summary()
	{
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectReviewScopeAsync(GitReviewScope.Unmerged, CancellationToken.None);
		var shown = changes.ShownDiff;
		using var editor = changes.RetainCurrentComparison();
		_reader.LocalText = "new local\n";
		_reader.UnmergedFiles = 4;
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldBeSameAs(shown);
		changes.UnmergedEntryTitle.ShouldContain("4");
		editor.Dispose();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeNull().NewLines.ShouldBe(["new local"]);
	}

	private sealed class ReviewRepository : IGitWorkingTreeReader, IGitCommitReader, IGitComparisonReader, IGitReviewReader
	{
		public static GitCommitInfo Commit { get; } = new(new string('b', 40), [new string('e', 40)], "author", DateTimeOffset.MinValue, "work");
		public string Base { get; set; } = new('a', 40);
		public string BranchName { get; set; } = "feature";
		public string ObservedHead { get; set; } = Commit.Id;
		public string? OriginalPath { get; set; }
		public bool FailFixedRead { get; set; }
		public bool FailDiff { get; set; }
		public string LocalText { get; set; } = "local\n";
		public int UnmergedFiles { get; set; } = 3;
		public TaskCompletionSource<GitReviewChanges>? NextScope { get; set; }
		public TaskCompletionSource<FileDiff>? NextDiff { get; set; }
		private GitFileEntry[] Files(int count) => Enumerable.Range(0, count).Select(i => new GitFileEntry(i == 0 ? "a" : "f" + i, i == 0 ? OriginalPath : null, GitChangeKind.Modified)).ToArray();
		public GitReviewChanges Scope(GitReviewScope scope) => new(scope, BranchName, scope == GitReviewScope.Unpushed ? "origin/feature" : "origin/main",
			new(scope == GitReviewScope.Uncommitted ? ObservedHead : Base, ObservedHead, Files(scope == GitReviewScope.Unmerged ? UnmergedFiles : scope == GitReviewScope.Unpushed ? 2 : 1), 7, 2), 1, 0, false, null);
		public Task<GitReviewChanges> ReadReviewChangesAsync(string root, GitReviewScope scope, string? targetBranch, CancellationToken ct)
		{
			var gate = NextScope;
			NextScope = null;
			return gate?.Task ?? Task.FromResult(Scope(scope));
		}
		public Task<GitLocalChanges> ReadLocalChangesAsync(string root, string? baseline, CancellationToken ct) => FailFixedRead ? throw new IOException("Base vanished") : Task.FromResult(new GitLocalChanges(baseline, ObservedHead, Files(1), 1, 1));
		public Task<FileDiff> ReadLocalDiffAsync(string root, GitFileEntry entry, string? baseline, CancellationToken ct)
		{
			if (FailDiff)
			{ throw new IOException("File changed during read"); }
			var gate = NextDiff;
			NextDiff = null;
			return gate?.Task ?? Task.FromResult(FileDiff.AllAdded(LocalText));
		}
		public Task<IReadOnlyList<string>> ReadBranchesAsync(string root, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["refs/heads/main"]);
		public Task<IReadOnlyList<GitCommitInfo>> ReadScopeCommitsAsync(string root, string? baseline, string head, int skip, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<GitCommitInfo>>(skip == 0 ? [Commit with { Id = ObservedHead }] : []);
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken ct) => Task.FromResult<WorkingTreeChanges?>(new(Commit.Id, "feature", Files(1), 1, 1));
		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? head, CancellationToken ct) => Task.FromResult(FileDiff.AllAdded(LocalText));
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken ct) => Task.FromResult<string?>(LocalText);
		public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken ct) => Task.FromResult<IReadOnlyList<GitCommitInfo>>([Commit]);
		public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken ct) => Task.FromResult<GitCommitInfo?>(revision == "HEAD" ? Commit : Commit with { Id = revision });
		public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken ct) => Task.FromResult(new CommitChanges(commit, Files(1), 1, 0));
		public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken ct) => Task.FromResult(FileDiff.AllAdded("historical\n"));
		public Task<string?> ReadCommitTextAsync(string root, string path, string commit, CancellationToken ct) => Task.FromResult<string?>("historical\n");
		public Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseline, string target, CancellationToken ct) => Task.FromResult(new GitRevisionChanges(baseline, target, Files(1), 1, 0));
		public Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseline, string target, CancellationToken ct) => Task.FromResult(FileDiff.AllAdded("historical\n"));
	}
}