using Pact.Core.Reviews;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.Tests.Services.Reviews;

public sealed class ReviewMessageRendererTests
{
	[Test]
	public void Rules_are_literal_and_only_appear_in_the_opening_reviewer_turn()
	{
		const string rules = "Check compatibility with {target}.";
		ReviewMessageRenderer renderer = new(rules, true, "docs/plan.md");

		var first = renderer.Render(ReviewRole.Reviewer, 1);
		first.ShouldContain("docs/plan.md");
		first.ShouldContain(rules);
		first.ShouldContain("pact_submit_review_turn");

		var author = renderer.Render(ReviewRole.Author, 1);
		author.ShouldNotContain(rules);
		author.ShouldContain("previousTurn");
		author.ShouldNotContain("approved");
		var next = renderer.Render(ReviewRole.Reviewer, 2);
		next.ShouldNotContain(rules);
		next.ShouldContain("previousTurn");
	}

	[TestCase(true, "every severity")]
	[TestCase(false, "remaining minor or nit findings")]
	public void Approval_policy_applies_to_every_reviewer_turn(bool strict, string policy)
	{
		ReviewMessageRenderer renderer = new("", strict, "target");
		foreach (var round in new[] { 1, 2 })
		{
			var message = renderer.Render(ReviewRole.Reviewer, round);
			message.ShouldContain(policy);
			message.ShouldContain("final run summary");
			message.ShouldContain("approved");
		}
	}
}
