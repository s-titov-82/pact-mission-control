using System.Diagnostics.CodeAnalysis;

namespace Pact.Core.Web;

/// <summary>Validates addresses that Pact may load in a saved browser tab.</summary>
public static class HttpWebAddress
{
	/// <summary>
	/// Trims and parses an absolute HTTP or HTTPS address, rejecting relative and unsafe schemes.
	/// </summary>
	public static bool TryParse(
		string? value,
		[NotNullWhen(true)] out Uri? uri)
	{
		var candidate = value?.Trim();
		if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
			&& (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
		{
			uri = parsed;
			return true;
		}

		uri = null;
		return false;
	}
}
