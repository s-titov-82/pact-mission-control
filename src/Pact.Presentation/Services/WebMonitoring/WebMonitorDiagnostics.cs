using Pact.Core.Web.Monitoring;

namespace Pact.Presentation.Services.WebMonitoring;

/// <summary>Immutable live monitoring facts for one loaded browser tab.</summary>
/// <param name="WebPageId">Saved page that owns the monitoring registration.</param>
/// <param name="ObservedUrl">Latest confirmed normalized document URL.</param>
/// <param name="RuleId">Matched rule identifier, or null when no rule matches.</param>
/// <param name="RuleTitle">Matched rule title, or null when no rule matches.</param>
/// <param name="Status">Current monitoring projection.</param>
/// <param name="Activity">Latest observed activity value, or null when unknown.</param>
/// <param name="Revision">Latest observed revision, or null when unavailable.</param>
/// <param name="Unread">Whether the latest snapshot contains an unacknowledged change.</param>
/// <param name="ObservedAt">Time of the latest successful DOM observation.</param>
/// <param name="Attempt">Monotonic live evaluation attempt.</param>
/// <param name="NextAttemptAt">Scheduled time of the next evaluation.</param>
/// <param name="Navigating">Whether main-frame navigation currently suspends monitoring.</param>
/// <param name="LastError">Latest sanitized monitoring error, or null.</param>
public sealed record WebMonitorDiagnostics(
	string WebPageId,
	string? ObservedUrl,
	string? RuleId,
	string? RuleTitle,
	WebMonitorStatus Status,
	bool? Activity,
	string? Revision,
	bool Unread,
	DateTimeOffset? ObservedAt,
	int Attempt,
	DateTimeOffset? NextAttemptAt,
	bool Navigating,
	string? LastError);

/// <summary>Reports a complete replacement for one page's live monitoring facts.</summary>
public sealed class WebMonitorDiagnosticsChangedEventArgs : EventArgs
{
	/// <summary>Creates an event carrying the supplied diagnostic snapshot.</summary>
	public WebMonitorDiagnosticsChangedEventArgs(WebMonitorDiagnostics diagnostics)
	{
		Diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
	}

	/// <summary>Complete live state after the change.</summary>
	public WebMonitorDiagnostics Diagnostics { get; }
}
