using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectChangesViewModelTests
{
	private readonly ManualTimeProvider _time = new();
	private readonly FakeReader _reader = new();

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

		changes.EntryTitle.ShouldBe("Changes · 5");
		changes.EntrySubtitle.ShouldBe("+84 −12");
		changes.TabHeader.ShouldBe("Changes 5");

		_reader.Current = Changes();
		_time.Advance(ProjectChangesViewModel.ReconcileInterval);
		changes.EntryTitle.ShouldBe("No uncommitted changes");
		changes.EntrySubtitle.ShouldBe(string.Empty);
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

		public Task<FileDiff> ReadDiffAsync(
			string root,
			GitFileEntry entry,
			string? headCommit,
			CancellationToken cancellationToken) =>
			Task.FromResult(Diffs.GetValueOrDefault(entry.Path) ?? new FileDiff(null, []));
	}
}
