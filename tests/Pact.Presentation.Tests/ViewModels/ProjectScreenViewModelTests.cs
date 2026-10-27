using Pact.Core.Git;
using Pact.Core.Projects;
using Pact.Infrastructure.Storage;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class ProjectScreenViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();
	private readonly ManualTimeProvider _time = new();
	private readonly List<ProjectChangesViewModel> _changes = [];

	[Test]
	public async Task Opening_a_review_log_selects_the_tab_and_loads_its_text()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		WriteLog("2026-10-25-0900-code-review-b.md", "# Second");
		var screen = Create(git: false);

		(await screen.OpenReviewLogAsync(".pact/reviews/2026-10-24-1400-code-review-a.md", CancellationToken.None))
			.ShouldBeTrue();

		screen.SelectedTab.ShouldBe(ProjectScreenTab.ReviewLogs);
		screen.SelectedReviewLog.ShouldBe(".pact/reviews/2026-10-24-1400-code-review-a.md");
		screen.ReviewLogText.ShouldBe("# First");
	}

	[Test]
	public async Task Selecting_the_review_logs_tab_opens_the_newest_log()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		WriteLog("2026-10-25-0900-code-review-b.md", "# Second");
		var screen = Create(git: false);

		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		screen.ReviewLogs.Count.ShouldBe(2);
		screen.SelectedReviewLog.ShouldBe(".pact/reviews/2026-10-25-0900-code-review-b.md");
		screen.ReviewLogText.ShouldBe("# Second");
	}

	[Test]
	public async Task Deleting_the_shown_log_removes_it_and_shows_the_next_one()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		WriteLog("2026-10-25-0900-code-review-b.md", "# Second");
		var screen = Create(git: false);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		(await screen.DeleteReviewLogAsync(".pact/reviews/2026-10-25-0900-code-review-b.md", CancellationToken.None))
			.ShouldBe(ReviewLogDeletion.Deleted);

		screen.ReviewLogs.ShouldBe([".pact/reviews/2026-10-24-1400-code-review-a.md"]);
		screen.SelectedReviewLog.ShouldBe(".pact/reviews/2026-10-24-1400-code-review-a.md");
		screen.ReviewLogText.ShouldBe("# First");
	}

	[Test]
	public async Task Deleting_the_last_log_clears_the_view()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		var screen = Create(git: false);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		await screen.DeleteReviewLogAsync(".pact/reviews/2026-10-24-1400-code-review-a.md", CancellationToken.None);

		screen.ReviewLogs.ShouldBeEmpty();
		screen.SelectedReviewLog.ShouldBeNull();
		screen.ReviewLogText.ShouldBeEmpty();
	}

	[Test]
	public async Task A_log_a_running_review_writes_is_not_deleted()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		var screen = Create(git: false, logInUse: path => path.EndsWith("code-review-a.md", StringComparison.Ordinal));
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		(await screen.DeleteReviewLogAsync(".pact/reviews/2026-10-24-1400-code-review-a.md", CancellationToken.None))
			.ShouldBe(ReviewLogDeletion.InUse);

		File.Exists(Path.Combine(_root.Path, ".pact", "reviews", "2026-10-24-1400-code-review-a.md")).ShouldBeTrue();
		screen.ReviewLogs.Count.ShouldBe(1);
	}

	[Test]
	public async Task Opening_an_unknown_log_reports_false_and_changes_nothing()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# First");
		var screen = Create(git: false);

		(await screen.OpenReviewLogAsync(".pact/reviews/missing.md", CancellationToken.None)).ShouldBeFalse();

		screen.SelectedTab.ShouldBe(ProjectScreenTab.Overview);
		screen.SelectedReviewLog.ShouldBeNull();
	}

	[Test]
	public async Task A_project_without_git_has_no_changes_tab()
	{
		var screen = Create(git: false);

		screen.HasChanges.ShouldBeFalse();
		await screen.SelectTabAsync(ProjectScreenTab.Changes, CancellationToken.None);
		screen.SelectedTab.ShouldBe(ProjectScreenTab.Overview);
	}

	[Test]
	public void Overview_shows_name_root_and_branch()
	{
		var screen = Create(git: true);
		using var observation = screen.Changes!.Observe();

		screen.Name.ShouldBe("Pact");
		screen.RootPath.ShouldBe(_root.Path);
		screen.Branch.ShouldBe("main");
		screen.HasChanges.ShouldBeTrue();
	}

	[Test]
	public async Task A_log_that_loads_late_does_not_replace_the_one_picked_after_it()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# A");
		WriteLog("2026-10-25-0900-code-review-b.md", "# B");
		GatedStore store = new();
		var screen = Create(git: false, store);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		TaskCompletionSource lateA = new();
		store.Gates["2026-10-24-1400-code-review-a.md"] = lateA;

		var pickA = screen.SelectReviewLogAsync(".pact/reviews/2026-10-24-1400-code-review-a.md", CancellationToken.None);
		await screen.SelectReviewLogAsync(".pact/reviews/2026-10-25-0900-code-review-b.md", CancellationToken.None);
		lateA.SetResult();
		await pickA;

		screen.SelectedReviewLog.ShouldBe(".pact/reviews/2026-10-25-0900-code-review-b.md");
		screen.ReviewLogText.ShouldBe("# B");
	}

	[Test]
	public async Task Returning_to_review_logs_shows_turns_appended_meanwhile()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# Turn 1");
		var screen = Create(git: false);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);
		await screen.SelectTabAsync(ProjectScreenTab.Overview, CancellationToken.None);

		WriteLog("2026-10-24-1400-code-review-a.md", "# Turn 1\n# Turn 2");
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		screen.ReviewLogText.ShouldBe("# Turn 1\n# Turn 2");
	}

	[Test]
	public async Task Reloading_the_open_log_picks_up_an_appended_turn()
	{
		WriteLog("2026-10-24-1400-code-review-a.md", "# Turn 1");
		var screen = Create(git: false);
		await screen.SelectTabAsync(ProjectScreenTab.ReviewLogs, CancellationToken.None);

		WriteLog("2026-10-24-1400-code-review-a.md", "# Turn 1\n# Turn 2");
		await screen.ReloadReviewLogAsync(CancellationToken.None);

		screen.ReviewLogText.ShouldBe("# Turn 1\n# Turn 2");
	}

	public void Dispose()
	{
		_changes.ForEach(changes => changes.Dispose());
		_root.Dispose();
	}

	private ProjectScreenViewModel Create(bool git, IProjectMarkdownFileStore? store = null, Func<string, bool>? logInUse = null)
	{
		var now = DateTimeOffset.UtcNow;
		WorkspaceViewModel workspace = new(new ProjectRecord("p1", "Pact", _root.Path, now, now, null), _ => git);
		ProjectChangesViewModel? changes = null;
		if (git)
		{
			changes = new ProjectChangesViewModel(_root.Path, new MainBranchReader(), _time, action => action());
			_changes.Add(changes);
		}

		return new ProjectScreenViewModel(workspace, changes, store ?? new ProjectMarkdownFileStore(), logInUse);
	}

	private sealed class GatedStore : IProjectMarkdownFileStore
	{
		private readonly ProjectMarkdownFileStore _inner = new();

		public Dictionary<string, TaskCompletionSource> Gates { get; } = [];

		public async Task<ProjectMarkdownFileSnapshot> LoadAsync(string path, CancellationToken cancellationToken)
		{
			var snapshot = await _inner.LoadAsync(path, cancellationToken);
			if (Gates.Remove(Path.GetFileName(path), out var gate))
			{
				await gate.Task;
			}

			return snapshot;
		}

		public Task<ProjectMarkdownSaveResult> TrySaveAsync(
			string path,
			string text,
			string expectedRevision,
			CancellationToken cancellationToken) =>
			_inner.TrySaveAsync(path, text, expectedRevision, cancellationToken);

		public Task<ProjectMarkdownFileSnapshot> OverwriteAsync(
			string path,
			string text,
			CancellationToken cancellationToken) =>
			_inner.OverwriteAsync(path, text, cancellationToken);
	}

	private void WriteLog(string name, string text)
	{
		var folder = Path.Combine(_root.Path, ".pact", "reviews");
		Directory.CreateDirectory(folder);
		File.WriteAllText(Path.Combine(folder, name), text);
	}

	private sealed class MainBranchReader : IGitWorkingTreeReader
	{
		public Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken cancellationToken) =>
			Task.FromResult<WorkingTreeChanges?>(new WorkingTreeChanges("head", "main", [], 0, 0));

		public Task<FileDiff> ReadDiffAsync(
			string root,
			GitFileEntry entry,
			string? headCommit,
			CancellationToken cancellationToken) =>
			Task.FromResult(new FileDiff(null, []));
	}
}
