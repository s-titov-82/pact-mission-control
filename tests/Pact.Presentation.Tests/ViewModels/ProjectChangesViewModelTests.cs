using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectChangesViewModelTests
{
	private readonly ManualTimeProvider _time = new();
	private readonly FakeReader _reader = new();

	[Test]
	public async Task Entering_diff_opens_the_first_file_and_preserves_an_existing_pick()
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["a"] = Diff("first");
		using var changes = Create();
		using var observation = changes.Observe();
		changes.SelectedFile.ShouldBeNull();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe("a");
		changes.DiffRows.ShouldContain(row => row.Text == "first");
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		await changes.SetAutoSelectFirstFileAsync(false, CancellationToken.None);
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe("b");
	}

	[Test]
	public async Task First_file_is_opened_when_a_previously_empty_list_arrives()
	{
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		changes.SelectedFile.ShouldBeNull();
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("loaded later");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe("a");
		changes.DiffRows.ShouldContain(row => row.Text == "loaded later");
	}

	[Test]
	public async Task A_manual_pick_wins_over_the_pending_automatic_first_diff()
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["b"] = Diff("user pick");
		TaskCompletionSource<FileDiff> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_reader.DiffGates["a"] = gate;
		using var changes = Create();
		using var observation = changes.Observe();
		var first = changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		gate.SetResult(Diff("automatic pick"));
		await first;
		changes.SelectedFile.ShouldNotBeNull().Path.ShouldBe("b");
		changes.DiffRows.ShouldContain(row => row.Text == "user pick");
		changes.DiffRows.ShouldNotContain(row => row.Text == "automatic pick");
	}

	[Test]
	public async Task Leaving_diff_before_the_list_arrives_does_not_open_a_file_in_background()
	{
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SetAutoSelectFirstFileAsync(true, CancellationToken.None);
		await changes.SetAutoSelectFirstFileAsync(false, CancellationToken.None);
		_reader.Current = Changes("a");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedFile.ShouldBeNull();
		changes.ShownDiff.ShouldBeNull();
	}

	[Test]
	public void Observing_reads_at_once_and_then_every_reconcile_interval()
	{
		using var changes = Create();

		var observation = changes.Observe();
		_reader.ChangeReads.ShouldBe(1);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		_reader.ChangeReads.ShouldBe(2);

		observation.Dispose();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		_reader.ChangeReads.ShouldBe(2);
	}

	[Test]
	public void A_baseline_change_without_any_signal_shows_on_the_next_tick()
	{
		using var changes = Create();
		using var observation = changes.Observe();
		changes.FileCount.ShouldBe(0);

		_reader.Current = Changes("a.txt");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		changes.FileCount.ShouldBe(1);
	}

	[Test]
	public void Requests_inside_the_minimum_interval_collapse_into_one_read()
	{
		using var changes = Create();
		using var observation = changes.Observe();

		_time.Advance(TimeSpan.FromMilliseconds(500));
		changes.RequestRefresh();
		changes.RequestRefresh();
		changes.RequestRefresh();
		_reader.ChangeReads.ShouldBe(1);

		_time.Advance(ProjectChangesViewModel.MinRefreshInterval - TimeSpan.FromMilliseconds(500));
		_reader.ChangeReads.ShouldBe(2);
		_time.Advance(TimeSpan.FromSeconds(1));
		_reader.ChangeReads.ShouldBe(2);
	}

	[Test]
	public void A_request_after_the_minimum_interval_reads_at_once()
	{
		using var changes = Create();
		using var observation = changes.Observe();

		_time.Advance(ProjectChangesViewModel.MinRefreshInterval);
		changes.RequestRefresh();

		_reader.ChangeReads.ShouldBe(2);
	}

	[Test]
	public async Task A_request_during_a_read_schedules_exactly_one_more()
	{
		using var changes = Create();
		// The read finishes on another thread, as a real git call does.
		TaskCompletionSource<WorkingTreeChanges?> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_reader.Gate = gate;
		using var observation = changes.Observe();

		changes.RequestRefresh();
		changes.RequestRefresh();
		_reader.Gate = null;
		gate.SetResult(Changes());
		_reader.ChangeReads.ShouldBe(1);

		_time.Advance(ProjectChangesViewModel.MinRefreshInterval);
		await EventuallyAsync(() => _reader.ChangeReads == 2);
		_time.Advance(ProjectChangesViewModel.MinRefreshInterval);
		_reader.ChangeReads.ShouldBe(2);
	}

	// Fires timers the finished read armed after the clock already moved, until the condition holds.
	private async Task EventuallyAsync(Func<bool> condition)
	{
		for (var attempt = 0; attempt < 200 && !condition(); attempt++)
		{
			_time.Advance(TimeSpan.Zero);
			if (!condition())
			{
				await Task.Delay(10);
			}
		}

		condition().ShouldBeTrue();
	}

	[Test]
	public void Requests_without_observers_read_nothing()
	{
		using var changes = Create();

		changes.RequestRefresh();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		_reader.ChangeReads.ShouldBe(0);
	}

	[Test]
	public async Task Selection_follows_the_path_and_falls_to_the_neighbour_when_the_file_leaves()
	{
		_reader.Current = Changes("a", "b", "c");
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		changes.SelectedFile!.Path.ShouldBe("b");

		_reader.Current = Changes("x", "a", "b", "c");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedFile!.Path.ShouldBe("b");

		_reader.Current = Changes("x", "a", "c");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedFile!.Path.ShouldBe("c");

		_reader.Current = Changes();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.SelectedFile.ShouldBeNull();
		changes.DiffRows.ShouldBeEmpty();
	}

	[Test]
	public async Task The_selected_diff_is_reread_when_the_list_refreshes()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("one");
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		changes.DiffRows[^1].Text.ShouldBe("one");

		_reader.Diffs["a"] = Diff("two");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		changes.DiffRows[^1].Text.ShouldBe("two");
	}

	[Test]
	public async Task Long_diffs_are_cut_and_can_be_shown_whole()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff(Enumerable.Range(0, ProjectChangesViewModel.MaxShownLines + 10)
			.Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.ToArray());
		using var changes = Create();
		using var observation = changes.Observe();

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		changes.HiddenLineCount.ShouldBe(10);
		changes.ShowAllText.ShouldBe("Show all (10 more lines)");

		changes.ShowAllLines();
		changes.HiddenLineCount.ShouldBe(0);
		changes.DiffRows.Count(row => row.Kind != DiffRowKind.HunkHeader)
			.ShouldBe(ProjectChangesViewModel.MaxShownLines + 10);
	}

	[Test]
	public void Entry_texts_follow_the_counts()
	{
		_reader.Current = Changes("a", "b", "c", "d", "e") with { AddedLines = 84, RemovedLines = 12 };
		using var changes = Create();
		using var observation = changes.Observe();

		changes.EntryTitle.ShouldBe("Working tree · 5");
		changes.EntrySubtitle.ShouldBe("+84 −12");
		changes.TabHeader.ShouldBe("Changes 5");

		_reader.Current = Changes();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.EntryTitle.ShouldBe("Working tree · 0");
		changes.EntrySubtitle.ShouldBe("+0 −0");
		changes.TabHeader.ShouldBe("Changes");
	}

	[Test]
	public void Status_letters_and_folders_describe_each_file()
	{
		_reader.Current = new WorkingTreeChanges("h", "main",
		[
			new GitFileEntry("src/a.cs", null, GitChangeKind.Modified, IsStaged: true),
			new GitFileEntry("src/new.cs", "src/old.cs", GitChangeKind.Modified),
			new GitFileEntry("b.txt", null, GitChangeKind.Untracked)
		], 0, 0);
		using var changes = Create();
		using var observation = changes.Observe();

		changes.Files.Select(file => (file.StatusLetter, file.FileName, file.Folder, file.IsStaged)).ShouldBe(
		[
			("M", "a.cs", "src", true),
			("R", "new.cs", "src", false),
			("U", "b.txt", string.Empty, false)
		]);
	}

	[Test]
	public void A_folder_that_is_not_a_repository_is_unavailable()
	{
		_reader.Current = null;
		using var changes = Create();
		using var observation = changes.Observe();

		changes.IsAvailable.ShouldBeFalse();
	}

	[Test]
	public void A_timer_refresh_publishes_only_through_the_ui_poster()
	{
		List<Action> queued = [];
		using var changes = new ProjectChangesViewModel("C:\\repo", _reader, _time, queued.Add);
		using var observation = changes.Observe();
		queued.ForEach(action => action());
		queued.Clear();
		List<string?> raised = [];
		changes.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		_reader.Current = Changes("a");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		_reader.ChangeReads.ShouldBe(2);
		changes.FileCount.ShouldBe(0);
		raised.ShouldBeEmpty();
		queued.ForEach(action => action());
		changes.FileCount.ShouldBe(1);
		raised.ShouldContain(nameof(ProjectChangesViewModel.FileCount));
	}

	[Test]
	public async Task A_refresh_that_finishes_late_keeps_the_file_the_user_picked_meanwhile()
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["a"] = Diff("a-line");
		_reader.Diffs["b"] = Diff("b-line");
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		TaskCompletionSource<FileDiff> lateA = new();
		_reader.DiffGates["a"] = lateA;

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		lateA.SetResult(Diff("a-line"));

		changes.SelectedFile!.Path.ShouldBe("b");
		changes.DiffRows[^1].Text.ShouldBe("b-line");
	}

	[Test]
	public async Task An_older_diff_read_does_not_replace_a_newer_one_for_the_same_file()
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["a"] = Diff("fresh");
		using var changes = Create();
		using var observation = changes.Observe();
		TaskCompletionSource<FileDiff> stale = new();
		_reader.DiffGates["a"] = stale;
		var first = changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		stale.SetResult(Diff("stale"));
		await first;

		changes.DiffRows[^1].Text.ShouldBe("fresh");
	}

	[TestCase(true)]
	[TestCase(false)]
	public async Task A_pick_whose_file_left_the_list_does_not_overwrite_the_refresh(bool neighbourRemains)
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["b"] = Diff("b-line");
		using var changes = Create();
		using var observation = changes.Observe();
		TaskCompletionSource<FileDiff> lateA = new();
		_reader.DiffGates["a"] = lateA;
		var pickA = changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		_reader.Current = neighbourRemains ? Changes("b") : Changes();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		lateA.SetResult(Diff("a-line"));
		await pickA;

		if (neighbourRemains)
		{
			changes.SelectedFile!.Path.ShouldBe("b");
			changes.DiffRows[^1].Text.ShouldBe("b-line");
		}
		else
		{
			changes.SelectedFile.ShouldBeNull();
			changes.DiffRows.ShouldBeEmpty();
		}
	}

	[Test]
	public async Task A_pick_read_older_than_the_refresh_of_the_same_file_does_not_overwrite_it()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("fresh");
		using var changes = Create();
		using var observation = changes.Observe();
		TaskCompletionSource<FileDiff> stale = new();
		_reader.DiffGates["a"] = stale;
		var pickA = changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.DiffRows[^1].Text.ShouldBe("fresh");
		stale.SetResult(Diff("stale"));
		await pickA;

		changes.DiffRows[^1].Text.ShouldBe("fresh");
	}

	[Test]
	public async Task The_shown_diff_carries_the_text_it_was_built_from()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("one", "two");
		_reader.Texts["a"] = new(["one\r\ntwo\n"]);
		using var changes = Create();
		using var observation = changes.Observe();

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		var shown = changes.ShownDiff.ShouldNotBeNull();
		shown.Path.ShouldBe("a");
		shown.HeadCommit.ShouldBe("head");
		shown.Rows.ShouldBeSameAs(changes.DiffRows);
		shown.NewLines.ShouldBe(["one", "two"]);
	}

	// The list is re-read every few seconds; a remark being written on the shown diff must not
	// lose it to a read that changed nothing.
	[Test]
	public async Task The_shown_diff_survives_a_refresh_that_changed_nothing()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("one");
		_reader.Texts["a"] = new(["one\n"]);
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		var shown = changes.ShownDiff;

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldBeSameAs(shown);

		_reader.Diffs["a"] = Diff("two");
		_reader.Texts["a"] = new(["two\n"]);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.ShownDiff.ShouldNotBeSameAs(shown);
		changes.ShownDiff!.NewLines.ShouldBe(["two"]);
	}

	[Test]
	public async Task A_file_that_changes_while_it_is_read_shows_no_text()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff("one");
		_reader.Texts["a"] = new(["one\n", "one\nmore\n"]);
		using var changes = Create();
		using var observation = changes.Observe();

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.ShownDiff.ShouldNotBeNull().NewLines.ShouldBeNull();
		changes.DiffRows.ShouldNotBeEmpty();
	}

	[Test]
	public async Task A_pick_still_loading_has_no_shown_diff()
	{
		_reader.Current = Changes("a", "b");
		_reader.Diffs["a"] = Diff("from a");
		_reader.Texts["a"] = new(["from a\n"]);
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		var gate = new TaskCompletionSource<FileDiff>();
		_reader.DiffGates["b"] = gate;

		var pick = changes.SelectFileAsync(changes.Files[1], CancellationToken.None);

		changes.ShownDiff.ShouldBeNull();
		changes.DiffRows[^1].Text.ShouldBe("from a");
		gate.SetResult(Diff("from b"));
		await pick;
		changes.ShownDiff.ShouldNotBeNull().Path.ShouldBe("b");
	}

	[Test]
	public async Task Show_all_keeps_the_text()
	{
		var lines = Enumerable.Range(0, ProjectChangesViewModel.MaxShownLines + 5)
			.Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.ToArray();
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff(lines);
		_reader.Texts["a"] = new([string.Join('\n', lines)]);
		using var changes = Create();
		using var observation = changes.Observe();
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.ShowAllLines();

		var shown = changes.ShownDiff.ShouldNotBeNull();
		shown.Rows.ShouldBeSameAs(changes.DiffRows);
		shown.NewLines!.Count.ShouldBe(lines.Length);
	}

	[Test]
	public void Observing_again_right_after_a_read_waits_for_the_minimum_interval()
	{
		using var changes = Create();
		changes.Observe().Dispose();
		_reader.ChangeReads.ShouldBe(1);

		using var observation = changes.Observe();
		_reader.ChangeReads.ShouldBe(1);

		_time.Advance(ProjectChangesViewModel.MinRefreshInterval);
		_reader.ChangeReads.ShouldBe(2);
	}

	[Test]
	public async Task Whole_file_shows_every_new_line_with_added_lines_marked_and_removed_runs_folded()
	{
		using var changes = await WholeFileAsync("a");

		changes.DiffRows.Select(row => (row.Kind, row.NewNumber, row.Text)).ShouldBe([
			(DiffRowKind.Context, "1", "a"),
			(DiffRowKind.Fold, "", "▸ 2 lines removed"),
			(DiffRowKind.Added, "2", "B"),
			(DiffRowKind.Context, "3", "c"),
			(DiffRowKind.Context, "4", "d")]);
		var run = changes.ShownDiff.ShouldNotBeNull().RemovedRuns.ShouldHaveSingleItem();
		(run.BeforeNewLine, run.OldStart).ShouldBe((2, 2));
		run.Lines.ShouldBe(["b", "x"]);
	}

	[Test]
	public async Task Toggling_a_fold_shows_the_old_lines_as_removed_rows_with_old_numbers_and_folds_them_back()
	{
		using var changes = await WholeFileAsync("a");

		changes.ToggleFold(1);
		changes.DiffRows.Skip(1).Take(3).Select(row => (row.Kind, row.OldNumber, row.Text)).ShouldBe([
			(DiffRowKind.Fold, "", "▾ 2 lines removed"),
			(DiffRowKind.Removed, "2", "b"),
			(DiffRowKind.Removed, "3", "x")]);

		changes.ToggleFold(1);
		changes.DiffRows.Count.ShouldBe(5);
		changes.DiffRows[1].Text.ShouldBe("▸ 2 lines removed");
	}

	[Test]
	public async Task The_mode_stays_when_another_file_is_selected()
	{
		_reader.Current = Changes("a", "b");
		Edit("b");
		using var changes = await WholeFileAsync("a", list: false);

		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);

		changes.Mode.ShouldBe(DiffViewMode.WholeFile);
		changes.DiffRows.Count.ShouldBe(5);
	}

	[Test]
	public async Task A_deleted_file_shows_its_old_text_as_removed_rows_without_a_fold()
	{
		_reader.Current = new("head", "main", [new GitFileEntry("a", null, GitChangeKind.Deleted)], 0, 0);
		_reader.Diffs["a"] = new FileDiff(null, [new DiffHunk(1, 2, 0, 0, string.Empty,
			[new DiffLine(DiffLineKind.Removed, 1, null, "one"), new DiffLine(DiffLineKind.Removed, 2, null, "two")])]);
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.WholeFile;

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.EffectiveMode.ShouldBe(DiffViewMode.WholeFile);
		changes.DiffRows.Select(row => (row.Kind, row.OldNumber, row.Text)).ShouldBe([
			(DiffRowKind.Removed, "1", "one"), (DiffRowKind.Removed, "2", "two")]);
	}

	[Test]
	public async Task A_placeholder_file_cannot_show_whole_and_keeps_the_placeholder()
	{
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = FileDiff.Binary;
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.WholeFile;

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.CanShowWholeFile.ShouldBeFalse();
		changes.EffectiveMode.ShouldBe(DiffViewMode.Diff);
		changes.DiffPlaceholder.ShouldNotBeNull();
		changes.DiffRows.ShouldBeEmpty();
	}

	[Test]
	public async Task Rendered_is_offered_for_markdown_only()
	{
		_reader.Current = Changes("x.md", "x.cs");
		Edit("x.md");
		Edit("x.cs");
		using var changes = Create();
		using var observation = changes.Observe();

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		changes.CanRender.ShouldBeTrue();
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		changes.CanRender.ShouldBeFalse();
		changes.CanShowWholeFile.ShouldBeTrue();
	}

	[Test]
	public async Task Text_read_inconsistently_falls_back_to_hunk_rows_in_whole_file_mode()
	{
		_reader.Current = Changes("a");
		Edit("a");
		_reader.Texts["a"] = new(["a\nB\nc\nd\n", "a\nB\nc\nd\nmore\n"]);
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.WholeFile;

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.EffectiveMode.ShouldBe(DiffViewMode.Diff);
		changes.DiffRows[0].Kind.ShouldBe(DiffRowKind.HunkHeader);
	}

	[Test]
	public async Task A_refresh_that_changed_nothing_keeps_the_whole_file_shown_diff_instance()
	{
		using var changes = await WholeFileAsync("a");
		var shown = changes.ShownDiff;

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		changes.ShownDiff.ShouldBeSameAs(shown);
	}

	[Test]
	public async Task An_expanded_fold_stays_expanded_after_a_refresh()
	{
		using var changes = await WholeFileAsync("a");
		changes.ToggleFold(1);

		_reader.Texts["a"] = new(["a\nB\nc\nd2\n"]);
		_reader.Diffs["a"] = EditDiff("d2");
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);

		changes.DiffRows[1].Text.ShouldBe("▾ 2 lines removed");
		changes.DiffRows[^1].Text.ShouldBe("d2");
	}

	[Test]
	public async Task Whole_file_rows_are_not_cut_at_the_shown_line_limit()
	{
		var lines = Enumerable.Range(0, ProjectChangesViewModel.MaxShownLines + 500)
			.Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture))
			.ToArray();
		_reader.Current = Changes("a");
		_reader.Diffs["a"] = Diff(lines);
		_reader.Texts["a"] = new([string.Join('\n', lines)]);
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.WholeFile;

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);

		changes.DiffRows.Count.ShouldBe(lines.Length);
		changes.HiddenLineCount.ShouldBe(0);
	}

	[Test]
	public async Task Rendered_falls_back_to_whole_file_for_a_deleted_markdown_file_and_for_a_non_markdown_file()
	{
		_reader.Current = new("head", "main",
			[new GitFileEntry("gone.md", null, GitChangeKind.Deleted), new GitFileEntry("x.cs", null, GitChangeKind.Modified)], 0, 0);
		_reader.Diffs["gone.md"] = new FileDiff(null, [new DiffHunk(1, 1, 0, 0, string.Empty, [new DiffLine(DiffLineKind.Removed, 1, null, "old")])]);
		Edit("x.cs");
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.Rendered;

		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		changes.EffectiveMode.ShouldBe(DiffViewMode.WholeFile);
		await changes.SelectFileAsync(changes.Files[1], CancellationToken.None);
		changes.EffectiveMode.ShouldBe(DiffViewMode.WholeFile);
		changes.Mode.ShouldBe(DiffViewMode.Rendered);
	}

	[Test]
	public async Task Rendered_falls_back_to_hunk_rows_when_a_refresh_reads_the_text_inconsistently()
	{
		_reader.Current = Changes("x.md");
		Edit("x.md");
		using var changes = Create();
		using var observation = changes.Observe();
		changes.Mode = DiffViewMode.Rendered;
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		changes.EffectiveMode.ShouldBe(DiffViewMode.Rendered);

		_reader.Texts["x.md"] = new(["a\nB\nc\nd\n", "moving\n", "a\nB\nc\nd\n"]);
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.EffectiveMode.ShouldBe(DiffViewMode.Diff);

		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.EffectiveMode.ShouldBe(DiffViewMode.Rendered);
	}

	// Old a,b,x,c,d; new a,B,c,d: b and x removed above B.
	private void Edit(string path, string last = "d")
	{
		_reader.Diffs[path] = EditDiff(last);
		_reader.Texts[path] = new([$"a\nB\nc\n{last}\n"]);
	}

	private static FileDiff EditDiff(string last) =>
		new(null,
		[
			new DiffHunk(1, 5, 1, 4, string.Empty,
			[
				new DiffLine(DiffLineKind.Context, 1, 1, "a"),
				new DiffLine(DiffLineKind.Removed, 2, null, "b"),
				new DiffLine(DiffLineKind.Removed, 3, null, "x"),
				new DiffLine(DiffLineKind.Added, null, 2, "B"),
				new DiffLine(DiffLineKind.Context, 4, 3, "c"),
				new DiffLine(DiffLineKind.Context, 5, 4, last)
			])
		]);

	private async Task<ProjectChangesViewModel> WholeFileAsync(string path, bool list = true)
	{
		if (list)
		{
			_reader.Current = Changes(path);
		}

		Edit(path);
		var changes = Create();
		changes.Observe();
		changes.Mode = DiffViewMode.WholeFile;
		await changes.SelectFileAsync(changes.Files[0], CancellationToken.None);
		return changes;
	}

	private ProjectChangesViewModel Create() => new("C:\\repo", _reader, _time, action => action());

	private static WorkingTreeChanges Changes(params string[] paths) =>
		new("head", "main", paths.Select(path => new GitFileEntry(path, null, GitChangeKind.Modified)).ToArray(), 0, 0);

	private static FileDiff Diff(params string[] lines) =>
		new(null,
		[
			new DiffHunk(1, lines.Length, 1, lines.Length, string.Empty,
				lines.Select((line, index) => new DiffLine(DiffLineKind.Added, null, index + 1, line)).ToArray())
		]);

	private sealed class FakeReader : IGitWorkingTreeReader
	{
		public WorkingTreeChanges? Current { get; set; } = Changes();

		public TaskCompletionSource<WorkingTreeChanges?>? Gate { get; set; }

		public Dictionary<string, FileDiff> Diffs { get; } = [];

		public int ChangeReads { get; private set; }

		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken)
		{
			ChangeReads++;
			return Gate?.Task ?? Task.FromResult(Current);
		}

		public Dictionary<string, TaskCompletionSource<FileDiff>> DiffGates { get; } = [];

		public Task<FileDiff> ReadDiffAsync(
			string root,
			GitFileEntry entry,
			string? headCommit,
			CancellationToken cancellationToken) =>
			DiffGates.Remove(entry.Path, out var gate)
				? gate.Task
				: Task.FromResult(Diffs.GetValueOrDefault(entry.Path) ?? new FileDiff(null, []));

		// Each read takes the next text; the last one repeats.
		public Dictionary<string, Queue<string>> Texts { get; } = [];

		public Task<string?> ReadWorkingTextAsync(string root, string path, CancellationToken cancellationToken) =>
			Task.FromResult(Texts.TryGetValue(path, out var texts)
				? texts.Count > 1 ? texts.Dequeue() : texts.Peek()
				: null);
	}
}
