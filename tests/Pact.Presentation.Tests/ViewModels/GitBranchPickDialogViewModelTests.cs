using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class GitBranchPickDialogViewModelTests
{
	[Test]
	public void Selected_existing_branch_can_be_accepted()
	{
		GitBranchPickDialogViewModel viewModel = new(["main", "feature/a"], allowCreate: false)
		{
			SelectedBranch = "feature/a"
		};

		var result = viewModel.CreateResult();

		viewModel.CanAccept.ShouldBeTrue();
		result.ShouldNotBeNull();
		result.Branch.ShouldBe("feature/a");
		result.Create.ShouldBeFalse();
	}

	[Test]
	public void New_branch_requires_creation_to_be_allowed()
	{
		GitBranchPickDialogViewModel viewModel = new(["main"], allowCreate: false)
		{
			NewBranchName = "feature/new"
		};

		viewModel.CanAccept.ShouldBeFalse();
		viewModel.CreateResult().ShouldBeNull();
	}

	[Test]
	public void Typed_new_branch_wins_over_selected_existing_branch()
	{
		GitBranchPickDialogViewModel viewModel = new(["main"], allowCreate: true)
		{
			SelectedBranch = "main",
			NewBranchName = "  feature/new  "
		};

		var result = viewModel.CreateResult();

		viewModel.CanAccept.ShouldBeTrue();
		result.ShouldNotBeNull();
		result.Branch.ShouldBe("feature/new");
		result.Create.ShouldBeTrue();
	}

	[Test]
	public void Blank_selection_and_blank_new_name_cannot_be_accepted()
	{
		GitBranchPickDialogViewModel viewModel = new(["main"], allowCreate: true)
		{
			NewBranchName = " "
		};

		viewModel.CanAccept.ShouldBeFalse();
		viewModel.CreateResult().ShouldBeNull();
	}
}