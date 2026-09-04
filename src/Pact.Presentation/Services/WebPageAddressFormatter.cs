using System.Text.RegularExpressions;

namespace Pact.Presentation.Services;

/// <summary>
/// Compacts known web-tool addresses while preserving the path needed to identify the page.
/// </summary>
public static class WebPageAddressFormatter
{
	private static readonly (Regex Pattern, string Prefix)[] Rules =
	[
		(CreatePattern("(?:ci|teamcity)"), "CI:"),
		(CreatePattern("git(?:lab|hub)"), "GIT:"),
		(CreatePattern("(?:wiki|confluence)"), "WIKI:"),
		(CreatePattern("jira"), "JIRA:")
	];

	/// <summary>
	/// Returns a prefixed path for known tools, or the original address when no rule matches.
	/// </summary>
	public static string Format(string address)
	{
		ArgumentNullException.ThrowIfNull(address);
		foreach ((var pattern, var prefix) in Rules)
		{
			var match = pattern.Match(address);
			if (match.Success)
			{
				return prefix + match.Groups["restPath"].Value;
			}
		}

		return address;
	}

	private static Regex CreatePattern(string hostMarker) => new(
		$@"^https?://.*?{hostMarker}.*?/(?<restPath>.*)$",
		RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}