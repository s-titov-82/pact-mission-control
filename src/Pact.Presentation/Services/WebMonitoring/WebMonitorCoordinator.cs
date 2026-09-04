using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Settings.ViewModels;

namespace Pact.Presentation.Services.WebMonitoring;

/// <summary>
/// Carries the projected monitoring status for one saved web page.
/// </summary>
public sealed class WebMonitorStatusChangedEventArgs : EventArgs
{
	/// <summary>
	/// Creates a status notification scoped to a saved web-page identifier.
	/// </summary>
	public WebMonitorStatusChangedEventArgs(string webPageId, WebMonitorStatus status)
	{
		WebPageId = webPageId;
		Status = status;
	}

	/// <summary>Gets the saved web-page identifier that owns the projection.</summary>
	public string WebPageId { get; }

	/// <summary>Gets the single activity, unread, paused, or empty projection.</summary>
	public WebMonitorStatus Status { get; }
}

/// <summary>
/// Reports a sanitized monitoring failure without carrying document or authentication data.
/// </summary>
public sealed class WebMonitorDiagnosticEventArgs : EventArgs
{
	/// <summary>
	/// Creates a diagnostic for one page and evaluation attempt.
	/// </summary>
	public WebMonitorDiagnosticEventArgs(
		string webPageId,
		string? ruleId,
		string category,
		int attempt,
		string message)
	{
		WebPageId = webPageId;
		RuleId = ruleId;
		Category = category;
		Attempt = attempt;
		Message = message;
	}

	/// <summary>Gets the saved web-page identifier whose evaluation failed.</summary>
	public string WebPageId { get; }

	/// <summary>Gets the matched rule identifier, or null for a URL-only probe.</summary>
	public string? RuleId { get; }

	/// <summary>Gets the stable failure category suitable for UI presentation.</summary>
	public string Category { get; }

	/// <summary>Gets the monotonically increasing live evaluation attempt for this registration.</summary>
	public int Attempt { get; }

	/// <summary>Gets a sanitized message that never includes returned page content.</summary>
	public string Message { get; }
}

/// <summary>
/// Publishes one confirmed SPA document URL together with its fragment-free monitoring identity.
/// </summary>
public sealed class WebMonitorStableUrlChangedEventArgs : EventArgs
{
	/// <summary>
	/// Creates a confirmed stable-URL notification for one saved web page.
	/// </summary>
	public WebMonitorStableUrlChangedEventArgs(
		string webPageId,
		Uri documentUrl,
		Uri normalizedUrl)
	{
		WebPageId = webPageId;
		DocumentUrl = documentUrl;
		NormalizedUrl = normalizedUrl;
	}

	/// <summary>Gets the saved web-page identifier whose live document moved.</summary>
	public string WebPageId { get; }

	/// <summary>Gets the raw absolute URL returned by the confirming browser probe.</summary>
	public Uri DocumentUrl { get; }

	/// <summary>Gets the fragment-free URL used for rule matching and baseline identity.</summary>
	public Uri NormalizedUrl { get; }
}

/// <summary>
/// Owns exactly one serialized monitoring loop and state engine for each loaded web-page host.
/// </summary>
public sealed class WebMonitorCoordinator : IAsyncDisposable
{
	private static readonly TimeSpan DomSettleDelay = TimeSpan.FromMilliseconds(500);
	private static readonly TimeSpan EvaluationTimeout = TimeSpan.FromSeconds(5);
	private static readonly TimeSpan UnmatchedProbeInterval = TimeSpan.FromSeconds(30);
	private static readonly TimeSpan ActivelyViewedPollInterval = TimeSpan.FromSeconds(2);

	private readonly WebMonitorSnapshotStore _snapshotStore;
	private readonly TimeProvider _timeProvider;
	private readonly Action<Action> _uiDispatcher;
	private readonly Action<string>? _beforePresentationMutation;
	private readonly Lock _sync = new();
	private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
	private readonly Dictionary<string, Registration> _registrations =
		new(StringComparer.Ordinal);
	private WebMonitorCompiledRule[] _compiledRules = [];
	private string? _selectedWebPageId;
	private bool _windowVisible;
	private bool _windowActive;
	private bool _disposed;

	/// <summary>
	/// Creates a coordinator whose delays use the supplied clock and whose presentation callbacks
	/// are marshalled by the supplied dispatcher.
	/// </summary>
	public WebMonitorCoordinator(
		WebMonitorSnapshotStore snapshotStore,
		TimeProvider timeProvider,
		Action<Action> uiDispatcher)
		: this(
			snapshotStore,
			timeProvider,
			uiDispatcher,
			beforePresentationMutation: null)
	{
	}

	internal WebMonitorCoordinator(
		WebMonitorSnapshotStore snapshotStore,
		TimeProvider timeProvider,
		Action<Action> uiDispatcher,
		Action<string>? beforePresentationMutation)
	{
		ArgumentNullException.ThrowIfNull(snapshotStore);
		ArgumentNullException.ThrowIfNull(timeProvider);
		ArgumentNullException.ThrowIfNull(uiDispatcher);

		_snapshotStore = snapshotStore;
		_timeProvider = timeProvider;
		_uiDispatcher = uiDispatcher;
		_beforePresentationMutation = beforePresentationMutation;
	}

	/// <summary>
	/// Raised when a loaded page's single monitoring projection changes.
	/// </summary>
	public event EventHandler<WebMonitorStatusChangedEventArgs>? StatusChanged;

	/// <summary>
	/// Raised for sanitized transient evaluation failures; failures never erase live state.
	/// </summary>
	public event EventHandler<WebMonitorDiagnosticEventArgs>? DiagnosticChanged;

	/// <summary>Raised with metadata-only live monitoring facts after state changes.</summary>
	public event EventHandler<WebMonitorDiagnosticsChangedEventArgs>? LiveDiagnosticsChanged;

