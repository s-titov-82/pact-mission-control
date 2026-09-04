namespace Pact.Core.Web.Monitoring;

/// <summary>
/// Identifies the single monitoring state projected for a saved web page.
/// </summary>
public enum WebMonitorStatus
{
	/// <summary>
	/// Indicates that the loaded page has no activity or unread event.
	/// </summary>
	None,

	/// <summary>
	/// Indicates that the page is not loaded and has no higher-priority state.
	/// </summary>
	Paused,

	/// <summary>
	/// Indicates that the page has an unacknowledged completion or revision event.
	/// </summary>
	Unread,

	/// <summary>
	/// Indicates that the latest known observation reports current activity.
	/// </summary>
	Activity
}

/// <summary>
/// Projects one state-engine operation and whether its retained snapshot must be persisted.
/// </summary>
/// <param name="Status">The page status after the operation.</param>
/// <param name="Snapshot">The current retained snapshot, or <see langword="null"/> before any observation or restore.</param>
/// <param name="SnapshotChanged">Whether this operation changed <paramref name="Snapshot"/> and requires persistence.</param>
public sealed record WebMonitorTransition(
	WebMonitorStatus Status,
	WebMonitorSnapshot? Snapshot,
	bool SnapshotChanged);

/// <summary>
/// Owns pure monitoring baseline, unread acknowledgement, and status projection for exactly one saved web page.
/// </summary>
public sealed class WebMonitorStateEngine
{
	private readonly string _webPageId;
	private WebMonitorSnapshot? _snapshot;
	private bool? _projectedActivity;
	private bool _loaded;
	private bool _selected;
	private bool _windowVisible;
	private bool _windowActive;

	/// <summary>
	/// Creates an engine whose observations and restored snapshots are scoped to one saved web-page identifier.
	/// </summary>
	/// <param name="webPageId">The non-empty saved web-page identifier owned by this engine.</param>
	/// <exception cref="ArgumentException">
	/// Thrown when <paramref name="webPageId"/> is blank or contains a directory separator.
	/// </exception>
	public WebMonitorStateEngine(string webPageId)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		if (!IsValidWebPageId(webPageId))
		{
			throw new ArgumentException(
				"The web-page ID must not contain a directory separator.",
				nameof(webPageId));
		}

