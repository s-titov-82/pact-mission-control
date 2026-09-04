using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class GitPushDialogViewModelTests
{
	[Test]
	public void Without_upstream_defaults_to_normal_push_and_sets_upstream()
	{
		GitPushDialogViewModel viewModel = new("feature/test", hasUpstream: false);

		viewModel.Branch.ShouldBe("feature/test");
		viewModel.Mode.ShouldBe(GitPushMode.Normal);
		viewModel.SetUpstream.ShouldBeTrue();
		viewModel.CanChangeSetUpstream.ShouldBeTrue();
	}

	[Test]
	public void Existing_upstream_disables_and_clears_set_upstream()
	{
		GitPushDialogViewModel viewModel = new("main", hasUpstream: true);

		viewModel.SetUpstream.ShouldBeFalse();
		viewModel.CanChangeSetUpstream.ShouldBeFalse();
	}

	[Test]
	public void Result_uses_selected_mode_and_fixed_origin_remote()
	{
		GitPushDialogViewModel viewModel = new("feature/test", hasUpstream: false)
		{
			Mode = GitPushMode.ForceWithLease,
			SetUpstream = false
		};

		var result = viewModel.CreateResult();

		result.Remote.ShouldBe("origin");
		result.Mode.ShouldBe(GitPushMode.ForceWithLease);
		result.SetUpstream.ShouldBeFalse();
	}
}