using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pact.Core.Web.Monitoring;

/// <summary>
/// Validates declarative monitoring rules and compiles their immutable matching and DOM-query contracts.
/// </summary>
public static class WebMonitorRuleCompiler
{
	/// <summary>
	/// Gets the shortest supported polling cadence, protecting the UI host from overly aggressive rules.
	/// </summary>
	public const int MinimumPollIntervalSeconds = 5;

	private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

	/// <summary>
	/// Validates every rule and extractor field that can be checked without executing against a live DOM.
	/// </summary>
	/// <param name="rule">The rule to validate.</param>
	/// <returns>All validation errors found; an empty set denotes a compilable rule.</returns>
	public static WebMonitorRuleValidationResult Validate(WebMonitorRule rule)
	{
		ArgumentNullException.ThrowIfNull(rule);

		List<string> errors = [];

		if (string.IsNullOrWhiteSpace(rule.Id))
		{
			errors.Add("Rule id is required.");
		}

		if (string.IsNullOrWhiteSpace(rule.Title))
		{
			errors.Add("Rule title is required.");
		}

		if (string.IsNullOrWhiteSpace(rule.UrlPattern))
		{
			errors.Add("URL pattern is required.");
		}
		else
		{
			TryCreateRegex(rule.UrlPattern, "URL pattern", errors);
		}

		if (rule.Enabled
			&& rule.UrlPattern?.Contains("CHANGE-ME-", StringComparison.Ordinal) == true)
		{
			errors.Add("Enabled rules cannot contain a CHANGE-ME- URL marker.");
		}

		if (rule.PollIntervalSeconds < MinimumPollIntervalSeconds)
		{
			errors.Add(
				$"Poll interval must be at least {MinimumPollIntervalSeconds} seconds.");
		}

		if (rule.Activity is null && rule.Revision is null)
		{
			errors.Add("A rule must define an activity extractor, a revision extractor, or both.");
		}

		if (rule.Activity is not null)
		{
			ValidateExtractor(rule.Activity, isActivity: true, errors);
		}

		if (rule.Revision is not null)
		{
			ValidateExtractor(rule.Revision, isActivity: false, errors);
		}

		return new WebMonitorRuleValidationResult(errors.ToArray());
	}

	/// <summary>
	/// Compiles a valid rule into reusable URL matching, fingerprint, and structured DOM-query data.
	/// </summary>
	/// <param name="rule">The declarative rule to compile.</param>
	/// <returns>An immutable compiled rule.</returns>
	/// <exception cref="ArgumentException">Thrown when <paramref name="rule"/> is invalid.</exception>
	public static WebMonitorCompiledRule Compile(WebMonitorRule rule)
	{
		var validation = Validate(rule);
		if (!validation.IsValid)
		{
			throw new ArgumentException(
				string.Join(Environment.NewLine, validation.Errors),
				nameof(rule));
		}

		var urlRegex = CreateRegex(rule.UrlPattern);
		var fingerprint = CreateFingerprint(rule);
		WebMonitorDomQuery query = new(
			rule.Activity,
			rule.Revision,
			ActivityWhenExtractorMissing: false);

		return new WebMonitorCompiledRule(rule, urlRegex, fingerprint, query);
	}

	private static void ValidateExtractor(
		WebMonitorExtractor extractor,
		bool isActivity,
		List<string> errors)
	{
		var label = isActivity ? "Activity extractor" : "Revision extractor";

		if (string.IsNullOrWhiteSpace(extractor.Selector))
		{
			errors.Add($"{label} selector is required.");
		}

		if (!Enum.IsDefined(extractor.Source))
		{
			errors.Add($"{label} source is invalid.");
			return;
		}

		if (extractor.Source == WebMonitorValueSource.Attribute
			&& string.IsNullOrWhiteSpace(extractor.AttributeName))
		{
			errors.Add($"{label} attribute name is required for an attribute source.");
		}

		if (!isActivity
			&& extractor.Source is WebMonitorValueSource.Exists or WebMonitorValueSource.Count)
		{
			errors.Add("Revision extractors must use text or attribute sources.");
		}

		if (isActivity
			&& extractor.Source is WebMonitorValueSource.Text or WebMonitorValueSource.Attribute
			&& string.IsNullOrWhiteSpace(extractor.MatchPattern))
		{
			errors.Add("Activity text and attribute extractors require a match pattern.");
		}

		Regex? matchRegex = null;
		if (!string.IsNullOrWhiteSpace(extractor.MatchPattern))
		{
			matchRegex = TryCreateExtractorRegex(
				extractor.MatchPattern,
				$"{label} regular expression",
				errors);
		}

		if (extractor.CaptureGroup is not int captureGroup)
		{
			return;
		}

		if (matchRegex is null)
		{
			errors.Add($"{label} capture group requires a valid regular expression.");
			return;
		}

		if (captureGroup < 0 || !matchRegex.GetGroupNumbers().Contains(captureGroup))
		{
			errors.Add($"{label} capture group {captureGroup} does not exist.");
		}
	}

