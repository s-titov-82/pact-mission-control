using Pact.Core.Web;

namespace Pact.Core.Tests.Web;

public sealed class HttpWebAddressTests
{
	[TestCase("https://example.test/path?q=1", true)]
	[TestCase(" http://example.test/a ", true)]
	[TestCase("/relative", false)]
	[TestCase("file:///C:/secret.txt", false)]
	[TestCase("javascript:alert(1)", false)]
	[TestCase("not a URL", false)]
	public void TryParse_accepts_only_absolute_http_addresses(string value, bool expected)
	{
		var result = HttpWebAddress.TryParse(value, out var uri);

		result.ShouldBe(expected);
		(uri is not null).ShouldBe(expected);
	}
}
