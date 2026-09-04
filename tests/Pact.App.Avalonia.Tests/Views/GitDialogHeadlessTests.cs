using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Pact.App.Avalonia.Views.Dialogs;
using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.App.Avalonia.Tests.Views;

public sealed class GitDialogHeadlessTests
{
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