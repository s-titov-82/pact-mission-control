using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Controllers;

internal sealed record GitBranchDialogRequest(
	string Title,
	string HelpText,
	string AcceptText,
	GitBranchPickDialogViewModel ViewModel);

internal sealed class AvaloniaGitActionCoordinator
{
	private readonly IGitCliRunner _runner;
	private readonly Func<GitCommitDialogViewModel, Task<GitCommitDialogResult?>> _commitDialog;
	private readonly Func<GitPushDialogViewModel, Task<GitPushDialogResult?>> _pushDialog;
	private readonly Func<GitBranchDialogRequest, Task<GitBranchPickDialogResult?>> _branchDialog;

	public AvaloniaGitActionCoordinator(
		IGitCliRunner runner,
		Func<GitCommitDialogViewModel, Task<GitCommitDialogResult?>> commitDialog,
		Func<GitPushDialogViewModel, Task<GitPushDialogResult?>> pushDialog,
		Func<GitBranchDialogRequest, Task<GitBranchPickDialogResult?>> branchDialog)
	{
		_runner = runner ?? throw new ArgumentNullException(nameof(runner));
		_commitDialog = commitDialog ?? throw new ArgumentNullException(nameof(commitDialog));
		_pushDialog = pushDialog ?? throw new ArgumentNullException(nameof(pushDialog));
		_branchDialog = branchDialog ?? throw new ArgumentNullException(nameof(branchDialog));
	}

	public Task RunPopupButtonAsync(
		GitPanelViewModel panel,
		GitPopupButtonViewModel button,
		CancellationToken cancellationToken = default) => button.Id switch
		{
			GitButtonCommandSet.CommitId => RunCommitAsync(panel, cancellationToken),
			GitButtonCommandSet.PushId => RunPushAsync(panel, cancellationToken),
			GitButtonCommandSet.SwitchId => RunSwitchAsync(panel, cancellationToken),
			GitButtonCommandSet.RebaseId => RunRebaseAsync(panel, cancellationToken),
			GitButtonCommandSet.MergeId => RunMergeAsync(panel, cancellationToken),
			_ => RunDirectAsync(
				panel,
				button.Label,
				button.Kind == GitPopupButtonKind.Custom
					? button.CustomArguments ?? []
					: panel.Commands.Arguments(button.Id),
				cancellationToken)
		};

