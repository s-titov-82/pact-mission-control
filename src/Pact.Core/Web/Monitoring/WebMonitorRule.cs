namespace Pact.Core.Web.Monitoring;

/// <summary>
/// Declares one configurable DOM-monitoring rule without embedding executable browser code.
/// </summary>
/// <param name="Id">The stable identifier used to associate retained state with this rule.</param>
/// <param name="Title">The user-facing rule title.</param>
/// <param name="Enabled">Whether the rule participates in URL matching.</param>
/// <param name="UrlPattern">The regular expression matched against a normalized absolute URL.</param>
/// <param name="PollIntervalSeconds">The polling cadence in whole seconds.</param>
/// <param name="Activity">The optional activity extractor; absence means activity is always false.</param>
/// <param name="Revision">The optional revision extractor.</param>
public sealed record WebMonitorRule(
	string Id,
	string Title,
	bool Enabled,
	string UrlPattern,
	int PollIntervalSeconds,
	WebMonitorExtractor? Activity,
	WebMonitorExtractor? Revision);

/// <summary>
/// Describes how a monitoring rule reads and optionally matches one DOM value.
/// </summary>
/// <param name="Selector">The CSS selector evaluated by the browser adapter.</param>
/// <param name="Source">The kind of DOM value to read.</param>
/// <param name="AttributeName">The attribute to read when <paramref name="Source"/> is <see cref="WebMonitorValueSource.Attribute"/>.</param>
/// <param name="MatchPattern">
/// The optional expression from Pact's browser-portable ECMAScript subset. It supports JavaScript character and
/// control escapes, fixed-width hexadecimal and Unicode escapes, escaped regex punctuation, unambiguous backward
/// references, uniquely named captures, and lookaround; activity text and attribute sources require it.
/// </param>
/// <param name="CaptureGroup">The optional regular-expression group selected from a matched value.</param>
public sealed record WebMonitorExtractor(
	string Selector,
	WebMonitorValueSource Source,
	string? AttributeName,
	string? MatchPattern,
	int? CaptureGroup);

/// <summary>
/// Identifies the DOM value an extractor reads.
/// </summary>
public enum WebMonitorValueSource
{
	/// <summary>
	/// Produces a boolean indicating whether at least one matching element exists.
	/// </summary>
	Exists,

	/// <summary>
	/// Produces the number of matching elements for conversion to an activity boolean.
	/// </summary>
	Count,

	/// <summary>
	/// Reads the text of the first matching element.
	/// </summary>
	Text,

	/// <summary>
	/// Reads one named attribute from the first matching element.
	/// </summary>
	Attribute
}