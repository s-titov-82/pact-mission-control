namespace Pact.Core.Reviews;

/// <summary>
/// The turn rules of one review: whose turn it is, which answers are taken, and when the review
/// ends. Safe to call from several threads.
/// </summary>
public sealed class ReviewConversation
{
	private readonly Lock _gate = new();
	private readonly string _authorSessionId;
	private readonly string _reviewerSessionId;
	private readonly int _maxIterations;
	private readonly Dictionary<string, (string SessionId, ReviewSubmitOutcome Outcome)> _acceptedTurns =
		new(StringComparer.Ordinal);
	private readonly Dictionary<ReviewRole, string> _lastTexts = [];
	private int _turnCount;

	/// <summary>Creates a review that has not started yet.</summary>
	public ReviewConversation(
		string reviewId,
		string authorSessionId,
		string reviewerSessionId,
		int maxIterations)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reviewId);
		ArgumentException.ThrowIfNullOrWhiteSpace(authorSessionId);
		ArgumentException.ThrowIfNullOrWhiteSpace(reviewerSessionId);
		ArgumentOutOfRangeException.ThrowIfLessThan(maxIterations, 1);
		ReviewId = reviewId;
		_authorSessionId = authorSessionId;
		_reviewerSessionId = reviewerSessionId;
		_maxIterations = maxIterations;
	}

	/// <summary>Identifies the review.</summary>
	public string ReviewId { get; }

	/// <summary>Where the review stands.</summary>
	public ReviewState State
	{
		get { lock (_gate) { return field; } }
		private set;
	} = ReviewState.Running;

	/// <summary>Why the review ended, or <see langword="null"/> while it runs.</summary>
	public string? EndReason
	{
		get { lock (_gate) { return field; } }
		private set;
	}

	/// <summary>The turn being waited for; <see langword="null"/> before the start and after the end.</summary>
	public ReviewTurn? CurrentTurn
	{
		get { lock (_gate) { return field; } }
		private set;
	}

	/// <summary>The last answer taken from <paramref name="from"/>, or <see langword="null"/>.</summary>
	public string? LastText(ReviewRole from)
	{
		lock (_gate)
		{
			return _lastTexts.GetValueOrDefault(from);
		}
	}

	/// <summary>The last answer taken from either side, or <see langword="null"/> before the first.</summary>
	public ReviewAnswer? LastAnswer
	{
		get { lock (_gate) { return field; } }
		private set;
	}

	/// <summary>Whether the session takes part in this review.</summary>
	public bool IsParticipant(string sessionId) =>
		string.Equals(sessionId, _authorSessionId, StringComparison.Ordinal)
		|| string.Equals(sessionId, _reviewerSessionId, StringComparison.Ordinal);

	/// <summary>The side a participant takes.</summary>
	/// <exception cref="ArgumentException">The session does not take part.</exception>
	public ReviewRole RoleOf(string sessionId) =>
		string.Equals(sessionId, _authorSessionId, StringComparison.Ordinal) ? ReviewRole.Author
		: string.Equals(sessionId, _reviewerSessionId, StringComparison.Ordinal) ? ReviewRole.Reviewer
		: throw new ArgumentException($"Session '{sessionId}' does not take part in review '{ReviewId}'.", nameof(sessionId));

	/// <summary>Gives the first turn to the reviewer.</summary>
	public ReviewTurn Start()
	{
		lock (_gate)
		{
			if (CurrentTurn is not null || _turnCount > 0 || State != ReviewState.Running)
			{
				throw new InvalidOperationException($"Review '{ReviewId}' has already started.");
			}

			return NextTurn(ReviewRole.Reviewer, iteration: 1);
		}
	}

	/// <summary>Takes or refuses a participant's answer to a turn.</summary>
	public ReviewSubmitOutcome Submit(string sessionId, string turnId, string text, bool approved)
	{
		lock (_gate)
		{
			if (!IsParticipant(sessionId))
			{
				return Refused("not-a-participant", $"This session does not take part in review '{ReviewId}'.");
			}

			// A retried call after a lost result is answered from what was stored, even after the end.
			if (_acceptedTurns.TryGetValue(turnId, out var accepted)
				&& string.Equals(accepted.SessionId, sessionId, StringComparison.Ordinal))
			{
				return accepted.Outcome with { Repeated = true };
			}

			if (State != ReviewState.Running)
			{
				return Refused("review-ended", $"Review '{ReviewId}' has ended: {State}.");
			}

			var role = RoleOf(sessionId);
			if (CurrentTurn is not { } turn
				|| turn.Role != role
				|| !string.Equals(turn.TurnId, turnId, StringComparison.Ordinal))
			{
				return Refused(
					"not-your-turn",
					$"Turn '{turnId}' is not this session's current turn; review '{ReviewId}' is {State}"
					+ (CurrentTurn is { } current ? $", waiting for the {current.Role.ToString().ToLowerInvariant()}." : "."));
			}

			if (approved && role == ReviewRole.Author)
			{
				return Refused("invalid-argument", "Only the reviewer can approve.");
			}

			if (string.IsNullOrWhiteSpace(text))
			{
				return Refused("invalid-argument", "text must be non-blank.");
			}

			_lastTexts[role] = text;
			LastAnswer = new ReviewAnswer(role, turn.Iteration, text);
			if (role == ReviewRole.Reviewer)
			{
				if (approved)
				{
					Finish(ReviewState.Completed, "The reviewer approved the work.");
				}
				else
				{
					NextTurn(ReviewRole.Author, turn.Iteration);
				}
			}
			else if (turn.Iteration >= _maxIterations)
			{
				Finish(ReviewState.MaxIterationsReached, $"The author answered the last of {_maxIterations} iterations.");
			}
			else
			{
				NextTurn(ReviewRole.Reviewer, turn.Iteration + 1);
			}

			ReviewSubmitOutcome outcome = new(Accepted: true, Repeated: false, null, null, State);
			_acceptedTurns[turnId] = (sessionId, outcome);
			return outcome;
		}
	}

	/// <summary>Ends the review from outside; does nothing once it has ended.</summary>
	public void End(ReviewState state, string reason)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(reason);
		if (state == ReviewState.Running)
		{
			throw new ArgumentOutOfRangeException(nameof(state), state, "A review cannot end as Running.");
		}

		lock (_gate)
		{
			if (State == ReviewState.Running)
			{
				Finish(state, reason);
			}
		}
	}

	private ReviewTurn NextTurn(ReviewRole role, int iteration)
	{
		_turnCount++;
		ReviewTurn turn = new($"{ReviewId}-{_turnCount}", role, iteration);
		CurrentTurn = turn;
		return turn;
	}

	private void Finish(ReviewState state, string reason)
	{
		State = state;
		EndReason = reason;
		CurrentTurn = null;
	}

	private ReviewSubmitOutcome Refused(string code, string message) =>
		new(Accepted: false, Repeated: false, code, message, State);
}
