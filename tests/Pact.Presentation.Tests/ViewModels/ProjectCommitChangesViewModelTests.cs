using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectCommitChangesViewModelTests
{
	private readonly ManualTimeProvider _time = new();
	private readonly Repository _repository = new();

	[Test]
	public async Task Failed_remark_navigation_cannot_accept_a_different_base_with_the_same_target()
	{
		var head = _repository.AddCommit('a', "same", "captured\n");
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.OpenRemarkSourceAsync(head.Id, "same", new string('c', 40), CancellationToken.None);
		TaskCompletionSource<CommitChanges> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
		failure.SetException(new IOException("comparison read failed"));
		_repository.NextCommitRead = failure;
		await Should.ThrowAsync<IOException>(() => changes.OpenRemarkSourceAsync(head.Id, "same", head.BaseCommit, CancellationToken.None));
		changes.ShownDiff.ShouldNotBeNull().HeadCommit.ShouldBe(new string('c', 40));
	}

	[Test]
	public async Task A_branch_editor_retains_its_comparison_until_closed_while_summary_tracks_new_head()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "new-file", "second\n");
		_repository.BranchState = Branch(first, new string('c', 40));
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCurrentBranchAsync(CancellationToken.None);
		using var editor = changes.RetainCurrentComparison();
		_repository.BranchState = new("feature", "origin/feature", new(new string('d', 40), second.Id,
			[new("new-file", null, GitChangeKind.Added), new("other-file", null, GitChangeKind.Added)], 9, 0), null);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.CurrentBranchFileCount.ShouldBe(2);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(first.Id);
		editor.Dispose();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
	}

	[Test]
	public async Task A_pending_branch_diff_cannot_overwrite_a_newer_head_or_source()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "same", "second\n");
		_repository.BranchState = Branch(first, new string('c', 40));
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		TaskCompletionSource<FileDiff> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextDiffRead = gate;
		var selecting = changes.SelectCurrentBranchAsync(CancellationToken.None);
		_repository.BranchState = Branch(second, new string('d', 40));
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
		gate.SetResult(FileDiff.AllAdded("late first\n"));
		await selecting;
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
		changes.DiffRows.ShouldNotContain(row => row.Text == "late first");
		await changes.SelectCommitAsync(first, CancellationToken.None);
		changes.IsCurrentBranch.ShouldBeFalse();
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(first.Id);
	}

	[Test]
	public async Task Late_branch_metadata_cannot_replace_an_explicit_commit_selection()
	{
		var head = _repository.AddCommit('a', "same", "first\n");
		using var changes = Create();
		TaskCompletionSource<GitBranchChanges> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextBranchRead = gate;
		var selecting = changes.SelectCurrentBranchAsync(CancellationToken.None);
		await changes.SelectCommitAsync(head, CancellationToken.None);
		gate.SetResult(Branch(head, new string('c', 40)));
		await selecting;
		changes.SelectedCommit.ShouldBeSameAs(head);
		changes.IsCurrentBranch.ShouldBeFalse();
	}

	[Test]
	public async Task Current_branch_uses_tracking_base_and_committed_text_while_working_summary_stays_independent()
	{
		var head = _repository.AddCommit('a', "same.md", "committed branch\n");
		_repository.BranchState = Branch(head, new string('c', 40));
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCurrentBranchAsync(CancellationToken.None);
		changes.IsCurrentBranch.ShouldBeTrue();
		changes.IsWorkingTree.ShouldBeFalse();
		changes.HeadCommit.ShouldBe(new string('c', 40));
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(head.Id);
		changes.DiffRows.ShouldContain(row => row.Text == "committed branch");
		changes.CurrentBranchFileCount.ShouldBe(1);
		changes.WorkingTreeFileCount.ShouldBe(1);
		changes.TrackingUpstream.ShouldBe("origin/feature");
	}

	[Test]
	public async Task Branch_refresh_updates_its_rows_and_both_summaries_without_replacing_a_selected_commit()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "same", "second\n");
		_repository.BranchState = Branch(first, new string('c', 40));
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCurrentBranchAsync(CancellationToken.None);
		_repository.BranchState = Branch(second, new string('d', 40));
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
		changes.HeadCommit.ShouldBe(new string('d', 40));
		await changes.SelectCommitAsync(first, CancellationToken.None);
		_repository.BranchState = new("feature", "origin/feature", new(second.Id, second.Id, [], 0, 0), null);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.CurrentBranchFileCount.ShouldBe(0);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(first.Id);
	}

	[Test]
	public async Task Captured_branch_remark_reopens_its_exact_upstream_base_even_after_refs_move()
	{
		var head = _repository.AddCommit('a', "same", "captured\n");
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.OpenRemarkSourceAsync(head.Id, "same", new string('c', 40), CancellationToken.None);
		changes.HeadCommit.ShouldBe(new string('c', 40));
		changes.ShownDiff.ShouldNotBeNull().HeadCommit.ShouldBe(new string('c', 40));
	}

	[Test]
	public async Task Missing_upstream_is_not_reported_as_a_clean_branch_or_working_tree()
	{
		_repository.BranchState = new("feature", null, null, "No tracking upstream is configured.");
		using var changes = Create();
		await changes.SelectCurrentBranchAsync(CancellationToken.None);
		changes.IsCurrentBranch.ShouldBeTrue();
		changes.IsWorkingTree.ShouldBeFalse();
		changes.SourceDescription.ShouldContain("upstream");
		changes.CurrentBranchEntrySubtitle.ShouldContain("upstream");
	}

	private GitBranchChanges Branch(GitCommitInfo head, string baseline) =>
		new("feature", "origin/feature", new(baseline, head.Id, _repository.Commits[head.Id].Files, 2, 1), null);

	[Test]
	public async Task Picker_pages_history_and_resolves_a_ref_to_immutable_identity()
	{
		for (var index = 0; index < 51; index++)
		{
			var id = index.ToString("x40", System.Globalization.CultureInfo.InvariantCulture);
			GitCommitInfo commit = new(id, [], "Author", DateTimeOffset.MinValue, $"Commit {index}");
			_repository.Commits.Add(id, new(commit, [], 0, 0));
		}
		using var changes = Create();
		using var picker = changes.CreateCommitPicker();
		await picker.LoadMoreAsync(CancellationToken.None);
		picker.Commits.Count.ShouldBe(50);
		picker.HasMore.ShouldBeTrue();
		await picker.LoadMoreAsync(CancellationToken.None);
		picker.Commits.Count.ShouldBe(51);
		picker.HasMore.ShouldBeFalse();
		picker.Revision = "HEAD";
		var accepted = await picker.AcceptAsync(CancellationToken.None);
		accepted.ShouldBeSameAs(picker.Commits[0]);
		accepted!.Id.Length.ShouldBe(40);
	}

	[Test]
	public async Task Invalid_picker_revision_keeps_history_and_selection()
	{
		var commit = _repository.AddCommit('a', "file", "text");
		using var changes = Create();
		using var picker = changes.CreateCommitPicker();
		await picker.LoadMoreAsync(CancellationToken.None);
		picker.SelectedCommit = commit;
		picker.Revision = "missing";
		(await picker.AcceptAsync(CancellationToken.None)).ShouldBeNull();
		picker.Error.ShouldNotBeNull().ShouldContain("missing");
		picker.Commits.ShouldHaveSingleItem().ShouldBeSameAs(commit);
		picker.SelectedCommit.ShouldBeSameAs(commit);
	}

	[Test]
	public async Task Closed_picker_cannot_publish_a_pending_history_page()
	{
		var commit = _repository.AddCommit('a', "file", "text");
		TaskCompletionSource<IReadOnlyList<GitCommitInfo>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextHistoryRead = gate;
		using var changes = Create();
		using var picker = changes.CreateCommitPicker();
		var pending = picker.LoadMoreAsync(CancellationToken.None);
		picker.Dispose();
		gate.SetResult([commit]);
		await pending;
		picker.Commits.ShouldBeEmpty();
	}

	[Test]
	public async Task Missing_historical_source_keeps_display_and_draft_navigation_failure_visible()
	{
		var commit = _repository.AddCommit('a', "file", "text");
		using var changes = Create();
		await changes.SelectCommitAsync(commit, CancellationToken.None);
		await Should.ThrowAsync<IOException>(() => changes.OpenRemarkSourceAsync(new string('b', 40), "file", CancellationToken.None));
		changes.SelectedCommit.ShouldBeSameAs(commit);
		changes.SourceError.ShouldNotBeNull().ShouldContain("no longer available");
	}

	[Test]
	public async Task Pending_remark_navigation_cannot_replace_a_later_source_selection()
	{
		var first = _repository.AddCommit('a', "file", "first");
		var second = _repository.AddCommit('b', "file", "second");
		using var changes = Create();
		TaskCompletionSource<GitCommitInfo?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextResolutionRead = gate;
		var opening = changes.OpenRemarkSourceAsync(first.Id, "file", CancellationToken.None);
		await changes.SelectCommitAsync(second, CancellationToken.None);
		gate.SetResult(first);
		await Should.ThrowAsync<OperationCanceledException>(() => opening);
		changes.SelectedCommit.ShouldBeSameAs(second);
	}

	[Test]
	public async Task Later_remark_navigation_wins_when_the_earlier_resolution_completes_first()
	{
		var first = _repository.AddCommit('a', "file", "first");
		var second = _repository.AddCommit('b', "file", "second");
		using var changes = Create();
		TaskCompletionSource<GitCommitInfo?> firstGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<GitCommitInfo?> secondGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextResolutionRead = firstGate;
		var earlier = changes.OpenRemarkSourceAsync(first.Id, "file", CancellationToken.None);
		_repository.NextResolutionRead = secondGate;
		var later = changes.OpenRemarkSourceAsync(second.Id, "file", CancellationToken.None);
		firstGate.SetResult(first);
		await Should.ThrowAsync<OperationCanceledException>(() => earlier);
		secondGate.SetResult(second);
		await later;
		changes.SelectedCommit.ShouldBeSameAs(second);
	}

	[Test]
	public async Task Explicit_working_tree_selection_cancels_pending_remark_navigation_even_when_already_selected()
	{
		var commit = _repository.AddCommit('a', "file", "first");
		using var changes = Create();
		await changes.SelectCommitAsync(null, CancellationToken.None);
		TaskCompletionSource<GitCommitInfo?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextResolutionRead = gate;
		var opening = changes.OpenRemarkSourceAsync(commit.Id, "file", CancellationToken.None);
		await changes.SelectCommitAsync(null, CancellationToken.None);
		gate.SetResult(commit);
		await Should.ThrowAsync<OperationCanceledException>(() => opening);
		changes.IsWorkingTree.ShouldBeTrue();
	}

	[Test]
	public async Task A_pending_historical_file_cannot_publish_after_a_new_source()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "same", "second\n");
		TaskCompletionSource<FileDiff> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextDiffRead = blocked;
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		var earlier = changes.SelectCommitAsync(first, CancellationToken.None);
		await changes.SelectCommitAsync(second, CancellationToken.None);
		blocked.SetResult(FileDiff.AllAdded("late first\n"));
		await earlier;
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
		changes.DiffRows.ShouldNotContain(row => row.Text == "late first");
	}

	[Test]
	public async Task A_late_working_tree_read_cannot_replace_a_newer_return_to_working_tree()
	{
		var commit = _repository.AddCommit('a', "history", "historical\n");
		TaskCompletionSource<WorkingTreeChanges?> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource oldApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextWorkingRead = blocked;
		var oldReleased = false;
		using ProjectChangesViewModel changes = new("C:/repo", _repository, _time, action =>
		{
			action();
			if (Volatile.Read(ref oldReleased))
			{ oldApplied.TrySetResult(); }
		});
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCommitAsync(commit, CancellationToken.None);
		_repository.Working = Working("latest-head", "latest");
		await changes.SelectCommitAsync(null, CancellationToken.None);
		Volatile.Write(ref oldReleased, true);
		blocked.SetResult(Working("stale-head", "stale"));
		await oldApplied.Task.WaitAsync(TimeSpan.FromSeconds(10));
		changes.WorkingTreeHeadCommit.ShouldBe("latest-head");
		changes.HeadCommit.ShouldBe("latest-head");
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("latest");
	}

	[Test]
	public async Task A_source_load_finishing_after_disposal_cannot_publish()
	{
		var commit = _repository.AddCommit('a', "same", "first\n");
		TaskCompletionSource<CommitChanges> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextCommitRead = blocked;
		using var changes = Create();
		var loading = changes.SelectCommitAsync(commit, CancellationToken.None);
		changes.Dispose();
		blocked.SetResult(_repository.Commits[commit.Id]);
		await Should.ThrowAsync<OperationCanceledException>(() => loading);
		changes.SelectedCommit.ShouldBeNull();
		changes.ShownDiff.ShouldBeNull();
	}

	[Test]
	public async Task A_source_read_failure_preserves_the_preceding_valid_display()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "same", "second\n");
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCommitAsync(first, CancellationToken.None);
		TaskCompletionSource<CommitChanges> failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
		failure.SetException(new IOException("read failed"));
		_repository.NextCommitRead = failure;
		await changes.SelectCommitAsync(second, CancellationToken.None);
		changes.SelectedCommit.ShouldBeSameAs(first);
		changes.SourceError.ShouldBe("read failed");
		changes.IsSourceLoading.ShouldBeFalse();
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(first.Id);
		changes.DiffRows.ShouldContain(row => row.Text == "first");
	}

	[Test]
	public async Task Commit_selection_reads_historical_text_and_selects_first_file()
	{
		var commit = _repository.AddCommit('a', "readme.md", "# historical\n");
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		changes.Mode = DiffViewMode.Rendered;
		await changes.SelectCommitAsync(commit, CancellationToken.None);
		changes.SelectedCommit.ShouldBeSameAs(commit);
		changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe("readme.md");
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(commit.Id);
		changes.ShownDiff!.NewLines.ShouldNotBeNull().ShouldContain("# historical");
		changes.EffectiveMode.ShouldBe(DiffViewMode.Rendered);
	}

	[Test]
	public async Task Working_tree_refresh_does_not_replace_commit_rows()
	{
		var commit = _repository.AddCommit('a', "historic", "committed\n");
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCommitAsync(commit, CancellationToken.None);
		_repository.Working = Working("next-head", "live-one", "live-two");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.EntryTitle.ShouldBe("Working tree · 2");
		changes.WorkingTreeHeadCommit.ShouldBe("next-head");
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("historic");
		changes.HeadCommit.ShouldBe(commit.BaseCommit);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(commit.Id);
		changes.DiffRows.ShouldContain(row => row.Text == "committed");
	}

	[Test]
	public async Task Later_source_selection_wins_over_a_pending_commit_read()
	{
		var first = _repository.AddCommit('a', "same", "first\n");
		var second = _repository.AddCommit('b', "same", "second\n");
		TaskCompletionSource<CommitChanges> blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_repository.NextCommitRead = blocked;
		using var changes = Create();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		var earlier = changes.SelectCommitAsync(first, CancellationToken.None);
		await changes.SelectCommitAsync(second, CancellationToken.None);
		blocked.SetResult(_repository.Commits[first.Id]);
		await earlier;
		changes.SelectedCommit.ShouldBeSameAs(second);
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBe(second.Id);
		changes.DiffRows.ShouldContain(row => row.Text == "second");
		changes.DiffRows.ShouldNotContain(row => row.Text == "first");
	}

	[Test]
	public async Task Returning_to_working_tree_restores_live_changes()
	{
		var commit = _repository.AddCommit('a', "history", "historical\n");
		using var changes = Create();
		using var observer = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectCommitAsync(commit, CancellationToken.None);
		await changes.SelectCommitAsync(null, CancellationToken.None);
		changes.IsWorkingTree.ShouldBeTrue();
		changes.Files.ShouldHaveSingleItem().Path.ShouldBe("working");
		changes.ShownDiff.ShouldNotBeNull().TargetCommit.ShouldBeNull();
		changes.DiffRows.ShouldContain(row => row.Text == "working content");
	}

	private ProjectChangesViewModel Create() => new("C:/repo", _repository, _time, a => a());
	private static WorkingTreeChanges Working(string head, params string[] paths) =>
		new(head, "main", paths.Select(path => new GitFileEntry(path, null, GitChangeKind.Modified)).ToArray(), 3, 1);

	private sealed class Repository : IGitWorkingTreeReader, IGitCommitReader, IGitComparisonReader
	{
		public GitBranchChanges BranchState { get; set; } = new("main", null, null, "No tracking upstream is configured.");
		public TaskCompletionSource<GitBranchChanges>? NextBranchRead { get; set; }
		public Task<GitBranchChanges> ReadBranchChangesAsync(string root, CancellationToken cancellationToken)
		{
			var gate = NextBranchRead;
			NextBranchRead = null;
			return gate?.Task ?? Task.FromResult(BranchState);
		}
		public Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken) =>
			Task.FromResult(new GitRevisionChanges(baseCommit, targetCommit, Commits[targetCommit].Files, 2, 1));
		public Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken) =>
			ReadCommitDiffAsync(root, entry, Commits[targetCommit].Commit, cancellationToken);
		public WorkingTreeChanges Working { get; set; } = ProjectCommitChangesViewModelTests.Working("head", "working");
		public Dictionary<string, CommitChanges> Commits { get; } = [];
		public Dictionary<string, string> Texts { get; } = [];
		public TaskCompletionSource<CommitChanges>? NextCommitRead { get; set; }
		public TaskCompletionSource<FileDiff>? NextDiffRead { get; set; }
		public TaskCompletionSource<WorkingTreeChanges?>? NextWorkingRead { get; set; }
		public TaskCompletionSource<IReadOnlyList<GitCommitInfo>>? NextHistoryRead { get; set; }
		public TaskCompletionSource<GitCommitInfo?>? NextResolutionRead { get; set; }
		public GitCommitInfo AddCommit(char id, string path, string text)
		{
			GitCommitInfo commit = new(new string(id, 40), [new string('f', 40)], "author", DateTimeOffset.MinValue, "subject");
			Commits.Add(commit.Id, new(commit, [new(path, null, GitChangeKind.Added)], 1, 0));
			Texts.Add(commit.Id + ":" + path, text);
			return commit;
		}
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken)
		{
			var gate = NextWorkingRead;
			NextWorkingRead = null;
			return gate?.Task ?? Task.FromResult<WorkingTreeChanges?>(Working);
		}
		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) => Task.FromResult(FileDiff.AllAdded("working content\n"));
		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) => Task.FromResult<string?>("working content\n");
		public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken) => NextHistoryRead?.Task
			?? Task.FromResult<IReadOnlyList<GitCommitInfo>>(Commits.Values.Skip(skip).Take(count).Select(value => value.Commit).ToArray());
		public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken) =>
			NextResolutionRead?.Task ?? Task.FromResult(revision == "HEAD" ? Commits.Values.First().Commit : Commits.GetValueOrDefault(revision)?.Commit);
		public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken)
		{
			var gate = NextCommitRead;
			NextCommitRead = null;
			return gate?.Task ?? Task.FromResult(Commits[commit.Id]);
		}
		public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken)
		{
			var gate = NextDiffRead;
			NextDiffRead = null;
			return gate?.Task ?? Task.FromResult(FileDiff.AllAdded(Texts[commit.Id + ":" + entry.Path]));
		}
		public Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken) => Task.FromResult<string?>(Texts[commitId + ":" + path]);
	}
}