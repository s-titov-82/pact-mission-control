using Pact.Core.Git;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class GitCommitDialogViewModelTests
{
	[Test]
	public void Constructor_selects_every_file()
	{
		GitCommitDialogViewModel viewModel = new(
		[
			Entry("a.txt", GitChangeKind.Added),
			Entry("b.txt", GitChangeKind.Modified)
		]);

		viewModel.Files.ShouldAllBe(file => file.IsSelected);
		viewModel.AreAllFilesSelected.ShouldBeTrue();
	}

	[Test]
	[TestCase(GitChangeKind.Added, "+")]
	[TestCase(GitChangeKind.Modified, "~")]
	[TestCase(GitChangeKind.Deleted, "-")]
	[TestCase(GitChangeKind.Untracked, "?")]
	[TestCase(GitChangeKind.Conflicted, "!")]
	public void File_choice_maps_change_kind_to_marker(GitChangeKind kind, string marker)
	{
		GitCommitDialogViewModel viewModel = new([Entry("file.txt", kind)]);

		viewModel.Files.ShouldHaveSingleItem().Marker.ShouldBe(marker);
	}

	[Test]
	public void File_choice_displays_rename_from_original_to_current_path()
	{
		GitCommitDialogViewModel viewModel = new(
			[new GitFileEntry("new.txt", "old.txt", GitChangeKind.Modified)]);

		viewModel.Files.ShouldHaveSingleItem().DisplayPath.ShouldBe("old.txt -> new.txt");
	}

	[Test]
	public void Select_all_updates_every_file_and_aggregate_state()
	{
		GitCommitDialogViewModel viewModel = new(
		[
			Entry("a.txt", GitChangeKind.Added),
			Entry("b.txt", GitChangeKind.Modified)
		]);

		viewModel.SetAllSelected(false);

		viewModel.Files.ShouldAllBe(file => !file.IsSelected);
		viewModel.AreAllFilesSelected.ShouldBeFalse();
		viewModel.SetAllSelected(true);
		viewModel.Files.ShouldAllBe(file => file.IsSelected);
		viewModel.AreAllFilesSelected.ShouldBeTrue();
	}

	[Test]
	public void Accept_requires_nonblank_message_and_at_least_one_selected_file()
	{
		GitCommitDialogViewModel viewModel = new([Entry("file.txt", GitChangeKind.Modified)]);

		viewModel.CanAccept.ShouldBeFalse();
		viewModel.Message = " commit ";
		viewModel.CanAccept.ShouldBeTrue();
		viewModel.Files[0].IsSelected = false;
		viewModel.CanAccept.ShouldBeFalse();
		viewModel.CreateResult().ShouldBeNull();
	}

	[Test]
	public void Result_trims_message_and_returns_only_selected_files()
	{
		var selected = Entry("selected.txt", GitChangeKind.Added);
		GitCommitDialogViewModel viewModel = new(
		[
			selected,
			Entry("ignored.txt", GitChangeKind.Deleted)
		])
		{
			Message = "  message  "
		};
		viewModel.Files[1].IsSelected = false;

		var result = viewModel.CreateResult();

		result.ShouldNotBeNull();
		result.Message.ShouldBe("message");
		result.Files.ShouldBe([selected]);
	}

	private static GitFileEntry Entry(string path, GitChangeKind kind) => new(path, null, kind);
}