	private static Regex? TryCreateExtractorRegex(
		string pattern,
		string label,
		List<string> errors)
	{
		try
		{
			if (UsesUnsupportedPortableEcmaScriptSyntax(pattern))
			{
				throw new ArgumentException(
					"The pattern uses syntax outside Pact's browser-portable ECMAScript subset. "
					+ "Only standard character/control escapes, fixed-width hex/Unicode escapes, "
					+ "escaped regex punctuation, and unambiguous backward references are accepted.");
			}

			return new Regex(
				pattern,
				RegexOptions.CultureInvariant | RegexOptions.ECMAScript,
				RegexTimeout);
		}
		catch (ArgumentException exception)
		{
			errors.Add(
				$"{label} is not valid for Pact's portable ECMAScript subset: {exception.Message}");
			return null;
		}
	}

	private static bool UsesUnsupportedPortableEcmaScriptSyntax(string pattern)
	{
		var insideCharacterClass = false;
		var captureGroupCount = 0;
		HashSet<string> namedGroups = new(StringComparer.Ordinal);

		for (var index = 0; index < pattern.Length; index++)
		{
			var current = pattern[index];
			if (current == '\\')
			{
				if (!TryConsumePortableEscape(
						pattern,
						ref index,
						insideCharacterClass,
						captureGroupCount,
						namedGroups))
				{
					return true;
				}

				continue;
			}

			if (current == '[')
			{
				if (insideCharacterClass)
				{
					return true;
				}

				insideCharacterClass = true;
				continue;
			}

			if (current == ']' && insideCharacterClass)
			{
				insideCharacterClass = false;
				continue;
			}

			if (insideCharacterClass || current != '(')
			{
				continue;
			}

			if (index + 1 >= pattern.Length || pattern[index + 1] != '?')
			{
				captureGroupCount++;
				continue;
			}

			if (index + 2 >= pattern.Length)
			{
				return true;
			}

			var groupKind = pattern[index + 2];
			if (groupKind is ':' or '=' or '!')
			{
				continue;
			}

			if (groupKind == '<' && index + 3 < pattern.Length)
			{
				var lookbehindOrName = pattern[index + 3];
				if (lookbehindOrName is '=' or '!')
				{
					continue;
				}

				var nameEnd = pattern.IndexOf('>', index + 3);
				if (nameEnd >= 0)
				{
					var name = pattern.AsSpan(
						index + 3,
						nameEnd - index - 3);
					if (IsSupportedEcmaScriptGroupName(name)
						&& namedGroups.Add(name.ToString()))
					{
						captureGroupCount++;
						continue;
					}
				}
			}

			return true;
		}

		return false;
	}

