using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class GitPanelViewModelTests
{
	[Test]
	public async Task RefreshAsync_maps_git_state_to_display_properties()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(
			args => args.Contains("status"),
			new GitCommandResult(0, DirtyStatus, string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["remote", "get-url", "origin"]),
			new GitCommandResult(0, "https://example/repo.git\n", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]),
			new GitCommandResult(0, "2\n", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]),
			new GitCommandResult(0, @"D:\repo\.git\rebase-merge", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"]),
			new GitCommandResult(0, @"D:\repo\.git\rebase-apply", string.Empty));
		GitPanelViewModel viewModel = new(
			@"D:\repo",
			runner,
			helperActions: [],
			launchHelperAction: (_, _, _) => { },
			directoryExists: path => path.EndsWith("rebase-merge", StringComparison.Ordinal));

		await viewModel.RefreshAsync();

		viewModel.BranchText.ShouldBe("feat/x");
		viewModel.AheadBehindText.ShouldBe("↑2 ↓1");
		viewModel.SummaryText.ShouldBe("+1 ~2 -1 ?1 !1");
		viewModel.RemoteText.ShouldBe("https://example/repo.git");
		viewModel.StashCount.ShouldBe(2);
		viewModel.IsRebaseInProgress.ShouldBeTrue();
		viewModel.HasConflicts.ShouldBeTrue();
		viewModel.HasStashableChanges.ShouldBeTrue();
	}

	[Test]
	public async Task RefreshAsync_resolves_relative_rebase_git_path_against_project_root()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(args => args.Contains("status"), new GitCommandResult(0, DirtyStatus, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["remote", "get-url", "origin"]), new GitCommandResult(0, "origin-url\n", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]), new GitCommandResult(1, string.Empty, "missing"));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]),
			new GitCommandResult(0, @".git\rebase-merge", string.Empty));
		string? inspectedPath = null;
		GitPanelViewModel viewModel = new(
			@"D:\repo",
			runner,
			helperActions: [],
			launchHelperAction: (_, _, _) => { },
			directoryExists: path =>
			{
				inspectedPath = path;
				return string.Equals(path, @"D:\repo\.git\rebase-merge", StringComparison.OrdinalIgnoreCase);
			});

		await viewModel.RefreshAsync();

		inspectedPath.ShouldBe(@"D:\repo\.git\rebase-merge");
		viewModel.IsRebaseInProgress.ShouldBeTrue();
	}

	[Test]
	public async Task RefreshAsync_keeps_base_status_counters_visible_when_clean()
	{
		FakeGitRunner runner = new();
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		await viewModel.RefreshAsync();

		viewModel.SummaryText.ShouldBe("+0 ~0 -0");
		viewModel.HasStashableChanges.ShouldBeFalse();
	}

	[Test]
	public async Task RefreshAsync_does_not_offer_stash_for_untracked_only_status()
	{
		FakeGitRunner runner = new();
		EnqueueStatusRefresh(runner, UntrackedOnlyStatus);
		var viewModel = CreateViewModel(runner);

		await viewModel.RefreshAsync();

		viewModel.SummaryText.ShouldBe("+0 ~0 -0 ?1");
		viewModel.HasStashableChanges.ShouldBeFalse();
	}

	[Test]
	public async Task RefreshAsync_logs_status_failure_without_throwing()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(
			args => args.Contains("status"),
			new GitCommandResult(1, string.Empty, "git.exe not found in PATH"));
		var viewModel = CreateViewModel(runner);

		await viewModel.RefreshAsync();

		viewModel.BranchText.ShouldBe(string.Empty);
		viewModel.LogText.ShouldContain("git.exe not found in PATH");
	}

	[Test]
	public async Task RefreshAsync_falls_back_to_first_remote_when_origin_is_missing()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(args => args.Contains("status"), new GitCommandResult(0, CleanStatus, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["remote", "get-url", "origin"]), new GitCommandResult(2, string.Empty, "No such remote"));
		runner.Enqueue(
			args => args.SequenceEqual(["remote", "-v"]),
			new GitCommandResult(0, "upstream\thttps://example/upstream.git (fetch)\nupstream\thttps://example/upstream.git (push)\n", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]), new GitCommandResult(1, string.Empty, "missing"));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]), new GitCommandResult(0, @"D:\repo\.git\rebase-merge", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"]), new GitCommandResult(0, @"D:\repo\.git\rebase-apply", string.Empty));
		var viewModel = CreateViewModel(runner);

		await viewModel.RefreshAsync();

		viewModel.RemoteText.ShouldBe("https://example/upstream.git");
	}

	[Test]
	public async Task RefreshAsync_clears_previous_snapshot_when_status_fails()
	{
		FakeGitRunner runner = new();
		EnqueueCleanRefresh(runner);
		runner.Enqueue(
			args => args.Contains("status"),
			new GitCommandResult(1, string.Empty, "status failed"));
		var viewModel = CreateViewModel(runner);
		await viewModel.RefreshAsync();

		await viewModel.RefreshAsync();

		viewModel.Snapshot.ShouldBeNull();
		viewModel.BranchText.ShouldBe(string.Empty);
		viewModel.StashCount.ShouldBe(0);
	}

	[Test]
	public async Task RunCommandAsync_streams_log_refreshes_after_completion_and_blocks_second_command()
	{
		FakeGitRunner runner = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Enqueue(
			args => args.SequenceEqual(["pull"]),
			async progress =>
			{
				progress?.Report("first");
				progress?.Report("second");
				await release.Task;
				return new GitCommandResult(0, "first\nsecond\n", string.Empty);
			});
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		var first = viewModel.RunCommandAsync("Pull", ["pull"]);
		await runner.WaitForCallCountAsync(1);
		var second = viewModel.RunCommandAsync("Pull", ["pull"]);

		viewModel.IsBusy.ShouldBeTrue();
		viewModel.LogText.ShouldContain("> git pull");
		viewModel.LogText.ShouldContain("first");
		viewModel.LogText.ShouldContain("second");

		release.SetResult();
		await Task.WhenAll(first, second);

		viewModel.IsBusy.ShouldBeFalse();
		runner.Calls.Count(call => call.SequenceEqual(["pull"])).ShouldBe(1);
		runner.Calls.ShouldContain(call => call.Contains("status"));
		viewModel.LogText.ShouldContain("first");
	}

	[Test]
	public async Task Running_git_is_reported_while_any_invocation_is_in_flight()
	{
		FakeGitRunner runner = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Enqueue(
			args => args.SequenceEqual(["pull"]),
			async _ =>
			{
				await release.Task;
				return new GitCommandResult(0, string.Empty, string.Empty);
			});
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);
		List<string> changes = [];
		viewModel.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(GitPanelViewModel.IsGitRunning))
			{
				changes.Add($"{viewModel.IsGitRunning}");
			}
		};
		viewModel.IsGitRunning.ShouldBeFalse();

		var command = viewModel.RunCommandAsync("Pull", ["pull"]);
		await runner.WaitForCallCountAsync(1);

		viewModel.IsGitRunning.ShouldBeTrue();

		release.SetResult();
		await command;

		viewModel.IsGitRunning.ShouldBeFalse();
		changes.First().ShouldBe("True");
		changes.Last().ShouldBe("False");
	}

	[Test]
	public async Task Refresh_alone_reports_running_git_without_blocking_commands()
	{
		FakeGitRunner runner = new();
		TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
		runner.Enqueue(
			args => args.Contains("status"),
			async _ =>
			{
				await release.Task;
				return new GitCommandResult(0, CleanStatus, string.Empty);
			});
		EnqueueCleanRefreshWithoutStatus(runner);
		var viewModel = CreateViewModel(runner);

		var refresh = viewModel.RefreshAsync();
		await runner.WaitForCallCountAsync(1);

		viewModel.IsGitRunning.ShouldBeTrue();
		viewModel.IsBusy.ShouldBeFalse();

		release.SetResult();
		await refresh;

		viewModel.IsGitRunning.ShouldBeFalse();
	}

	[Test]
	public async Task Finished_command_reports_its_outcome_as_the_last_log_line()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(args => args.SequenceEqual(["pull"]), new GitCommandResult(0, string.Empty, string.Empty));
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		await viewModel.RunCommandAsync("Pull", ["pull"]);

		viewModel.LogText.TrimEnd().Split('\n')[^1].Trim().ShouldBe("Pull ok");
	}

	[Test]
	public async Task Failed_command_reports_its_exit_code_as_the_last_log_line()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(args => args.SequenceEqual(["pull"]), new GitCommandResult(2, string.Empty, "boom"));
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		await viewModel.RunCommandAsync("Pull", ["pull"]);

		viewModel.LogText.ShouldContain("boom");
		viewModel.LogText.TrimEnd().Split('\n')[^1].Trim().ShouldBe("Pull failed (exit 2)");
	}

	[Test]
	public async Task RunCommandAsync_clears_previous_command_log_before_next_command()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(
			args => args.SequenceEqual(["pull"]),
			progress =>
			{
				progress?.Report("old-output");
				return Task.FromResult(new GitCommandResult(0, "old-output\n", string.Empty));
			});
		EnqueueCleanRefresh(runner);
		runner.Enqueue(
			args => args.SequenceEqual(["status", "--short"]),
			progress =>
			{
				progress?.Report("new-output");
				return Task.FromResult(new GitCommandResult(0, "new-output\n", string.Empty));
			});
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		await viewModel.RunCommandAsync("Pull", ["pull"]);
		await viewModel.RunCommandAsync("Status", ["status", "--short"]);

		viewModel.LogText.ShouldContain("> git status --short");
		viewModel.LogText.ShouldContain("new-output");
		viewModel.LogText.ShouldNotContain("> git pull");
		viewModel.LogText.ShouldNotContain("old-output");
	}

	[Test]
	public async Task RunCommandAsync_marshals_streamed_output_to_captured_synchronization_context()
	{
		RecordingSynchronizationContext context = new();
		var previous = SynchronizationContext.Current;
		FakeGitRunner runner = new();
		runner.Enqueue(
			args => args.SequenceEqual(["pull"]),
			async progress =>
			{
				await Task.Run(() => progress?.Report("background-line"));
				return new GitCommandResult(0, string.Empty, string.Empty);
			});
		EnqueueCleanRefresh(runner);

		SynchronizationContext.SetSynchronizationContext(context);
		var viewModel = CreateViewModel(runner);
		SynchronizationContext.SetSynchronizationContext(previous);

		try
		{
			await viewModel.RunCommandAsync("Pull", ["pull"]);

			context.PostCount.ShouldBe(1);
			viewModel.LogText.ShouldNotContain("background-line");

			context.Drain();

			viewModel.LogText.ShouldContain("background-line");
		}
		finally
		{
			SynchronizationContext.SetSynchronizationContext(previous);
		}
	}

	[Test]
	public async Task RunRebaseOntoBaseScenarioAsync_runs_dirty_plan_in_order_and_stops_on_failure()
	{
		FakeGitRunner runner = new();
		EnqueueStatusRefresh(runner, DirtyWithoutConflictsStatus);
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--verify", "--quiet", "refs/heads/master"]),
			new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["stash", "push"]), new GitCommandResult(0, "stashed", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["switch", "master"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["pull", "--no-rebase"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["switch", "feat/x"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rebase", "master"]), new GitCommandResult(1, string.Empty, "conflict"));
		EnqueueStatusRefresh(runner, DirtyWithoutConflictsStatus);
		var viewModel = CreateViewModel(runner);

		await viewModel.RunRebaseOntoBaseScenarioAsync();

		viewModel.LogText.ShouldContain("Resolve conflicts via Resolve, then run git rebase --continue.");
		viewModel.LogText.ShouldContain("Skipped: Restore stashed changes");
		runner.Calls.Any(call => call.SequenceEqual(["stash", "pop"])).ShouldBeFalse();
	}

	[Test]
	public async Task ResolveAsync_launches_resolve_helper_when_available()
	{
		ResolvedGitHelperAction resolve = new(
			"Helper",
			"resolve",
			"Resolve",
			"helper.exe",
			new ExternalGitHelperAction("resolve", "Resolve", ["resolve", "{root}"]));
		List<ResolvedGitHelperAction> launched = [];
		GitPanelViewModel viewModel = new(
			@"D:\repo",
			new FakeGitRunner(),
			[resolve],
			(action, _, _) => launched.Add(action),
			_ => false);

		await viewModel.ResolveAsync();

		launched.ShouldBe([resolve]);
	}

	[Test]
	public async Task ResolveAsync_falls_back_to_mergetool_and_logs_configuration_hint()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(
			args => args.SequenceEqual(["mergetool", "-y"]),
			new GitCommandResult(1, string.Empty, "No known merge resolution program available."));
		EnqueueCleanRefresh(runner);
		var viewModel = CreateViewModel(runner);

		await viewModel.ResolveAsync();

		viewModel.LogText.ShouldContain("git config --global merge.tool <tool>");
	}

	[Test]
	public void ReportError_clears_previous_log_and_appends_message()
	{
		var viewModel = CreateViewModel(new FakeGitRunner());

		viewModel.ReportError("old failure");
		viewModel.ReportError("new failure");

		viewModel.LogText.ShouldNotContain("old failure");
		viewModel.LogText.ShouldContain("new failure");
	}

	[Test]
	public void Commands_default_to_builtin_arguments_and_visible_buttons()
	{
		var viewModel = CreateViewModel(new FakeGitRunner());

		viewModel.Commands.Arguments(GitButtonCommandSet.PullId).ShouldBe(["pull", "--no-rebase"]);
		viewModel.IsPullVisible.ShouldBeTrue();
		viewModel.IsMergeVisible.ShouldBeTrue();
		viewModel.CustomCommands.ShouldBeEmpty();
	}

	[Test]
	public void Disabled_builtin_command_hides_its_button()
	{
		var commands = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord(GitButtonCommandSet.MergeId, "Merge", Enabled: false)]);
		var viewModel = CreateViewModel(new FakeGitRunner(), commands);

		viewModel.IsMergeVisible.ShouldBeFalse();
		viewModel.IsPullVisible.ShouldBeTrue();
	}

	[Test]
	public void PopupButtons_render_in_configured_order_with_custom_entries_interleaved()
	{
		var commands = GitButtonCommandSet.Create(
		[
			new GitButtonCommandRecord("push", "Push"),
			new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune"),
			new GitButtonCommandRecord("pull", "Pull", Command: "pull --no-rebase")
		]);
		var viewModel = CreateViewModel(new FakeGitRunner(), commands);

		viewModel.PopupButtons.Take(3).Select(button => button.Id).ShouldBe(["push", "fetch-prune", "pull"]);
		var custom = viewModel.PopupButtons.Where(button => button.Kind == GitPopupButtonKind.Custom).ShouldHaveSingleItem();
		custom.Label.ShouldBe("Fetch");
		custom.CustomArguments.ShouldBe(["fetch", "--prune"]);
	}

	[Test]
	public async Task PopupButtons_keep_stash_slots_visible_and_update_enablement_from_state()
	{
		FakeGitRunner runner = new();
		runner.Enqueue(args => args.Contains("status"), new GitCommandResult(0, DirtyStatus, string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["remote", "get-url", "origin"]),
			new GitCommandResult(0, "https://example/repo.git\n", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]),
			new GitCommandResult(0, "2\n", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]),
			new GitCommandResult(0, @"D:\repo\.git\rebase-merge", string.Empty));
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"]),
			new GitCommandResult(0, @"D:\repo\.git\rebase-apply", string.Empty));
		var viewModel = CreateViewModel(runner);
		var stash = viewModel.PopupButtons
			.Single(button => button.Id == GitButtonCommandSet.StashId);
		var popStash = viewModel.PopupButtons
			.Single(button => button.Id == GitButtonCommandSet.StashPopId);

		stash.IsVisible.ShouldBeTrue();
		stash.IsEnabled.ShouldBeFalse();
		popStash.IsVisible.ShouldBeTrue();
		popStash.IsEnabled.ShouldBeFalse();

		await viewModel.RefreshAsync();

		stash.IsVisible.ShouldBeTrue();
		stash.IsEnabled.ShouldBeTrue();
		popStash.IsVisible.ShouldBeTrue();
		popStash.IsEnabled.ShouldBeTrue();
	}

	[Test]
	public void PopupButtons_excludes_disabled_builtin()
	{
		var commands = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord(GitButtonCommandSet.MergeId, "Merge", Enabled: false)]);
		var viewModel = CreateViewModel(new FakeGitRunner(), commands);

		viewModel.PopupButtons.ShouldNotContain(button => button.Id == GitButtonCommandSet.MergeId);
	}

	[Test]
	public void Dialog_popup_buttons_use_the_same_ordinary_visibility_contract()
	{
		var viewModel = CreateViewModel(new FakeGitRunner());
		var push = viewModel.PopupButtons
			.Single(button => button.Id == GitButtonCommandSet.PushId);

		push.IsVisible.ShouldBeTrue();
	}

	[Test]
	public void CustomCommands_surfaces_user_added_entries()
	{
		var commands = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("fetch-prune", "Fetch", Command: "fetch --prune")]);
		var viewModel = CreateViewModel(new FakeGitRunner(), commands);

		var custom = viewModel.CustomCommands.ShouldHaveSingleItem();
		custom.Label.ShouldBe("Fetch");
		custom.Arguments.ShouldBe(["fetch", "--prune"]);
	}

	[Test]
	public async Task RunRebaseOntoBaseScenarioAsync_uses_configured_pull_command()
	{
		var commands = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord(GitButtonCommandSet.PullId, "Pull", Command: "pull --rebase")]);
		FakeGitRunner runner = new();
		EnqueueStatusRefresh(runner, DirtyWithoutConflictsStatus);
		runner.Enqueue(
			args => args.SequenceEqual(["rev-parse", "--verify", "--quiet", "refs/heads/master"]),
			new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["stash", "push"]), new GitCommandResult(0, "stashed", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["switch", "master"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["pull", "--rebase"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["switch", "feat/x"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rebase", "master"]), new GitCommandResult(0, string.Empty, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["stash", "pop"]), new GitCommandResult(0, string.Empty, string.Empty));
		EnqueueStatusRefresh(runner, DirtyWithoutConflictsStatus);
		var viewModel = CreateViewModel(runner, commands);

		await viewModel.RunRebaseOntoBaseScenarioAsync();

		runner.Calls.Any(call => call.SequenceEqual(["pull", "--rebase"])).ShouldBeTrue();
	}

	[Test]
	public void HelperActions_exposes_history_and_custom_actions_only()
	{
		var history = CreateAction("history");
		var custom = CreateAction("custom");
		var resolve = CreateAction("resolve");

		GitPanelViewModel viewModel = new(
			@"D:\repo",
			new FakeGitRunner(),
			[history, custom, resolve],
			(_, _, _) => { },
			_ => false);

		viewModel.HelperActions.ShouldBe([history, custom]);
	}

	private const string CleanStatus = """
        # branch.oid 1111111111111111111111111111111111111111
        # branch.head feat/x
        # branch.upstream origin/feat/x
        # branch.ab +0 -0
        """;

	private const string DirtyStatus = """
        # branch.oid 1111111111111111111111111111111111111111
        # branch.head feat/x
        # branch.upstream origin/feat/x
        # branch.ab +2 -1
        1 A. N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 added.txt
        1 .M N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 modified.txt
        1 .D N... 100644 100644 000000 1111111111111111111111111111111111111111 0000000000000000000000000000000000000000 deleted.txt
        2 R. N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 R100 new-name.txt	old-name.txt
        ? untracked.txt
        u UU N... 100644 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 3333333333333333333333333333333333333333 4444444444444444444444444444444444444444 conflict.txt
        """;

	private const string DirtyWithoutConflictsStatus = """
        # branch.oid 1111111111111111111111111111111111111111
        # branch.head feat/x
        # branch.upstream origin/feat/x
        # branch.ab +2 -1
        1 .M N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 modified.txt
        ? untracked.txt
        """;

	private const string UntrackedOnlyStatus = """
        # branch.oid 1111111111111111111111111111111111111111
        # branch.head feat/x
        # branch.upstream origin/feat/x
        # branch.ab +0 -0
        ? untracked.txt
        """;

	private static GitPanelViewModel CreateViewModel(FakeGitRunner runner, GitButtonCommandSet? commands = null) => new GitPanelViewModel(
			@"D:\repo",
			runner,
			helperActions: [],
			launchHelperAction: (_, _, _) => { },
			directoryExists: _ => false,
			commands);

	private static ResolvedGitHelperAction CreateAction(string slot) => new ResolvedGitHelperAction(
			"Helper",
			slot,
			slot,
			"helper.exe",
			new ExternalGitHelperAction(slot, slot, [slot]));

	private static void EnqueueStatusRefresh(FakeGitRunner runner, string status)
	{
		runner.Enqueue(args => args.Contains("status"), new GitCommandResult(0, status, string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["remote", "get-url", "origin"]), new GitCommandResult(0, "origin-url\n", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]), new GitCommandResult(1, string.Empty, "missing"));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]), new GitCommandResult(0, @"D:\repo\.git\rebase-merge", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"]), new GitCommandResult(0, @"D:\repo\.git\rebase-apply", string.Empty));
	}

	private static void EnqueueCleanRefresh(FakeGitRunner runner) => EnqueueStatusRefresh(runner, CleanStatus);

	private static void EnqueueCleanRefreshWithoutStatus(FakeGitRunner runner)
	{
		runner.Enqueue(args => args.SequenceEqual(["remote", "get-url", "origin"]), new GitCommandResult(0, "origin-url\n", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-list", "--walk-reflogs", "--count", "refs/stash"]), new GitCommandResult(1, string.Empty, "missing"));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-merge"]), new GitCommandResult(0, @"D:\repo\.git\rebase-merge", string.Empty));
		runner.Enqueue(args => args.SequenceEqual(["rev-parse", "--git-path", "rebase-apply"]), new GitCommandResult(0, @"D:\repo\.git\rebase-apply", string.Empty));
	}

	private sealed class FakeGitRunner : IGitCliRunner
	{
		private readonly Queue<(Func<IReadOnlyList<string>, bool> Matches, Func<IProgress<string>?, Task<GitCommandResult>> Run)> _scripts = [];
		private readonly TaskCompletionSource _callChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public List<IReadOnlyList<string>> Calls { get; } = [];

		public void Enqueue(Func<IReadOnlyList<string>, bool> matches, GitCommandResult result) => Enqueue(matches, _ => Task.FromResult(result));

		public void Enqueue(Func<IReadOnlyList<string>, bool> matches, Func<IProgress<string>?, Task<GitCommandResult>> run) => _scripts.Enqueue((matches, run));

		public async Task<GitCommandResult> RunAsync(
			string workingDirectory,
			IReadOnlyList<string> arguments,
			IProgress<string>? outputLine,
			CancellationToken cancellationToken)
		{
			Calls.Add(arguments.ToArray());
			_callChanged.TrySetResult();
			if (_scripts.Count == 0)
			{
				return new GitCommandResult(0, string.Empty, string.Empty);
			}

			(var matches, var run) = _scripts.Dequeue();
			matches(arguments).ShouldBeTrue($"Unexpected git arguments: {string.Join(' ', arguments)}");
			return await run(outputLine);
		}

		public async Task WaitForCallCountAsync(int count)
		{
			if (Calls.Count >= count)
			{
				return;
			}

			await _callChanged.Task;
		}
	}

	private sealed class RecordingSynchronizationContext : SynchronizationContext
	{
		private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = [];

		public int PostCount { get; private set; }

		public override void Post(SendOrPostCallback d, object? state)
		{
			PostCount++;
			_callbacks.Enqueue((d, state));
		}

		public void Drain()
		{
			while (_callbacks.Count > 0)
			{
				(var callback, var state) = _callbacks.Dequeue();
				callback(state);
			}
		}
	}
}