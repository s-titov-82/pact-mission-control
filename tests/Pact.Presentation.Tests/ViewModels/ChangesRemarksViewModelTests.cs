using System.Collections.Concurrent;
using Pact.Core.Git;
using Pact.Core.Remarks;
using Pact.Core.Sessions;
using Pact.Infrastructure.Remarks;
using Pact.Presentation.Services.Remarks;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ChangesRemarksViewModelTests : IDisposable
{
	[Test]
	public async Task Historical_markers_and_editors_distinguish_the_same_target_with_different_comparison_bases()
	{
		var remarks = await OpenAsync();
		await _changes!.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		var target = Historical('a');
		await _changes.OpenRemarkSourceAsync(target.Id, Path1, new string('c', 40), CancellationToken.None);
		var captured = _changes.ShownDiff!;
		var thread = (await remarks.AddRemarkAsync(captured, 2, "old upstream line", CancellationToken.None)).ShouldNotBeNull();
		thread.Thread.Anchor.BaseCommit.ShouldBe(new string('c', 40));
		await _changes.OpenRemarkSourceAsync(target.Id, Path1, new string('d', 40), CancellationToken.None);
		remarks.CanRemarkOn(captured, 2).ShouldBeFalse();
		remarks.MapRow(captured, 2, _changes.ShownDiff!).ShouldBeNull();
		_changes.Files[0].OpenRemarks.ShouldBe(0);
		await thread.OpenSourceAsync(CancellationToken.None);
		_changes.ShownDiff.ShouldNotBeNull().HeadCommit.ShouldBe(new string('c', 40));
	}
	[Test]
	public async Task Last_resolve_offers_cleanup_and_no_keeps_the_list_until_explicit_cleanup()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "first", CancellationToken.None);
		await remarks.AddRemarkAsync(_changes.ShownDiff!, 3, "second", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		remarks.PendingCleanup.ShouldBeNull();
		await _book.ResolveAsync("C2", CancellationToken.None);
		remarks.PendingCleanup.ShouldNotBeNull().Threads.Count.ShouldBe(2);
		remarks.DismissCleanup();
		_book.Threads.Count.ShouldBe(2);
		await _changes.SelectCommitAsync(null, CancellationToken.None);
		remarks.PendingCleanup.ShouldBeNull();
		remarks.CanRequestCleanup.ShouldBeTrue();
		remarks.RequestCleanup();
		await remarks.ConfirmCleanupAsync(remarks.PendingCleanup.ShouldNotBeNull(), CancellationToken.None);
		_book.Threads.ShouldBeEmpty();
		remarks.CanRequestCleanup.ShouldBeFalse();
	}

	[Test]
	public async Task Last_resolve_across_all_sources_offers_one_project_cleanup_independent_of_selection()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await _changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await _changes.SelectCommitAsync(Historical('a'), CancellationToken.None);
		await remarks.AddRemarkAsync(_changes.ShownDiff!, 3, "historical", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.AgentReplyAsync("C1", "done", resolve: true, CancellationToken.None);
		remarks.PendingCleanup.ShouldBeNull();
		await _changes.SelectCommitAsync(null, CancellationToken.None);
		remarks.PendingCleanup.ShouldBeNull();
		remarks.CanRequestCleanup.ShouldBeFalse();
		await _book.AgentReplyAsync("C2", "also done", resolve: true, CancellationToken.None);
		var request = remarks.PendingCleanup.ShouldNotBeNull();
		request.Threads.Count.ShouldBe(2);
		await _changes.SelectCommitAsync(Historical('a'), CancellationToken.None);
		remarks.PendingCleanup.ShouldBeSameAs(request);
		remarks.DismissCleanup();
		await _changes.SelectCommitAsync(null, CancellationToken.None);
		remarks.PendingCleanup.ShouldBeNull();
		remarks.RequestCleanup();
		await remarks.ConfirmCleanupAsync(remarks.PendingCleanup.ShouldNotBeNull(), CancellationToken.None);
		_book.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task Loaded_closed_threads_do_not_ask_again_but_manual_cleanup_remains_available()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		_remarks!.Dispose();
		_observation!.Dispose();
		_changes.Dispose();
		_book.Dispose();
		var restored = await OpenAsync();
		restored.PendingCleanup.ShouldBeNull();
		restored.CanRequestCleanup.ShouldBeTrue();
		restored.RequestCleanup();
		restored.PendingCleanup.ShouldNotBeNull();
	}

	[Test]
	public async Task Typed_reply_prevents_cleanup_and_a_late_agent_reply_invalidates_the_offer()
	{
		var remarks = await OpenAsync();
		var thread = (await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None)).ShouldNotBeNull();
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		var request = remarks.PendingCleanup.ShouldNotBeNull();
		thread.DraftText = "unfinished reply";
		remarks.CanRequestCleanup.ShouldBeFalse();
		await remarks.ConfirmCleanupAsync(request, CancellationToken.None);
		_book.Threads.ShouldHaveSingleItem();
		thread.DraftText.ShouldBe("unfinished reply");
		thread.DraftText = string.Empty;
		remarks.RequestCleanup();
		request = remarks.PendingCleanup.ShouldNotBeNull();
		await _book.AgentReplyAsync("C1", "late closed answer", resolve: true, CancellationToken.None);
		await remarks.ConfirmCleanupAsync(request, CancellationToken.None);
		_book.Threads.ShouldHaveSingleItem().Messages.ShouldContain(message => message.Text == "late closed answer");
	}

	[Test]
	public async Task Queued_book_notifications_preserve_completion_and_reopened_activity_can_offer_again()
	{
		ConcurrentQueue<Action> queue = new();
		var remarks = await OpenAsync(post: queue.Enqueue);
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		while (queue.TryDequeue(out var action))
		{ action(); }
		remarks.PendingCleanup.ShouldNotBeNull();
		remarks.DismissCleanup();
		await _book.ReopenAsync("C1", CancellationToken.None);
		await _book.ResolveAsync("C1", CancellationToken.None);
		while (queue.TryDequeue(out var action))
		{ action(); }
		remarks.PendingCleanup.ShouldNotBeNull();
	}

	[Test]
	public async Task Projection_of_newer_state_cannot_replay_a_cleanup_question_after_no()
	{
		ConcurrentQueue<Action> queue = new();
		var remarks = await OpenAsync(post: queue.Enqueue);
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None);
		while (queue.TryDequeue(out var initialize))
		{ initialize(); }
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		while (remarks.OpenCount != 0 && queue.TryDequeue(out var first))
		{ first(); }
		remarks.OpenCount.ShouldBe(0);
		remarks.PendingCleanup.ShouldBeNull();
		var offers = 0;
		while (queue.TryDequeue(out var action))
		{
			action();
			if (remarks.PendingCleanup is not null)
			{ offers++; remarks.DismissCleanup(); }
		}
		offers.ShouldBe(1);
		remarks.PendingCleanup.ShouldBeNull();
		remarks.CanRequestCleanup.ShouldBeTrue();
	}

	[Test]
	public async Task Failed_cleanup_preserves_records_and_can_retry_while_editors_are_unblocked()
	{
		CleanupStore store = new(new RemarkFileStore(_root.Path));
		var remarks = await OpenAsync(store: store);
		var thread = (await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None)).ShouldNotBeNull();
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		var request = remarks.PendingCleanup.ShouldNotBeNull();
		store.Fail = true;
		await remarks.ConfirmCleanupAsync(request, CancellationToken.None);
		_book.Threads.ShouldHaveSingleItem();
		remarks.PendingCleanup.ShouldBeSameAs(request);
		remarks.StatusText.ShouldNotBeNull().ShouldContain("Try again");
		thread.IsEditingEnabled.ShouldBeTrue();
		store.Fail = false;
		await remarks.ConfirmCleanupAsync(request, CancellationToken.None);
		_book.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task Confirmed_cleanup_blocks_new_typing_only_until_the_atomic_save_finishes()
	{
		CleanupStore store = new(new RemarkFileStore(_root.Path));
		var remarks = await OpenAsync(store: store);
		var thread = (await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working", CancellationToken.None)).ShouldNotBeNull();
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		store.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var cleanup = remarks.ConfirmCleanupAsync(remarks.PendingCleanup.ShouldNotBeNull(), CancellationToken.None);
		await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
		remarks.IsClearingReview.ShouldBeTrue();
		thread.IsEditingEnabled.ShouldBeFalse();
		thread.DraftText = "input while disabled";
		thread.DraftText.ShouldBeEmpty();
		_book.Threads.ShouldHaveSingleItem();
		store.Release.SetResult();
		await cleanup;
		thread.IsEditingEnabled.ShouldBeTrue();
		remarks.IsClearingReview.ShouldBeFalse();
		_book.Threads.ShouldBeEmpty();
	}

	private sealed class CleanupStore(IRemarkStore inner) : IRemarkStore
	{
		public bool Fail { get; set; }
		public TaskCompletionSource? Release { get; set; }
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public Task<RemarkFile> LoadAsync(CancellationToken cancellationToken) => inner.LoadAsync(cancellationToken);
		public async Task SaveAsync(RemarkFile file, CancellationToken cancellationToken)
		{
			if (Fail)
			{ throw new IOException("save failed"); }
			if (Release is { } gate)
			{ Started.TrySetResult(); await gate.Task.WaitAsync(cancellationToken); }
			await inner.SaveAsync(file, cancellationToken);
		}
		public Task<string> SaveSnapshotAsync(string text, CancellationToken cancellationToken) => inner.SaveSnapshotAsync(text, cancellationToken);
		public Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string id, CancellationToken cancellationToken) => inner.LoadSnapshotLinesAsync(id, cancellationToken);
	}
	[Test]
	public async Task Historical_thread_and_agent_reply_are_discoverable_and_openable_after_restart_in_working_tree()
	{
		var remarks = await OpenAsync();
		await _changes!.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		var commit = Historical('a');
		await _changes.SelectCommitAsync(commit, CancellationToken.None);
		var created = (await remarks.AddRemarkAsync(_changes.ShownDiff!, 3, "historical question", CancellationToken.None)).ShouldNotBeNull();
		await remarks.SendAsync("s1", CancellationToken.None);
		await _book!.AgentReplyAsync(created.Id, "historical answer", resolve: false, CancellationToken.None);
		_remarks!.Dispose();
		_observation!.Dispose();
		_changes.Dispose();
		_book.Dispose();
		var restored = await OpenAsync();
		await _changes!.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await WaitForRemarksAsync(restored, () => restored.OpenCount == 1 && restored.OpenThreads[0].Messages.Count == 2);
		_changes.IsWorkingTree.ShouldBeTrue();
		restored.DraftCount.ShouldBe(0);
		var thread = restored.OpenThreads.ShouldHaveSingleItem();
		thread.SourceLabel.ShouldContain(commit.ShortId);
		thread.Messages.ShouldContain(message => message.Text == "historical answer");
		thread.DraftText = "keep my unfinished reply";
		await thread.OpenSourceAsync(CancellationToken.None);
		_changes.SelectedCommit.ShouldNotBeNull().Id.ShouldBe(commit.Id);
		_changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe(Path1);
		thread.DraftText.ShouldBe("keep my unfinished reply");
		await WaitForRemarksAsync(restored, () => restored.DraftCount == 1);
	}
	[Test]
	public async Task Sending_from_any_source_includes_all_open_project_drafts_and_typed_replies()
	{
		var remarks = await OpenAsync();
		var live = (await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "working draft", CancellationToken.None)).ShouldNotBeNull();
		var commit = Historical('a');
		await _changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await _changes.SelectCommitAsync(commit, CancellationToken.None);
		var history = (await remarks.AddRemarkAsync(_changes.ShownDiff!, 3, "historical draft", CancellationToken.None)).ShouldNotBeNull();
		history.Thread.Anchor.TargetCommit.ShouldBe(commit.Id);
		history.Thread.Anchor.OriginalPath.ShouldBe("old/X.cs");
		await _changes.SelectCommitAsync(Historical('b'), CancellationToken.None);
		var otherHistory = (await remarks.AddRemarkAsync(_changes.ShownDiff!, 3, "other commit draft", CancellationToken.None)).ShouldNotBeNull();
		remarks.DraftCount.ShouldBe(3);
		await remarks.SendAsync("s1", CancellationToken.None);
		_delivered.ShouldHaveSingleItem().Text.ShouldContain(commit.Id);
		_delivered[0].Text.ShouldContain("historical draft");
		_delivered[0].Text.ShouldContain("working draft");
		_delivered[0].Text.ShouldContain("other commit draft");
		_delivered[0].Text.ShouldContain(Historical('b').Id);
		history.DraftText = "unsaved historical reply";
		live.DraftText = "unsaved working reply";
		await _changes.SelectCommitAsync(null, CancellationToken.None);
		remarks.DraftCount.ShouldBe(2);
		remarks.OpenThreads.Single(thread => thread.Id == history.Id).ShouldBeSameAs(history);
		await remarks.SendAsync("s1", CancellationToken.None);
		_delivered[1].Text.ShouldContain("unsaved working reply");
		_delivered[1].Text.ShouldContain("unsaved historical reply");
		history.DraftText.ShouldBeEmpty();
		_book!.Threads.Single(thread => thread.Id == live.Id).State.ShouldBe(RemarkThreadState.Sent);
		await _book.ResolveAsync(history.Id, CancellationToken.None);
		history.DraftText = "keep resolved reply unsent";
		otherHistory.DraftText = "send open reply";
		await remarks.SendAsync("s1", CancellationToken.None);
		_delivered[2].Text.ShouldContain("send open reply");
		_delivered[2].Text.ShouldNotContain("keep resolved reply unsent");
		history.DraftText.ShouldBe("keep resolved reply unsent");
	}

	[Test]
	public async Task Sibling_commit_editors_cannot_cross_target_identity()
	{
		var remarks = await OpenAsync();
		await _changes!.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await _changes.SelectCommitAsync(Historical('a'), CancellationToken.None);
		var from = _changes.ShownDiff!;
		await _changes.SelectCommitAsync(Historical('b'), CancellationToken.None);
		remarks.MapRow(from, 3, _changes.ShownDiff!).ShouldBeNull();
		remarks.CanRemarkOn(from, 3).ShouldBeFalse();
		(await remarks.AddRemarkAsync(from, 3, "stale editor", CancellationToken.None)).ShouldBeNull();
		_book!.Threads.ShouldBeEmpty();
	}

	private static GitCommitInfo Historical(char id) => new(new string(id, 40), [new string('f', 40)], "author", DateTimeOffset.MinValue, "history");
	private static async Task WaitForRemarksAsync(ChangesRemarksViewModel remarks, Func<bool> ready)
	{
		TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
		void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
		{ if (ready()) { signal.TrySetResult(); } }
		remarks.PropertyChanged += Changed;
		try
		{
			if (ready())
			{ return; }
			await signal.Task.WaitAsync(TimeSpan.FromSeconds(10));
		}
		finally { remarks.PropertyChanged -= Changed; }
	}
	private const string Path1 = "src/X.cs";
	private static readonly PromptDeliveryResult Confirmed = new(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true);
	private static readonly PromptDeliveryResult Unconfirmed = new(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true, SubmitAttempted: true);

	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();
	private readonly ManualTimeProvider _time = new();
	private readonly Reader _reader = new();
	private readonly List<(string Session, string Text)> _delivered = [];
	private IReadOnlyList<SessionTarget> _sessions = [new("s1", "Claude")];
	private Func<string, string, CancellationToken, Task<PromptDeliveryResult>>? _deliver;
	private ProjectChangesViewModel? _changes;
	private RemarkBook? _book;
	private ChangesRemarksViewModel? _remarks;
	private IDisposable? _observation;

	public void Dispose()
	{
		_remarks?.Dispose();
		_observation?.Dispose();
		_changes?.Dispose();
		_book?.Dispose();
		_root.Dispose();
	}

	[Test]
	public async Task A_remark_on_an_added_line_is_placed_on_its_row()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;

		var thread = await remarks.AddRemarkAsync(shown, 3, "why?", CancellationToken.None);

		thread.ShouldNotBeNull().Id.ShouldBe("C1");
		var anchor = _book!.Threads.Single().Anchor;
		(anchor.Side, anchor.Line, anchor.LineText, anchor.BaseCommit).ShouldBe((RemarkSide.New, 2, "b", "head1"));
		anchor.Before.ShouldBe(["a"]);
		anchor.After.ShouldBe(["c"]);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		remarks.ThreadsByRow!.ForDiff.ShouldBeSameAs(shown);
		remarks.ThreadsByRow.ByRow[3].Single().ShouldBeSameAs(thread);
		thread.LocationText.ShouldBe("src/X.cs:2");
	}

	[Test]
	public async Task A_remark_on_a_removed_line_is_old_side_against_head()
	{
		var remarks = await OpenAsync();

		var thread = await remarks.AddRemarkAsync(_changes!.ShownDiff!, 2, "why gone?", CancellationToken.None);

		var anchor = _book!.Threads.Single().Anchor;
		(anchor.Side, anchor.Line, anchor.LineText, anchor.BaseCommit, anchor.SnapshotId)
			.ShouldBe((RemarkSide.Old, 2, "old", "head1", null));
		anchor.Before.ShouldBe(["a"]);
		anchor.After.ShouldBe(["c"]);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(2) == true);
		thread!.LocationText.ShouldBe("src/X.cs, removed line 2");
	}

	[Test]
	public async Task Hunk_headers_take_no_remark()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;

		remarks.CanRemarkOn(shown, 0).ShouldBeFalse();
		remarks.CanRemarkOn(shown, 1).ShouldBeTrue();
		remarks.CanRemarkOn(shown, 2).ShouldBeTrue();
		(await remarks.AddRemarkAsync(shown, 0, "header?", CancellationToken.None)).ShouldBeNull();
		_book!.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task A_remark_anchors_to_the_shown_text_even_after_the_disk_moves_on()
	{
		const string repeated = "x\np\nq\nr\np\nq\nr\ny\n";
		var remarks = await OpenAsync(repeated, FileDiff.AllAdded(repeated));
		var shown = _changes!.ShownDiff!;
		const string trimmed = "x\np\nq\nr\ny\n";
		await WriteFileAsync(trimmed);
		_reader.Texts[Path1] = trimmed;
		_reader.Diffs[Path1] = FileDiff.AllAdded(trimmed);

		await remarks.AddRemarkAsync(shown, 3, "this q", CancellationToken.None);

		var thread = _book!.Threads.Single();
		thread.Anchor.Line.ShouldBe(3);
		(await _book.LoadSnapshotLinesAsync(thread.Anchor.SnapshotId!, CancellationToken.None))
			.ShouldBe(["x", "p", "q", "r", "p", "q", "r", "y"]);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		await EventuallyAsync(() => remarks.Elsewhere.Any(item => item.Id == "C1" && item.IsOutdated));
		remarks.ThreadsByRow!.ForDiff.ShouldBeSameAs(_changes.ShownDiff);
		remarks.ThreadsByRow.ByRow.ShouldBeEmpty();
	}

	// A remark started before an edit of its file anchors to the text the user saw when typing began.
	[Test]
	public async Task A_row_of_a_superseded_diff_of_the_same_file_still_takes_a_remark()
	{
		var remarks = await OpenAsync();
		var old = _changes!.ShownDiff!;
		_reader.Texts[Path1] = "a\nb\nc\nd\n";
		_reader.Diffs[Path1] = FileDiff.AllAdded("a\nb\nc\nd\n");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		_changes.ShownDiff.ShouldNotBeSameAs(old);

		remarks.CanRemarkOn(old, 1).ShouldBeTrue();
		remarks.CanRemarkOn(old with { Path = "src/Other.cs" }, 1).ShouldBeFalse();
		(await remarks.AddRemarkAsync(old, 3, "late", CancellationToken.None)).ShouldNotBeNull();

		var anchor = _book!.Threads.Single().Anchor;
		(anchor.Side, anchor.Line, anchor.LineText).ShouldBe((RemarkSide.New, 2, "b"));
		(await _book.LoadSnapshotLinesAsync(anchor.SnapshotId!, CancellationToken.None)).ShouldBe(["a", "b", "c"]);
	}

	// One screen shows several projects in turn; a file of the same name elsewhere is not this file.
	[Test]
	public async Task A_diff_from_another_project_with_the_same_path_takes_no_remark()
	{
		var remarks = await OpenAsync();
		using var otherRoot = TemporaryDirectory.Create();
		Reader otherReader = new();
		otherReader.Texts[Path1] = "a\nb\nc\n";
		otherReader.Diffs[Path1] = DefaultDiff();
		using ProjectChangesViewModel other = new(otherRoot.Path, otherReader, _time, action => action());
		using var observation = other.Observe();
		await other.SelectFileAsync(other.Files[0], CancellationToken.None);
		var foreign = other.ShownDiff!;

		remarks.CanRemarkOn(foreign, 3).ShouldBeFalse();
		(await remarks.AddRemarkAsync(foreign, 3, "wrong project", CancellationToken.None)).ShouldBeNull();
		remarks.MapRow(foreign, 3, _changes!.ShownDiff!).ShouldBeNull();
		_book!.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task A_remark_started_before_an_edit_anchors_to_the_text_seen_when_typing_began()
	{
		var remarks = await OpenAsync();
		var seen = _changes!.ShownDiff!;
		_reader.Texts[Path1] = "new\na\nb\nc\n";
		_reader.Diffs[Path1] = FileDiff.AllAdded("new\na\nb\nc\n");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		await remarks.AddRemarkAsync(seen, 3, "on b", CancellationToken.None);

		_book!.Threads.Single().Anchor.Line.ShouldBe(2);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ForDiff == _changes.ShownDiff
			&& remarks.ThreadsByRow!.ByRow.Values.SelectMany(threads => threads).Any());
		var row = remarks.ThreadsByRow!.ByRow.Single().Key;
		_changes.ShownDiff!.Rows[row].NewNumber.ShouldBe("3");
	}

	[Test]
	public async Task A_row_maps_to_the_same_line_after_lines_were_inserted_above_it()
	{
		var remarks = await OpenAsync("a\nb\nc\n", FileDiff.AllAdded("a\nb\nc\n"));
		var from = _changes!.ShownDiff!;
		_reader.Texts[Path1] = "x\ny\na\nb\nc\n";
		_reader.Diffs[Path1] = FileDiff.AllAdded("x\ny\na\nb\nc\n");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		var to = _changes.ShownDiff!;

		// Row 2 of "a b c" (after the header) is b, line 2; it is line 4 now.
		var mapped = remarks.MapRow(from, 2, to).ShouldNotBeNull();
		to.Rows[mapped].NewNumber.ShouldBe("4");
	}

	[Test]
	public async Task A_row_whose_line_changed_maps_to_nothing()
	{
		var remarks = await OpenAsync("a\nb\nc\n", FileDiff.AllAdded("a\nb\nc\n"));
		var from = _changes!.ShownDiff!;
		_reader.Texts[Path1] = "a\nB\nc\n";
		_reader.Diffs[Path1] = FileDiff.AllAdded("a\nB\nc\n");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		remarks.MapRow(from, 2, _changes.ShownDiff!).ShouldBeNull();
	}

	[Test]
	public async Task A_remark_on_a_whole_file_line_far_from_any_hunk_anchors_to_that_new_line()
	{
		var text = string.Join('\n', Enumerable.Range(1, 50).Select(index => $"line {index}")) + "\n";
		var remarks = await OpenAsync(text, ChangedSecondLine(text));
		_changes!.Mode = DiffViewMode.WholeFile;
		var shown = _changes.ShownDiff!;
		var row = shown.Rows.ToList().FindIndex(item => item.NewNumber == "40");

		await remarks.AddRemarkAsync(shown, row, "far", CancellationToken.None);

		var anchor = _book!.Threads.Single().Anchor;
		(anchor.Side, anchor.Line, anchor.LineText).ShouldBe((RemarkSide.New, 40, "line 40"));
	}

	[Test]
	public async Task A_fold_row_takes_no_remark()
	{
		var remarks = await OpenAsync();
		_changes!.Mode = DiffViewMode.WholeFile;
		var shown = _changes.ShownDiff!;
		var fold = shown.Rows.ToList().FindIndex(row => row.Kind == DiffRowKind.Fold);

		remarks.CanRemarkOn(shown, fold).ShouldBeFalse();
		(await remarks.AddRemarkAsync(shown, fold, "fold?", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task A_thread_on_a_folded_removed_line_has_no_row_and_shows_in_place_once_expanded()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 2, "why gone?", CancellationToken.None);

		_changes.Mode = DiffViewMode.WholeFile;
		await EventuallyAsync(() => remarks.ThreadsByRow?.ForDiff == _changes.ShownDiff);
		remarks.ThreadsByRow!.ByRow.ShouldBeEmpty();
		remarks.OpenThreads.ShouldHaveSingleItem();

		_changes.ToggleFold(_changes.DiffRows.ToList().FindIndex(row => row.Kind == DiffRowKind.Fold));
		await EventuallyAsync(() => remarks.ThreadsByRow?.ForDiff == _changes.ShownDiff && remarks.ThreadsByRow!.ByRow.Count == 1);
		var row = remarks.ThreadsByRow!.ByRow.Single().Key;
		(_changes.DiffRows[row].Kind, _changes.DiffRows[row].Text).ShouldBe((DiffRowKind.Removed, "old"));
	}

	[Test]
	public async Task A_thread_made_in_diff_mode_sits_on_its_line_in_whole_file_mode()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);

		_changes.Mode = DiffViewMode.WholeFile;

		await EventuallyAsync(() => remarks.ThreadsByRow?.ForDiff == _changes.ShownDiff && remarks.ThreadsByRow!.ByRow.Count == 1);
		var row = remarks.ThreadsByRow!.ByRow.Single().Key;
		_changes.DiffRows[row].NewNumber.ShouldBe("2");
	}

	// Line 2 of the text changed from "old 2"; one hunk around it.
	private static FileDiff ChangedSecondLine(string text)
	{
		var lines = text.TrimEnd('\n').Split('\n');
		return new(null, [new DiffHunk(1, 3, 1, 3, string.Empty,
		[
			new DiffLine(DiffLineKind.Context, 1, 1, lines[0]),
			new DiffLine(DiffLineKind.Removed, 2, null, "old 2"),
			new DiffLine(DiffLineKind.Added, null, 2, lines[1]),
			new DiffLine(DiffLineKind.Context, 3, 3, lines[2])
		])]);
	}

	[Test]
	public async Task A_pick_still_loading_takes_no_remark()
	{
		_reader.Files = [Path1, "src/Y.cs"];
		var remarks = await OpenAsync();
		var shownX = _changes!.ShownDiff!;
		var gate = new TaskCompletionSource<FileDiff>();
		_reader.Gates["src/Y.cs"] = gate;

		var pick = _changes.SelectFileAsync(_changes.Files[1], CancellationToken.None);

		remarks.CanRemarkOn(shownX, 1).ShouldBeFalse();
		gate.SetResult(FileDiff.AllAdded("y\n"));
		await pick;
	}

	[Test]
	public async Task Markers_stay_on_the_shown_rows_when_the_disk_moves_first()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		await WriteFileAsync("new top\na\nb\nc\n");

		await _book!.SaveDraftAsync("C1", "why, really?", CancellationToken.None);

		await EventuallyAsync(() => remarks.ThreadsByRow!.ByRow.TryGetValue(3, out var threads) && threads[0].Messages.Any(message => message.Text == "why, really?"));
		remarks.ThreadsByRow!.ByRow.Keys.ShouldBe([3]);
	}

	[Test]
	public async Task A_late_placement_does_not_land_on_a_newer_diff()
	{
		ConcurrentQueue<Action> queue = new();
		var remarks = await OpenAsync(post: queue.Enqueue);
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await DrainUntilAsync(queue, () => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		var old = _changes.ShownDiff!;

		await _book!.SaveDraftAsync("C1", "again", CancellationToken.None);
		_reader.Texts[Path1] = "z\na\nb\nc\n";
		_reader.Diffs[Path1] = FileDiff.AllAdded("z\na\nb\nc\n");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		var current = _changes.ShownDiff!;
		current.ShouldNotBeSameAs(old);

		await DrainUntilAsync(queue, () => ReferenceEquals(remarks.ThreadsByRow?.ForDiff, current));
		remarks.ThreadsByRow!.ForDiff.ShouldBeSameAs(current);
	}

	[Test]
	public async Task An_outdated_thread_moves_to_elsewhere()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		_reader.Texts[Path1] = "a\nB\nc\n";
		await WriteFileAsync("a\nB\nc\n");
		_reader.Diffs[Path1] = Diff("B");

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		await EventuallyAsync(() => remarks.Elsewhere.Any(thread => thread.Id == "C1" && thread.IsOutdated));
		remarks.ThreadsByRow!.ByRow.ShouldBeEmpty();
	}

	[Test]
	public async Task Counts_follow_the_book()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;
		await remarks.AddRemarkAsync(shown, 3, "one", CancellationToken.None);
		await EventuallyAsync(() => remarks.BarText == "1 remark to send");

		await remarks.AddRemarkAsync(shown, 2, "two", CancellationToken.None);

		await EventuallyAsync(() => remarks.DraftCount == 2);
		remarks.OpenCount.ShouldBe(2);
		remarks.BarText.ShouldBe("2 remarks to send");
		_changes.Files[0].OpenRemarks.ShouldBe(2);
		await _book!.ResolveAsync("C1", CancellationToken.None);
		await EventuallyAsync(() => remarks.OpenCount == 1);
		_changes.Files[0].OpenRemarks.ShouldBe(1);
	}

	[Test]
	public async Task Send_delivers_one_prompt_with_every_draft()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;
		await remarks.AddRemarkAsync(shown, 3, "why b?", CancellationToken.None);
		await remarks.AddRemarkAsync(shown, 2, "why gone?", CancellationToken.None);

		await remarks.SendAsync("s1", CancellationToken.None);

		var (session, text) = _delivered.ShouldHaveSingleItem();
		session.ShouldBe("s1");
		text.ShouldContain("C1 · src/X.cs:2 · why b?\n> b");
		text.ShouldContain("C2 · src/X.cs, removed line 2 of HEAD head1 · why gone?\n> old");
		_book!.Threads.Select(thread => thread.State).ShouldBe([RemarkThreadState.Sent, RemarkThreadState.Sent]);
		await EventuallyAsync(() => remarks.DraftCount == 0);
		remarks.BarText.ShouldBeEmpty();
		remarks.StatusText.ShouldBeNull();
	}

	[Test]
	public async Task An_agent_answer_during_delivery_stays_answered()
	{
		TaskCompletionSource<PromptDeliveryResult> gate = new();
		TaskCompletionSource started = new();
		_deliver = (_, _, _) =>
		{
			started.SetResult();
			return gate.Task;
		};
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);

		var send = remarks.SendAsync("s1", CancellationToken.None);
		await started.Task;
		await _book!.AgentReplyAsync("C1", "because", resolve: false, CancellationToken.None);
		gate.SetResult(Unconfirmed);
		await send;

		_book.Threads.Single().State.ShouldBe(RemarkThreadState.Answered);
		await EventuallyAsync(() => remarks.StatusText == "Delivery was not confirmed.");
	}

	[Test]
	public async Task A_send_that_fails_before_delivery_returns_the_drafts()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		using CancellationTokenSource cancellation = new();
		_book!.Changed += (_, _) => cancellation.Cancel();

		await remarks.SendAsync("s1", cancellation.Token);

		_delivered.ShouldBeEmpty();
		_book.Threads.Single().State.ShouldBe(RemarkThreadState.Draft);
	}

	[Test]
	public async Task A_delivery_that_throws_is_uncertain()
	{
		_deliver = (_, _, _) => throw new InvalidOperationException("terminal gone");
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);

		await remarks.SendAsync("s1", CancellationToken.None);

		_book!.Threads.Single().State.ShouldBe(RemarkThreadState.DeliveryUncertain);
		await EventuallyAsync(() => remarks.StatusText is not null);
	}

	[Test]
	public async Task Sending_is_blocked_without_a_session()
	{
		_sessions = [];
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await EventuallyAsync(() => remarks.DraftCount == 1);

		remarks.CanSend.ShouldBeFalse();
		remarks.SendBlockedReason.ShouldBe("No session in this project to send to");
		_sessions = [new("s1", "Claude"), new("s2", "Codex")];
		remarks.CanSend.ShouldBeTrue();
		remarks.SendTargets.Select(target => target.Title).ShouldBe(["Claude", "Codex"]);
	}

	[Test]
	public async Task A_set_aside_remarks_file_is_reported()
	{
		var folder = Path.Combine(_root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(Path.Combine(folder, "remarks.json"), "{broken");

		var remarks = await OpenAsync();

		await EventuallyAsync(() => remarks.StatusText is not null);
		remarks.StatusText.ShouldStartWith("The remarks file could not be read; it was kept as remarks.json.broken-");
	}

	[Test]
	public async Task A_reply_typed_during_a_failed_delivery_keeps_the_returned_message()
	{
		var thread = await FailedDeliveryWhileTypingAsync("B");

		await thread.SaveDraftAsync(CancellationToken.None);

		_book!.Threads.Single().Unsent.Select(message => message.Text).ShouldBe(["A", "B"]);
	}

	[Test]
	public async Task Discarding_a_reply_typed_during_a_failed_delivery_keeps_the_returned_message()
	{
		var thread = await FailedDeliveryWhileTypingAsync("B");

		await thread.DiscardDraftAsync(CancellationToken.None);

		_book!.Threads.Single().Unsent.Select(message => message.Text).ShouldBe(["A"]);
		thread.DraftText.ShouldBeEmpty();
	}

	[Test]
	public async Task Open_threads_list_every_unresolved_thread_in_id_order()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;
		await remarks.AddRemarkAsync(shown, 3, "one", CancellationToken.None);
		await remarks.AddRemarkAsync(shown, 2, "two", CancellationToken.None);
		await remarks.AddRemarkAsync(shown, 1, "three", CancellationToken.None);

		await _book!.ResolveAsync("C2", CancellationToken.None);

		await EventuallyAsync(() => remarks.OpenThreads.Select(thread => thread.Id).SequenceEqual(["C1", "C3"]));
	}

	[Test]
	public async Task Resolved_threads_list_the_reviewed_ones_newest_first()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;
		await remarks.AddRemarkAsync(shown, 3, "one", CancellationToken.None);
		await remarks.AddRemarkAsync(shown, 2, "two", CancellationToken.None);
		await remarks.AddRemarkAsync(shown, 1, "three", CancellationToken.None);

		await _book!.ResolveAsync("C1", CancellationToken.None);
		await _book.ResolveAsync("C3", CancellationToken.None);

		await EventuallyAsync(() => remarks.ResolvedThreads.Select(thread => thread.Id).SequenceEqual(["C3", "C1"]));
		await _book.ReopenAsync("C3", CancellationToken.None);
		await EventuallyAsync(() => remarks.ResolvedThreads.Select(thread => thread.Id).SequenceEqual(["C1"]));
		remarks.OpenThreads.Select(thread => thread.Id).ShouldBe(["C2", "C3"]);
	}

	// A list rebuilt on every change takes the focus away from a reply typed in it.
	[Test]
	public async Task The_lists_stay_the_same_while_their_threads_do()
	{
		var remarks = await OpenAsync();
		var shown = _changes!.ShownDiff!;
		var thread = (await remarks.AddRemarkAsync(shown, 3, "one", CancellationToken.None))!;
		await remarks.AddRemarkAsync(shown, 2, "two", CancellationToken.None);
		await _book!.ResolveAsync("C2", CancellationToken.None);
		await EventuallyAsync(() => remarks.ResolvedThreads.Count == 1);
		var open = remarks.OpenThreads;
		var resolved = remarks.ResolvedThreads;

		thread.DraftText = "more";
		await thread.SaveDraftAsync(CancellationToken.None);
		await EventuallyAsync(() => _book.Threads.Single(item => item.Id == "C1").Unsent.Count == 2);
		await Task.Delay(50);

		remarks.OpenThreads.ShouldBeSameAs(open);
		remarks.ResolvedThreads.ShouldBeSameAs(resolved);
	}

	[Test]
	public async Task Adding_a_reply_shows_it_as_unsent_and_empties_the_box()
	{
		var thread = await SentThreadAsync();
		thread.DraftText = "more";

		await thread.SaveDraftAsync(CancellationToken.None);

		thread.DraftText.ShouldBeEmpty();
		var added = thread.Messages[^1];
		(added.Text, added.DeliveryText, added.IsUnsent).ShouldBe(("more", "Not sent yet", true));
		_book!.Threads.Single().Unsent.Select(message => message.Text).ShouldBe(["more"]);
	}

	[Test]
	public async Task The_pencil_puts_an_unsent_message_back_in_the_box()
	{
		var thread = await SentThreadAsync();
		thread.DraftText = "more";
		await thread.SaveDraftAsync(CancellationToken.None);
		var id = thread.Messages[^1].Id;

		thread.EditMessage(id);

		thread.DraftText.ShouldBe("more");
		thread.Messages.ShouldNotContain(message => message.Id == id);
		thread.DraftText = "more, please";
		await thread.SaveDraftAsync(CancellationToken.None);
		_book!.Threads.Single().Unsent.Select(message => (message.Id, message.Text)).ShouldBe([(id, "more, please")]);
		thread.DraftText.ShouldBeEmpty();
	}

	[Test]
	public async Task The_cross_removes_an_unsent_message()
	{
		var thread = await SentThreadAsync();
		thread.DraftText = "more";
		await thread.SaveDraftAsync(CancellationToken.None);

		await thread.RemoveMessageAsync(thread.Messages[^1].Id, CancellationToken.None);

		_book!.Threads.Single().Unsent.ShouldBeEmpty();
		thread.Messages.ShouldNotContain(message => message.IsUnsent);
	}

	[Test]
	public async Task A_new_remark_shows_its_text_as_unsent_with_an_empty_box()
	{
		var remarks = await OpenAsync();

		var thread = (await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None))!;

		thread.DraftText.ShouldBeEmpty();
		thread.Messages.Single().ShouldSatisfyAllConditions(
			message => message.Text.ShouldBe("why?"),
			message => message.IsUnsent.ShouldBeTrue());
	}

	private async Task<RemarkThreadViewModel> SentThreadAsync()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		return remarks.ThreadsByRow!.ByRow[3][0];
	}

	// The typed reply has the same text as the message handed back; text proves nothing about which is which.
	[Test]
	public async Task An_identical_reply_typed_during_a_failed_delivery_is_discarded_alone()
	{
		var thread = await FailedDeliveryWhileTypingAsync("A");

		await thread.DiscardDraftAsync(CancellationToken.None);

		_book!.Threads.Single().Unsent.Select(message => message.Text).ShouldBe(["A"]);
	}

	[Test]
	public async Task Typing_on_while_a_reply_is_added_keeps_the_new_text_for_the_next_one()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		var thread = remarks.ThreadsByRow!.ByRow[3][0];
		thread.DraftText = "B";

		var save = thread.SaveDraftAsync(CancellationToken.None);
		thread.DraftText = "C";
		await save;
		await thread.SaveDraftAsync(CancellationToken.None);

		// B was added as its own message; C, typed meanwhile, stays in the box and goes in as the next one.
		_book!.Threads.Single().Unsent.Select(message => message.Text).ShouldBe(["B", "C"]);
	}

	[Test]
	public async Task A_thread_without_a_proper_id_sets_the_file_aside()
	{
		var folder = Path.Combine(_root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		await File.WriteAllTextAsync(
			Path.Combine(folder, "remarks.json"),
			"""{"version":1,"nextNumber":2,"threads":[{"id":"","anchor":{"path":"other.cs","side":"old","line":1,"lineText":"x","before":[],"after":[],"baseCommit":"head1"},"messages":[{"id":"m1","author":"user","text":"why?","at":"2026-10-27T10:00:00+00:00","delivery":"sent","sessionId":"s1"}],"isResolved":false}]}""");

		var remarks = await OpenAsync();

		await EventuallyAsync(() => remarks.StatusText is not null);
		remarks.StatusText.ShouldStartWith("The remarks file could not be read; it was kept as remarks.json.broken-");
		_book!.Threads.ShouldBeEmpty();
	}

	[Test]
	public async Task A_remark_on_a_final_empty_line_keeps_its_place()
	{
		var remarks = await OpenAsync("a\n\n", FileDiff.AllAdded("a\n\n"));
		var shown = _changes!.ShownDiff!;
		shown.NewLines.ShouldBe(["a", ""]);

		await remarks.AddRemarkAsync(shown, 2, "why blank?", CancellationToken.None);

		var thread = _book!.Threads.Single();
		(await _book.LoadSnapshotLinesAsync(thread.Anchor.SnapshotId!, CancellationToken.None)).ShouldBe(["a", ""]);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(2) == true);
	}

	[Test]
	public async Task A_remarks_file_that_cannot_be_read_is_reported()
	{
		var folder = Path.Combine(_root.Path, ".pact", "remarks");
		Directory.CreateDirectory(folder);
		var path = Path.Combine(folder, "remarks.json");
		await File.WriteAllTextAsync(path, """{"version":1,"nextNumber":1,"threads":[]}""");
		using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
		{
			var remarks = await OpenAsync(loadBook: false);

			await EventuallyAsync(() => remarks.StatusText is not null);
			remarks.StatusText.ShouldStartWith("The remarks could not be read");
		}
	}

	[Test]
	public async Task Typing_a_reply_makes_it_sendable()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		remarks.CanSend.ShouldBeFalse();
		List<string?> raised = [];
		remarks.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		remarks.ThreadsByRow!.ByRow[3][0].DraftText = "and also";

		remarks.CanSend.ShouldBeTrue();
		remarks.BarText.ShouldBe("1 remark to send");
		raised.ShouldContain(nameof(ChangesRemarksViewModel.CanSend));
	}

	private async Task<RemarkThreadViewModel> FailedDeliveryWhileTypingAsync(string typed)
	{
		TaskCompletionSource<PromptDeliveryResult> gate = new();
		TaskCompletionSource started = new();
		_deliver = (_, _, _) =>
		{
			started.TrySetResult();
			return gate.Task;
		};
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "A", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		var thread = remarks.ThreadsByRow!.ByRow[3][0];

		var send = remarks.SendAsync("s1", CancellationToken.None);
		await started.Task;
		await EventuallyAsync(() => thread.DraftText.Length == 0);
		thread.DraftText = typed;
		gate.SetResult(new PromptDeliveryResult(PromptDeliveryOutcome.NotRunning, "Not running"));
		await send;
		await EventuallyAsync(() => _book!.Threads.Single().Unsent.Count == 1 && thread.Messages.Count == 1);

		thread.DraftText.ShouldBe(typed);
		return thread;
	}

	[Test]
	public async Task A_typed_reply_goes_out_with_send()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		remarks.ThreadsByRow!.ByRow[3][0].DraftText = "and also";

		await remarks.SendAsync("s1", CancellationToken.None);

		_delivered.Count.ShouldBe(2);
		_delivered[1].Text.ShouldContain("you: and also");
		_book!.Threads.Single().Unsent.ShouldBeEmpty();
	}

	[Test]
	public async Task Typing_survives_a_book_change()
	{
		var remarks = await OpenAsync();
		await remarks.AddRemarkAsync(_changes!.ShownDiff!, 3, "why?", CancellationToken.None);
		await remarks.SendAsync("s1", CancellationToken.None);
		await EventuallyAsync(() => remarks.ThreadsByRow?.ByRow.ContainsKey(3) == true);
		var thread = remarks.ThreadsByRow!.ByRow[3][0];
		thread.DraftText = "half typed";

		await _book!.AgentReplyAsync("C1", "because", resolve: false, CancellationToken.None);

		await EventuallyAsync(() => thread.Messages.Count == 2);
		thread.DraftText.ShouldBe("half typed");
		thread.StateText.ShouldBe("Answered");
	}

	// Rows: 0 header, 1 "a" (1,1), 2 "old" removed (2,-), 3 "b" added (-,2), 4 "c" (3,3).
	private static FileDiff DefaultDiff() =>
		new(null,
		[
			new DiffHunk(1, 3, 1, 3, string.Empty,
			[
				new DiffLine(DiffLineKind.Context, 1, 1, "a"),
				new DiffLine(DiffLineKind.Removed, 2, null, "old"),
				new DiffLine(DiffLineKind.Added, null, 2, "b"),
				new DiffLine(DiffLineKind.Context, 3, 3, "c")
			])
		]);

	private static FileDiff Diff(string middle) =>
		new(null, [new DiffHunk(1, 3, 1, 3, string.Empty,
		[
			new DiffLine(DiffLineKind.Context, 1, 1, "a"),
			new DiffLine(DiffLineKind.Added, null, 2, middle),
			new DiffLine(DiffLineKind.Context, 3, 3, "c")
		])]);

	private async Task<ChangesRemarksViewModel> OpenAsync(
		string text = "a\nb\nc\n",
		FileDiff? diff = null,
		Action<Action>? post = null,
		bool loadBook = true,
		IRemarkStore? store = null)
	{
		await WriteFileAsync(text);
		_reader.Texts[Path1] = text;
		_reader.Diffs[Path1] = diff ?? DefaultDiff();
		_changes = new ProjectChangesViewModel(_root.Path, _reader, _time, action => action());
		_observation = _changes.Observe();
		await _changes.SelectFileAsync(_changes.Files[0], CancellationToken.None);
		_book = new RemarkBook(_root.Path, store ?? new RemarkFileStore(_root.Path), TimeProvider.System);
		_remarks = new ChangesRemarksViewModel(
			_changes,
			_book,
			() => _sessions,
			(session, prompt, cancellationToken) =>
			{
				_delivered.Add((session, prompt));
				return _deliver?.Invoke(session, prompt, cancellationToken) ?? Task.FromResult(Confirmed);
			},
			post ?? (action => action()));
		if (loadBook)
		{
			await _book.EnsureLoadedAsync(CancellationToken.None);
		}

		return _remarks;
	}

	private async Task WriteFileAsync(string text)
	{
		Directory.CreateDirectory(Path.Combine(_root.Path, "src"));
		await File.WriteAllTextAsync(Path.Combine(_root.Path, "src", "X.cs"), text);
	}

	private static async Task EventuallyAsync(Func<bool> condition)
	{
		for (var attempt = 0; attempt < 200 && !condition(); attempt++)
		{
			await Task.Delay(10);
		}

		condition().ShouldBeTrue();
	}

	private static async Task DrainUntilAsync(ConcurrentQueue<Action> queue, Func<bool> condition)
	{
		for (var attempt = 0; attempt < 200 && !condition(); attempt++)
		{
			while (queue.TryDequeue(out var action))
			{
				action();
			}

			await Task.Delay(10);
		}

		condition().ShouldBeTrue();
	}

	private sealed class Reader : IGitWorkingTreeReader, IGitCommitReader, IGitComparisonReader
	{
		public Task<GitBranchChanges> ReadBranchChangesAsync(string root, CancellationToken cancellationToken) => Task.FromResult(new GitBranchChanges("feature", null, null, "No tracking upstream is configured."));
		public Task<GitRevisionChanges> ReadComparisonChangesAsync(string root, string? baseCommit, string targetCommit, CancellationToken cancellationToken) =>
			Task.FromResult(new GitRevisionChanges(baseCommit, targetCommit, [new(Path1, "old/X.cs", GitChangeKind.Modified)], 1, 1));
		public Task<FileDiff> ReadComparisonDiffAsync(string root, GitFileEntry entry, string? baseCommit, string targetCommit, CancellationToken cancellationToken) => Task.FromResult(DefaultDiff());
		public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitCommitInfo>>([]);
		public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken) => Task.FromResult<GitCommitInfo?>(Historical(revision[0]));
		public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken) => Task.FromResult(new CommitChanges(commit, [new(Path1, "old/X.cs", GitChangeKind.Modified)], 1, 1));
		public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken) => Task.FromResult(DefaultDiff());
		public Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken) => Task.FromResult<string?>("a\nb\nc\n");
		public string[] Files { get; set; } = [Path1];

		public Dictionary<string, FileDiff> Diffs { get; } = [];

		public Dictionary<string, string> Texts { get; } = [];

		public Dictionary<string, TaskCompletionSource<FileDiff>> Gates { get; } = [];

		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges(
				"head1",
				"main",
				Files.Select(path => new GitFileEntry(path, null, GitChangeKind.Modified)).ToArray(),
				1,
				1));

		public Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken cancellationToken) =>
			Gates.Remove(entry.Path, out var gate)
				? gate.Task
				: Task.FromResult(Diffs.GetValueOrDefault(entry.Path) ?? new FileDiff(null, []));

		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult(Texts.GetValueOrDefault(path));
	}
}