		_webPageId = webPageId;
	}

	/// <summary>
	/// Applies one normalized observation, using a compatible retained snapshot as its comparison baseline.
	/// </summary>
	/// <param name="url">The absolute document URL observed with the DOM values.</param>
	/// <param name="rule">The compiled rule that produced the observation.</param>
	/// <param name="observation">The independently nullable activity and revision values.</param>
	/// <param name="observedAt">The timestamp stored when this operation produces a changed snapshot.</param>
	/// <returns>The projected status and current snapshot after the observation.</returns>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="observedAt"/> is the default timestamp and cannot form a valid snapshot.
	/// </exception>
	public WebMonitorTransition Observe(
		Uri url,
		WebMonitorCompiledRule rule,
		WebMonitorObservation observation,
		DateTimeOffset observedAt)
	{
		ArgumentNullException.ThrowIfNull(url);
		ArgumentNullException.ThrowIfNull(rule);
		ArgumentNullException.ThrowIfNull(observation);
		if (observedAt == default)
		{
			throw new ArgumentOutOfRangeException(
				nameof(observedAt),
				observedAt,
				"An observation timestamp is required.");
		}

		var normalizedUrl = WebMonitorUrl.Normalize(url);
		var compatibleBaseline = IsCompatibleBaseline(
			_snapshot,
			normalizedUrl,
			rule);
		var previousActivity = compatibleBaseline
			? rule.Source.Activity is null
				? false
				: _snapshot!.Activity
			: null;
		var previousRevision = compatibleBaseline ? _snapshot!.Revision : null;
		var retainedUnread = _snapshot?.Unread == true;
		var observedActivity = rule.Source.Activity is null
			? false
			: observation.Activity;
		var nextActivity = observedActivity ?? previousActivity;
		var nextRevision = observation.Revision ?? previousRevision;

		var createsEvent = compatibleBaseline
			&& (previousActivity == true && observedActivity == false
				|| previousRevision is not null
				&& observation.Revision is not null
				&& nextActivity == false
				&& !string.Equals(
					previousRevision,
					observation.Revision,
					StringComparison.Ordinal));
		var nextUnread = retainedUnread
			|| createsEvent && !IsActivelyViewed();
		var normalizedUrlText = normalizedUrl.AbsoluteUri;
		var snapshotChanged = !compatibleBaseline
			|| !string.Equals(
				_snapshot!.Url,
				normalizedUrlText,
				StringComparison.Ordinal)
			|| _snapshot.Activity != nextActivity
			|| !string.Equals(
				_snapshot.Revision,
				nextRevision,
				StringComparison.Ordinal)
			|| _snapshot.Unread != nextUnread;

		if (snapshotChanged)
		{
			_snapshot = new WebMonitorSnapshot(
				_webPageId,
				normalizedUrlText,
				rule.Source.Id,
				rule.Fingerprint,
				nextActivity,
				nextRevision,
				nextUnread,
				observedAt);
		}

		_projectedActivity = nextActivity;
		return CreateTransition(snapshotChanged);
	}

	/// <summary>
	/// Atomically updates loading and active-view facts, acknowledging unread only when all three view facts are true.
	/// </summary>
	/// <param name="loaded">Whether the page currently has a loaded browser host.</param>
	/// <param name="selected">Whether this page is the selected project item.</param>
	/// <param name="windowVisible">Whether the application window is visible and not minimized.</param>
	/// <param name="windowActive">Whether the application owns the active foreground window.</param>
	/// <returns>The projected status and any snapshot change caused by acknowledgement.</returns>
	public WebMonitorTransition SetPresentationFacts(
		bool loaded,
		bool selected,
		bool windowVisible,
		bool windowActive)
	{
		_loaded = loaded;
		_selected = selected;
		_windowVisible = windowVisible;
		_windowActive = windowActive;

		var snapshotChanged = _snapshot?.Unread == true
			&& IsActivelyViewed();
		if (snapshotChanged)
		{
			_snapshot = _snapshot! with { Unread = false };
		}

		return CreateTransition(snapshotChanged);
	}

	/// <summary>
	/// Restores retained state for this page, deferring activity projection until a compatible live observation arrives.
	/// </summary>
	/// <param name="snapshot">
	/// The retained snapshot, or <see langword="null"/> to clear retained baseline and unread state.
	/// A malformed snapshot or one owned by another page is treated as entirely absent.
	/// </param>
	public void Restore(WebMonitorSnapshot? snapshot)
	{
		_snapshot = TryValidateSnapshotShape(snapshot, out _) ? snapshot : null;
		_projectedActivity = null;
	}

	private WebMonitorTransition CreateTransition(bool snapshotChanged)
	{
		var status = _projectedActivity == true
			? WebMonitorStatus.Activity
			: _snapshot?.Unread == true
				? WebMonitorStatus.Unread
				: !_loaded
					? WebMonitorStatus.Paused
					: WebMonitorStatus.None;
		return new WebMonitorTransition(status, _snapshot, snapshotChanged);
	}

	private bool IsActivelyViewed()
	{
		return _selected && _windowVisible && _windowActive;
	}

	private bool TryValidateSnapshotShape(
		WebMonitorSnapshot? snapshot,
		out Uri? snapshotUrl)
	{
		snapshotUrl = null;
		if (snapshot is null
			|| string.IsNullOrWhiteSpace(snapshot.WebPageId)
			|| !string.Equals(
				snapshot.WebPageId,
				_webPageId,
				StringComparison.Ordinal)
			|| !IsValidWebPageId(snapshot.WebPageId)
			|| string.IsNullOrWhiteSpace(snapshot.Url)
			|| string.IsNullOrWhiteSpace(snapshot.RuleId)
			|| string.IsNullOrWhiteSpace(snapshot.RuleFingerprint)
			|| snapshot.ObservedAt == default
			|| !Uri.TryCreate(
				snapshot.Url,
				UriKind.Absolute,
				out snapshotUrl))
		{
			snapshotUrl = null;
			return false;
		}

		if (!string.IsNullOrEmpty(snapshotUrl.Fragment))
		{
			snapshotUrl = null;
			return false;
		}

		return true;
	}

	private static bool IsValidWebPageId(string webPageId)
	{
		return !webPageId.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
			&& !webPageId.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	private bool IsCompatibleBaseline(
		WebMonitorSnapshot? snapshot,
		Uri normalizedUrl,
		WebMonitorCompiledRule rule)
	{
		if (!TryValidateSnapshotShape(snapshot, out var snapshotUrl)
			|| !string.Equals(
				snapshot!.RuleId,
				rule.Source.Id,
				StringComparison.Ordinal)
			|| !string.Equals(
				snapshot.RuleFingerprint,
				rule.Fingerprint,
				StringComparison.Ordinal))
		{
			return false;
		}

		return string.Equals(
			snapshotUrl!.AbsoluteUri,
			normalizedUrl.AbsoluteUri,
			StringComparison.Ordinal);
	}
}