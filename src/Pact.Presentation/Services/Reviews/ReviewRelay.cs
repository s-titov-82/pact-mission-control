using System.Text.Json;
using System.Text.Json.Serialization;
using Pact.Core.AgentControl;
using Pact.Core.Reviews;
using Pact.Core.Scenarios;
using Pact.Infrastructure.Reviews;
using Pact.Presentation.Services.AgentControl;

namespace Pact.Presentation.Services.Reviews;

/// <summary>An active review, as the orchestrator sees it.</summary>
public sealed record ReviewSummary(
	string ReviewId,
	string ProjectId,
	string AuthorSessionId,
	string ReviewerSessionId,
	ReviewState State,
	int Iteration,
	ReviewRole? Turn,
	string LogPath,
	int MaxIterations);

/// <summary>
/// Runs reviews between two agents through Pact: starts the reviewer, hands turns from one side
/// to the other, nudges the side whose turn it is with a short trigger, keeps the log and ends the
/// review. Agents pull their turn and hand it back over MCP; nothing passes through files.
/// </summary>
public sealed class ReviewRelay
{
	/// <summary>What is typed into the terminal of the side whose turn it is.</summary>
	public const string TriggerText = "Pact: your review turn is ready — call pact_get_review_turn.";

	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly IReviewRelayHost _host;
	private readonly Func<IReadOnlyList<ScenarioDefinition>> _templates;
	private readonly Func<IReadOnlyList<ReviewProfile>> _profiles;
	private readonly ReviewLogWriter _log;
	private readonly TimeProvider _time;
	private readonly Lock _gate = new();
	private readonly List<Review> _active = [];
	private readonly Dictionary<string, Review> _finished = new(StringComparer.Ordinal);
	private bool _shuttingDown;

	/// <summary>Creates a relay over the shell operations and the live settings.</summary>
	public ReviewRelay(
		IReviewRelayHost host,
		Func<IReadOnlyList<ScenarioDefinition>> templates,
		Func<IReadOnlyList<ReviewProfile>> profiles,
		ReviewLogWriter log,
		TimeProvider time)
	{
		_host = host ?? throw new ArgumentNullException(nameof(host));
		_templates = templates ?? throw new ArgumentNullException(nameof(templates));
		_profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
		_log = log ?? throw new ArgumentNullException(nameof(log));
		_time = time ?? throw new ArgumentNullException(nameof(time));
	}

	/// <summary>
	/// Raised, never under a lock and on any thread, when a review starts, its turn passes or it ends.
	/// </summary>
	public event EventHandler? Changed;

	/// <summary>
	/// Starts a review of the caller's work: creates and starts the reviewer, then gives it the
	/// first turn.
	/// </summary>
	/// <returns>The review id, or why the review did not start.</returns>
	public async Task<AgentControlResult> RequestAsync(
		string authorSessionId,
		RequestReviewRequest request,
		CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);
		if (!_host.TryGetProject(authorSessionId, out var project))
		{
			return AgentControlResult.Refused(
				"owner-not-a-project",
				"This session is a ROOT tab and has no project, so it cannot start a review.");
		}

