using System.Text.Json.Nodes;
using Pact.Core.AgentControl;
using Pact.Core.Agents;
using Pact.Core.Reviews;
using Pact.Core.Sessions;
using Pact.Infrastructure.Reviews;
using Pact.Presentation.Services.Reviews;

namespace Pact.Presentation.Tests.Services.Reviews;

public sealed class ReviewRelayTests : IDisposable
{
	private const string Author = "author-1";
	private const string Reviewer = "reviewer-1";
	private readonly TemporaryDirectory _root = TemporaryDirectory.Create();
	private readonly ReviewLogWriter _log = new();

	[Test]
	public async Task Both_sides_receive_the_full_review_exchange_instruction_before_reading_their_turns()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(maxIterations: 2), CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "finding", false, CancellationToken.None);
		await host.WaitForDeliveriesAsync(2);
		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed", false, CancellationToken.None);
		await host.WaitForDeliveriesAsync(3);

		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author, Reviewer]);
		foreach (var delivery in host.Deliveries)
		{
			delivery.Text.ShouldContain("pact_get_review_turn");
			delivery.Text.ShouldContain("pact_submit_review_turn");
			delivery.Text.ShouldContain("all iterations");
			delivery.Text.ShouldContain("code and documentation");
			delivery.Text.ShouldContain("other participant in this Pact review");
			delivery.Text.ShouldContain("target");
			delivery.Text.ShouldContain("without asking for separate confirmation for each reply");
		}
	}

	[Test]
	public async Task Request_starts_a_visible_reviewer_and_triggers_it()
	{
		var (relay, host) = Create();

		var result = await relay.RequestAsync(Author, Request(), CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.CreatedReviewers.ShouldBe([("project-1", "codex-high")]);
		host.StartedReviewers.ShouldBe([Reviewer]);
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer]);
		host.Deliveries[0].Text.ShouldBe(ReviewRelay.TriggerText);
		relay.Find(result.Payload!)!.Turn.ShouldBe(ReviewRole.Reviewer);
	}

	[Test]
	public async Task Changed_is_raised_as_the_review_starts_moves_and_ends()
	{
		var (relay, _) = Create();
		List<string> seen = [];
		relay.Changed += (_, _) =>
		{
			var active = relay.ListActive();
			seen.Add(active.Count == 0 ? "none" : $"{active[0].Turn?.ToString() ?? "starting"} {active[0].Iteration}/{active[0].MaxIterations}");
		};

		await relay.RequestAsync(Author, Request(maxIterations: 2), CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "1. finding", false, CancellationToken.None);
		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed", false, CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "All good.", true, CancellationToken.None);

		seen.Distinct().ShouldBe(["starting 0/2", "Reviewer 1/2", "Author 1/2", "Reviewer 2/2", "none"]);
	}

	[Test]
	public async Task A_running_review_s_log_opens_on_request()
	{
		var (relay, host) = Create();
		var reviewId = (await relay.RequestAsync(Author, Request(), CancellationToken.None)).Payload!;

		(await relay.OpenLogAsync(reviewId)).ShouldBeTrue();
		(await relay.OpenLogAsync("no-such-review")).ShouldBeFalse();

		host.OpenedDocuments.ShouldHaveSingleItem().ProjectId.ShouldBe("project-1");
		host.OpenedDocuments[0].RelativePath.ShouldStartWith(".pact/reviews/");
	}

	[Test]
	public async Task An_unknown_profile_or_template_is_refused_with_the_ones_that_exist()
	{
		var (relay, _) = Create();

		var profile = await relay.RequestAsync(Author, new RequestReviewRequest("code-review", "renamed", "main..HEAD", null), CancellationToken.None);
		var template = await relay.RequestAsync(Author, new RequestReviewRequest("gone", "codex-high", "main..HEAD", null), CancellationToken.None);

		profile.Failure!.Message.ShouldBe("Unknown reviewer profile 'renamed'. Available: codex-high.");
		template.Failure!.Message.ShouldBe("Unknown review template 'gone'. Available: code-review.");
	}

	[Test]
	public async Task A_review_ending_while_Pact_closes_neither_closes_tabs_nor_opens_the_log()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		relay.BeginShutdown();
		var approved = await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);
		var another = await relay.RequestAsync(Author, Request(), CancellationToken.None);

		approved.Succeeded.ShouldBeTrue();
		host.ClosedSessions.ShouldBeEmpty();
		host.OpenedDocuments.ShouldBeEmpty();
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer]);
		another.Failure!.Code.ShouldBe("pact-shutting-down");
	}

	[Test]
	public async Task Closing_the_reviewer_at_the_end_tells_the_relay_without_spoiling_the_end()
	{
		var (relay, host) = Create();
		host.ClosesThrough = relay;
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(10));

		host.ClosedSessions.ShouldBe([Reviewer]);
		host.OpenedDocuments.ShouldHaveSingleItem();
		await host.WaitForDeliveriesAsync(2);
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
		((string?)Turn(relay, Author)["state"]).ShouldBe("Completed");
		relay.GetTurn(Reviewer).Failure!.Code.ShouldBe("no-review");
	}

	[Test]
	public async Task Request_from_root_is_refused()
	{
		var (relay, host) = Create();

		var result = await relay.RequestAsync("root-session", Request(), CancellationToken.None);

		result.Failure!.Code.ShouldBe("owner-not-a-project");
		host.CreatedReviewers.ShouldBeEmpty();
	}

	[Test]
	public async Task Request_from_a_participant_is_refused()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		var result = await relay.RequestAsync(Reviewer, Request(), CancellationToken.None);

		result.Failure!.Code.ShouldBe("review-already-active");
		host.CreatedReviewers.Count.ShouldBe(1);
	}

	[TestCase("missing-template", "codex-high")]
	[TestCase("code-review", "missing-profile")]
	public async Task Unknown_template_or_profile_is_refused(string templateId, string profileId)
	{
		var (relay, _) = Create();

		var result = await relay.RequestAsync(
			Author,
			new RequestReviewRequest(templateId, profileId, "main..HEAD", null),
			CancellationToken.None);

		result.Failure!.Code.ShouldBe("invalid-argument");
	}

	[Test]
	public async Task Second_request_while_the_reviewer_starts_is_refused()
	{
		var (relay, host) = Create();
		var start = host.HoldStart();
		var first = relay.RequestAsync(Author, Request(), CancellationToken.None);

		var second = await relay.RequestAsync(Author, Request(), CancellationToken.None);

		second.Failure!.Code.ShouldBe("review-already-active");
		host.CreatedReviewers.Count.ShouldBe(1);
		start.SetResult(new ReviewerStart(Ready: true, string.Empty));
		(await first).Succeeded.ShouldBeTrue();
	}

	[Test]
	public async Task Closing_the_author_while_the_reviewer_starts_aborts()
	{
		var (relay, host) = Create();
		host.HoldStart();
		var request = relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.NotifySessionClosedAsync(Author);

		var result = await request;
		result.Failure!.Code.ShouldBe("review-start-failed");
		result.Failure.Message.ShouldContain("author tab closed");
		host.StartTokens.Single().IsCancellationRequested.ShouldBeTrue();
		host.Deliveries.ShouldBeEmpty();
		relay.GetTurn(Author).Failure!.Code.ShouldBe("no-review");
	}

	[Test]
	public async Task Closing_the_reviewer_while_it_starts_aborts()
	{
		var (relay, host) = Create();
		host.HoldStart();
		var request = relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.NotifySessionClosedAsync(Reviewer);

		var result = await request;
		result.Failure!.Code.ShouldBe("review-start-failed");
		result.Failure.Message.ShouldContain("reviewer tab closed");
		host.Deliveries.ShouldBeEmpty();
		relay.GetTurn(Reviewer).Failure!.Code.ShouldBe("no-review");
	}

	[Test]
	public async Task Shutdown_while_the_reviewer_starts_aborts()
	{
		var (relay, host) = Create();
		host.HoldStart();
		var request = relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.ShutdownAsync();

		(await request).Failure!.Code.ShouldBe("review-start-failed");
		host.Deliveries.ShouldBeEmpty();
		host.OpenedDocuments.ShouldBeEmpty();
	}

	[Test]
	public async Task Reviewer_without_mcp_is_refused()
	{
		var (relay, host) = Create();
		host.AgentControlKinds.Clear();

		var result = await relay.RequestAsync(Author, Request(), CancellationToken.None);

		result.Failure!.Code.ShouldBe("reviewer-has-no-mcp");
		host.CreatedReviewers.ShouldBeEmpty();
	}

	[Test]
	public async Task Reviewer_that_never_gets_ready_fails_and_keeps_its_tab()
	{
		var (relay, host) = Create();
		host.StartResult = new ReviewerStart(Ready: false, "Trust this folder?");

		var result = await relay.RequestAsync(Author, Request(), CancellationToken.None);

		result.Failure!.Code.ShouldBe("review-start-failed");
		result.Failure.Message.ShouldContain("Trust this folder?");
		host.ClosedSessions.ShouldBeEmpty();
		host.Deliveries.ShouldBeEmpty();
		(await ReadLogAsync(host)).ShouldContain("Ended: Failed");
	}

	[Test]
	public async Task Turn_returns_previous_text_and_turn_id()
	{
		var (relay, _) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var reviewerTurn = Turn(relay, Reviewer);
		await relay.SubmitAsync(Reviewer, (string)reviewerTurn["turnId"]!, "1. finding", approved: false, CancellationToken.None);

		var authorTurn = Turn(relay, Author);

		((bool)authorTurn["yourTurn"]!).ShouldBeTrue();
		((string?)authorTurn["previousTurn"]).ShouldBe("1. finding");
		((string?)authorTurn["turnId"]).ShouldNotBeNullOrEmpty();
		((string?)authorTurn["message"]).ShouldNotBeNull().ShouldContain("pact_submit_review_turn");
		var waiting = Turn(relay, Reviewer);
		((bool)waiting["yourTurn"]!).ShouldBeFalse();
		waiting["turnId"].ShouldBeNull();
	}

	[Test]
	public async Task Accepting_a_turn_cancels_and_drains_its_trigger()
	{
		var (relay, host) = Create();
		var trigger = host.HoldNextDelivery();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		var submit = relay.SubmitAsync(Reviewer, turnId, "1. finding", approved: false, CancellationToken.None);

		host.Deliveries[0].Token.IsCancellationRequested.ShouldBeTrue();
		submit.IsCompleted.ShouldBeFalse();
		host.Deliveries.Count.ShouldBe(1);
		trigger.SetCanceled();
		(await submit).Succeeded.ShouldBeTrue();
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
	}

	[Test]
	public async Task Nothing_written_trigger_is_retried_when_ready()
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new PromptDeliveryResult(PromptDeliveryOutcome.RefusedQuestion, "Allow?", WriteAttempted: false));
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		relay.NotifyStatusChanged(Reviewer);
		host.Deliveries.Count.ShouldBe(1);
		host.ReadySessions.Add(Reviewer);
		relay.NotifyStatusChanged(Reviewer);
		await host.WaitForDeliveriesAsync(2);

		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Reviewer]);
	}

	[TestCase(PromptDeliveryOutcome.RefusedQuestion)]
	[TestCase(PromptDeliveryOutcome.Unconfirmed)]
	[TestCase(PromptDeliveryOutcome.PasteNotShown)]
	public async Task Written_trigger_is_never_retyped(PromptDeliveryOutcome outcome)
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new PromptDeliveryResult(outcome, string.Empty, WriteAttempted: true));
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		host.ReadySessions.Add(Reviewer);

		relay.NotifyStatusChanged(Reviewer);

		host.Deliveries.Count.ShouldBe(1);
		host.Traces.ShouldContain(line => line.Contains(outcome.ToString(), StringComparison.Ordinal));
	}

	[TestCase(PromptDeliveryOutcome.PasteNotShown)]
	[TestCase(PromptDeliveryOutcome.Unconfirmed)]
	[TestCase(PromptDeliveryOutcome.RefusedQuestion)]
	[TestCase(PromptDeliveryOutcome.NotRunning)]
	public async Task A_failed_notification_preserves_the_turn_and_publishes_attention(PromptDeliveryOutcome outcome)
	{
		var (relay, host) = Create();
		List<string?> seen = [];
		relay.Changed += (_, _) => seen.Add(relay.ListActive().SingleOrDefault()?.DeliveryIssue);
		host.NextResults.Enqueue(new(outcome, "reason", WriteAttempted: outcome != PromptDeliveryOutcome.NotRunning,
			SubmitAttempted: outcome == PromptDeliveryOutcome.Unconfirmed));

		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		var review = relay.ListActive().Single();
		review.State.ShouldBe(ReviewState.Running);
		review.Turn.ShouldBe(ReviewRole.Reviewer);
		review.Iteration.ShouldBe(1);
		var issue = review.DeliveryIssue.ShouldNotBeNull();
		issue.ShouldContain("reviewer", Case.Insensitive);
		seen.ShouldContain(issue);
		host.Deliveries.Count.ShouldBe(1);
	}

	[Test]
	public async Task Only_the_owning_agent_fetching_its_turn_clears_attention_and_can_continue()
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new(PromptDeliveryOutcome.PasteNotShown, WriteAttempted: true));
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		relay.ListActive().Single().DeliveryIssue.ShouldNotBeNull();

		Turn(relay, Author)["yourTurn"]!.GetValue<bool>().ShouldBeFalse();
		relay.ListActive().Single().DeliveryIssue.ShouldNotBeNull();
		var changes = 0;
		relay.Changed += (_, _) => changes++;

		var turn = Turn(relay, Reviewer);

		relay.ListActive().Single().DeliveryIssue.ShouldBeNull();
		changes.ShouldBe(1);
		(await relay.SubmitAsync(Reviewer, (string)turn["turnId"]!, "1. finding", false, CancellationToken.None))
			.Succeeded.ShouldBeTrue();
		relay.ListActive().Single().Turn.ShouldBe(ReviewRole.Author);
		relay.ListActive().Single().DeliveryIssue.ShouldBeNull();
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
	}

	[Test]
	public async Task A_late_failed_notification_does_not_recreate_attention_after_the_turn_was_fetched()
	{
		var (relay, host) = Create();
		var delivery = host.HoldNextDelivery();
		TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.OnTrace = line =>
		{
			if (line.Contains("phase=trigger-settled", StringComparison.Ordinal))
			{
				settled.TrySetResult();
			}
		};
		List<string?> seen = [];
		relay.Changed += (_, _) => seen.Add(relay.ListActive().SingleOrDefault()?.DeliveryIssue);
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		Turn(relay, Reviewer)["yourTurn"]!.GetValue<bool>().ShouldBeTrue();

		delivery.SetResult(new(PromptDeliveryOutcome.PasteNotShown, WriteAttempted: true));
		await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

		relay.ListActive().Single().DeliveryIssue.ShouldBeNull();
		seen.ShouldAllBe(issue => issue == null);
		host.ReadySessions.Add(Reviewer);
		relay.NotifyStatusChanged(Reviewer);
		host.Deliveries.Count.ShouldBe(1);
	}

	[TestCase(false)]
	[TestCase(true)]
	public async Task An_unexpected_delivery_exception_or_cancellation_is_visible(bool cancel)
	{
		var (relay, host) = Create();
		var delivery = host.HoldNextDelivery();
		TaskCompletionSource settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.OnTrace = line =>
		{
			if (line.Contains("phase=trigger-settled", StringComparison.Ordinal))
			{
				settled.TrySetResult();
			}
		};
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		if (cancel)
		{
			delivery.SetCanceled();
		}
		else
		{
			delivery.SetException(new IOException("private diagnostic detail"));
		}
		await settled.Task.WaitAsync(TimeSpan.FromSeconds(5));

		var issue = relay.ListActive().Single().DeliveryIssue.ShouldNotBeNull();
		issue.ShouldNotContain("private diagnostic detail");
		host.ReadySessions.Add(Reviewer);
		relay.NotifyStatusChanged(Reviewer);
		host.Deliveries.Count.ShouldBe(1);
	}

	[Test]
	public async Task Successful_no_write_retry_clears_attention()
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new(PromptDeliveryOutcome.RefusedQuestion, "Allow?"));
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		relay.ListActive().Single().DeliveryIssue.ShouldNotBeNull();

		host.ReadySessions.Add(Reviewer);
		relay.NotifyStatusChanged(Reviewer);

		host.Deliveries.Count.ShouldBe(2);
		relay.ListActive().Single().DeliveryIssue.ShouldBeNull();
	}

	[Test]
	public async Task Fetching_the_turn_stops_a_pending_no_write_retry()
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new(PromptDeliveryOutcome.RefusedQuestion, "Allow?"));
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		Turn(relay, Reviewer);

		host.ReadySessions.Add(Reviewer);
		relay.NotifyStatusChanged(Reviewer);

		host.Deliveries.Count.ShouldBe(1);
		relay.ListActive().Single().DeliveryIssue.ShouldBeNull();
	}

	[Test]
	public async Task Approval_closes_the_reviewer_opens_the_log_and_tells_the_author()
	{
		var (relay, host) = Create();
		var reviewId = (await relay.RequestAsync(Author, Request(), CancellationToken.None)).Payload!;
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		var result = await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		host.ClosedSessions.ShouldBe([Reviewer]);
		host.OpenedDocuments.Single().ProjectId.ShouldBe("project-1");
		host.OpenedDocuments.Single().RelativePath.ShouldStartWith(".pact/reviews/");
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
		relay.Find(reviewId).ShouldBeNull();
		var ended = Turn(relay, Author);
		((string?)ended["state"]).ShouldBe("Completed");
		((string?)ended["finalText"]).ShouldBe("All good.");
		ended["turnId"].ShouldBeNull();
	}

	[Test]
	public async Task A_review_ended_on_the_author_s_answer_reports_that_answer_and_its_iteration()
	{
		var (relay, _) = Create();
		await relay.RequestAsync(Author, Request(maxIterations: 2), CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "1. finding", false, CancellationToken.None);
		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed", false, CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "2. finding", false, CancellationToken.None);

		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed again", false, CancellationToken.None);

		var ended = Turn(relay, Reviewer);
		((string?)ended["state"]).ShouldBe("MaxIterationsReached");
		((int)ended["iteration"]!).ShouldBe(2);
		((string?)ended["finalText"]).ShouldBe("fixed again");
		((string?)ended["finalFrom"]).ShouldBe("author");
	}

	[Test]
	public async Task Max_iterations_close_the_reviewer_too()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(maxIterations: 1), CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "1. finding", false, CancellationToken.None);

		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed", false, CancellationToken.None);

		host.ClosedSessions.ShouldBe([Reviewer]);
		((string?)Turn(relay, Author)["state"]).ShouldBe("MaxIterationsReached");
	}

	[Test]
	public async Task Closing_the_reviewer_aborts_and_tells_the_author()
	{
		var (relay, host) = Create();
		var trigger = host.HoldNextDelivery();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		var closing = relay.NotifySessionClosedAsync(Reviewer);
		host.Deliveries[0].Token.IsCancellationRequested.ShouldBeTrue();
		trigger.SetCanceled();
		await closing;

		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
		host.ClosedSessions.ShouldBeEmpty();
		((string?)Turn(relay, Author)["state"]).ShouldBe("Aborted");
		(await ReadLogAsync(host)).ShouldContain("reviewer tab closed");
	}

	[Test]
	public async Task Closing_the_author_does_not_try_to_tell_it()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.NotifySessionClosedAsync(Author);

		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer]);
	}

	[Test]
	public async Task A_dead_reviewer_fails_the_review()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.NotifySessionDiedAsync(Reviewer);

		((string?)Turn(relay, Author)["state"]).ShouldBe("Failed");
		(await ReadLogAsync(host)).ShouldContain("reviewer process exited");
	}

	[Test]
	public async Task Shutdown_aborts_without_deliveries_or_documents()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.ShutdownAsync();

		host.Deliveries.Count.ShouldBe(1);
		host.OpenedDocuments.ShouldBeEmpty();
		host.ClosedSessions.ShouldBeEmpty();
		(await ReadLogAsync(host)).ShouldContain("Ended: Aborted");
	}

	[Test]
	public async Task Log_records_every_accepted_turn()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var reviewerTurn = (string)Turn(relay, Reviewer)["turnId"]!;
		await relay.SubmitAsync(Reviewer, reviewerTurn, "1. finding", false, CancellationToken.None);
		await relay.SubmitAsync(Reviewer, reviewerTurn, "1. finding", false, CancellationToken.None);
		await relay.SubmitAsync(Author, (string)Turn(relay, Author)["turnId"]!, "fixed it", false, CancellationToken.None);

		var log = await ReadLogAsync(host);

		log.Split("1. finding").Length.ShouldBe(2);
		log.IndexOf("fixed it", StringComparison.Ordinal).ShouldBeGreaterThan(log.IndexOf("1. finding", StringComparison.Ordinal));
	}

	[Test]
	public async Task A_log_that_cannot_be_written_does_not_strand_the_review()
	{
		var (relay, host) = Create();
		var reviewId = (await relay.RequestAsync(Author, Request(), CancellationToken.None)).Payload!;
		var logPath = relay.Find(reviewId)!.LogPath;
		File.Delete(logPath);
		Directory.CreateDirectory(logPath);
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		var result = await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);

		result.Succeeded.ShouldBeTrue();
		relay.Find(reviewId).ShouldBeNull();
		host.ClosedSessions.ShouldBe([Reviewer]);
		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Author]);
		host.Traces.ShouldContain(line => line.Contains("log-failed", StringComparison.Ordinal));
	}

	[Test]
	public async Task A_log_stays_claimed_until_its_footer_is_written()
	{
		var (relay, host) = Create();
		var reviewId = (await relay.RequestAsync(Author, Request(), CancellationToken.None)).Payload!;
		var logPath = relay.Find(reviewId)!.LogPath;
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;
		File.SetAttributes(logPath, FileAttributes.ReadOnly);
		host.OnTrace = line =>
		{
			if (!line.Contains("step=footer", StringComparison.Ordinal))
			{
				return;
			}

			host.OnTrace = null;
			File.SetAttributes(logPath, FileAttributes.Normal);
			for (var index = 0; index < 9; index++)
			{
				var newer = Path.Combine(Path.GetDirectoryName(logPath)!, $"newer-{index}.md");
				File.WriteAllText(newer, "log");
				File.SetCreationTimeUtc(newer, DateTime.UtcNow.AddMinutes(10));
			}

			// Another review starts while this one is still writing its footer.
			relay.RequestAsync(Author, Request(), CancellationToken.None).GetAwaiter().GetResult()
				.Succeeded.ShouldBeTrue();
		};

		await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);

		host.OnTrace.ShouldBeNull();
		File.Exists(logPath).ShouldBeTrue();
	}

	[Test]
	public async Task A_log_counts_as_in_use_until_its_footer_is_written()
	{
		var (relay, host) = Create();
		var reviewId = (await relay.RequestAsync(Author, Request(), CancellationToken.None)).Payload!;
		var logPath = relay.Find(reviewId)!.LogPath;
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;
		relay.IsLogInUse(logPath.ToUpperInvariant()).ShouldBeTrue();
		File.SetAttributes(logPath, FileAttributes.ReadOnly);
		bool? inUseWhileEnding = null;
		bool? listedWhileEnding = null;
		host.OnTrace = line =>
		{
			if (!line.Contains("step=footer", StringComparison.Ordinal))
			{
				return;
			}

			host.OnTrace = null;
			File.SetAttributes(logPath, FileAttributes.Normal);
			inUseWhileEnding = relay.IsLogInUse(logPath);
			listedWhileEnding = relay.ListActive().Count > 0;
		};

		await relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);

		listedWhileEnding.ShouldBe(false);
		inUseWhileEnding.ShouldBe(true);
		relay.IsLogInUse(logPath).ShouldBeFalse();
	}

	[Test]
	public async Task Ending_a_project_s_reviews_is_quiet()
	{
		var (relay, host) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);

		await relay.EndProjectReviewsAsync("project-1");

		((string?)Turn(relay, Author)["state"]).ShouldBe("Aborted");
		host.Deliveries.Count.ShouldBe(1);
		host.OpenedDocuments.ShouldBeEmpty();
		host.ClosedSessions.ShouldBeEmpty();
		(await ReadLogAsync(host)).ShouldContain("Ended: Aborted");
	}

	[Test]
	public async Task A_status_change_while_the_trigger_is_still_finishing_is_not_lost()
	{
		var (relay, host) = Create();
		host.NextResults.Enqueue(new PromptDeliveryResult(PromptDeliveryOutcome.RefusedQuestion, "Allow?", WriteAttempted: false));
		host.OnTrace = line =>
		{
			if (line.Contains("retry=True", StringComparison.Ordinal))
			{
				host.OnTrace = null;
				host.ReadySessions.Add(Reviewer);
				relay.NotifyStatusChanged(Reviewer);
			}
		};

		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		await host.WaitForDeliveriesAsync(2);

		host.Deliveries.Select(delivery => delivery.SessionId).ShouldBe([Reviewer, Reviewer]);
	}

	[Test]
	public async Task A_closed_tab_forgets_its_finished_review()
	{
		var (relay, _) = Create();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		await relay.SubmitAsync(Reviewer, (string)Turn(relay, Reviewer)["turnId"]!, "All good.", approved: true, CancellationToken.None);

		await relay.NotifySessionClosedAsync(Reviewer);
		await relay.NotifySessionClosedAsync(Author);

		relay.GetTurn(Reviewer).Failure!.Code.ShouldBe("no-review");
		relay.GetTurn(Author).Failure!.Code.ShouldBe("no-review");
	}

	[Test]
	public async Task A_tab_closed_while_its_review_is_ending_is_not_brought_back()
	{
		var (relay, host) = Create();
		var trigger = host.HoldNextDelivery();
		await relay.RequestAsync(Author, Request(), CancellationToken.None);
		var turnId = (string)Turn(relay, Reviewer)["turnId"]!;

		// The approval waits for the reviewer's trigger to drain. Meanwhile the author's tab
		// closes, which ends the review but must wait to finish it, and then the reviewer's tab
		// closes too: the review has ended and is not yet retired.
		var approval = relay.SubmitAsync(Reviewer, turnId, "All good.", approved: true, CancellationToken.None);
		var authorClosing = relay.NotifySessionClosedAsync(Author);
		var reviewerClosing = relay.NotifySessionClosedAsync(Reviewer);
		trigger.SetCanceled();
		await approval;
		await authorClosing;
		await reviewerClosing;

		relay.GetTurn(Reviewer).Failure!.Code.ShouldBe("no-review");
	}

	[Test]
	public async Task Submitting_without_a_review_is_refused()
	{
		var (relay, _) = Create();

		var result = await relay.SubmitAsync(Author, "x-1", "text", false, CancellationToken.None);

		result.Failure!.Code.ShouldBe("no-review");
	}

	public void Dispose()
	{
		_log.Dispose();
		_root.Dispose();
	}

	private (ReviewRelay Relay, FakeReviewRelayHost Host) Create()
	{
		FakeReviewRelayHost host = new(_root.Path);
		ReviewRelay relay = new(
			host,
			() => [Definition()],
			() => [new ReviewProfile("codex-high", "Codex high", AgentKind.Codex, "codex")],
			_log,
			TimeProvider.System);
		return (relay, host);
	}

	private static RequestReviewRequest Request(int? maxIterations = null) =>
		new("code-review", "codex-high", "main..HEAD", maxIterations);

	private static JsonNode Turn(ReviewRelay relay, string sessionId)
	{
		var result = relay.GetTurn(sessionId);
		result.Succeeded.ShouldBeTrue(result.Failure?.Message);
		return JsonNode.Parse(result.Payload!)!;
	}

	private async Task<string> ReadLogAsync(FakeReviewRelayHost host)
	{
		var logs = Directory.GetFiles(Path.Combine(_root.Path, ".pact", "reviews"), "*.md");
		return await File.ReadAllTextAsync(logs.Single());
	}

	private static ScenarioDefinition Definition() => new(
		"code-review",
		ScenarioKind.ReviewLoop,
		"Code review loop",
		MaxIterations: 3,
		StopMarker: "AGENT_TERMINAL_DONE",
		StartPromptTemplate: "Review {target}.",
		FirstFeedbackTemplate: "Findings: {reviewerOutput}",
		AuthorReturnTemplate: "Answer: {authorOutput}",
		FeedbackTemplate: "More: {reviewerOutput}",
		ReviewerInstructions: [new("strict", "Strict", "Approve only when all is resolved.")],
		DefaultReviewerInstructionId: "strict");

	internal sealed class FakeReviewRelayHost(string root) : IReviewRelayHost
	{
		private readonly Lock _gate = new();
		private TaskCompletionSource<ReviewerStart>? _heldStart;
		private TaskCompletionSource<PromptDeliveryResult>? _heldDelivery;
		private TaskCompletionSource? _deliveryWaiter;
		private int _deliveryTarget;

		public HashSet<AgentKind> AgentControlKinds { get; } = [AgentKind.Codex, AgentKind.Claude];
		public HashSet<string> ReadySessions { get; } = [];
		public Queue<PromptDeliveryResult> NextResults { get; } = new();
		public ReviewerStart StartResult { get; set; } = new(Ready: true, string.Empty);
		public List<(string ProjectId, string ProfileId)> CreatedReviewers { get; } = [];
		public List<string> StartedReviewers { get; } = [];
		public List<CancellationToken> StartTokens { get; } = [];
		public List<(string SessionId, string Text, CancellationToken Token)> Deliveries { get; } = [];
		public List<string> ClosedSessions { get; } = [];
		public List<(string ProjectId, string RelativePath)> OpenedDocuments { get; } = [];
		public List<string> Traces { get; } = [];

		public TaskCompletionSource<ReviewerStart> HoldStart() =>
			_heldStart = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public TaskCompletionSource<PromptDeliveryResult> HoldNextDelivery() =>
			_heldDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public Task WaitForDeliveriesAsync(int count)
		{
			lock (_gate)
			{
				if (Deliveries.Count >= count)
				{
					return Task.CompletedTask;
				}

				_deliveryTarget = count;
				_deliveryWaiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
				return _deliveryWaiter.Task.WaitAsync(TimeSpan.FromSeconds(10));
			}
		}

		public bool TryGetProject(string sessionId, out ReviewProject project)
		{
			project = sessionId switch
			{
				Author => new ReviewProject("project-1", root, "Claude", AgentKind.Claude),
				Reviewer => new ReviewProject("project-1", root, "Codex reviewer", AgentKind.Codex),
				_ => null!
			};
			return project is not null;
		}

		public bool HasAgentControl(AgentKind kind) => AgentControlKinds.Contains(kind);

		public Task<string> CreateReviewerAsync(string projectId, ReviewProfile profile, CancellationToken cancellationToken)
		{
			CreatedReviewers.Add((projectId, profile.Id));
			return Task.FromResult(Reviewer);
		}

		public async Task<ReviewerStart> StartReviewerAsync(string reviewerSessionId, CancellationToken cancellationToken)
		{
			StartedReviewers.Add(reviewerSessionId);
			StartTokens.Add(cancellationToken);
			if (_heldStart is { } held)
			{
				return await held.Task.WaitAsync(cancellationToken);
			}

			return StartResult;
		}

		public Task<PromptDeliveryResult> DeliverTriggerAsync(string sessionId, string text, CancellationToken cancellationToken)
		{
			TaskCompletionSource<PromptDeliveryResult>? held;
			lock (_gate)
			{
				Deliveries.Add((sessionId, text, cancellationToken));
				held = _heldDelivery;
				_heldDelivery = null;
				if (_deliveryWaiter is not null && Deliveries.Count >= _deliveryTarget)
				{
					_deliveryWaiter.TrySetResult();
				}
			}

			if (held is not null)
			{
				return held.Task;
			}

			return Task.FromResult(NextResults.TryDequeue(out var result)
				? result
				: new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true));
		}

		public bool IsReadyForTrigger(string sessionId) => ReadySessions.Contains(sessionId);

		// Set to close tabs the way the shell does: closing one tells the relay right away.
		public ReviewRelay? ClosesThrough { get; set; }

		public async Task CloseSessionAsync(string sessionId)
		{
			ClosedSessions.Add(sessionId);
			if (ClosesThrough is { } relay)
			{
				await relay.NotifySessionClosedAsync(sessionId);
			}
		}

		public Task OpenProjectDocumentAsync(string projectId, string relativePath)
		{
			OpenedDocuments.Add((projectId, relativePath));
			return Task.CompletedTask;
		}

		public Action<string>? OnTrace { get; set; }

		public void Trace(string line)
		{
			lock (_gate)
			{
				Traces.Add(line);
			}

			OnTrace?.Invoke(line);
		}
	}
}
