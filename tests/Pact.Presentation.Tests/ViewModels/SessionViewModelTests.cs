using Pact.Core.Agents;
using Pact.Core.Reviews;
using Pact.Core.Sessions;
using Pact.Presentation.Services.Reviews;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.ViewModels;

public sealed class SessionViewModelTests
{
	[Test]
	public void ApplyTerminalStatus_projects_indicator_and_engine_busy_timestamp()
	{
		SessionViewModel session = new(CreateSessionRecord());
		var startedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, startedAt, "Thinking");

		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		session.BusySince.ShouldBe(startedAt);
		session.StatusDescription.ShouldBe("Thinking");
		changedProperties.ShouldContain(nameof(SessionViewModel.Indicator));
		changedProperties.ShouldContain(nameof(SessionViewModel.BusySince));
		changedProperties.ShouldContain(nameof(SessionViewModel.StatusDescription));
	}

	[Test]
	public void ApplyTerminalStatus_notifies_description_when_indicator_is_unchanged()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, DateTimeOffset.UtcNow, "Thinking");
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.ApplyTerminalStatus(TerminalTabIndicator.Busy, null, "Working");

		session.StatusDescription.ShouldBe("Working");
		changedProperties.ShouldBe([nameof(SessionViewModel.StatusDescription)]);
	}

	[Test]
	public void Title_uses_session_title()
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Title = "Reviewer"
		});

		session.Title.ShouldBe("Reviewer");
	}

	[Test]
	public void TerminalKind_uses_lowercase_session_kind()
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Kind = AgentKind.Pwsh
		});

		session.TerminalKind.ShouldBe("pwsh");
	}

	[Test]
	public void Working_directory_line_is_hidden_when_it_matches_project_root()
	{
		SessionViewModel session = new(
			CreateSessionRecord() with { WorkingDirectory = @"D:\Work\Project" },
			@"D:\Work\Project\");

		session.ShowWorkingDirectory.ShouldBeFalse();
		session.WorkingDirectoryText.ShouldBe(string.Empty);
	}

	[Test]
	public void Working_directory_line_is_visible_when_it_differs_from_project_root()
	{
		SessionViewModel session = new(
			CreateSessionRecord() with { WorkingDirectory = @"D:\Work\Project\subdir" },
			@"D:\Work\Project");

		session.ShowWorkingDirectory.ShouldBeTrue();
		session.WorkingDirectoryText.ShouldBe(@"D:\Work\Project\subdir");
	}

	[Test]
	public void Current_terminal_selection_only_updates_presentation_state()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.ApplyTerminalStatus(TerminalTabIndicator.Unread, null, string.Empty);

		session.SetCurrentTerminal(true);

		session.IsCurrentTerminal.ShouldBeTrue();
		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);
	}

	[Test]
	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Codex, true)]
	[TestCase(AgentKind.Pwsh, false)]
	[TestCase(AgentKind.Hermes, false)]
	[TestCase(AgentKind.Custom, false)]
	public void CanResetAgentSession_true_only_for_resumable_agents(AgentKind kind, bool expected)
	{
		SessionViewModel session = new(CreateSessionRecord() with
		{
			Kind = kind
		});

		session.CanResetAgentSession.ShouldBe(expected);
	}

	[Test]
	public void A_review_badge_shows_the_session_s_side_and_whose_turn_it_is()
	{
		SessionViewModel session = new(CreateSessionRecord());
		List<string?> changedProperties = [];
		session.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

		session.ApplyReviews([Summary("author-1", "session-1", ReviewRole.Reviewer, iteration: 1)]);

		session.Review.ShouldNotBeNull();
		session.Review.IsOwnTurn.ShouldBeTrue();
		session.Review.ToolTip.ShouldBe("Review · reviewer · iteration 1 of 3 · your turn. Click to open the log.");
		changedProperties.ShouldContain(nameof(SessionViewModel.Review));
		changedProperties.ShouldContain(nameof(SessionViewModel.HasReview));
	}

	[Test]
	public void An_author_waiting_on_its_reviewer_gets_a_dim_badge()
	{
		SessionViewModel session = new(CreateSessionRecord());

		session.ApplyReviews([Summary("session-1", "reviewer-1", ReviewRole.Reviewer, iteration: 2)]);

		session.Review!.IsOwnTurn.ShouldBeFalse();
		session.Review.ToolTip.ShouldBe("Review · author · iteration 2 of 3 · reviewer's turn. Click to open the log.");
	}

	[Test]
	public void A_review_still_starting_says_so()
	{
		SessionViewModel session = new(CreateSessionRecord());

		session.ApplyReviews([Summary("session-1", string.Empty, turn: null, iteration: 0)]);

		session.Review!.IsOwnTurn.ShouldBeFalse();
		session.Review.ToolTip.ShouldBe("Review · author · starting the reviewer. Click to open the log.");
	}

	[Test]
	public void The_badge_goes_away_when_the_session_s_review_ends()
	{
		SessionViewModel session = new(CreateSessionRecord());
		session.ApplyReviews([Summary("session-1", "reviewer-1", ReviewRole.Author, iteration: 1)]);

		session.ApplyReviews([Summary("author-2", "reviewer-2", ReviewRole.Author, iteration: 1)]);

		session.Review.ShouldBeNull();
		session.HasReview.ShouldBeFalse();
	}

	private static ReviewSummary Summary(string author, string reviewer, ReviewRole? turn, int iteration) =>
		new("review-1", "project-1", author, reviewer, ReviewState.Running, iteration, turn, @"D:\Work\.pact\reviews\log.md", MaxIterations: 3);

	private static SessionRecord CreateSessionRecord()
	{
		var now = DateTimeOffset.UtcNow;
		return new SessionRecord(
			"session-1",
			AgentKind.Codex,
			"Codex session",
			@"D:\Work",
			"codex",
			null,
			SessionStatus.Running,
			now,
			now);
	}
}
