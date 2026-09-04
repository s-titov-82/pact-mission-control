using Pact.Core.Git;

namespace Pact.Infrastructure.Tests.Git;

public sealed class GitRepositoryWorkflowTests
{
	[Test]
	[NonParallelizable]
	public async Task Production_git_services_observe_staged_and_unstaged_changes()
	{
		using var root = TemporaryDirectory.Create();
		GitCliRunner runner = new();

		await RunGitAsync(runner, root.Path, "init", "--initial-branch=pact-test");
		await File.WriteAllTextAsync(
			Path.Combine(root.Path, "tracked.txt"),
			"one",
			TestContext.CurrentContext.CancellationToken);
		await RunGitAsync(runner, root.Path, "add", "tracked.txt");
		await RunGitAsync(
			runner,
			root.Path,
			"-c",
			"user.name=PACT Test",
			"-c",
			"user.email=pact@example.com",
			"-c",
			"commit.gpgsign=false",
			"commit",
			"-m",
			"initial");

		await File.AppendAllTextAsync(
			Path.Combine(root.Path, "tracked.txt"),
			"two",
			TestContext.CurrentContext.CancellationToken);
		await File.WriteAllTextAsync(
			Path.Combine(root.Path, "staged.txt"),
			"staged",
			TestContext.CurrentContext.CancellationToken);
		await RunGitAsync(runner, root.Path, "add", "staged.txt");

		var status = await runner.RunAsync(
			root.Path,
			["--no-optional-locks", "status", "--porcelain=v2", "--branch"],
			outputLine: null,
			TestContext.CurrentContext.CancellationToken);
		var snapshot = GitStatusParser.Parse(status.StandardOutput);

		status.Succeeded.ShouldBeTrue(status.StandardError);
		GitRepositoryDetector.IsGitRepository(root.Path).ShouldBeTrue();
		snapshot.Branch.ShouldBe("pact-test");
		snapshot.IsDetached.ShouldBeFalse();
		snapshot.Files.ShouldContain(file =>
			file.Path == "tracked.txt" && file.Kind == GitChangeKind.Modified);
		snapshot.Files.ShouldContain(file =>
			file.Path == "staged.txt" && file.Kind == GitChangeKind.Added);
	}

	private static async Task RunGitAsync(
		GitCliRunner runner,
		string workingDirectory,
		params string[] arguments)
	{
		var result = await runner.RunAsync(
			workingDirectory,
			arguments,
			outputLine: null,
			TestContext.CurrentContext.CancellationToken);

		result.Succeeded.ShouldBeTrue(
			$"git {string.Join(' ', arguments)} failed: {result.StandardError}");
	}
}