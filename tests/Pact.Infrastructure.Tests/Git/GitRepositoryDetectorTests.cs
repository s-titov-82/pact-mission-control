namespace Pact.Infrastructure.Tests.Git;

public sealed class GitRepositoryDetectorTests : IDisposable
{
	private readonly List<TemporaryDirectory> _temporaryDirectories = [];

	[Test]
	public void IsGitRepository_returns_false_for_null_or_missing_path()
	{
		GitRepositoryDetector.IsGitRepository(null).ShouldBeFalse();
		GitRepositoryDetector.IsGitRepository("   ").ShouldBeFalse();
		GitRepositoryDetector.IsGitRepository(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).ShouldBeFalse();
	}

	[Test]
	public void IsGitRepository_returns_true_when_dot_git_is_directory()
	{
		var root = CreateTempDirectory();
		Directory.CreateDirectory(Path.Combine(root, ".git"));

		GitRepositoryDetector.IsGitRepository(root).ShouldBeTrue();
	}

	[Test]
	public void IsGitRepository_returns_true_when_dot_git_is_file()
	{
		var root = CreateTempDirectory();
		File.WriteAllText(Path.Combine(root, ".git"), "gitdir: ../.git/worktrees/example");

		GitRepositoryDetector.IsGitRepository(root).ShouldBeTrue();
	}

	private string CreateTempDirectory()
	{
		var directory = TemporaryDirectory.Create();
		_temporaryDirectories.Add(directory);
		return directory.Path;
	}

	public void Dispose() => _temporaryDirectories.ForEach(static directory => directory.Dispose());
}