		// The ids in an agent's tool schema can be older than the settings, so a refusal names the
		// ones that exist now.
		var templates = _templates();
		var template = templates.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ScenarioId, StringComparison.Ordinal));
		if (template is null)
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				$"Unknown review template '{request.ScenarioId}'. Available: {string.Join(", ", templates.Select(candidate => candidate.Id))}.");
		}

		var profiles = _profiles();
		var profile = profiles.FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ReviewProfileId, StringComparison.Ordinal));
		if (profile is null)
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				$"Unknown reviewer profile '{request.ReviewProfileId}'. Available: {string.Join(", ", profiles.Select(candidate => candidate.Id))}.");
		}

		if (AgentControlRequestValidator.Validate(new AgentControlOwner(IsRoot: false, project.ProjectId), request)
			is { } invalid)
		{
			return AgentControlResult.Refused(invalid);
		}

		if (!_host.HasAgentControl(profile.Kind))
		{
			return AgentControlResult.Refused(
				"reviewer-has-no-mcp",
				$"Reviewer profile '{profile.Id}' starts an agent without Pact's MCP tools, so it could not take part.");
		}

		// The author is reserved before the first await, so a second request cannot start a
		// second reviewer while this one is still starting.
		Review review;
		lock (_gate)
		{
			if (_shuttingDown)
			{
				return AgentControlResult.Refused("pact-shutting-down", "Pact is closing, so no review can start.");
			}

			if (FindActive(authorSessionId) is not null)
			{
				return AlreadyActive();
			}

			review = new Review(
				Guid.NewGuid().ToString("N"),
				project,
				authorSessionId,
				template,
				profile,
				request.Target,
				request.MaxIterations ?? template.MaxIterations);
			_active.Add(review);
		}

		OnChanged();

		_host.Trace($"review phase=requested review={review.Id} author={authorSessionId} template={template.Id} profile={profile.Id}");
		using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, review.StartCancellation.Token);
		try
		{
			var reviewerSessionId = await _host.CreateReviewerAsync(project.ProjectId, profile, start.Token).ConfigureAwait(false);
			lock (_gate)
			{
				review.ReviewerSessionId = reviewerSessionId;
			}

			OnChanged();

			await WriteHeaderAsync(review).ConfigureAwait(false);
			var started = await _host.StartReviewerAsync(reviewerSessionId, start.Token).ConfigureAwait(false);
			_host.Trace($"review phase=reviewer-start review={review.Id} ready={started.Ready}");
			if (!started.Ready)
			{
				return await FailStartAsync(
					review,
					ReviewState.Failed,
					$"The reviewer did not become ready ({started.StatusLine}).").ConfigureAwait(false);
			}

			await review.Transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
			try
			{
				ReviewTurn? first = null;
				lock (_gate)
				{
					if (!review.Ended)
					{
						review.Conversation = new ReviewConversation(
							review.Id,
							authorSessionId,
							reviewerSessionId,
							review.MaxIterations);
						first = review.Conversation.Start();
					}
				}

				if (first is null)
				{
					return await FailStartAsync(review, review.EndState, review.EndReason).ConfigureAwait(false);
				}

				OnChanged();
				StartTrigger(review, reviewerSessionId, first.TurnId);
			}
			finally
			{
				review.Transition.Release();
			}

			return AgentControlResult.Ok(review.Id);
		}
		catch (OperationCanceledException) when (review.StartCancellation.IsCancellationRequested)
		{
			return await FailStartAsync(review, review.EndState, review.EndReason).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			await FailStartAsync(review, ReviewState.Aborted, "The review request was cancelled.").ConfigureAwait(false);
			throw;
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_host.Trace($"review phase=start-failed review={review.Id} error={exception.GetType().Name}");
			return await FailStartAsync(review, ReviewState.Failed, exception.Message).ConfigureAwait(false);
		}
	}

	/// <summary>
	/// Describes the caller's review: whose turn it is, the message and the other side's last
	/// answer for its own turn, or how the review ended.
	/// </summary>
	public AgentControlResult GetTurn(string sessionId)
	{
		lock (_gate)
		{
			var review = FindActive(sessionId) ?? _finished.GetValueOrDefault(sessionId);
			if (review is null)
			{
				return NoReview();
			}

			var conversation = review.Conversation;
			if (review.Ended || conversation is null || conversation.State != ReviewState.Running)
			{
				var state = conversation?.State is { } finalState and not ReviewState.Running ? finalState : review.EndState;
				var last = review.Ended ? conversation?.LastAnswer : null;
				return AgentControlResult.Ok(JsonSerializer.Serialize(
					new TurnView(
						review.Id,
						review.Ended ? state.ToString() : "Starting",
						last?.Iteration ?? conversation?.CurrentTurn?.Iteration ?? 0,
						YourTurn: false,
						Message: null,
						PreviousTurn: null,
						TurnId: null,
						EndReason: review.Ended ? conversation?.EndReason ?? review.EndReason : null,
						FinalText: last?.Text,
						FinalFrom: last is null ? null : last.Role == ReviewRole.Author ? "author" : "reviewer"),
					JsonOptions));
			}

			var role = conversation.RoleOf(sessionId);
			var turn = conversation.CurrentTurn!;
			var yourTurn = turn.Role == role;
			var previousTurn = conversation.LastText(role == ReviewRole.Author ? ReviewRole.Reviewer : ReviewRole.Author);
			return AgentControlResult.Ok(JsonSerializer.Serialize(
				new TurnView(
					review.Id,
					conversation.State.ToString(),
					turn.Iteration,
					yourTurn,
					yourTurn ? review.Renderer.Render(role, turn.Iteration, previousTurn) : null,
					previousTurn,
					yourTurn ? turn.TurnId : null,
					EndReason: null,
					FinalText: null,
					FinalFrom: null),
				JsonOptions));
		}
	}

	/// <summary>Takes the caller's answer to its turn and hands the turn to the other side.</summary>
	public async Task<AgentControlResult> SubmitAsync(
		string sessionId,
		string turnId,
		string text,
		bool approved,
		CancellationToken cancellationToken)
	{
		Review? review;
		lock (_gate)
		{
			review = FindActive(sessionId) ?? _finished.GetValueOrDefault(sessionId);
		}

		if (review is null)
		{
			return NoReview();
		}

		await review.Transition.WaitAsync(cancellationToken).ConfigureAwait(false);
		try
		{
			var conversation = review.Conversation;
			if (conversation is null)
			{
				return AgentControlResult.Refused(
					review.Ended ? "review-ended" : "not-your-turn",
					review.Ended ? $"The review ended before it started: {review.EndReason}" : "The review is still starting.");
			}

			if (review.Ended)
			{
				// An end that has begun but not reached the conversation yet still closes it to
				// new answers; a repeated answer keeps its stored result.
				conversation.End(review.EndState, review.EndReason);
			}

			var answered = conversation.CurrentTurn;
			var outcome = conversation.Submit(sessionId, turnId, text, approved);
			if (!outcome.Accepted)
			{
				return AgentControlResult.Refused(outcome.ErrorCode!, outcome.ErrorMessage!);
			}

			var payload = JsonSerializer.Serialize(
				new SubmitView(review.Id, outcome.State.ToString(), outcome.Repeated),
				JsonOptions);
			if (outcome.Repeated)
			{
				return AgentControlResult.Ok(payload);
			}

			_host.Trace($"review phase=turn-accepted review={review.Id} role={answered!.Role} iteration={answered.Iteration} approved={approved}");
			await DrainTriggerAsync(review).ConfigureAwait(false);
			await WriteLogAsync(
				review,
				"turn",
				path => _log.AppendTurnAsync(path, answered.Role, answered.Iteration, _time.GetLocalNow(), text, CancellationToken.None))
				.ConfigureAwait(false);
			if (conversation.State == ReviewState.Running)
			{
				OnChanged();
				var next = conversation.CurrentTurn!;
				StartTrigger(
					review,
					next.Role == ReviewRole.Author ? review.AuthorSessionId : review.ReviewerSessionId!,
					next.TurnId);
			}
			else
			{
				bool ending;
				lock (_gate)
				{
					ending = TryBeginEnd(review, conversation.State, conversation.EndReason!);
				}

				if (ending)
				{
					await FinishAsync(review).ConfigureAwait(false);
				}
			}

			return AgentControlResult.Ok(payload);
		}
		finally
		{
			review.Transition.Release();
		}
	}

	/// <summary>Retries a trigger that nothing was written for once its session can take it.</summary>
	public void NotifyStatusChanged(string sessionId)
	{
		Review? review;
		Trigger? trigger;
		lock (_gate)
		{
			review = _active.Concat(_finished.Values)
				.FirstOrDefault(candidate => candidate.Trigger is { } pending
					&& (pending.WaitingForReady || pending.Running)
					&& string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal));
			trigger = review?.Trigger;
			if (trigger is null || review!.Quiet || !_host.IsReadyForTrigger(sessionId))
			{
				return;
			}

			if (trigger.Running)
			{
				// The delivery is still finishing; it retries on its own if it wrote nothing.
				trigger.RetryRequested = true;
				return;
			}

			trigger.WaitingForReady = false;
		}

		_ = RunTriggerAsync(review, trigger);
	}

	/// <summary>Aborts the review a closed tab took part in and forgets the tab's finished review.</summary>
	public async Task NotifySessionClosedAsync(string sessionId)
	{
		lock (_gate)
		{
			// A review still starting or ending retires later; it must not bring the tab back.
			// Ended reviews count too: one may be between its end and its retirement.
			_active.FirstOrDefault(review => IsParticipant(review, sessionId))?.ClosedSessionIds.Add(sessionId);
		}

		await EndFromOutsideAsync(sessionId, ReviewState.Aborted, "tab closed").ConfigureAwait(false);
		lock (_gate)
		{
			// Nobody can ask about it any more once the tab is gone.
			_finished.Remove(sessionId);
		}
	}

	/// <summary>Fails the review a session whose process exited took part in.</summary>
	public Task NotifySessionDiedAsync(string sessionId) =>
		EndFromOutsideAsync(sessionId, ReviewState.Failed, "process exited");

	/// <summary>Aborts a project's reviews quietly when the project is paused or closed.</summary>
	/// <remarks>
	/// The log gets its footer, but no tab closes, no document opens and the author is not told:
	/// the project is stopping and the user is in the middle of that.
	/// </remarks>
	public Task EndProjectReviewsAsync(string projectId) =>
		EndQuietlyAsync(
			review => string.Equals(review.Project.ProjectId, projectId, StringComparison.Ordinal),
			"The project was paused or closed.");

	/// <summary>
	/// Silences every review the moment Pact starts closing: from here a review that ends, even
	/// on an answer still in flight, closes no tab, opens no document and tells no one, and no
	/// new review starts.
	/// </summary>
	public void BeginShutdown()
	{
		lock (_gate)
		{
			_shuttingDown = true;
			foreach (var review in _active.Concat(_finished.Values))
			{
				review.Quiet = true;
				review.Trigger?.Cancellation.Cancel();
			}
		}
	}

	/// <summary>Aborts every review while Pact closes; nothing is delivered and no document opens.</summary>
	public Task ShutdownAsync()
	{
		BeginShutdown();
		return EndQuietlyAsync(static _ => true, "Pact is shutting down.");
	}

	/// <summary>Reviews that are starting or running.</summary>
	public IReadOnlyList<ReviewSummary> ListActive()
	{
		lock (_gate)
		{
			return [.. _active.Where(review => !review.Ended).Select(Summarize)];
		}
	}

	/// <summary>
	/// Whether a review still writes the log at a path: from its start until its footer is in,
	/// which outlasts <see cref="ListActive"/> while the review ends.
	/// </summary>
	public bool IsLogInUse(string logPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(logPath);
		var path = Path.GetFullPath(logPath);
		lock (_gate)
		{
			return LogsInUse().Any(used => string.Equals(Path.GetFullPath(used), path, StringComparison.OrdinalIgnoreCase));
		}
	}

	/// <summary>An active review, or <see langword="null"/>.</summary>
	public ReviewSummary? Find(string reviewId)
	{
		lock (_gate)
		{
			return _active
				.Where(review => !review.Ended && string.Equals(review.Id, reviewId, StringComparison.Ordinal))
				.Select(Summarize)
				.FirstOrDefault();
		}
	}

	/// <summary>Shows an active review's log in its project's Docs pane.</summary>
	/// <returns><see langword="false"/> when the review is not active or has no log yet.</returns>
	public async Task<bool> OpenLogAsync(string reviewId)
	{
		Review? review;
		lock (_gate)
		{
			review = _active.FirstOrDefault(candidate => !candidate.Ended
				&& candidate.LogPath is not null
				&& string.Equals(candidate.Id, reviewId, StringComparison.Ordinal));
		}

		if (review is null)
		{
			return false;
		}

		await OpenLogAsync(review).ConfigureAwait(false);
		return true;
	}

	private async Task EndFromOutsideAsync(string sessionId, ReviewState state, string what)
	{
		Review? review;
		lock (_gate)
		{
			review = FindActive(sessionId);
			if (review is null)
			{
				return;
			}

			var isAuthor = string.Equals(sessionId, review.AuthorSessionId, StringComparison.Ordinal);
			if (!TryBeginEnd(review, state, $"{(isAuthor ? "author" : "reviewer")} {what}"))
			{
				return;
			}

			review.TellAuthor = !isAuthor;
			if (review.Conversation is null)
			{
				// The start in progress sees its cancellation and finishes the review itself.
				return;
			}
		}

		await EndRunningAsync(review).ConfigureAwait(false);
	}

	private async Task EndQuietlyAsync(Func<Review, bool> applies, string reason)
	{
		List<Review> running = [];
		lock (_gate)
		{
			foreach (var review in _active.Where(applies).ToArray())
			{
				review.Quiet = true;
				if (TryBeginEnd(review, ReviewState.Aborted, reason) && review.Conversation is not null)
				{
					running.Add(review);
				}
			}
		}

		foreach (var review in running)
		{
			await EndRunningAsync(review).ConfigureAwait(false);
		}
	}

	private async Task EndRunningAsync(Review review)
	{
		await review.Transition.WaitAsync(CancellationToken.None).ConfigureAwait(false);
		try
		{
			review.Conversation!.End(review.EndState, review.EndReason);
			await FinishAsync(review).ConfigureAwait(false);
		}
		finally
		{
			review.Transition.Release();
		}
	}

	// Runs once per review, after its end has begun and while the transition gate is held.
	private async Task FinishAsync(Review review)
	{
		OnChanged();
		await DrainTriggerAsync(review).ConfigureAwait(false);
		var conversation = review.Conversation!;
		var state = conversation.State;
		var reason = conversation.EndReason ?? review.EndReason;
		await WriteFooterThenRetireAsync(review, state, reason).ConfigureAwait(false);
		_host.Trace($"review phase=ended review={review.Id} state={state}");
		PruneLogs(review);
		if (review.Quiet)
		{
			return;
		}

		if (state is ReviewState.Completed or ReviewState.MaxIterationsReached)
		{
			await RunHostStepAsync(review, "close-reviewer", () => _host.CloseSessionAsync(review.ReviewerSessionId!))
				.ConfigureAwait(false);
		}

		await RunHostStepAsync(review, "open-log", () => OpenLogAsync(review)).ConfigureAwait(false);
		if (review.TellAuthor)
		{
			StartTrigger(review, review.AuthorSessionId, "final");
		}
	}

	private async Task<AgentControlResult> FailStartAsync(Review review, ReviewState state, string reason)
	{
		lock (_gate)
		{
			TryBeginEnd(review, state, reason);
		}

		OnChanged();
		_host.Trace($"review phase=start-ended review={review.Id} state={review.EndState}");
		await WriteFooterThenRetireAsync(review, review.EndState, review.EndReason).ConfigureAwait(false);
		PruneLogs(review);
		if (!review.Quiet && review.LogPath is not null)
		{
			await RunHostStepAsync(review, "open-log", () => OpenLogAsync(review)).ConfigureAwait(false);
		}

		return AgentControlResult.Refused(
			"review-start-failed",
			$"The review did not start: {review.EndReason} The reviewer tab, if it was created, stays open.");
	}

	// The review keeps its log claimed until the footer is in, so a review starting meanwhile
	// cannot prune the log out from under it.
	private async Task WriteFooterThenRetireAsync(Review review, ReviewState state, string? reason)
	{
		try
		{
			await WriteLogAsync(
				review,
				"footer",
				path => _log.WriteFooterAsync(path, state, reason, _time.GetLocalNow(), CancellationToken.None))
				.ConfigureAwait(false);
		}
		finally
		{
			Retire(review);
		}
	}

	// The log is the user's record of the review; failing to write it never stops the review.
	private async Task WriteLogAsync(Review review, string step, Func<string, Task> write)
	{
		if (review.LogPath is not { } path)
		{
			return;
		}

		try
		{
			await write(path).ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_host.Trace($"review phase=log-failed review={review.Id} step={step} error={exception.GetType().Name}");
		}
	}

	// Closing the reviewer tab and opening the log are courtesies at the end; a failure is traced
	// and the end goes on, so the agent's accepted answer is never reported as an error.
	private async Task RunHostStepAsync(Review review, string step, Func<Task> action)
	{
		try
		{
			await action().ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OperationCanceledException)
		{
			_host.Trace($"review phase=host-failed review={review.Id} step={step} error={exception.GetType().Name}");
		}
	}

	private async Task WriteHeaderAsync(Review review)
	{
		var reviewerSessionId = review.ReviewerSessionId!;
		var reviewer = _host.TryGetProject(reviewerSessionId, out var reviewerProject)
			? (reviewerProject.SessionTitle, Kind: reviewerProject.Kind.ToString())
			: (SessionTitle: review.Profile.DisplayName, Kind: review.Profile.Kind.ToString());
		var startedAt = _time.GetLocalNow();
		try
		{
			// The list of logs in use and the new path are taken together, so a review starting
			// alongside never prunes a log another review has just claimed.
			lock (_gate)
			{
				review.LogPath = ReviewLogWriter.CreatePath(
					review.Project.RootPath,
					startedAt,
					review.Template.Id,
					review.Id,
					LogsInUse());
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_host.Trace($"review phase=log-failed review={review.Id} step=create error={exception.GetType().Name}");
			return;
		}

		await WriteLogAsync(review, "header", path => _log.WriteHeaderAsync(
			path,
			new ReviewLogHeader(
				review.Template.Name,
				review.Target,
				review.Project.SessionTitle,
				review.Project.Kind.ToString(),
				reviewer.SessionTitle,
				reviewer.Kind,
				review.MaxIterations,
				startedAt),
			CancellationToken.None)).ConfigureAwait(false);
	}

	private Task OpenLogAsync(Review review) =>
		_host.OpenProjectDocumentAsync(
			review.Project.ProjectId,
			Path.GetRelativePath(review.Project.RootPath, review.LogPath!).Replace(Path.DirectorySeparatorChar, '/'));

	private void StartTrigger(Review review, string sessionId, string label)
	{
		Trigger trigger = new(sessionId, label);
		lock (_gate)
		{
			if (review.Quiet || (review.Ended && label != "final"))
			{
				return;
			}

			review.Trigger = trigger;
		}

		_ = RunTriggerAsync(review, trigger);
	}

	private async Task RunTriggerAsync(Review review, Trigger trigger)
	{
		lock (_gate)
		{
			if (trigger.Running || trigger.Cancellation.IsCancellationRequested)
			{
				return;
			}

			trigger.Running = true;
			trigger.Idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}

		var nothingWritten = false;
		try
		{
			var result = await _host.DeliverTriggerAsync(trigger.SessionId, TriggerText, trigger.Cancellation.Token)
				.ConfigureAwait(false);
			nothingWritten = !result.WriteAttempted && !result.IsSent;
			_host.Trace(
				$"review phase=trigger review={review.Id} turn={trigger.Label} session={trigger.SessionId} "
				+ $"outcome={result.Outcome} written={result.WriteAttempted} retry={nothingWritten}");
		}
		catch (OperationCanceledException)
		{
			_host.Trace($"review phase=trigger review={review.Id} turn={trigger.Label} session={trigger.SessionId} outcome=cancelled");
		}
		catch (Exception exception)
		{
			_host.Trace($"review phase=trigger review={review.Id} turn={trigger.Label} session={trigger.SessionId} error={exception.GetType().Name}");
		}
		finally
		{
			bool retryNow;
			lock (_gate)
			{
				// The pending state and the end of the run change together, so a status change
				// seen while the delivery was finishing is never lost.
				var pending = nothingWritten && !trigger.Cancellation.IsCancellationRequested;
				retryNow = pending && trigger.RetryRequested && !review.Quiet;
				trigger.RetryRequested = false;
				trigger.WaitingForReady = pending && !retryNow;
				trigger.Running = false;
				trigger.Idle.TrySetResult();
			}

			if (retryNow)
			{
				_ = RunTriggerAsync(review, trigger);
			}
		}
	}

	// A trigger belongs to its turn: it is cancelled and finished before the turn moves on.
	private async Task DrainTriggerAsync(Review review)
	{
		Task idle;
		lock (_gate)
		{
			var trigger = review.Trigger;
			if (trigger is null)
			{
				return;
			}

			trigger.WaitingForReady = false;
			trigger.Cancellation.Cancel();
			idle = trigger.Running ? trigger.Idle!.Task : Task.CompletedTask;
		}

		await idle.ConfigureAwait(false);
	}

	private static bool TryBeginEnd(Review review, ReviewState state, string reason)
	{
		if (review.Ended)
		{
			return false;
		}

		review.Ended = true;
		review.EndState = state;
		review.EndReason = reason;
		review.StartCancellation.Cancel();
		review.Trigger?.Cancellation.Cancel();
		return true;
	}

	private void Retire(Review review)
	{
		lock (_gate)
		{
			_active.Remove(review);
			foreach (var sessionId in new[] { review.AuthorSessionId, review.ReviewerSessionId })
			{
				if (sessionId is not null && !review.ClosedSessionIds.Contains(sessionId))
				{
					_finished[sessionId] = review;
				}
			}
		}
	}

	private Review? FindActive(string sessionId) =>
		_active.FirstOrDefault(review => !review.Ended && IsParticipant(review, sessionId));

	private static bool IsParticipant(Review review, string sessionId) =>
		string.Equals(review.AuthorSessionId, sessionId, StringComparison.Ordinal)
		|| string.Equals(review.ReviewerSessionId, sessionId, StringComparison.Ordinal);

	// Called under _gate.
	private string[] LogsInUse() => [.. _active.Select(active => active.LogPath).OfType<string>()];

	// A review that ended no longer protects older logs, so the folder shrinks back to ten; its
	// own log stays, since it is the one being opened.
	private void PruneLogs(Review review)
	{
		if (review.LogPath is not { } finishedLog)
		{
			return;
		}

		try
		{
			lock (_gate)
			{
				ReviewLogWriter.Prune(review.Project.RootPath, [.. LogsInUse(), finishedLog]);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_host.Trace($"review phase=log-failed review={review.Id} step=prune error={exception.GetType().Name}");
		}
	}

	private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

	private static ReviewSummary Summarize(Review review) => new(
		review.Id,
		review.Project.ProjectId,
		review.AuthorSessionId,
		review.ReviewerSessionId ?? string.Empty,
		review.Conversation?.State ?? ReviewState.Running,
		review.Conversation?.CurrentTurn?.Iteration ?? 0,
		review.Conversation?.CurrentTurn?.Role,
		review.LogPath ?? string.Empty,
		review.MaxIterations);

	private static AgentControlResult AlreadyActive() =>
		AgentControlResult.Refused(
			"review-already-active",
			"This session already takes part in an active review; wait for it to end.");

	private static AgentControlResult NoReview() =>
		AgentControlResult.Refused("no-review", "This session takes part in no review.");

	private sealed record TurnView(
		string ReviewId,
		string State,
		int Iteration,
		bool YourTurn,
		string? Message,
		string? PreviousTurn,
		string? TurnId,
		string? EndReason,
		string? FinalText,
		string? FinalFrom);

	private sealed record SubmitView(string ReviewId, string State, bool Repeated);

	private sealed class Review(
		string id,
		ReviewProject project,
		string authorSessionId,
		ScenarioDefinition template,
		ReviewProfile profile,
		string target,
		int maxIterations)
	{
		public string Id { get; } = id;
		public ReviewProject Project { get; } = project;
		public string AuthorSessionId { get; } = authorSessionId;
		public string? ReviewerSessionId { get; set; }
		public ScenarioDefinition Template { get; } = template;
		public ReviewProfile Profile { get; } = profile;
		public string Target { get; } = target;
		public int MaxIterations { get; } = maxIterations;
		public ReviewMessageRenderer Renderer { get; } = new(
			template,
			template.ReviewerInstructions
				.FirstOrDefault(instruction => string.Equals(instruction.Id, template.DefaultReviewerInstructionId, StringComparison.Ordinal))
				?.Text
				?? template.ReviewerInstructions.FirstOrDefault()?.Text
				?? string.Empty,
			target);

		// Never disposed: a start may still observe it after the review has ended.
		public CancellationTokenSource StartCancellation { get; } = new();

		// Serializes turn hand-overs and the end of the review.
		public SemaphoreSlim Transition { get; } = new(1, 1);

		public ReviewConversation? Conversation { get; set; }
		public string? LogPath { get; set; }
		public Trigger? Trigger { get; set; }
		public bool Ended { get; set; }
		public ReviewState EndState { get; set; } = ReviewState.Running;
		public string EndReason { get; set; } = string.Empty;
		public bool Quiet { get; set; }
		public bool TellAuthor { get; set; } = true;
		public HashSet<string> ClosedSessionIds { get; } = new(StringComparer.Ordinal);
	}

	// Replaced triggers are left to the collector: a late status notice may still look at one.
	private sealed class Trigger(string sessionId, string label)
	{
		public string SessionId { get; } = sessionId;
		public string Label { get; } = label;
		public CancellationTokenSource Cancellation { get; } = new();
		public bool Running { get; set; }
		public bool WaitingForReady { get; set; }
		public bool RetryRequested { get; set; }
		public TaskCompletionSource? Idle { get; set; }
	}
}
