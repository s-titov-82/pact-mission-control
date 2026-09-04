using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class DirectorySelectionViewModelTests : IDisposable
{
	private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
	private string _existingDirectory => _temporaryDirectory.Path;

	[Test]
	public void Constructor_preserves_initial_text_and_filters_blank_recent_directories()
	{
		DirectorySelectionViewModel viewModel = new(
			["first", " ", "second"],
			"initial");

		viewModel.DirectoryText.ShouldBe("initial");
		viewModel.RecentDirectories.ShouldBe(["first", "second"]);
	}

	[Test]
	public void Selecting_recent_directory_replaces_editable_text()
	{
		DirectorySelectionViewModel viewModel = new([_existingDirectory], "initial")
		{
			SelectedRecentDirectory = _existingDirectory
		};

		viewModel.DirectoryText.ShouldBe(_existingDirectory);
	}

	[Test]
	public void Missing_directory_cannot_be_accepted_and_shows_validation()
	{
		DirectorySelectionViewModel viewModel = new([], Path.Combine(_existingDirectory, "missing"));

		var result = viewModel.CreateResult();

		viewModel.CanAccept.ShouldBeFalse();
		viewModel.ValidationMessage.ShouldBe("Directory does not exist.");
		result.ShouldBeNull();
	}

	[Test]
	public void Existing_directory_is_accepted_as_trimmed_full_path()
	{
		DirectorySelectionViewModel viewModel = new([], $"  {_existingDirectory}  ");

		var result = viewModel.CreateResult();

		viewModel.CanAccept.ShouldBeTrue();
		viewModel.ValidationMessage.ShouldBe(string.Empty);
		result.ShouldNotBeNull();
		result.Directory.ShouldBe(Path.GetFullPath(_existingDirectory));
	}
	public void Dispose()
	{
		_temporaryDirectory.Dispose();
	}
}
