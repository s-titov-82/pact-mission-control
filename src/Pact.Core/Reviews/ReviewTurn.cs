namespace Pact.Core.Reviews;

/// <summary>One turn a side of a review is expected to answer.</summary>
/// <param name="TurnId">Identifies the turn, as <c>&lt;reviewId&gt;-&lt;n&gt;</c>.</param>
/// <param name="Role">The side whose turn it is.</param>
/// <param name="Iteration">One-based iteration; a reviewer turn and the author's answer share one.</param>
public sealed record ReviewTurn(string TurnId, ReviewRole Role, int Iteration);

/// <summary>What happened to a submitted turn.</summary>
/// <param name="Accepted">Whether the answer was taken.</param>
/// <param name="Repeated">Whether it repeats a turn taken before, so nothing is relayed again.</param>
/// <param name="ErrorCode">Why the answer was refused, or <see langword="null"/>.</param>
/// <param name="ErrorMessage">Explanation for the agent, or <see langword="null"/>.</param>
/// <param name="State">The review state the answer led to, or the current one when refused.</param>
public sealed record ReviewSubmitOutcome(
	bool Accepted,
	bool Repeated,
	string? ErrorCode,
	string? ErrorMessage,
	ReviewState State);