	public async Task RunCommitAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) => await RunSafelyAsync(panel, "Commit", async () =>
																													 {
																														 await panel.RefreshAsync(cancellationToken);
																														 if (panel.Snapshot is not { Files.Count: > 0 } snapshot)
																														 {
																															 return;
																														 }

																														 var result = await _commitDialog(new GitCommitDialogViewModel(snapshot.Files));
																														 if (result is null)
																														 {
																															 return;
																														 }

																														 await panel.RunCommandAsync(
																															 "Stage",
																															 GitCommandBuilder.BuildStageArguments(result.Files),
																															 cancellationToken);
																														 await panel.RunCommandAsync(
																															 "Commit",
																															 GitCommandBuilder.BuildCommitArguments(
																																 result.Message,
																																 result.Files,
																																 panel.Commands.ExtraArguments(GitButtonCommandSet.CommitId)),
																															 cancellationToken);
																													 });

	public async Task RunPushAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) => await RunSafelyAsync(panel, "Push", async () =>
																												   {
																													   await panel.RefreshAsync(cancellationToken);
																													   if (string.IsNullOrWhiteSpace(panel.BranchText))
																													   {
																														   return;
																													   }

																													   var result = await _pushDialog(new GitPushDialogViewModel(
																														   panel.BranchText,
																														   hasUpstream: !string.IsNullOrWhiteSpace(panel.Snapshot?.Upstream)));
																													   if (result is null)
																													   {
																														   return;
																													   }

																													   await panel.RunCommandAsync(
																														   "Push",
																														   GitCommandBuilder.BuildPushArguments(
																															   result.Remote,
																															   panel.BranchText,
																															   result.Mode,
																															   result.SetUpstream,
																															   panel.Commands.ExtraArguments(GitButtonCommandSet.PushId)),
																														   cancellationToken);
																												   });

	public Task RunSwitchAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunBranchAsync(
			panel,
			"Switch branch",
			"Choose a local branch, choose a remote branch to track, or enter a new local branch name.",
			"Switch",
			allowCreate: true,
			includeRemotes: true,
			(result, trackRemote) => GitCommandBuilder.BuildSwitchArguments(
				result.Branch,
				result.Create,
				trackRemote,
				panel.Commands.ExtraArguments(GitButtonCommandSet.SwitchId)),
			cancellationToken);

	public Task RunRebaseAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunBranchAsync(
			panel,
			"Rebase current branch onto selected branch",
			"The current branch will be replayed on top of the selected local branch.",
			"Rebase",
			allowCreate: false,
			includeRemotes: false,
			(result, _) => GitCommandBuilder.BuildRebaseArguments(
				result.Branch,
				panel.Commands.ExtraArguments(GitButtonCommandSet.RebaseId)),
			cancellationToken);

	public Task RunMergeAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunBranchAsync(
			panel,
			"Merge selected branch into current branch",
			"The selected local branch will be merged into the current branch.",
			"Merge",
			allowCreate: false,
			includeRemotes: false,
			(result, _) => GitCommandBuilder.BuildMergeArguments(
				result.Branch,
				panel.Commands.ExtraArguments(GitButtonCommandSet.MergeId)),
			cancellationToken);

	public static Task RunRebaseOntoBaseAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunSafelyAsync(panel, "Rebase onto base", () => panel.RunRebaseOntoBaseScenarioAsync(cancellationToken));

	public static Task RunResolveAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunSafelyAsync(panel, "Resolve", () => panel.ResolveAsync(cancellationToken));

	public static Task RunAbortRebaseAsync(GitPanelViewModel panel, CancellationToken cancellationToken = default) =>
		RunSafelyAsync(panel, "Abort rebase", () => panel.AbortRebaseAsync(cancellationToken));

	public static void LaunchHelper(GitPanelViewModel panel, ResolvedGitHelperAction action) =>
		panel.LaunchHelperAction(action);

	private static Task RunDirectAsync(
		GitPanelViewModel panel,
		string title,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken) =>
		RunSafelyAsync(panel, title, () => panel.RunCommandAsync(title, arguments, cancellationToken));

	private async Task RunBranchAsync(
		GitPanelViewModel panel,
		string title,
		string helpText,
		string acceptText,
		bool allowCreate,
		bool includeRemotes,
		Func<GitBranchPickDialogResult, bool, IReadOnlyList<string>> buildArguments,
		CancellationToken cancellationToken) => await RunSafelyAsync(panel, title, async () =>
													 {
														 await panel.RefreshAsync(cancellationToken);
														 var branches = (await ReadGitLinesAsync(
																 panel.RootPath,
																 ["for-each-ref", "--format=%(refname:short)", "refs/heads"],
																 cancellationToken))
															 .Where(branch => !string.Equals(branch, panel.BranchText, StringComparison.Ordinal))
															 .Distinct(StringComparer.Ordinal)
															 .ToList();
														 HashSet<string> remoteBranches = new(StringComparer.Ordinal);
														 if (includeRemotes)
														 {
															 foreach (var branch in await ReadGitLinesAsync(
																		  panel.RootPath,
																		  ["for-each-ref", "--format=%(refname:short)", "refs/remotes"],
																		  cancellationToken))
															 {
																 if (!branch.EndsWith("/HEAD", StringComparison.Ordinal)
																	 && remoteBranches.Add(branch))
																 {
																	 branches.Add(branch);
																 }
															 }
														 }

														 var result = await _branchDialog(new GitBranchDialogRequest(
															 title,
															 helpText,
															 acceptText,
															 new GitBranchPickDialogViewModel(branches, allowCreate)));
														 if (result is null)
														 {
															 return;
														 }

														 await panel.RunCommandAsync(
															 title,
															 buildArguments(result, remoteBranches.Contains(result.Branch)),
															 cancellationToken);
													 });

	private async Task<IReadOnlyList<string>> ReadGitLinesAsync(
		string rootPath,
		IReadOnlyList<string> arguments,
		CancellationToken cancellationToken)
	{
		var result = await _runner.RunAsync(rootPath, arguments, null, cancellationToken);
		return result.Succeeded
			? result.StandardOutput.Split(
				['\r', '\n'],
				StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			: [];
	}

	private static async Task RunSafelyAsync(
		GitPanelViewModel panel,
		string title,
		Func<Task> action)
	{
		try
		{
			await action();
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception exception)
		{
			panel.ReportError($"{title} failed: {exception.Message}");
		}
	}
}
