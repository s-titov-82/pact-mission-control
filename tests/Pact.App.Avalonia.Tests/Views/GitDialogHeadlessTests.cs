using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.LogicalTree;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class GitDialogHeadlessTests
{
	[AvaloniaTest]
	public async Task Commit_picker_binds_history_and_accepts_an_explicit_ref_at_narrow_width()
	{
		using GitCommitPickerViewModel model = new("C:/repo", new CommitReader());
		Pact.App.Avalonia.Lifecycle.ObservedTaskGroup tasks = new(static (_, _) => Task.CompletedTask);
		using GitCommitPickerWindow dialog = new(model, tasks) { Width = 520, Height = 400 };
		dialog.Show();
		try
		{
			await tasks.WaitForIdleAsync();
			dialog.UpdateLayout();
			var list = dialog.GetLogicalDescendants().OfType<ListBox>().Single();
			list.ItemCount.ShouldBe(1);
			list.SelectedItem = model.Commits[0];
			model.SelectedCommit.ShouldBeSameAs(model.Commits[0]);
			dialog.FindControl<TextBox>("RevisionBox")!.Text = "HEAD~1";
			model.Revision.ShouldBe("HEAD~1");
			(await model.AcceptAsync(CancellationToken.None)).ShouldNotBeNull().Id.ShouldBe(new string('a', 40));
			list.Bounds.Width.ShouldBeGreaterThan(400);
		}
		finally { dialog.Close(); }
	}

	private sealed class CommitReader : IGitCommitReader
	{
		private readonly GitCommitInfo _commit = new(new string('a', 40), [], "Author", DateTimeOffset.MinValue, "A descriptive historical commit");
		public Task<IReadOnlyList<GitCommitInfo>> ReadCommitsAsync(string root, int skip, int count, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GitCommitInfo>>([_commit]);
		public Task<GitCommitInfo?> ResolveCommitAsync(string root, string revision, CancellationToken cancellationToken) => Task.FromResult<GitCommitInfo?>(_commit);
		public Task<CommitChanges> ReadCommitChangesAsync(string root, GitCommitInfo commit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<FileDiff> ReadCommitDiffAsync(string root, GitFileEntry entry, GitCommitInfo commit, CancellationToken cancellationToken) => throw new NotSupportedException();
		public Task<string?> ReadCommitTextAsync(string root, string path, string commitId, CancellationToken cancellationToken) => throw new NotSupportedException();
	}
	[AvaloniaTest]
	public void Commit_dialog_binds_shared_model_and_keeps_file_list_resizable()
	{
		GitCommitDialogViewModel viewModel = new([new("a.txt", null, GitChangeKind.Modified)]);
		GitCommitDialog dialog = new(viewModel);

		dialog.DataContext.ShouldBeSameAs(viewModel);
		dialog.FindControl<ItemsControl>("FilesList").ShouldNotBeNull();
		dialog.FindControl<GridSplitter>("FilesSplitter").ShouldNotBeNull();
		dialog.FindControl<Button>("CommitButton")!.IsDefault.ShouldBeTrue();
	}

	[AvaloniaTest]
	public void Push_and_branch_dialogs_bind_shared_models_and_offer_cancel()
	{
		GitPushDialog push = new(new GitPushDialogViewModel("feat/x", hasUpstream: false));
		GitBranchPickDialog branch = new(
			new GitBranchPickDialogViewModel(["main"], allowCreate: true),
			"Switch", "Help", "Switch");

		push.FindControl<ComboBox>("PushModeBox").ShouldNotBeNull();
		push.FindControl<Button>("PushCancelButton")!.IsCancel.ShouldBeTrue();
		branch.DataContext.ShouldBeSameAs(branch.ViewModel);
		branch.FindControl<ListBox>("BranchList").ShouldNotBeNull();
		branch.FindControl<Button>("BranchCancelButton")!.IsCancel.ShouldBeTrue();
	}
}
