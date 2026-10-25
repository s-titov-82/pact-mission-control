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
			return new SessionReviewBadge(
				review.ReviewId,
				review.ProjectId,
				review.LogPath,
				isOwnTurn,
				$"Review · {(side == ReviewRole.Author ? "author" : "reviewer")} · {progress}. Click to open the log.");
		}

		return null;
	}
}