	private static bool TryConsumePortableEscape(
		string pattern,
		ref int slashIndex,
		bool insideCharacterClass,
		int captureGroupCount,
		HashSet<string> namedGroups)
	{
		var tokenIndex = slashIndex + 1;
		if (tokenIndex >= pattern.Length)
		{
			return false;
		}

		var token = pattern[tokenIndex];
		if ("dDsSwWbBfnrtv".Contains(token, StringComparison.Ordinal)
			|| IsPortableEscapedPunctuation(token))
		{
			slashIndex = tokenIndex;
			return true;
		}

		if (token == '0')
		{
			if (tokenIndex + 1 < pattern.Length && char.IsDigit(pattern[tokenIndex + 1]))
			{
				return false;
			}

			slashIndex = tokenIndex;
			return true;
		}

		if (token is >= '1' and <= '9')
		{
			var referencedGroup = token - '0';
			if (insideCharacterClass
				|| referencedGroup > captureGroupCount
				|| tokenIndex + 1 < pattern.Length && char.IsDigit(pattern[tokenIndex + 1]))
			{
				return false;
			}

			slashIndex = tokenIndex;
			return true;
		}

		if (token == 'c')
		{
			if (tokenIndex + 1 >= pattern.Length
				|| !IsAsciiLetter(pattern[tokenIndex + 1]))
			{
				return false;
			}

			slashIndex = tokenIndex + 1;
			return true;
		}

		if (token == 'x')
		{
			return TryConsumeFixedHexEscape(pattern, ref slashIndex, tokenIndex, digitCount: 2);
		}

		if (token == 'u')
		{
			return TryConsumeFixedHexEscape(pattern, ref slashIndex, tokenIndex, digitCount: 4);
		}

		if (token == 'k' && !insideCharacterClass)
		{
			var nameStart = tokenIndex + 2;
			if (tokenIndex + 1 >= pattern.Length
				|| pattern[tokenIndex + 1] != '<'
				|| nameStart >= pattern.Length)
			{
				return false;
			}

			var nameEnd = pattern.IndexOf('>', nameStart);
			if (nameEnd < 0)
			{
				return false;
			}

			var name = pattern.AsSpan(nameStart, nameEnd - nameStart);
			if (!IsSupportedEcmaScriptGroupName(name)
				|| !namedGroups.Contains(name.ToString()))
			{
				return false;
			}

			slashIndex = nameEnd;
			return true;
		}

		return false;
	}

	private static bool TryConsumeFixedHexEscape(
		string pattern,
		ref int slashIndex,
		int tokenIndex,
		int digitCount)
	{
		var lastDigitIndex = tokenIndex + digitCount;
		if (lastDigitIndex >= pattern.Length)
		{
			return false;
		}

		for (var index = tokenIndex + 1; index <= lastDigitIndex; index++)
		{
			if (!Uri.IsHexDigit(pattern[index]))
			{
				return false;
			}
		}

		slashIndex = lastDigitIndex;
		return true;
	}

	private static bool IsPortableEscapedPunctuation(char value)
	{
		return @"^$\.*+?()[]{}|/-".Contains(value, StringComparison.Ordinal);
	}

	private static bool IsAsciiLetter(char value)
	{
		return value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
	}

	private static bool IsSupportedEcmaScriptGroupName(ReadOnlySpan<char> name)
	{
		if (name.IsEmpty || !IsGroupNameStart(name[0]))
		{
			return false;
		}

		for (var index = 1; index < name.Length; index++)
		{
			var current = name[index];
			if (!IsGroupNameStart(current) && !char.IsDigit(current))
			{
				return false;
			}
		}

		return true;
	}

	private static bool IsGroupNameStart(char value)
	{
		return char.IsLetter(value) || value is '_' or '$';
	}

	private static Regex? TryCreateRegex(
		string pattern,
		string label,
		List<string> errors)
	{
		try
		{
			return CreateRegex(pattern);
		}
		catch (ArgumentException exception)
		{
			errors.Add($"{label} is not a valid regular expression: {exception.Message}");
			return null;
		}
	}

	private static Regex CreateRegex(string pattern)
	{
		return new Regex(
			pattern,
			RegexOptions.CultureInvariant,
			RegexTimeout);
	}

	private static string CreateFingerprint(WebMonitorRule rule)
	{
		using MemoryStream stream = new();
		using (Utf8JsonWriter writer = new(
			stream,
			new JsonWriterOptions
			{
				Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
			}))
		{
			writer.WriteStartObject();
			writer.WriteString("urlPattern", rule.UrlPattern);
			writer.WriteNumber("pollIntervalSeconds", rule.PollIntervalSeconds);
			WriteExtractor(writer, "activity", rule.Activity);
			WriteExtractor(writer, "revision", rule.Revision);
			writer.WriteEndObject();
		}

		return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
	}

