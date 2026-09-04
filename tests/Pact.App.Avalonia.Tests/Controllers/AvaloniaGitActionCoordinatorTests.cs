using Pact.App.Avalonia.Controllers;
using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Controllers;

public sealed class AvaloniaGitActionCoordinatorTests
{
	[Test]
	public async Task Commit_stages_selected_files_and_uses_configured_extra_flags()
	{
		RecordingGitRunner runner = new();
		var panel = CreatePanel(runner, Commands(
			new GitButtonCommandRecord(GitButtonCommandSet.CommitId, "Commit", ExtraArgs: "--signoff")));
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: viewModel => Task.FromResult<GitCommitDialogResult?>(
				new("message", [viewModel.Files[0].Entry])),
			pushDialog: _ => Task.FromResult<GitPushDialogResult?>(null),
			branchDialog: _ => Task.FromResult<GitBranchPickDialogResult?>(null));

		await coordinator.RunCommitAsync(panel, TestContext.CurrentContext.CancellationToken);

		runner.Calls.Any(call => call.SequenceEqual(["add", "-A", "--", "modified.txt"]))
			.ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(
				["commit", "--signoff", "-m", "message", "--", "modified.txt"]))
			.ShouldBeTrue();
	}

	[Test]
	[TestCase(GitPushMode.Normal, false, "push,--follow-tags,origin,feat/x")]
	[TestCase(GitPushMode.ForceWithLease, true, "push,--follow-tags,--force-with-lease,-u,origin,feat/x")]
	[TestCase(GitPushMode.Force, false, "push,--follow-tags,--force,origin,feat/x")]
	public async Task Push_uses_mode_upstream_origin_branch_and_extra_flags(
		GitPushMode mode,
		bool setUpstream,
		string expectedCsv)
	{
		ArgumentNullException.ThrowIfNull(expectedCsv);
		RecordingGitRunner runner = new(hasUpstream: !setUpstream);
		var panel = CreatePanel(runner, Commands(
			new GitButtonCommandRecord(GitButtonCommandSet.PushId, "Push", ExtraArgs: "--follow-tags")));
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: _ => Task.FromResult<GitCommitDialogResult?>(null),
			pushDialog: viewModel =>
			{
				viewModel.Mode = mode;
				viewModel.SetUpstream = setUpstream;
				return Task.FromResult<GitPushDialogResult?>(viewModel.CreateResult());
			},
			branchDialog: _ => Task.FromResult<GitBranchPickDialogResult?>(null));

		await coordinator.RunPushAsync(panel, TestContext.CurrentContext.CancellationToken);

		var expected = expectedCsv.Split(',');
		runner.Calls.Any(call => call.SequenceEqual(expected)).ShouldBeTrue();
	}

	[Test]
	public async Task Branch_actions_filter_current_and_remote_head_and_build_exact_commands()
	{
		RecordingGitRunner runner = new();
		Queue<GitBranchPickDialogResult> choices = new([
			new("dev", false),
			new("origin/remote", false),
			new("new-local", true),
			new("dev", false),
			new("dev", false)
		]);
		List<GitBranchDialogRequest> requests = [];
		var panel = CreatePanel(runner, Commands(
			new GitButtonCommandRecord(GitButtonCommandSet.SwitchId, "Switch", ExtraArgs: "--quiet"),
			new GitButtonCommandRecord(GitButtonCommandSet.RebaseId, "Rebase", ExtraArgs: "--autostash"),
			new GitButtonCommandRecord(GitButtonCommandSet.MergeId, "Merge", ExtraArgs: "--no-ff")));
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: _ => Task.FromResult<GitCommitDialogResult?>(null),
			pushDialog: _ => Task.FromResult<GitPushDialogResult?>(null),
			branchDialog: request =>
			{
				requests.Add(request);
				return Task.FromResult<GitBranchPickDialogResult?>(choices.Dequeue());
			});

		await coordinator.RunSwitchAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunSwitchAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunSwitchAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunRebaseAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunMergeAsync(panel, TestContext.CurrentContext.CancellationToken);

		requests[0].ViewModel.Branches.Any(branch =>
				branch == "feat/x"
				|| branch.EndsWith("/HEAD", StringComparison.Ordinal))
			.ShouldBeFalse();
		runner.Calls.Any(call => call.SequenceEqual(["switch", "--quiet", "dev"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["switch", "--quiet", "--track", "origin/remote"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["switch", "--quiet", "-c", "new-local"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["rebase", "--autostash", "dev"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["merge", "--no-ff", "dev"])).ShouldBeTrue();
	}

	[Test]
	public async Task Cancelled_dialog_does_not_run_mutating_command()
	{
		RecordingGitRunner runner = new();
		var panel = CreatePanel(runner);
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: _ => Task.FromResult<GitCommitDialogResult?>(null),
			pushDialog: _ => Task.FromResult<GitPushDialogResult?>(null),
			branchDialog: _ => Task.FromResult<GitBranchPickDialogResult?>(null));

		await coordinator.RunCommitAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunPushAsync(panel, TestContext.CurrentContext.CancellationToken);
		await coordinator.RunSwitchAsync(panel, TestContext.CurrentContext.CancellationToken);

		runner.Calls.Any(call =>
				call.Count > 0
				&& call[0] is "add" or "commit" or "push" or "switch")
			.ShouldBeFalse();
	}

	[Test]
	public async Task Commit_dialog_failure_is_projected_into_the_git_panel_log()
	{
		RecordingGitRunner runner = new();
		var panel = CreatePanel(runner);
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: _ => Task.FromException<GitCommitDialogResult?>(
				new IOException("dialog failed")),
			pushDialog: _ => Task.FromResult<GitPushDialogResult?>(null),
			branchDialog: _ => Task.FromResult<GitBranchPickDialogResult?>(null));

		await coordinator.RunCommitAsync(
			panel,
			TestContext.CurrentContext.CancellationToken);

		panel.LogText.ShouldContain("Commit failed: dialog failed");
	}

	[Test]
	public async Task Direct_custom_conflict_and_helper_actions_dispatch_to_existing_panel_operations()
	{
		RecordingGitRunner runner = new();
		ResolvedGitHelperAction helper = new(
			"History", "history", "History", "helper.exe",
			new ExternalGitHelperAction("history", "History", ["{root}"]));
		List<ResolvedGitHelperAction> launched = [];
		var commands = GitButtonCommandSet.Create(
			[new GitButtonCommandRecord("status-short", "Status", Command: "status --short")]);
		GitPanelViewModel panel = new(
			@"D:\repo", runner, [helper], (action, _, _) => launched.Add(action), _ => false, commands);
		AvaloniaGitActionCoordinator coordinator = new(
			runner,
			commitDialog: _ => Task.FromResult<GitCommitDialogResult?>(null),
			pushDialog: _ => Task.FromResult<GitPushDialogResult?>(null),
			branchDialog: _ => Task.FromResult<GitBranchPickDialogResult?>(null));

		await coordinator.RunPopupButtonAsync(
			panel,
			panel.PopupButtons.Single(button => button.Id == "status-short"),
			TestContext.CurrentContext.CancellationToken);
		await AvaloniaGitActionCoordinator.RunAbortRebaseAsync(panel, TestContext.CurrentContext.CancellationToken);
		await AvaloniaGitActionCoordinator.RunResolveAsync(panel, TestContext.CurrentContext.CancellationToken);
		await AvaloniaGitActionCoordinator.RunRebaseOntoBaseAsync(panel, TestContext.CurrentContext.CancellationToken);
		AvaloniaGitActionCoordinator.LaunchHelper(panel, helper);

		runner.Calls.Any(call => call.SequenceEqual(["status", "--short"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["rebase", "--abort"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["mergetool", "-y"])).ShouldBeTrue();
		runner.Calls.Any(call => call.SequenceEqual(["rebase", "master"])).ShouldBeTrue();
		launched.ShouldBe([helper]);
	}

	private static GitPanelViewModel CreatePanel(RecordingGitRunner runner, GitButtonCommandSet? commands = null) =>
		new(@"D:\repo", runner, [], (_, _, _) => { }, _ => false, commands);

	private static GitButtonCommandSet Commands(params GitButtonCommandRecord[] records) =>
		GitButtonCommandSet.Create(records);

	private sealed class RecordingGitRunner(bool hasUpstream = true) : IGitCliRunner
	{
		public List<IReadOnlyList<string>> Calls { get; } = [];

		public Task<GitCommandResult> RunAsync(
			string workingDirectory,
			IReadOnlyList<string> arguments,
			IProgress<string>? outputLine,
			CancellationToken cancellationToken)
		{
			Calls.Add(arguments.ToArray());
			var stdout = arguments switch
			{
				["--no-optional-locks", "status", "--porcelain=v2", "--branch"] => Status(hasUpstream),
				["remote", "get-url", "origin"] => "https://example/repo.git\n",
				["rev-list", "--walk-reflogs", "--count", "refs/stash"] => "0\n",
				["rev-parse", "--git-path", _] => @"D:\repo\.git\missing",
				["for-each-ref", _, "refs/heads"] => "feat/x\ndev\n",
				["for-each-ref", _, "refs/remotes"] => "origin/HEAD\norigin/remote\n",
				_ => string.Empty
			};
			outputLine?.Report(stdout);
			return Task.FromResult(new GitCommandResult(0, stdout, string.Empty));
		}

		private static string Status(bool hasUpstream) => $$"""
            # branch.oid 1111111111111111111111111111111111111111
            # branch.head feat/x
            {{(hasUpstream ? "# branch.upstream origin/feat/x" : string.Empty)}}
            # branch.ab +0 -0
            1 .M N... 100644 100644 100644 1111111111111111111111111111111111111111 2222222222222222222222222222222222222222 modified.txt
            """;
	}
}