	/// <summary>Reads the current metadata-only live state for a loaded page.</summary>
	/// <returns><see langword="false"/> when the page has no live monitoring registration.</returns>
	public bool TryGetLiveDiagnostics(
		string webPageId,
		out WebMonitorDiagnostics diagnostics)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		var registration = TryGetRegistration(webPageId);
		if (registration is null)
		{
			diagnostics = null!;
			return false;
		}

		diagnostics = CreateLiveDiagnostics(registration);
		return true;
	}

	/// <summary>
	/// Raised after a non-main-frame URL candidate is confirmed and differs from the host's
	/// current fragment-free saved identity.
	/// </summary>
	public event EventHandler<WebMonitorStableUrlChangedEventArgs>? StableUrlChanged;

	/// <summary>
	/// Validates and compiles enabled rules in file order, stops existing loops, applies stable
	/// no-match cleanup, and restarts loaded registrations when monitoring remains enabled.
	/// </summary>
	public async Task SetRulesAsync(
		IReadOnlyList<WebMonitorRule> rules,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(rules);
		var compiled = rules
			.Where(rule => rule.Enabled)
			.Select(WebMonitorRuleCompiler.Compile)
			.ToArray();

		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			List<Registration> registrations;
			lock (_sync)
			{
				registrations = _registrations.Values.ToList();
			}

			await StopLoopsAsync(registrations).ConfigureAwait(false);
			lock (_sync)
			{
				_compiledRules = compiled;
			}

			foreach (var registration in registrations)
			{
				cancellationToken.ThrowIfCancellationRequested();
				bool cleanup;
				lock (registration.SyncRoot)
				{
					registration.PendingCandidate = null;
					registration.NextAttemptAt = _timeProvider.GetUtcNow();
					registration.CleanedForNoMatch = false;
					cleanup = compiled.Length == 0
						|| !registration.Navigating
						&& !registration.AwaitingPostNavigationConfirmation
						&& registration.HasConfirmedStableUrl
						&& FindFirstMatch(compiled, registration.ConfirmedUrl) is null;
				}

				if (cleanup)
				{
					await ClearForNoMatchAsync(registration).ConfigureAwait(false);
				}

				if (compiled.Length > 0)
				{
					StartLoop(registration);
				}
			}
		}
		finally
		{
			_lifecycleGate.Release();
		}

	}

	/// <summary>
	/// Restores retained state before exposing a loaded host to polling and starts monitoring only
	/// when at least one enabled rule exists.
	/// </summary>
	public async Task RegisterAsync(
		string webPageId,
		IWebPageHost host,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentNullException.ThrowIfNull(host);

		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			ThrowIfDisposed();
			var snapshot = await _snapshotStore
				.LoadAsync(webPageId, cancellationToken)
				.ConfigureAwait(false);
			Registration registration = new(webPageId, host);
			registration.Engine.Restore(snapshot);
			registration.Snapshot = snapshot;

			var sourceIdentity = TryNormalize(host.Source);
			if (snapshot is not null
				&& Uri.TryCreate(snapshot.Url, UriKind.Absolute, out var retainedUrl))
			{
				registration.ConfirmedUrl = retainedUrl;
				registration.HasConfirmedStableUrl = true;
			}
			else
			{
				registration.ConfirmedUrl = sourceIdentity;
			}

			registration.SavedResumeUrlIdentity = sourceIdentity;
			registration.Navigating = host.Source is null;
			registration.AwaitingPostNavigationConfirmation = registration.Navigating;
			ApplyCurrentPresentationFacts(registration);

			lock (_sync)
			{
				if (_registrations.ContainsKey(webPageId))
				{
					throw new InvalidOperationException(
						$"Web page '{webPageId}' is already registered.");
				}

				_registrations.Add(webPageId, registration);
			}

			WebMonitorTransition restored;
			lock (registration.SyncRoot)
			{
				restored = registration.Engine.SetPresentationFacts(
					loaded: true,
					registration.Selected,
					registration.WindowVisible,
					registration.WindowActive);
				registration.Snapshot = restored.Snapshot;
			}

			if (restored.SnapshotChanged)
			{
				await QueueSnapshotSave(registration, restored.Snapshot!)
					.ConfigureAwait(false);
			}

			PublishStatus(registration, restored.Status);

			var compiled = GetCompiledRules();
			if (compiled.Length == 0)
			{
				await ClearForNoMatchAsync(registration).ConfigureAwait(false);
				return;
			}

			bool cleanup;
			lock (registration.SyncRoot)
			{
				cleanup = !registration.Navigating
					&& registration.HasConfirmedStableUrl
					&& FindFirstMatch(compiled, registration.ConfirmedUrl) is null;
			}

			if (cleanup)
			{
				await ClearForNoMatchAsync(registration).ConfigureAwait(false);
			}

			StartLoop(registration);
		}
		finally
		{
			_lifecycleGate.Release();
		}

	}

	/// <summary>
	/// Suspends one registration during main-frame navigation and requires the first successful
	/// post-completion observation, after DOM settle, to establish a fresh baseline.
	/// </summary>
	public void SetNavigationState(string webPageId, bool navigating)
	{
		var registration = TryGetRegistration(webPageId);
		if (registration is null)
		{
			return;
		}

		lock (registration.SyncRoot)
		{
			if (registration.Stopping || registration.Removed)
			{
				return;
			}

			registration.Navigating = navigating;
			if (navigating)
			{
				registration.NavigationGeneration++;
				registration.NeedsFreshBaseline = true;
				registration.AwaitingPostNavigationConfirmation = true;
				registration.PendingCandidate = null;
			}
			else
			{
				var savedIdentity = TryNormalize(registration.Host.Source);
				if (savedIdentity is not null)
				{
					registration.SavedResumeUrlIdentity = savedIdentity;
				}

				registration.NextAttemptAt = _timeProvider.GetUtcNow() + DomSettleDelay;
			}
		}

		Pulse(registration);
	}

	/// <summary>
	/// Applies shared selection and window facts to every loaded engine, persisting any unread
	/// acknowledgement and dispatching the resulting status on the UI thread.
	/// </summary>
	public void SetPresentationFacts(
		string? selectedWebPageId,
		bool windowVisible,
		bool windowActive)
	{
		List<Registration> registrations;
		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_selectedWebPageId = selectedWebPageId;
			_windowVisible = windowVisible;
			_windowActive = windowActive;
			registrations = _registrations.Values.ToList();
		}

		foreach (var registration in registrations)
		{
			_beforePresentationMutation?.Invoke(registration.WebPageId);
			WebMonitorTransition transition;
			var becamePresented = false;
			lock (registration.SyncRoot)
			{
				lock (_sync)
				{
					if (!_registrations.TryGetValue(
							registration.WebPageId,
							out var activeRegistration)
						|| !ReferenceEquals(activeRegistration, registration)
						|| registration.Stopping
						|| registration.Removed)
					{
						continue;
					}

					var wasPresented =
						registration.Selected
						&& registration.WindowVisible
						&& registration.WindowActive;
					registration.Selected = string.Equals(
						registration.WebPageId,
						selectedWebPageId,
						StringComparison.Ordinal);
					registration.WindowVisible = windowVisible;
					registration.WindowActive = windowActive;
					becamePresented =
						!wasPresented
						&& registration.Selected
						&& registration.WindowVisible
						&& registration.WindowActive;
					if (becamePresented)
					{
						registration.NextAttemptAt = _timeProvider.GetUtcNow();
					}

					transition = registration.Engine.SetPresentationFacts(
						loaded: true,
						registration.Selected,
						registration.WindowVisible,
						registration.WindowActive);
					registration.Snapshot = transition.Snapshot;

					if (transition.SnapshotChanged && transition.Snapshot is not null)
					{
						_ = QueueSnapshotSave(registration, transition.Snapshot);
					}
				}
			}

			PublishStatus(registration, transition.Status);
			if (becamePresented)
			{
				Pulse(registration);
			}
		}
	}

	/// <summary>
	/// Validates and evaluates one rule exactly once against a loaded host without mutating its
	/// live engine, status, diagnostics, rule set, or retained snapshot.
	/// </summary>
	public async Task<WebMonitorTestResult> TestAsync(
		string webPageId,
		WebMonitorRule rule,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		ArgumentNullException.ThrowIfNull(rule);

		WebMonitorCompiledRule compiled;
		try
		{
			compiled = WebMonitorRuleCompiler.Compile(rule);
		}
		catch (ArgumentException exception)
		{
			return new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: exception.Message);
		}

		var registration = TryGetRegistration(webPageId);
		if (registration is null)
		{
			return new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: "No loaded web tab is registered for testing.");
		}

		using var linkedCancellation =
			CancellationTokenSource.CreateLinkedTokenSource(
				cancellationToken,
				registration.LifetimeCancellation.Token);
		long evaluationGeneration;
		lock (registration.SyncRoot)
		{
			evaluationGeneration = registration.NavigationGeneration;
		}
		var result = await EvaluateOnceAsync(
				registration,
				compiled.Query,
				compiled.Source.Id,
				attempt: 0,
				publishDiagnostic: false,
				evaluationGeneration,
				linkedCancellation.Token)
			.ConfigureAwait(false);
		if (result.Category is not null)
		{
			return new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: result.Category == "Timeout"
					? "The web monitor test timed out after five seconds."
					: "The web monitor test could not evaluate the current document.");
		}

		var evaluation = result.Evaluation!;
		Uri normalizedUrl;
		try
		{
			normalizedUrl = WebMonitorUrl.Normalize(evaluation.DocumentUrl);
		}
		catch (ArgumentException)
		{
			return new WebMonitorTestResult(
				UrlMatched: false,
				Activity: null,
				Revision: null,
				Error: "The web monitor test returned an invalid document URL.");
		}

		return new WebMonitorTestResult(
			compiled.MatchesUrlPattern(normalizedUrl),
			evaluation.Observation?.Activity,
			evaluation.Observation?.Revision,
			Error: null);
	}

	/// <summary>
	/// Stops one loaded-page loop, detaches and observes any pending browser evaluation, publishes
	/// a final non-activity projection, and retains or deletes its snapshot according to lifecycle
	/// intent.
	/// </summary>
	public async Task UnregisterAsync(
		string webPageId,
		bool deleteSnapshot,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(webPageId);
		await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			Registration? registration;
			lock (_sync)
			{
				_registrations.TryGetValue(webPageId, out registration);
			}

			if (registration is not null)
			{
				lock (registration.SyncRoot)
				{
					registration.Stopping = true;
					registration.LifetimeCancellation.Cancel();
				}
			}

			if (registration is null)
			{
				if (deleteSnapshot)
				{
					await _snapshotStore.DeleteAsync(webPageId, cancellationToken)
						.ConfigureAwait(false);
				}

				return;
			}

			await StopLoopAsync(registration).ConfigureAwait(false);
			await GetPersistenceTail(registration).ConfigureAwait(false);

			WebMonitorTransition final;
			lock (registration.SyncRoot)
			{
				registration.Engine.Restore(deleteSnapshot ? null : registration.Snapshot);
				final = registration.Engine.SetPresentationFacts(
					loaded: false,
					registration.Selected,
					registration.WindowVisible,
					registration.WindowActive);
				registration.Snapshot = deleteSnapshot ? null : final.Snapshot;
			}

			PublishStatus(registration, final.Status, force: true);
			if (deleteSnapshot)
			{
				await QueueSnapshotDelete(registration).ConfigureAwait(false);
			}

			lock (_sync)
			{
				_registrations.Remove(webPageId);
			}

			lock (registration.SyncRoot)
			{
				registration.Removed = true;
			}

			registration.LoopCancellation.Dispose();
		}
		finally
		{
			_lifecycleGate.Release();
		}
	}

	/// <summary>
	/// Cancels every registration, detaches and observes pending browser invocations, and awaits
	/// queued snapshot persistence without deleting retained state.
	/// </summary>
	public async ValueTask DisposeAsync()
	{
		await _lifecycleGate.WaitAsync().ConfigureAwait(false);
		try
		{
			List<Registration> registrations;
			lock (_sync)
			{
				if (_disposed)
				{
					return;
				}

				_disposed = true;
				registrations = _registrations.Values.ToList();
				_registrations.Clear();
			}

			foreach (var registration in registrations)
			{
				lock (registration.SyncRoot)
				{
					registration.Stopping = true;
					registration.LifetimeCancellation.Cancel();
				}
			}

			await StopLoopsAsync(registrations).ConfigureAwait(false);
			await Task.WhenAll(registrations.Select(GetPersistenceTail))
				.ConfigureAwait(false);
			foreach (var registration in registrations)
			{
				lock (registration.SyncRoot)
				{
					registration.Removed = true;
				}

				registration.LoopCancellation.Dispose();
			}
		}
		finally
		{
			_lifecycleGate.Release();
			_lifecycleGate.Dispose();
		}
	}

	private void StartLoop(Registration registration)
	{
		lock (registration.SyncRoot)
		{
			if (registration.Stopping
				|| registration.Removed
				|| !registration.LoopTask.IsCompleted)
			{
				return;
			}

			registration.LoopCancellation.Dispose();
			registration.LoopCancellation = new CancellationTokenSource();
			registration.LoopTask = RunLoopAsync(
				registration,
				registration.LoopCancellation.Token);
		}
	}

	private async Task RunLoopAsync(
		Registration registration,
		CancellationToken cancellationToken)
	{
		try
		{
			while (true)
			{
				cancellationToken.ThrowIfCancellationRequested();
				var rules = GetCompiledRules();
				if (rules.Length == 0)
				{
					return;
				}

				TimeSpan delay;
				WebMonitorCompiledRule? matchedRule;
				bool navigating;
				bool pendingCandidate;
				bool pendingInvocation;
				bool cleanup;
				long evaluationGeneration;
				lock (registration.SyncRoot)
				{
					if (registration.Stopping || registration.Removed)
					{
						return;
					}

					navigating = registration.Navigating;
					pendingCandidate = registration.PendingCandidate is not null;
					pendingInvocation =
						registration.PendingInvocation is { IsCompleted: false };
					matchedRule = FindFirstMatch(rules, registration.ConfirmedUrl);
					cleanup = !navigating
						&& !registration.AwaitingPostNavigationConfirmation
						&& registration.HasConfirmedStableUrl
						&& matchedRule is null
						&& !registration.CleanedForNoMatch;
					delay = registration.NextAttemptAt - _timeProvider.GetUtcNow();
					evaluationGeneration = registration.NavigationGeneration;
				}

				if (cleanup)
				{
					await ClearForNoMatchAsync(registration).ConfigureAwait(false);
					continue;
				}

				if (navigating)
				{
					await WaitForPulseAsync(registration, cancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				if (pendingInvocation)
				{
					await WaitForPulseAsync(registration, cancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				if (delay > TimeSpan.Zero)
				{
					await WaitForDelayOrPulseAsync(
							registration,
							delay,
							cancellationToken)
						.ConfigureAwait(false);
					continue;
				}

				var query =
					pendingCandidate || registration.ConfirmedUrl is null || matchedRule is null
						? null
						: matchedRule.Query;
				var ruleId = query is null ? null : matchedRule!.Source.Id;
				int attempt;
				lock (registration.SyncRoot)
				{
					attempt = ++registration.Attempt;
					evaluationGeneration = registration.NavigationGeneration;
				}

				var result = await EvaluateOnceAsync(
						registration,
						query,
						ruleId,
						attempt,
						publishDiagnostic: true,
						evaluationGeneration,
						cancellationToken)
					.ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				lock (registration.SyncRoot)
				{
					if (!IsEvaluationCurrentLocked(
							registration,
							evaluationGeneration))
					{
						continue;
					}
				}

				if (result.Evaluation is null)
				{
					ScheduleNext(
						registration,
						matchedRule?.Source.PollIntervalSeconds
							is int seconds
								? TimeSpan.FromSeconds(seconds)
								: UnmatchedProbeInterval);
					continue;
				}

				await ProcessEvaluationAsync(
						registration,
						result.Evaluation,
						query,
						matchedRule,
						rules,
						attempt,
						evaluationGeneration)
					.ConfigureAwait(false);
			}
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Registration lifecycle cancellation is the normal loop exit.
		}
	}

	private async Task ProcessEvaluationAsync(
		Registration registration,
		WebMonitorEvaluation evaluation,
		WebMonitorDomQuery? query,
		WebMonitorCompiledRule? attemptedRule,
		WebMonitorCompiledRule[] rules,
		int attempt,
		long evaluationGeneration)
	{
		lock (registration.SyncRoot)
		{
			if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
			{
				return;
			}
		}

		Uri normalizedUrl;
		try
		{
			normalizedUrl = WebMonitorUrl.Normalize(evaluation.DocumentUrl);
		}
		catch (ArgumentException)
		{
			PublishDiagnostic(
				registration.WebPageId,
				attemptedRule?.Source.Id,
				"InvalidResult",
				attempt,
				"The browser returned an invalid document URL.");
			ScheduleNext(
				registration,
				attemptedRule is null
					? UnmatchedProbeInterval
					: TimeSpan.FromSeconds(attemptedRule.Source.PollIntervalSeconds));
			return;
		}

		Uri? confirmed;
		Uri? pending;
		lock (registration.SyncRoot)
		{
			if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
			{
				return;
			}

			confirmed = registration.ConfirmedUrl;
			pending = registration.PendingCandidate;
		}

		if (confirmed is null || !SameIdentity(normalizedUrl, confirmed))
		{
			if (pending is not null && SameIdentity(normalizedUrl, pending))
			{
				await ConfirmCandidateAsync(
						registration,
						evaluation.DocumentUrl,
						normalizedUrl,
						rules,
						evaluationGeneration)
					.ConfigureAwait(false);
				return;
			}

			lock (registration.SyncRoot)
			{
				if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
				{
					return;
				}

				registration.PendingCandidate = normalizedUrl;
				registration.NextAttemptAt = _timeProvider.GetUtcNow() + DomSettleDelay;
			}

			return;
		}

		if (pending is not null)
		{
			lock (registration.SyncRoot)
			{
				if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
				{
					return;
				}

				registration.PendingCandidate = null;
				registration.HasConfirmedStableUrl = true;
				registration.AwaitingPostNavigationConfirmation = false;
				registration.NextAttemptAt = _timeProvider.GetUtcNow();
			}

			return;
		}

		lock (registration.SyncRoot)
		{
			if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
			{
				return;
			}

			registration.HasConfirmedStableUrl = true;
			registration.AwaitingPostNavigationConfirmation = false;
		}

		var currentRule = FindFirstMatch(rules, normalizedUrl);
		if (currentRule is null)
		{
			await ClearForNoMatchAsync(registration, evaluationGeneration)
				.ConfigureAwait(false);
			return;
		}

		if (query is null)
		{
			lock (registration.SyncRoot)
			{
				if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
				{
					return;
				}

				registration.NextAttemptAt = _timeProvider.GetUtcNow();
			}

			return;
		}

		if (evaluation.Observation is null)
		{
			lock (registration.SyncRoot)
			{
				if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
				{
					return;
				}

				registration.NextAttemptAt = _timeProvider.GetUtcNow()
					+ TimeSpan.FromSeconds(currentRule.Source.PollIntervalSeconds);
			}

			PublishDiagnostic(
				registration.WebPageId,
				currentRule.Source.Id,
				"InvalidResult",
				attempt,
				"The browser returned no DOM observation.");
			return;
		}

		WebMonitorTransition transition;
		lock (registration.SyncRoot)
		{
			if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
			{
				return;
			}

			if (registration.NeedsFreshBaseline)
			{
				RestoreFreshBaseline(registration);
				registration.NeedsFreshBaseline = false;
			}

			transition = registration.Engine.Observe(
				normalizedUrl,
				currentRule,
				evaluation.Observation,
				_timeProvider.GetUtcNow());
			registration.Snapshot = transition.Snapshot;
			registration.CleanedForNoMatch = false;
			registration.LastError = null;
			registration.NextAttemptAt = _timeProvider.GetUtcNow()
				+ NextObservationDelayLocked(
					registration,
					TimeSpan.FromSeconds(currentRule.Source.PollIntervalSeconds));
		}

		if (transition.SnapshotChanged && transition.Snapshot is not null)
		{
			await QueueSnapshotSave(registration, transition.Snapshot)
				.ConfigureAwait(false);
		}

		PublishStatus(registration, transition.Status);
	}

	private async Task ConfirmCandidateAsync(
		Registration registration,
		Uri rawDocumentUrl,
		Uri normalizedUrl,
		WebMonitorCompiledRule[] rules,
		long evaluationGeneration)
	{
		Uri? savedIdentity;
		lock (registration.SyncRoot)
		{
			if (!IsEvaluationCurrentLocked(registration, evaluationGeneration))
			{
				return;
			}

			savedIdentity = registration.SavedResumeUrlIdentity;
			registration.SavedResumeUrlIdentity = normalizedUrl;
			registration.ConfirmedUrl = normalizedUrl;
			registration.HasConfirmedStableUrl = true;
			registration.PendingCandidate = null;
			registration.AwaitingPostNavigationConfirmation = false;
			registration.NeedsFreshBaseline = true;
			registration.CleanedForNoMatch = false;
			registration.NextAttemptAt = _timeProvider.GetUtcNow();
		}

		if (savedIdentity is null || !SameIdentity(savedIdentity, normalizedUrl))
		{
			PublishStableUrl(registration.WebPageId, rawDocumentUrl, normalizedUrl);
		}

		if (FindFirstMatch(rules, normalizedUrl) is null)
		{
			await ClearForNoMatchAsync(registration, evaluationGeneration)
				.ConfigureAwait(false);
		}
	}

	private async Task<EvaluationAttempt> EvaluateOnceAsync(
		Registration registration,
		WebMonitorDomQuery? query,
		string? ruleId,
		int attempt,
		bool publishDiagnostic,
		long evaluationGeneration,
		CancellationToken cancellationToken)
	{
		Task<WebMonitorEvaluation> invocation;
		while (true)
		{
			await registration.EvaluationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
			Task? pendingWait = null;
			try
			{
				cancellationToken.ThrowIfCancellationRequested();
				lock (registration.SyncRoot)
				{
					if (registration.Stopping || registration.Removed)
					{
						throw new OperationCanceledException(cancellationToken);
					}

					if (registration.PendingInvocation is { IsCompleted: false })
					{
						pendingWait = registration.PulseSource.Task;
					}
				}

				if (pendingWait is null)
				{
					try
					{
						invocation = registration.Host.EvaluateMonitorAsync(
							query,
							cancellationToken);
					}
					catch (OperationCanceledException) when (
						cancellationToken.IsCancellationRequested)
					{
						throw;
					}
					catch (Exception)
					{
						if (publishDiagnostic)
						{
							PublishDiagnostic(
								registration.WebPageId,
								ruleId,
								"Evaluation",
								attempt,
								"The browser could not start the monitoring evaluation.");
						}

						return new EvaluationAttempt(null, "Evaluation");
					}

					lock (registration.SyncRoot)
					{
						registration.PendingInvocation = invocation;
						registration.PendingInvocationGeneration = evaluationGeneration;
					}

					break;
				}
			}
			finally
			{
				registration.EvaluationGate.Release();
			}

			await pendingWait.WaitAsync(cancellationToken).ConfigureAwait(false);
		}

		var timeout = Task.Delay(EvaluationTimeout, _timeProvider, cancellationToken);
		TaskCompletionSource<object?> cancellationSource =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		using var cancellationRegistration =
			cancellationToken.Register(
				static state =>
					((TaskCompletionSource<object?>)state!).TrySetResult(null),
				cancellationSource);
		Task cancellation = cancellationSource.Task;
		var completed = await Task
			.WhenAny(invocation, timeout, cancellation)
			.ConfigureAwait(false);

		if (ReferenceEquals(completed, invocation))
		{
			try
			{
				var evaluation =
					await invocation.ConfigureAwait(false);
				cancellationToken.ThrowIfCancellationRequested();
				ClearPendingInvocation(
					registration,
					invocation,
					evaluationGeneration,
					resumeImmediately: false);
				return new EvaluationAttempt(evaluation, Category: null);
			}
			catch (OperationCanceledException) when (
				cancellationToken.IsCancellationRequested)
			{
				ObserveDetachedInvocation(
					registration,
					invocation,
					evaluationGeneration,
					ruleId,
					attempt,
					publishDiagnostic);
				throw;
			}
			catch (Exception)
			{
				ClearPendingInvocation(
					registration,
					invocation,
					evaluationGeneration,
					resumeImmediately: false);
				cancellationToken.ThrowIfCancellationRequested();
				if (publishDiagnostic)
				{
					PublishDiagnostic(
						registration.WebPageId,
						ruleId,
						"Evaluation",
						attempt,
						"The browser could not start the monitoring evaluation.");
				}

				return new EvaluationAttempt(null, "Evaluation");
			}
		}

		if (ReferenceEquals(completed, timeout))
		{
			if (publishDiagnostic)
			{
				PublishDiagnostic(
					registration.WebPageId,
					ruleId,
					"Timeout",
					attempt,
					"The browser monitoring evaluation exceeded five seconds.");
			}

			ObserveDetachedInvocation(
				registration,
				invocation,
				evaluationGeneration,
				ruleId,
				attempt,
				publishDiagnostic);
			cancellationToken.ThrowIfCancellationRequested();
			return new EvaluationAttempt(null, "Timeout");
		}

		ObserveDetachedInvocation(
			registration,
			invocation,
			evaluationGeneration,
			ruleId,
			attempt,
			publishDiagnostic);
		cancellationToken.ThrowIfCancellationRequested();
		throw new OperationCanceledException(cancellationToken);
	}

	private void ObserveDetachedInvocation(
		Registration registration,
		Task<WebMonitorEvaluation> invocation,
		long invocationGeneration,
		string? ruleId,
		int attempt,
		bool publishDiagnostic)
	{
		var observer = ObserveDetachedInvocationAsync(
			registration,
			invocation,
			invocationGeneration,
			ruleId,
			attempt,
			publishDiagnostic);
		lock (registration.SyncRoot)
		{
			if (ReferenceEquals(registration.PendingInvocation, invocation)
				&& registration.PendingInvocationGeneration == invocationGeneration)
			{
				registration.PendingInvocationObserver = observer;
			}
		}
	}

	private async Task ObserveDetachedInvocationAsync(
		Registration registration,
		Task<WebMonitorEvaluation> invocation,
		long invocationGeneration,
		string? ruleId,
		int attempt,
		bool publishDiagnostic)
	{
		var failed = false;
		try
		{
			await invocation.ConfigureAwait(false);
		}
		catch (OperationCanceledException)
		{
			// A detached native invocation may honor its original lifecycle token.
		}
		catch (Exception)
		{
			failed = true;
		}

		var cleared = ClearPendingInvocation(
			registration,
			invocation,
			invocationGeneration,
			resumeImmediately: true);
		if (failed
			&& publishDiagnostic
			&& cleared
			&& IsRegistrationActive(registration))
		{
			PublishDiagnostic(
				registration.WebPageId,
				ruleId,
				"Evaluation",
				attempt,
				"The timed-out browser monitoring evaluation later failed.");
		}
	}

	private bool ClearPendingInvocation(
		Registration registration,
		Task<WebMonitorEvaluation> invocation,
		long invocationGeneration,
		bool resumeImmediately)
	{
		bool cleared;
		lock (registration.SyncRoot)
		{
			cleared = ReferenceEquals(registration.PendingInvocation, invocation)
				&& registration.PendingInvocationGeneration == invocationGeneration;
			if (cleared)
			{
				registration.PendingInvocation = null;
				registration.PendingInvocationObserver = Task.CompletedTask;
				if (resumeImmediately && !registration.Navigating)
				{
					registration.NextAttemptAt = _timeProvider.GetUtcNow();
				}
			}
		}

		if (cleared)
		{
			Pulse(registration);
		}

		return cleared;
	}

	private bool IsRegistrationActive(Registration registration)
	{
		bool registered;
		lock (_sync)
		{
			registered = _registrations.TryGetValue(
					registration.WebPageId,
					out var current)
				&& ReferenceEquals(current, registration);
		}

		if (!registered)
		{
			return false;
		}

		lock (registration.SyncRoot)
		{
			return !registration.Stopping && !registration.Removed;
		}
	}

	private async Task ClearForNoMatchAsync(
		Registration registration,
		long? evaluationGeneration = null)
	{
		WebMonitorTransition transition;
		lock (registration.SyncRoot)
		{
			if (registration.Stopping
				|| registration.Removed
				|| evaluationGeneration is not null
					&& !IsEvaluationCurrentLocked(
						registration,
						evaluationGeneration.Value))
			{
				return;
			}

			registration.Engine.Restore(null);
			transition = registration.Engine.SetPresentationFacts(
				loaded: true,
				registration.Selected,
				registration.WindowVisible,
				registration.WindowActive);
			registration.Snapshot = null;
			registration.CleanedForNoMatch = true;
			registration.NeedsFreshBaseline = false;
			registration.NextAttemptAt = _timeProvider.GetUtcNow() + UnmatchedProbeInterval;
		}

		await QueueSnapshotDelete(registration).ConfigureAwait(false);
		PublishStatus(registration, transition.Status);
	}

	private static void RestoreFreshBaseline(Registration registration)
	{
		var retained = registration.Snapshot;
		registration.Engine.Restore(
			retained is null
				? null
				: retained with
				{
					RuleFingerprint = "fresh-baseline:" + retained.RuleFingerprint
				});
		var facts = registration.Engine.SetPresentationFacts(
			loaded: true,
			registration.Selected,
			registration.WindowVisible,
			registration.WindowActive);
		registration.Snapshot = facts.Snapshot;
	}

	private Task QueueSnapshotSave(
		Registration registration,
		WebMonitorSnapshot snapshot) =>
		QueuePersistence(
			registration,
			() => _snapshotStore.SaveAsync(snapshot, CancellationToken.None));

	private Task QueueSnapshotDelete(Registration registration) =>
		QueuePersistence(
			registration,
			() => _snapshotStore.DeleteAsync(
				registration.WebPageId,
				CancellationToken.None));

	private Task QueuePersistence(
		Registration registration,
		Func<Task> operation)
	{
		lock (registration.SyncRoot)
		{
			registration.PersistenceTail = PersistAfterAsync(
				registration,
				registration.PersistenceTail,
				operation);
			return registration.PersistenceTail;
		}
	}

	private async Task PersistAfterAsync(
		Registration registration,
		Task previous,
		Func<Task> operation)
	{
		try
		{
			await previous.ConfigureAwait(false);
			await operation().ConfigureAwait(false);
		}
		catch (Exception)
		{
			PublishDiagnostic(
				registration.WebPageId,
				ruleId: null,
				"Persistence",
				registration.Attempt,
				"The retained web monitoring snapshot could not be updated.");
		}
	}

	private static Task GetPersistenceTail(Registration registration)
	{
		lock (registration.SyncRoot)
		{
			return registration.PersistenceTail;
		}
	}

	private void ApplyCurrentPresentationFacts(Registration registration)
	{
		lock (_sync)
		{
			registration.Selected = string.Equals(
				registration.WebPageId,
				_selectedWebPageId,
				StringComparison.Ordinal);
			registration.WindowVisible = _windowVisible;
			registration.WindowActive = _windowActive;
		}
	}

	/// <summary>
	/// Shortens the wait after a successful observation while the user is looking at the page, so a
	/// page that only refreshes its DOM once it becomes visible is re-read while still viewed rather
	/// than one rule interval later, when the resulting event would be recorded as unread.
	/// </summary>
	private static TimeSpan NextObservationDelayLocked(
		Registration registration,
		TimeSpan ruleInterval)
	{
		var activelyViewed = registration.Selected
			&& registration.WindowVisible
			&& registration.WindowActive;
		return activelyViewed && ruleInterval > ActivelyViewedPollInterval
			? ActivelyViewedPollInterval
			: ruleInterval;
	}

	private void ScheduleNext(Registration registration, TimeSpan interval)
	{
		lock (registration.SyncRoot)
		{
			registration.NextAttemptAt = _timeProvider.GetUtcNow() + interval;
		}

		PublishLiveDiagnostics(registration);
	}

	private static async Task StopLoopsAsync(IReadOnlyList<Registration> registrations)
	{
		foreach (var registration in registrations)
		{
			registration.LoopCancellation.Cancel();
			Pulse(registration);
		}

		await Task.WhenAll(registrations.Select(AwaitLoopAsync)).ConfigureAwait(false);
	}

	private static async Task StopLoopAsync(Registration registration)
	{
		registration.LoopCancellation.Cancel();
		Pulse(registration);
		await AwaitLoopAsync(registration).ConfigureAwait(false);
	}

	private static async Task AwaitLoopAsync(Registration registration)
	{
		Task loop;
		lock (registration.SyncRoot)
		{
			loop = registration.LoopTask;
		}

		await loop.ConfigureAwait(false);
	}

	private async Task WaitForDelayOrPulseAsync(
		Registration registration,
		TimeSpan delay,
		CancellationToken cancellationToken)
	{
		Task pulse;
		lock (registration.SyncRoot)
		{
			pulse = registration.PulseSource.Task;
		}

		var timer = Task.Delay(delay, _timeProvider, cancellationToken);
		await Task.WhenAny(timer, pulse).ConfigureAwait(false);
		cancellationToken.ThrowIfCancellationRequested();
	}

	private static async Task WaitForPulseAsync(
		Registration registration,
		CancellationToken cancellationToken)
	{
		Task pulse;
		lock (registration.SyncRoot)
		{
			pulse = registration.PulseSource.Task;
		}

		await pulse.WaitAsync(cancellationToken).ConfigureAwait(false);
	}

	private static void Pulse(Registration registration)
	{
		TaskCompletionSource<object?> pulse;
		lock (registration.SyncRoot)
		{
			pulse = registration.PulseSource;
			registration.PulseSource =
				new TaskCompletionSource<object?>(
					TaskCreationOptions.RunContinuationsAsynchronously);
		}

		pulse.TrySetResult(null);
	}

	private Registration? TryGetRegistration(string webPageId)
	{
		lock (_sync)
		{
			return _registrations.GetValueOrDefault(webPageId);
		}
	}

	private WebMonitorCompiledRule[] GetCompiledRules()
	{
		lock (_sync)
		{
			return _compiledRules;
		}
	}

	private static WebMonitorCompiledRule? FindFirstMatch(
		IReadOnlyList<WebMonitorCompiledRule> rules,
		Uri? url)
	{
		if (url is null)
		{
			return null;
		}

		foreach (var rule in rules)
		{
			if (rule.Matches(url))
			{
				return rule;
			}
		}

		return null;
	}

	private static Uri? TryNormalize(Uri? uri)
	{
		if (uri is null || !uri.IsAbsoluteUri)
		{
			return null;
		}

		return WebMonitorUrl.Normalize(uri);
	}

	private static bool SameIdentity(Uri left, Uri right) =>
		string.Equals(
			left.AbsoluteUri,
			right.AbsoluteUri,
			StringComparison.Ordinal);

	private void PublishStatus(
		Registration registration,
		WebMonitorStatus status,
		bool force = false)
	{
		var changed = false;
		lock (registration.SyncRoot)
		{
			if (force || registration.LastPublishedStatus != status)
			{
				registration.LastPublishedStatus = status;
				changed = true;
			}
		}

		PublishLiveDiagnostics(registration);
		if (!changed)
		{
			return;
		}

		WebMonitorStatusChangedEventArgs args =
			new(registration.WebPageId, status);
		DispatchSafely(() => StatusChanged?.Invoke(this, args));
	}

	private void PublishLiveDiagnostics(Registration registration)
	{
		var diagnostics = CreateLiveDiagnostics(registration);

		DispatchSafely(() => LiveDiagnosticsChanged?.Invoke(
			this,
			new WebMonitorDiagnosticsChangedEventArgs(diagnostics)));
	}

	private WebMonitorDiagnostics CreateLiveDiagnostics(Registration registration)
	{
		var rules = GetCompiledRules();
		lock (registration.SyncRoot)
		{
			var rule = FindFirstMatch(rules, registration.ConfirmedUrl);
			var snapshot = registration.Snapshot;
			return new WebMonitorDiagnostics(
				registration.WebPageId,
				registration.ConfirmedUrl?.AbsoluteUri,
				rule?.Source.Id,
				rule?.Source.Title,
				registration.LastPublishedStatus ?? WebMonitorStatus.None,
				snapshot?.Activity,
				snapshot?.Revision,
				snapshot?.Unread == true,
				snapshot?.ObservedAt,
				registration.Attempt,
				registration.NextAttemptAt == default
					? null
					: registration.NextAttemptAt,
				registration.Navigating,
				registration.LastError);
		}
	}

	private void PublishDiagnostic(
		string webPageId,
		string? ruleId,
		string category,
		int attempt,
		string message)
	{
		var registration = TryGetRegistration(webPageId);
		if (registration is not null)
		{
			lock (registration.SyncRoot)
			{
				registration.LastError =
					$"{ruleId ?? "URL probe"} / {category} / attempt {attempt}: {message}";
			}

			PublishLiveDiagnostics(registration);
		}

		WebMonitorDiagnosticEventArgs args =
			new(webPageId, ruleId, category, attempt, message);
		DispatchSafely(() => DiagnosticChanged?.Invoke(this, args));
	}

	private void PublishStableUrl(
		string webPageId,
		Uri rawDocumentUrl,
		Uri normalizedUrl)
	{
		WebMonitorStableUrlChangedEventArgs args =
			new(webPageId, rawDocumentUrl, normalizedUrl);
		DispatchSafely(() => StableUrlChanged?.Invoke(this, args));
	}

	private void DispatchSafely(Action callback)
	{
		try
		{
			_uiDispatcher(
				() =>
				{
					try
					{
						callback();
					}
					catch
					{
						// UI observers are isolated from monitoring lifecycle work.
					}
				});
		}
		catch
		{
			// Dispatcher failures cannot be reported through the same dispatcher.
		}
	}

	private static bool IsEvaluationCurrentLocked(
		Registration registration,
		long evaluationGeneration) =>
		!registration.Stopping
		&& !registration.Removed
		&& !registration.Navigating
		&& registration.NavigationGeneration == evaluationGeneration;

	private void ThrowIfDisposed() =>
		ObjectDisposedException.ThrowIf(_disposed, this);

	private sealed class Registration
	{
		public Registration(string webPageId, IWebPageHost host)
		{
			WebPageId = webPageId;
			Host = host;
			Engine = new WebMonitorStateEngine(webPageId);
		}

		public object SyncRoot { get; } = new();
		public string WebPageId { get; }
		public IWebPageHost Host { get; }
		public WebMonitorStateEngine Engine { get; }
		public SemaphoreSlim EvaluationGate { get; } = new(1, 1);
		public CancellationTokenSource LifetimeCancellation { get; } = new();
		public CancellationTokenSource LoopCancellation { get; set; } = new();
		public Task LoopTask { get; set; } = Task.CompletedTask;
		public Task PersistenceTail { get; set; } = Task.CompletedTask;
		public Task<WebMonitorEvaluation>? PendingInvocation { get; set; }
		public Task PendingInvocationObserver { get; set; } = Task.CompletedTask;
		public long PendingInvocationGeneration { get; set; }
		public TaskCompletionSource<object?> PulseSource { get; set; } =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		public WebMonitorSnapshot? Snapshot { get; set; }
		public Uri? ConfirmedUrl { get; set; }
		public Uri? SavedResumeUrlIdentity { get; set; }
		public Uri? PendingCandidate { get; set; }
		public DateTimeOffset NextAttemptAt { get; set; }
		public WebMonitorStatus? LastPublishedStatus { get; set; }
		public bool HasConfirmedStableUrl { get; set; }
		public bool Navigating { get; set; }
		public bool AwaitingPostNavigationConfirmation { get; set; }
		public bool NeedsFreshBaseline { get; set; }
		public bool CleanedForNoMatch { get; set; }
		public bool Selected { get; set; }
		public bool WindowVisible { get; set; }
		public bool WindowActive { get; set; }
		public bool Stopping { get; set; }
		public bool Removed { get; set; }
		public long NavigationGeneration { get; set; }
		public int Attempt { get; set; }
		public string? LastError { get; set; }
	}

	private sealed record EvaluationAttempt(
		WebMonitorEvaluation? Evaluation,
		string? Category);
}
