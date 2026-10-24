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
	string LogPath);

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

		var template = _templates().FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ScenarioId, StringComparison.Ordinal));
		if (template is null)
		{
			return AgentControlResult.Refused("invalid-argument", $"Unknown review template '{request.ScenarioId}'.");
		}

		var profile = _profiles().FirstOrDefault(candidate =>
			string.Equals(candidate.Id, request.ReviewProfileId, StringComparison.Ordinal));
		if (profile is null)
		{
			return AgentControlResult.Refused("invalid-argument", $"Unknown reviewer profile '{request.ReviewProfileId}'.");
		}

		if (string.IsNullOrWhiteSpace(request.Target) || request.MaxIterations is <= 0)
		{
			return AgentControlResult.Refused(
				"invalid-argument",
				"target must be non-blank and maxIterations greater than zero when supplied.");
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

		_host.Trace($"review phase=requested review={review.Id} author={authorSessionId} template={template.Id} profile={profile.Id}");
		using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, review.StartCancellation.Token);
		try
		{
			var reviewerSessionId = await _host.CreateReviewerAsync(project.ProjectId, profile, start.Token).ConfigureAwait(false);
			lock (_gate)
			{
				review.ReviewerSessionId = reviewerSessionId;
			}

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
				return AgentControlResult.Ok(JsonSerializer.Serialize(
					new TurnView(
						review.Id,
						review.Ended ? state.ToString() : "Starting",
						conversation?.CurrentTurn?.Iteration ?? 0,
						YourTurn: false,
						Message: null,
						PreviousTurn: null,
						TurnId: null,
						EndReason: review.Ended ? conversation?.EndReason ?? review.EndReason : null,
						FinalText: review.Ended ? conversation?.LastText(ReviewRole.Reviewer) : null),
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
					FinalText: null),
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
			await _log.AppendTurnAsync(review.LogPath!, answered.Role, answered.Iteration, _time.GetLocalNow(), text, CancellationToken.None)
				.ConfigureAwait(false);
			if (conversation.State == ReviewState.Running)
			{
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
				.FirstOrDefault(candidate => candidate.Trigger is { WaitingForReady: true } pending
					&& string.Equals(pending.SessionId, sessionId, StringComparison.Ordinal));
			trigger = review?.Trigger;
			if (trigger is null || review!.Shutdown || !_host.IsReadyForTrigger(sessionId))
			{
				return;
			}

			trigger.WaitingForReady = false;
		}

		_ = RunTriggerAsync(review, trigger);
	}

	/// <summary>Aborts the review a closed tab took part in.</summary>
	public Task NotifySessionClosedAsync(string sessionId) =>
		EndFromOutsideAsync(sessionId, ReviewState.Aborted, "tab closed");

	/// <summary>Fails the review a session whose process exited took part in.</summary>
	public Task NotifySessionDiedAsync(string sessionId) =>
		EndFromOutsideAsync(sessionId, ReviewState.Failed, "process exited");

	/// <summary>Aborts every review while Pact closes; nothing is delivered and no document opens.</summary>
	public async Task ShutdownAsync()
	{
		List<Review> running = [];
		lock (_gate)
		{
			foreach (var finished in _finished.Values)
			{
				finished.Shutdown = true;
				finished.Trigger?.Cancellation.Cancel();
			}

			foreach (var review in _active.ToArray())
			{
				review.Shutdown = true;
				if (TryBeginEnd(review, ReviewState.Aborted, "Pact is shutting down.") && review.Conversation is not null)
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

	/// <summary>Reviews that are starting or running.</summary>
	public IReadOnlyList<ReviewSummary> ListActive()
	{
		lock (_gate)
		{
			return [.. _active.Where(review => !review.Ended).Select(Summarize)];
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
		await DrainTriggerAsync(review).ConfigureAwait(false);
		var conversation = review.Conversation!;
		var state = conversation.State;
		var reason = conversation.EndReason ?? review.EndReason;
		await _log.WriteFooterAsync(review.LogPath!, state, reason, _time.GetLocalNow(), CancellationToken.None)
			.ConfigureAwait(false);
		_host.Trace($"review phase=ended review={review.Id} state={state}");
		Retire(review);
		if (review.Shutdown)
		{
			return;
		}

		if (state is ReviewState.Completed or ReviewState.MaxIterationsReached)
		{
			await _host.CloseSessionAsync(review.ReviewerSessionId!).ConfigureAwait(false);
		}

		await OpenLogAsync(review).ConfigureAwait(false);
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

		_host.Trace($"review phase=start-ended review={review.Id} state={review.EndState}");
		if (review.LogPath is not null)
		{
			await _log.WriteFooterAsync(review.LogPath, review.EndState, review.EndReason, _time.GetLocalNow(), CancellationToken.None)
				.ConfigureAwait(false);
		}

		Retire(review);
		if (!review.Shutdown && review.LogPath is not null)
		{
			await OpenLogAsync(review).ConfigureAwait(false);
		}

		return AgentControlResult.Refused(
			"review-start-failed",
			$"The review did not start: {review.EndReason} The reviewer tab, if it was created, stays open.");
	}

	private Task WriteHeaderAsync(Review review)
	{
		var reviewerSessionId = review.ReviewerSessionId!;
		var reviewer = _host.TryGetProject(reviewerSessionId, out var reviewerProject)
			? (reviewerProject.SessionTitle, Kind: reviewerProject.Kind.ToString())
			: (SessionTitle: review.Profile.DisplayName, Kind: review.Profile.Kind.ToString());
		var startedAt = _time.GetLocalNow();
		review.LogPath = ReviewLogWriter.CreatePath(review.Project.RootPath, startedAt, review.Template.Id, review.Id);
		return _log.WriteHeaderAsync(
			review.LogPath,
			new ReviewLogHeader(
				review.Template.Name,
				review.Target,
				review.Project.SessionTitle,
				review.Project.Kind.ToString(),
				reviewer.SessionTitle,
				reviewer.Kind,
				review.MaxIterations,
				startedAt),
			CancellationToken.None);
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
			if (review.Shutdown || (review.Ended && label != "final"))
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

		try
		{
			var result = await _host.DeliverTriggerAsync(trigger.SessionId, TriggerText, trigger.Cancellation.Token)
				.ConfigureAwait(false);
			var nothingWritten = !result.WriteAttempted && !result.IsSent;
			lock (_gate)
			{
				trigger.WaitingForReady = nothingWritten && !trigger.Cancellation.IsCancellationRequested;
			}

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
			lock (_gate)
			{
				trigger.Running = false;
				trigger.Idle.TrySetResult();
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
			_finished[review.AuthorSessionId] = review;
			if (review.ReviewerSessionId is { } reviewer)
			{
				_finished[reviewer] = review;
			}
		}
	}

	private Review? FindActive(string sessionId) =>
		_active.FirstOrDefault(review => !review.Ended
			&& (string.Equals(review.AuthorSessionId, sessionId, StringComparison.Ordinal)
				|| string.Equals(review.ReviewerSessionId, sessionId, StringComparison.Ordinal)));

	private static ReviewSummary Summarize(Review review) => new(
		review.Id,
		review.Project.ProjectId,
		review.AuthorSessionId,
		review.ReviewerSessionId ?? string.Empty,
		review.Conversation?.State ?? ReviewState.Running,
		review.Conversation?.CurrentTurn?.Iteration ?? 0,
		review.Conversation?.CurrentTurn?.Role,
		review.LogPath ?? string.Empty);

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
		string? FinalText);

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
		public bool Shutdown { get; set; }
		public bool TellAuthor { get; set; } = true;
	}

	// Replaced triggers are left to the collector: a late status notice may still look at one.
	private sealed class Trigger(string sessionId, string label)
	{
		public string SessionId { get; } = sessionId;
		public string Label { get; } = label;
		public CancellationTokenSource Cancellation { get; } = new();
		public bool Running { get; set; }
		public bool WaitingForReady { get; set; }
		public TaskCompletionSource? Idle { get; set; }
	}
}
