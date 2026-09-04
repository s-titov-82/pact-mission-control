using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class WebPageAddressFormatterTests
{
	[Test]
	[TestCase("https://gitlab.example.com/group/project/-/tags", "GIT:group/project/-/tags")]
	[TestCase("https://teamcity.example.com/project/Build?tab=1#top", "CI:project/Build?tab=1#top")]
	[TestCase("http://wiki.example.com/display/KB/Page", "WIKI:display/KB/Page")]
	[TestCase("https://jira.example.com/browse/APP-123", "JIRA:browse/APP-123")]
	[TestCase("https://example.test/very/long/path", "https://example.test/very/long/path")]
	public void Format_compacts_known_addresses_and_preserves_unknown_addresses(
		string address,
		string expected) => WebPageAddressFormatter.Format(address).ShouldBe(expected);

	[Test]
	public void Format_uses_ci_before_git_when_address_matches_both_patterns() => WebPageAddressFormatter.Format("https://gitlab-ci.example.test/build").ShouldBe("CI:build");
}
