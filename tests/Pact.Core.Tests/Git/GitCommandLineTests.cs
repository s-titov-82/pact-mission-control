using Pact.Core.Git;

namespace Pact.Core.Tests.Git;

public sealed class GitCommandLineTests
{
	[Test]
	[TestCase(null)]
	[TestCase("")]
	[TestCase("   ")]
	public void Split_returns_empty_for_blank_input(string? command) => GitCommandLine.Split(command).ShouldBeEmpty();

	[Test]
	public void Split_splits_on_whitespace() => GitCommandLine.Split("pull  --no-rebase").ShouldBe(["pull", "--no-rebase"]);

	[Test]
	public void Split_strips_leading_git_token() => GitCommandLine.Split("git stash push").ShouldBe(["stash", "push"]);

	[Test]
	public void Split_strips_leading_git_token_case_insensitively() => GitCommandLine.Split("GIT pull").ShouldBe(["pull"]);

	[Test]
	public void Split_keeps_git_when_it_is_the_only_token() => GitCommandLine.Split("git").ShouldBeEmpty();

	[Test]
	public void Split_groups_double_quoted_segments_into_one_argument() => GitCommandLine.Split("stash push -m \"work in progress\"").ShouldBe(["stash", "push", "-m", "work in progress"]);

	[Test]
	public void Split_supports_quotes_glued_to_a_token() => GitCommandLine.Split("log --format=\"%H %s\"").ShouldBe(["log", "--format=%H %s"]);

	[Test]
	public void Split_preserves_escaped_quote_inside_quotes() => GitCommandLine.Split("commit -m \"say \\\"hi\\\"\"").ShouldBe(["commit", "-m", "say \"hi\""]);

	[Test]
	public void Split_keeps_empty_quoted_argument() => GitCommandLine.Split("commit -m \"\"").ShouldBe(["commit", "-m", ""]);

	[Test]
	public void Split_throws_on_unbalanced_quote() => Should.Throw<FormatException>(() => GitCommandLine.Split("stash push -m \"oops"));

	[Test]
	public void TrySplit_reports_failure_instead_of_throwing()
	{
		GitCommandLine.TrySplit("pull \"broken", out _).ShouldBeFalse();
		GitCommandLine.TrySplit("pull --no-rebase", out var arguments).ShouldBeTrue();
		arguments.ShouldBe(["pull", "--no-rebase"]);
	}
}