	private static void WriteExtractor(
		Utf8JsonWriter writer,
		string propertyName,
		WebMonitorExtractor? extractor)
	{
		if (extractor is null)
		{
			writer.WriteNull(propertyName);
			return;
		}

		writer.WriteStartObject(propertyName);
		writer.WriteString("selector", extractor.Selector);
		writer.WriteString("source", extractor.Source.ToString());
		writer.WriteString("attributeName", extractor.AttributeName);
		writer.WriteString("matchPattern", extractor.MatchPattern);
		if (extractor.CaptureGroup is int captureGroup)
		{
			writer.WriteNumber("captureGroup", captureGroup);
		}
		else
		{
			writer.WriteNull("captureGroup");
		}

		writer.WriteEndObject();
	}
}

/// <summary>
/// Provides fragment-insensitive URL identity for monitoring while retaining query-string semantics.
/// </summary>
public static class WebMonitorUrl
{
	/// <summary>
	/// Removes only the fragment from an absolute URL and preserves its query.
	/// </summary>
	/// <param name="absoluteUrl">The absolute URL to normalize.</param>
	/// <returns>The fragment-free absolute URL.</returns>
	/// <exception cref="ArgumentException">Thrown when <paramref name="absoluteUrl"/> is relative.</exception>
	public static Uri Normalize(Uri absoluteUrl)
	{
		ArgumentNullException.ThrowIfNull(absoluteUrl);
		if (!absoluteUrl.IsAbsoluteUri)
		{
			throw new ArgumentException("Monitoring URLs must be absolute.", nameof(absoluteUrl));
		}

		if (string.IsNullOrEmpty(absoluteUrl.Fragment))
		{
			return absoluteUrl;
		}

		return new UriBuilder(absoluteUrl)
		{
			Fragment = string.Empty
		}.Uri;
	}
}

/// <summary>
/// Holds a validated rule together with its reusable matching and DOM-query artifacts.
/// </summary>
/// <param name="Source">The original declarative rule.</param>
/// <param name="UrlRegex">The validated URL regular expression.</param>
/// <param name="Fingerprint">The SHA-256 identity of every observation-semantic rule field.</param>
/// <param name="Query">The structured DOM query consumed by the browser adapter.</param>
public sealed record WebMonitorCompiledRule(
	WebMonitorRule Source,
	Regex UrlRegex,
	string Fingerprint,
	WebMonitorDomQuery Query)
{
	/// <summary>
	/// Determines whether this enabled rule matches the normalized absolute form of <paramref name="uri"/>.
	/// </summary>
	/// <param name="uri">The absolute document URL to normalize and match.</param>
	/// <returns><see langword="true"/> only when the rule is enabled and its URL expression matches.</returns>
	public bool Matches(Uri uri)
	{
		var normalized = WebMonitorUrl.Normalize(uri);
		return Source.Enabled && UrlRegex.IsMatch(normalized.AbsoluteUri);
	}

	/// <summary>
	/// Determines whether the URL expression matches independently of whether the rule is enabled.
	/// </summary>
	/// <param name="uri">The absolute document URL to normalize and match.</param>
	/// <returns><see langword="true"/> when the normalized URL matches the compiled expression.</returns>
	public bool MatchesUrlPattern(Uri uri)
	{
		var normalized = WebMonitorUrl.Normalize(uri);
		return UrlRegex.IsMatch(normalized.AbsoluteUri);
	}
}

/// <summary>
/// Describes a browser DOM evaluation using only validated structured fields.
/// </summary>
/// <param name="Activity">The optional activity extractor.</param>
/// <param name="Revision">The optional revision extractor.</param>
/// <param name="ActivityWhenExtractorMissing">
/// The effective activity value when <paramref name="Activity"/> is absent; rule compilation fixes this to false.
/// </param>
public sealed record WebMonitorDomQuery(
	WebMonitorExtractor? Activity,
	WebMonitorExtractor? Revision,
	bool ActivityWhenExtractorMissing);

/// <summary>
/// Reports all declarative validation errors for one monitoring rule.
/// </summary>
/// <param name="Errors">The complete set of validation errors.</param>
public sealed record WebMonitorRuleValidationResult(IReadOnlyList<string> Errors)
{
	/// <summary>
	/// Gets whether the rule has no validation errors and may be compiled.
	/// </summary>
	public bool IsValid => Errors.Count == 0;
}