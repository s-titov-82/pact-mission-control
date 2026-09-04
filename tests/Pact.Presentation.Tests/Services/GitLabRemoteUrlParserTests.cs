using Pact.Presentation.Services.Git;

namespace Pact.Presentation.Tests.Services;

public sealed class GitLabRemoteUrlParserTests
{
	[Test]
	[TestCase("git@gitlab.example.com:group/repo.git", "group/repo")]
	[TestCase("ssh://git@gitlab.example.com/group/sub/repo.git", "group/sub/repo")]
	[TestCase("https://gitlab.example.com/group/repo.git", "group/repo")]
	[TestCase("https://gitlab/group/repo", "group/repo")]
	public void TryGetRepoId_extracts_gitlab_repo_path(string remoteUrl, string expected)
	{
		GitLabRemoteUrlParser.TryGetRepoId(remoteUrl, out var repoId).ShouldBeTrue();
		repoId.ShouldBe(expected);
	}

	[Test]
	[TestCase("")]
	[TestCase("https://github.com/group/repo.git")]
	[TestCase("not a url")]
	public void TryGetRepoId_rejects_non_gitlab_or_invalid_remote(string remoteUrl)
	{
		GitLabRemoteUrlParser.TryGetRepoId(remoteUrl, out var repoId).ShouldBeFalse();
		repoId.ShouldBe(string.Empty);
	}
}
