namespace Pact.Core.Sessions;

/// <summary>
/// Reports that a tab's derived indicator or classifier description changed.
/// </summary>
/// <param name="indicator">The new indicator.</param>
/// <param name="activityStartedAt">
/// When the current activity began, or <see langword="null"/> when the tab is not busy.
/// </param>
/// <param name="description">The latest accepted classifier description.</param>
public sealed class TerminalTabIndicatorChangedEventArgs(
	TerminalTabIndicator indicator,
	DateTimeOffset? activityStartedAt,
	string description) : EventArgs
{
	/// <summary>The indicator now in effect for the tab.</summary>
	public TerminalTabIndicator Indicator { get; } = indicator;

	/// <summary>
	/// Start of the current activity, used to show elapsed busy time. Non-null only while
	/// <see cref="Indicator"/> is <see cref="TerminalTabIndicator.Busy"/>.
	/// </summary>
	public DateTimeOffset? ActivityStartedAt { get; } = activityStartedAt;

	/// <summary>Latest accepted text describing the classifier state.</summary>
	public string Description { get; } = description;
}
