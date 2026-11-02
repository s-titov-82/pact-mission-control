using System.Globalization;
using Pact.Core.Reviews;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.ViewModels;

/// <summary>The review a session takes part in, as its row shows it.</summary>
/// <param name="ReviewId">The review.</param>
/// <param name="ProjectId">The project the review's log belongs to.</param>
/// <param name="LogPath">The review's log; empty until it has one.</param>
/// <param name="IsOwnTurn">Whether the session is the one to act now.</param>
/// <param name="ToolTip">Which side the session is, the iteration and whose turn it is.</param>
public sealed record SessionReviewBadge(
	string ReviewId,
	string ProjectId,
	string LogPath,
	bool IsOwnTurn,
	string ToolTip)
{
	/// <summary>Why this review needs manual continuation; null when no warning is pending.</summary>
	public string? DeliveryIssue { get; init; }

	/// <summary>How to continue the pending turn manually, without assuming which UI element shows the hint.</summary>
	public string? RecoveryInstruction { get; init; }

	/// <summary>Whether both participants should display a delivery warning.</summary>
	public bool HasDeliveryIssue => DeliveryIssue is not null;

	/// <summary>The normal review symbol, replaced by an exclamation mark when delivery needs attention.</summary>
	public string Glyph => HasDeliveryIssue ? "!" : "⇄";

	/// <summary>The badge for <paramref name="sessionId"/>, or <see langword="null"/> when it takes part in none.</summary>
	public static SessionReviewBadge? For(IEnumerable<ReviewSummary> reviews, string sessionId)
	{
		ArgumentNullException.ThrowIfNull(reviews);
		foreach (var review in reviews)
		{
			ReviewRole side;
			if (string.Equals(review.AuthorSessionId, sessionId, StringComparison.Ordinal))
			{
				side = ReviewRole.Author;
			}
			else if (string.Equals(review.ReviewerSessionId, sessionId, StringComparison.Ordinal))
			{
				side = ReviewRole.Reviewer;
			}
			else
			{
				continue;
			}

			var isOwnTurn = review.Turn == side;
			var progress = review.Turn is { } turn
				? string.Create(
					CultureInfo.InvariantCulture,
					$"iteration {review.Iteration} of {review.MaxIterations} · {(isOwnTurn ? "your turn" : turn == ReviewRole.Author ? "author's turn" : "reviewer's turn")}")
				: "starting the reviewer";
			var recovery = review.DeliveryIssue is not null
				? $"Open the {(review.Turn == ReviewRole.Author ? "author" : "reviewer")}'s session "
					+ "and ask the agent to continue the review (pact_get_review_turn)."
				: null;
			var attention = review.DeliveryIssue is { } issue
				? $" {issue} {recovery} A possibly written notification is not pasted again automatically."
				: string.Empty;
			return new SessionReviewBadge(
				review.ReviewId,
				review.ProjectId,
				review.LogPath,
				isOwnTurn,
				$"Review · {(side == ReviewRole.Author ? "author" : "reviewer")} · {progress}. Click to open the log.{attention}")
			{
				DeliveryIssue = review.DeliveryIssue,
				RecoveryInstruction = recovery
			};
		}

		return null;
	}
}
