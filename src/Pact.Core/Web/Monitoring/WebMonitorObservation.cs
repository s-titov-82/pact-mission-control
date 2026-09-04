namespace Pact.Core.Web.Monitoring;

/// <summary>
/// Carries one normalized DOM observation, retaining null only for values that could not be observed reliably.
/// </summary>
/// <param name="Activity">The activity state, or <see langword="null"/> when a configured extractor could not produce it.</param>
/// <param name="Revision">The normalized revision, or <see langword="null"/> when it could not be extracted.</param>
public sealed record WebMonitorObservation(bool? Activity, string? Revision);

/// <summary>
/// Couples the document URL reported by the browser with an optional DOM observation.
/// </summary>
/// <param name="DocumentUrl">The actual absolute document URL at evaluation time.</param>
/// <param name="Observation">
/// The normalized observation, or <see langword="null"/> only for a lightweight URL-only probe.
/// </param>
public sealed record WebMonitorEvaluation(
	Uri DocumentUrl,
	WebMonitorObservation? Observation);

/// <summary>
/// Persists retained monitoring observation and unread state for one saved web page without carrying live polling state.
/// </summary>
/// <param name="WebPageId">The saved web-page identifier that owns this snapshot.</param>
/// <param name="Url">The normalized URL observed when the snapshot was produced.</param>
/// <param name="RuleId">The rule that produced the observation.</param>
/// <param name="RuleFingerprint">The semantic rule identity used to decide baseline compatibility.</param>
/// <param name="Activity">The observed activity value, or <see langword="null"/> when activity is unknown.</param>
/// <param name="Revision">The observed revision value, or <see langword="null"/> when no revision was extracted.</param>
/// <param name="Unread">Whether the page has an unacknowledged change.</param>
/// <param name="ObservedAt">The time at which the observation was recorded.</param>
public sealed record WebMonitorSnapshot(
	string WebPageId,
	string Url,
	string RuleId,
	string RuleFingerprint,
	bool? Activity,
	string? Revision,
	bool Unread,
	DateTimeOffset ObservedAt);