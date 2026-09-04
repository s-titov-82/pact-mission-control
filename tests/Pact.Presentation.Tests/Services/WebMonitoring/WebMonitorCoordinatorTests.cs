using Pact.Core.Presentation;
using Pact.Core.Web.Monitoring;
using Pact.Infrastructure.Storage;
using Pact.Presentation.Services.WebMonitoring;

namespace Pact.Presentation.Tests.Services.WebMonitoring;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
	"Reliability",
	"CA2000:Dispose objects before losing scope",
	Justification = "Fake host ownership is transferred to the coordinator context, whose unregister and disposal paths are under test.")]
public sealed class WebMonitorCoordinatorTests
{
	private static readonly Uri MatchingUrl = new("https://builds.test/job/1");
	private static readonly Uri OtherMatchingUrl = new("https://builds.test/job/2");
	private static readonly Uri UnmatchedUrl = new("https://example.test/home");

	[Test]
	public async Task RegisterAsync_restores_compatible_snapshot_before_first_evaluation()
	{
		await using CoordinatorContext context = new();
		var rule = CreateRule();
		var compiled = WebMonitorRuleCompiler.Compile(rule);
		await context.Store.SaveAsync(
			CreateSnapshot(
				compiled,
				MatchingUrl,
				activity: true,
				revision: "1"),
			CancellationToken.None);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([rule], CancellationToken.None);

		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		context.Statuses.ShouldContain(status => status.Status == WebMonitorStatus.Unread);
		var saved =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		saved.Unread.ShouldBeTrue();
	}

	[Test]
	public async Task Live_diagnostics_report_the_matched_rule_and_latest_observation()
	{
		await using CoordinatorContext context = new();
		var rule = CreateRule();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "42");
		await context.Coordinator.SetRulesAsync([rule], CancellationToken.None);

		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Revision == "42");

		var diagnostics = context.LiveDiagnostics.Last(state => state.Revision == "42");
		diagnostics.WebPageId.ShouldBe("web-1");
		diagnostics.ObservedUrl.ShouldBe(MatchingUrl.AbsoluteUri);
		diagnostics.RuleId.ShouldBe("rule-1");
		diagnostics.RuleTitle.ShouldBe("rule-1");
		diagnostics.Status.ShouldBe(WebMonitorStatus.Activity);
		diagnostics.Activity.ShouldBe(true);
		diagnostics.Revision.ShouldBe("42");
		diagnostics.Unread.ShouldBeFalse();
		diagnostics.ObservedAt.ShouldBe(context.Time.GetUtcNow());
		diagnostics.Attempt.ShouldBe(1);
		diagnostics.NextAttemptAt.ShouldBe(context.Time.GetUtcNow().AddSeconds(5));
		diagnostics.Navigating.ShouldBeFalse();
		diagnostics.LastError.ShouldBeNull();
		context.Coordinator.TryGetLiveDiagnostics("web-1", out var current).ShouldBeTrue();
		current.ShouldBe(diagnostics);
	}

	[Test]
	public async Task SetRulesAsync_without_registration_never_polls()
	{
		await using CoordinatorContext context = new();

		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.AdvanceAsync(TimeSpan.FromMinutes(5));

		context.Statuses.ShouldBeEmpty();
		context.Diagnostics.ShouldBeEmpty();
	}

	[Test]
	public async Task Zero_enabled_rules_execute_no_scripts_and_clear_retained_state()
	{
		await using CoordinatorContext context = new();
		var compiled = WebMonitorRuleCompiler.Compile(CreateRule());
		await context.Store.SaveAsync(
			CreateSnapshot(compiled, MatchingUrl, activity: true, revision: "1", unread: true),
			CancellationToken.None);
		FakeWebPageHost host = new("web-1", MatchingUrl);

		await context.Coordinator.SetRulesAsync(
			[CreateRule(enabled: false)],
			CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await context.AdvanceAsync(TimeSpan.FromMinutes(5));

		host.EvaluationCount.ShouldBe(0);
		(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);
	}

	[Test]
	public async Task Disabling_rules_stops_before_another_dispatch_and_reenabling_restarts()
	{
		await using CoordinatorContext context = new();
		var rule = CreateRule();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([rule], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		host.EvaluationCount.ShouldBe(1);

		await context.Coordinator.SetRulesAsync(
			[rule with { Enabled = false }],
			CancellationToken.None);
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		host.EvaluationCount.ShouldBe(1);
		(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();

		await context.Coordinator.SetRulesAsync([rule], CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		host.EvaluationCount.ShouldBe(2);
	}

	[Test]
	public async Task Unmatched_page_uses_thirty_second_url_probes_and_can_enter_monitoring_after_SPA_change()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", UnmatchedUrl);
		host.EnqueueProbe(UnmatchedUrl);
		host.EnqueueProbe(OtherMatchingUrl);
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		host.Queries.ShouldBe([null]);

		await context.AdvanceAsync(TimeSpan.FromSeconds(29));
		host.EvaluationCount.ShouldBe(1);
		await context.AdvanceAsync(TimeSpan.FromSeconds(1));
		host.EvaluationCount.ShouldBe(2);
		host.Queries[1].ShouldBeNull();

		await context.AdvanceAsync(TimeSpan.FromSeconds(1));

		host.EvaluationCount.ShouldBe(4);
		host.Queries[2].ShouldBeNull();
		host.Queries[3].ShouldNotBeNull();
		var snapshot =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		snapshot.Url.ShouldBe(OtherMatchingUrl.AbsoluteUri);
	}

	[Test]
	public async Task Navigation_suspends_polling_and_completion_waits_for_DOM_settle()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		host.EvaluationCount.ShouldBe(1);

		context.Coordinator.SetNavigationState("web-1", navigating: true);
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));
		host.EvaluationCount.ShouldBe(1);

		context.Coordinator.SetNavigationState("web-1", navigating: false);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(499));
		host.EvaluationCount.ShouldBe(1);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(1));

		host.EvaluationCount.ShouldBe(2);
		context.Statuses.ShouldNotContain(status => status.Status == WebMonitorStatus.Unread);
	}

	[Test]
	public async Task Navigation_generation_discards_an_evaluation_started_before_navigation()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> staleEvaluation =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		Uri staleUrl = new("https://builds.test/job/stale");
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(staleEvaluation.Task);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.Time
			.WaitForTimerCountAsync(TimeSpan.FromSeconds(5), minimumCount: 2)
			.WaitAsync(TimeSpan.FromSeconds(1));

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await host.WaitForEvaluationCountAsync(2);
		context.Coordinator.SetNavigationState("web-1", navigating: true);
		host.SetSource(OtherMatchingUrl);
		context.Coordinator.SetNavigationState("web-1", navigating: false);
		var navigationSettleScheduled =
			context.Time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		staleEvaluation.SetResult(
			new WebMonitorEvaluation(
				staleUrl,
				new WebMonitorObservation(true, "stale")));
		await navigationSettleScheduled.WaitAsync(TimeSpan.FromSeconds(1));

		var confirmationSettleScheduled =
			context.Time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationCountAsync(3);

		host.Queries[2].ShouldNotBeNull();
		context.StableUrls.ShouldNotContain(change => change.NormalizedUrl == staleUrl);

		await confirmationSettleScheduled.WaitAsync(TimeSpan.FromSeconds(1));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == OtherMatchingUrl.AbsoluteUri);
	}

	[Test]
	public async Task Timed_out_native_invocation_does_not_block_unregister()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.Enqueue(pending.Task);
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		context.Diagnostics.ShouldContain(diagnostic =>
			diagnostic.Category == "Timeout"
			&& diagnostic.Attempt == 1
			&& diagnostic.RuleId == "rule-1");
		host.EvaluationCount.ShouldBe(1);
		host.MaximumConcurrentEvaluations.ShouldBe(1);

		var unregister = context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);
		try
		{
			await unregister.WaitAsync(TimeSpan.FromSeconds(1));
		}
		finally
		{
			pending.TrySetException(new IOException("late invocation failure"));
			await unregister.WaitAsync(TimeSpan.FromSeconds(1));
		}

		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task Timed_out_native_invocation_does_not_block_dispose()
	{
		CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		try
		{
			FakeWebPageHost host = new("web-1", MatchingUrl);
			host.Enqueue(pending.Task);
			await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
			await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
			await CoordinatorContext.PumpAsync();
			await context.AdvanceAsync(TimeSpan.FromSeconds(5));

			var dispose = context.Coordinator.DisposeAsync().AsTask();
			try
			{
				await dispose.WaitAsync(TimeSpan.FromSeconds(1));
			}
			finally
			{
				pending.TrySetException(new IOException("late invocation failure"));
				await dispose.WaitAsync(TimeSpan.FromSeconds(1));
				context.MarkCoordinatorDisposed();
			}
		}
		finally
		{
			pending.TrySetCanceled();
			await context.DisposeAsync();
		}
	}

	[Test]
	public async Task Timed_out_invocation_prevents_parallel_replacement_until_it_finishes()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pending.Task);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMinutes(5));

		host.EvaluationCount.ShouldBe(1);
		host.MaximumConcurrentEvaluations.ShouldBe(1);
		pending.TrySetCanceled();
	}

	[Test]
	public async Task Navigation_does_not_start_a_second_invocation_while_old_one_is_pending()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pending.Task);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		for (var index = 0; index < 3; index++)
		{
			context.Coordinator.SetNavigationState("web-1", navigating: true);
			host.SetSource(OtherMatchingUrl);
			context.Coordinator.SetNavigationState("web-1", navigating: false);
			await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		}

		host.EvaluationCount.ShouldBe(1);
		host.MaximumConcurrentEvaluations.ShouldBe(1);
		pending.TrySetCanceled();
	}

	[Test]
	public async Task Monitoring_resumes_at_latest_generation_after_pending_invocation_finishes()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pending.Task);
		host.EnqueueProbe(OtherMatchingUrl);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		context.Coordinator.SetNavigationState("web-1", navigating: true);
		host.SetSource(OtherMatchingUrl);
		context.Coordinator.SetNavigationState("web-1", navigating: false);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		pending.SetResult(
			new WebMonitorEvaluation(
				MatchingUrl,
				new WebMonitorObservation(true, "stale")));
		await host.WaitForEvaluationCountAsync(2);

		host.EvaluationCount.ShouldBe(2);
		context.Statuses.ShouldNotContain(status =>
			status.Status == WebMonitorStatus.Unread);
	}

	[Test]
	public async Task Throwing_dispatcher_does_not_fault_registration_loop_or_unregister()
	{
		await using CoordinatorContext context = new(_ => throw new InvalidOperationException());
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);

		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		host.EvaluationCount.ShouldBe(2);
	}

	[Test]
	public async Task Throwing_status_subscriber_does_not_fault_registration_or_unregister()
	{
		await using CoordinatorContext context = new();
		context.Coordinator.StatusChanged += (_, _) => throw new InvalidOperationException();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);

		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task Throwing_diagnostic_subscriber_does_not_fault_polling_loop()
	{
		await using CoordinatorContext context = new();
		context.Coordinator.DiagnosticChanged += (_, _) => throw new InvalidOperationException();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.EnqueueException(new InvalidOperationException("evaluation failed"));
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		host.EvaluationCount.ShouldBe(2);
	}

	[Test]
	public async Task Throwing_stable_URL_subscriber_does_not_fault_polling_loop()
	{
		await using CoordinatorContext context = new();
		context.Coordinator.StableUrlChanged += (_, _) => throw new InvalidOperationException();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "3");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await host.WaitForEvaluationCountAsync(5);
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		host.EvaluationCount.ShouldBe(5);
	}

	[Test]
	public async Task Failure_retains_last_state_and_retries_on_normal_cadence()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.EnqueueException(new InvalidOperationException("secret page content"));
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Activity);

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		context.Diagnostics.Last().Category.ShouldBe("Evaluation");
		context.Diagnostics.Last().Message.ShouldNotContain("secret");
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Activity);
		var lastError = context.LiveDiagnostics.Last().LastError.ShouldNotBeNull();
		lastError.ShouldContain("Evaluation");
		lastError.ShouldNotContain("secret");

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		host.EvaluationCount.ShouldBe(3);
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Unread);
		context.LiveDiagnostics.Last().LastError.ShouldBeNull();
	}

	[Test]
	public async Task Rule_id_fingerprint_refresh_and_same_URL_reload_each_establish_fresh_baseline()
	{
		await using CoordinatorContext context = new();
		var original = CreateRule();
		var changed = original with
		{
			Revision = original.Revision! with { Selector = ".new-build" }
		};
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		host.Enqueue(MatchingUrl, activity: false, revision: "3");
		host.Enqueue(MatchingUrl, activity: false, revision: "4");
		await context.Coordinator.SetRulesAsync([original], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.Coordinator.SetRulesAsync([changed], CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		context.Statuses.ShouldNotContain(status => status.Status == WebMonitorStatus.Unread);

		await context.Coordinator.SetRulesAsync(
			[changed with { Id = "replacement-rule" }],
			CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		context.Statuses.ShouldNotContain(status => status.Status == WebMonitorStatus.Unread);

		context.Coordinator.SetNavigationState("web-1", navigating: true);
		context.Coordinator.SetNavigationState("web-1", navigating: false);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		host.EvaluationCount.ShouldBe(4);
		context.Statuses.ShouldNotContain(status => status.Status == WebMonitorStatus.Unread);
	}

	[Test]
	public async Task Redirect_and_unconfirmed_SPA_candidates_preserve_snapshot_until_stable_confirmation()
	{
		await using CoordinatorContext context = new();
		var rule = CreateRule();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		host.Enqueue(MatchingUrl, activity: true, revision: "2");
		host.Enqueue(UnmatchedUrl, activity: false, revision: "redirect");
		host.EnqueueProbe(OtherMatchingUrl);
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([rule], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Activity);

		context.Coordinator.SetNavigationState("web-1", navigating: true);
		context.Coordinator.SetNavigationState("web-1", navigating: false);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		var duringRedirect =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		duringRedirect.Activity.ShouldBe(true);
		duringRedirect.Revision.ShouldBe("2");
		duringRedirect.Unread.ShouldBeTrue();

		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		var duringUnconfirmedCandidate =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		duringUnconfirmedCandidate.Activity.ShouldBe(true);
		duringUnconfirmedCandidate.Unread.ShouldBeTrue();

		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		var confirmed =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		confirmed.Url.ShouldBe(OtherMatchingUrl.AbsoluteUri);
		confirmed.Revision.ShouldBe("2");
		confirmed.Unread.ShouldBeTrue();
	}

	[Test]
	public async Task Matching_SPA_transition_resets_baseline_before_comparing_new_values()
	{
		await using CoordinatorContext context = new();
		Uri rawConfirmedUrl = new(OtherMatchingUrl + "#confirmed");
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.EnqueueProbe(rawConfirmedUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		context.StableUrls.Count.ShouldBe(1);
		context.StableUrls[0].DocumentUrl.ShouldBe(rawConfirmedUrl);
		context.StableUrls[0].NormalizedUrl.ShouldBe(OtherMatchingUrl);
		context.Statuses.ShouldNotContain(status => status.Status == WebMonitorStatus.Unread);
	}

	[Test]
	public async Task SPA_round_trip_publishes_A_to_B_and_B_to_A_stable_URL_changes()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.Enqueue(MatchingUrl, activity: false, revision: "3");
		host.EnqueueProbe(MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "3");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == MatchingUrl.AbsoluteUri);

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == OtherMatchingUrl.AbsoluteUri);
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == MatchingUrl.AbsoluteUri
				&& snapshot.Revision == "3");

		context.StableUrls
			.Select(change => change.NormalizedUrl)
			.ShouldBe([OtherMatchingUrl, MatchingUrl]);
		var snapshot =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		snapshot.Url.ShouldBe(MatchingUrl.AbsoluteUri);
	}

	[Test]
	public async Task Main_frame_navigation_confirmation_does_not_republish_saved_Source_URL()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == MatchingUrl.AbsoluteUri);
		await context.Time
			.WaitForTimerCountAsync(TimeSpan.FromSeconds(5), minimumCount: 2)
			.WaitAsync(TimeSpan.FromSeconds(1));

		context.Coordinator.SetNavigationState("web-1", navigating: true);
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		host.EvaluationCount.ShouldBe(1);
		host.SetSource(OtherMatchingUrl);
		var navigationSettleScheduled =
			context.Time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		context.Coordinator.SetNavigationState("web-1", navigating: false);
		await navigationSettleScheduled.WaitAsync(TimeSpan.FromSeconds(1));

		var confirmationSettleScheduled =
			context.Time.WaitForTimerCreatedAsync(TimeSpan.FromMilliseconds(500));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await confirmationSettleScheduled.WaitAsync(TimeSpan.FromSeconds(1));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == OtherMatchingUrl.AbsoluteUri);

		context.StableUrls.ShouldBeEmpty();
		host.EvaluationCount.ShouldBe(4);
		var snapshot =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		snapshot.Url.ShouldBe(OtherMatchingUrl.AbsoluteUri);
	}

	[Test]
	public async Task Failed_pending_URL_confirmation_retries_at_matched_rule_cadence()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "discarded");
		host.EnqueueException(new InvalidOperationException("probe failed"));
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == MatchingUrl.AbsoluteUri);
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationCountAsync(3);
		host.EvaluationCount.ShouldBe(3);
		host.Queries.Last().ShouldBeNull();

		await context.AdvanceAsync(TimeSpan.FromMilliseconds(4999));
		host.EvaluationCount.ShouldBe(3);
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(1));
		await host.WaitForEvaluationCountAsync(5);

		host.EvaluationCount.ShouldBe(5);
		host.Queries[3].ShouldBeNull();
		context.Diagnostics.ShouldContain(diagnostic =>
			diagnostic.Category == "Evaluation"
			&& diagnostic.Attempt == 3);
	}

	[Test]
	public async Task Timed_out_pending_URL_confirmation_detaches_then_resumes_when_invocation_finishes()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		TaskCompletionSource<WebMonitorEvaluation> pendingProbe =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "discarded");
		host.Enqueue(pendingProbe.Task);
		host.EnqueueProbe(OtherMatchingUrl);
		host.Enqueue(OtherMatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.WaitForSnapshotAsync(
			"web-1",
			snapshot => snapshot.Url == MatchingUrl.AbsoluteUri);
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));
		await host.WaitForEvaluationCountAsync(3);
		host.EvaluationCount.ShouldBe(3);

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		context.Diagnostics.ShouldContain(diagnostic =>
			diagnostic.Category == "Timeout"
			&& diagnostic.Attempt == 3);
		await context.AdvanceAsync(TimeSpan.FromSeconds(30));
		host.EvaluationCount.ShouldBe(3);
		host.MaximumConcurrentEvaluations.ShouldBe(1);

		pendingProbe.SetResult(
			new WebMonitorEvaluation(OtherMatchingUrl, Observation: null));
		await host.WaitForEvaluationCountAsync(5);

		host.EvaluationCount.ShouldBe(5);
		host.MaximumConcurrentEvaluations.ShouldBe(1);
		host.Queries[3].ShouldBeNull();
	}

	[Test]
	public async Task Fragment_only_change_processes_observation_without_stable_URL_event_or_rebaseline()
	{
		await using CoordinatorContext context = new();
		Uri first = new(MatchingUrl + "#one");
		Uri second = new(MatchingUrl + "#two");
		FakeWebPageHost host = new("web-1", first);
		host.Enqueue(first, activity: false, revision: "1");
		host.Enqueue(second, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		context.StableUrls.ShouldBeEmpty();
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Unread);
		var snapshot =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		snapshot.Url.ShouldBe(MatchingUrl.AbsoluteUri);
		snapshot.Revision.ShouldBe("2");
	}

	[Test]
	public async Task First_matching_rule_in_file_order_wins()
	{
		await using CoordinatorContext context = new();
		var first = CreateRule("first") with
		{
			Activity = new WebMonitorExtractor(
				".first",
				WebMonitorValueSource.Exists,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null)
		};
		var second = CreateRule("second") with
		{
			Activity = new WebMonitorExtractor(
				".second",
				WebMonitorValueSource.Exists,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null)
		};
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");

		await context.Coordinator.SetRulesAsync([first, second], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		host.Queries.Single().ShouldBe(WebMonitorRuleCompiler.Compile(first).Query);
	}

	[Test]
	public async Task Rule_refresh_compiles_once_and_rematches_every_loaded_registration()
	{
		await using CoordinatorContext context = new();
		var original = CreateRule();
		var refreshed = original with
		{
			Revision = original.Revision! with { Selector = ".refreshed" }
		};
		FakeWebPageHost firstHost = new("web-1", MatchingUrl);
		FakeWebPageHost secondHost = new("web-2", MatchingUrl);
		firstHost.Enqueue(MatchingUrl, activity: false, revision: "1");
		secondHost.Enqueue(MatchingUrl, activity: false, revision: "1");
		firstHost.Enqueue(MatchingUrl, activity: false, revision: "2");
		secondHost.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([original], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", firstHost, CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-2", secondHost, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.Coordinator.SetRulesAsync([refreshed], CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		firstHost.EvaluationCount.ShouldBe(2);
		secondHost.EvaluationCount.ShouldBe(2);
		ReferenceEquals(firstHost.Queries[1], secondHost.Queries[1]).ShouldBeTrue();
	}

	[Test]
	public async Task Rule_refresh_detaches_pending_invocation_before_starting_new_loop()
	{
		await using CoordinatorContext context = new();
		var original = CreateRule();
		var refreshed = original with
		{
			Revision = original.Revision! with { Selector = ".refreshed" }
		};
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pending.Task);
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([original], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		var refresh =
			context.Coordinator.SetRulesAsync([refreshed], CancellationToken.None);
		await refresh.WaitAsync(TimeSpan.FromSeconds(1));
		var evaluationsBeforeCompletion = host.EvaluationCount;
		pending.TrySetException(new InvalidOperationException("old rule failure"));
		await host.WaitForEvaluationCountAsync(2);

		evaluationsBeforeCompletion.ShouldBe(1);
		host.MaximumConcurrentEvaluations.ShouldBe(1);
	}

	[Test]
	public async Task Unchanged_observation_does_not_rewrite_snapshot()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		var before =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		var snapshotPath = Path.Combine(
			context.Paths.WebMonitorSnapshotsDirectory,
			"web-1.json");
		DateTime fixedWriteTime = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(snapshotPath, fixedWriteTime);

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));

		var after =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		after.ShouldBe(before);
		File.GetLastWriteTimeUtc(snapshotPath).ShouldBe(fixedWriteTime);
		context.Diagnostics.ShouldBeEmpty();
	}

	[Test]
	public async Task Presentation_acknowledgement_is_persisted_and_status_uses_dispatcher()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);
		var unreadObserved =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.Unread);
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await unreadObserved;
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Unread);
		var dispatchesBeforeAck = context.DispatchCount;

		var acknowledged =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.None);
		context.Coordinator.SetPresentationFacts(
			selectedWebPageId: "web-1",
			windowVisible: true,
			windowActive: true);
		await acknowledged;
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);
		context.DispatchCount.ShouldBeGreaterThan(dispatchesBeforeAck);
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		var snapshot =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		snapshot.Unread.ShouldBeFalse();
	}

	[Test]
	public async Task BecomingPresented_ImmediatelyReevaluatesFreshDomWithoutWaitingForPollInterval()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		var initialEvaluation =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.Activity);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await initialEvaluation;
		host.EvaluationCount.ShouldBe(1);

		var reevaluated =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.None);
		context.Coordinator.SetPresentationFacts(
			selectedWebPageId: "web-1",
			windowVisible: true,
			windowActive: true);

		await host.WaitForEvaluationCountAsync(2);
		await reevaluated;
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);
	}

	[Test]
	public async Task Actively_viewed_page_keeps_polling_faster_than_its_rule_interval()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		var initialEvaluation =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.Activity);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await initialEvaluation;
		context.Coordinator.SetPresentationFacts(
			selectedWebPageId: "web-1",
			windowVisible: true,
			windowActive: true);
		await host.WaitForEvaluationCountAsync(2);

		var idleObserved =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.None);
		await context.AdvanceAsync(TimeSpan.FromSeconds(2));

		await host.WaitForEvaluationCountAsync(3);
		await idleObserved;
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);
	}

	[Test]
	public async Task Presentation_snapshot_cannot_acknowledge_after_unregister_starts()
	{
		using ManualResetEventSlim mutationEntered = new(initialState: false);
		using ManualResetEventSlim releaseMutation = new(initialState: false);
		var armed = 0;
		await using CoordinatorContext context = new(
			beforePresentationMutation: webPageId =>
			{
				if (webPageId == "web-1" && Volatile.Read(ref armed) == 1)
				{
					mutationEntered.Set();
					releaseMutation.Wait(TimeSpan.FromSeconds(5)).ShouldBeTrue();
				}
			});
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.WaitForSnapshotAsync("web-1", snapshot => snapshot.Unread);

		Volatile.Write(ref armed, 1);
		var presentation = Task.Run(
			() => context.Coordinator.SetPresentationFacts(
				selectedWebPageId: "web-1",
				windowVisible: true,
				windowActive: true));
		mutationEntered.Wait(TimeSpan.FromSeconds(1)).ShouldBeTrue();

		await context.Coordinator.UnregisterAsync(
				"web-1",
				deleteSnapshot: false,
				CancellationToken.None)
			.WaitAsync(TimeSpan.FromSeconds(1));
		releaseMutation.Set();
		await presentation.WaitAsync(TimeSpan.FromSeconds(1));
		await CoordinatorContext.PumpAsync();

		var retained =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		retained.Unread.ShouldBeTrue();
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Unread);
	}

	[Test]
	public async Task TestAsync_evaluates_once_without_live_status_or_snapshot_mutation()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: true, revision: "test");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		var before =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		var statusesBefore = context.Statuses.Count;

		var result = await context.Coordinator.TestAsync(
			"web-1",
			CreateRule("test-rule"),
			CancellationToken.None);

		result.UrlMatched.ShouldBeTrue();
		result.Activity.ShouldBe(true);
		result.Revision.ShouldBe("test");
		host.EvaluationCount.ShouldBe(2);
		context.Statuses.Count.ShouldBe(statusesBefore);
		(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldBe(before);
	}

	[Test]
	public async Task TestAsync_matches_customized_disabled_rule_before_enabling()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: true, revision: "test");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();
		var disabledRule = CreateRule(
			id: "disabled-test-rule",
			enabled: false,
			urlPattern: "^https://builds\\.test/job/1$");

		var result = await context.Coordinator.TestAsync(
			"web-1",
			disabledRule,
			CancellationToken.None);

		result.UrlMatched.ShouldBeTrue();
	}

	[Test]
	public async Task TestAsync_rejects_invalid_rule_without_evaluating_the_host()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		await context.Coordinator.SetRulesAsync([], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);

		var result = await context.Coordinator.TestAsync(
			"web-1",
			CreateRule() with { UrlPattern = "(" },
			CancellationToken.None);

		result.Error.ShouldNotBeNullOrWhiteSpace();
		host.EvaluationCount.ShouldBe(0);
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);
	}

	[Test]
	public async Task Queued_TestAsync_is_canceled_without_dispatch_when_unregister_begins()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> holding =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(holding.Task);
		await context.Coordinator.SetRulesAsync([], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);

		var holdingTest = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("holding-test-rule"),
			CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		var test = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("test-rule"),
			CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		var unregister = context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);
		var testCanceled = await CompletesAsCanceledWithinAsync(test);
		await unregister.WaitAsync(TimeSpan.FromSeconds(1));
		var evaluationsBeforeHoldingCompletion = host.EvaluationCount;
		holding.TrySetException(new InvalidOperationException("late holding failure"));
		var holdingTestCanceled = await CompletesAsCanceledWithinAsync(holdingTest);
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		holdingTestCanceled.ShouldBeTrue();
		testCanceled.ShouldBeTrue();
		evaluationsBeforeHoldingCompletion.ShouldBe(1);
		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task Queued_TestAsync_is_canceled_without_dispatch_when_dispose_begins()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> holding =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(holding.Task);
		await context.Coordinator.SetRulesAsync([], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);

		var holdingTest = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("holding-test-rule"),
			CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		var test = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("test-rule"),
			CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		var dispose = context.Coordinator.DisposeAsync().AsTask();
		var testCanceled = await CompletesAsCanceledWithinAsync(test);
		await dispose.WaitAsync(TimeSpan.FromSeconds(1));
		context.MarkCoordinatorDisposed();
		var evaluationsBeforeHoldingCompletion = host.EvaluationCount;
		holding.TrySetException(new InvalidOperationException("late holding failure"));
		var holdingTestCanceled = await CompletesAsCanceledWithinAsync(holdingTest);
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		holdingTestCanceled.ShouldBeTrue();
		testCanceled.ShouldBeTrue();
		evaluationsBeforeHoldingCompletion.ShouldBe(1);
		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task In_flight_TestAsync_is_canceled_without_blocking_unregister()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pendingTest =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pendingTest.Task);
		await context.Coordinator.SetRulesAsync([], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);

		var test = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("test-rule"),
			CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		var unregister = context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);
		await unregister.WaitAsync(TimeSpan.FromSeconds(1));
		var testCanceled = await CompletesAsCanceledWithinAsync(test);
		pendingTest.TrySetException(new InvalidOperationException("late test failure"));
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		testCanceled.ShouldBeTrue();
		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task In_flight_TestAsync_is_canceled_without_blocking_dispose()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pendingTest =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pendingTest.Task);
		await context.Coordinator.SetRulesAsync([], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);

		var test = context.Coordinator.TestAsync(
			"web-1",
			CreateRule("test-rule"),
			CancellationToken.None);
		await host.WaitForEvaluationCountAsync(1);

		var dispose = context.Coordinator.DisposeAsync().AsTask();
		await dispose.WaitAsync(TimeSpan.FromSeconds(1));
		context.MarkCoordinatorDisposed();
		var testCanceled = await CompletesAsCanceledWithinAsync(test);
		pendingTest.TrySetException(new InvalidOperationException("late test failure"));
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		testCanceled.ShouldBeTrue();
		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task Confirmed_stable_no_match_clears_status_and_snapshot_but_keeps_URL_probe_loop()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: true, revision: "1");
		host.Enqueue(UnmatchedUrl, activity: false, revision: "ignored");
		host.EnqueueProbe(UnmatchedUrl);
		host.EnqueueProbe(UnmatchedUrl);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		var beforeConfirmation =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		beforeConfirmation.Activity.ShouldBe(true);

		await context.AdvanceAsync(TimeSpan.FromMilliseconds(500));

		(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.None);

		await context.AdvanceAsync(TimeSpan.FromSeconds(30));
		host.Queries.Last().ShouldBeNull();
	}

	[Test]
	public async Task Unregister_clears_activity_preserves_unread_snapshot_or_deletes_it()
	{
		await using CoordinatorContext context = new();
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(MatchingUrl, activity: false, revision: "1");
		host.Enqueue(MatchingUrl, activity: false, revision: "2");
		host.Enqueue(MatchingUrl, activity: true, revision: "2");
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		var activityObserved =
			WaitForNextStatusAsync(context.Coordinator, WebMonitorStatus.Activity);
		await CoordinatorContext.PumpAsync();
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await context.AdvanceAsync(TimeSpan.FromSeconds(5));
		await activityObserved;
		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Activity);

		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);

		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Unread);
		var retained =
			(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldNotBeNull();
		retained.Unread.ShouldBeTrue();
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));
		host.EvaluationCount.ShouldBe(3);

		FakeWebPageHost reopened = new("web-1", MatchingUrl);
		await context.Coordinator.RegisterAsync("web-1", reopened, CancellationToken.None);
		await context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: true,
			CancellationToken.None);

		(await context.Store.LoadAsync("web-1", CancellationToken.None)).ShouldBeNull();
	}

	[Test]
	public async Task UnregisterAsync_detaches_dispatched_host_invocation()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> pending =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost host = new("web-1", MatchingUrl);
		host.Enqueue(pending.Task);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", host, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		var unregister = context.Coordinator.UnregisterAsync(
			"web-1",
			deleteSnapshot: false,
			CancellationToken.None);
		await unregister.WaitAsync(TimeSpan.FromSeconds(1));
		pending.TrySetException(new InvalidOperationException("host failure"));

		context.Statuses.Last().Status.ShouldBe(WebMonitorStatus.Paused);
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));
		host.EvaluationCount.ShouldBe(1);
	}

	[Test]
	public async Task DisposeAsync_detaches_every_dispatched_host_invocation()
	{
		await using CoordinatorContext context = new();
		TaskCompletionSource<WebMonitorEvaluation> first =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		TaskCompletionSource<WebMonitorEvaluation> second =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		FakeWebPageHost firstHost = new("web-1", MatchingUrl);
		FakeWebPageHost secondHost = new("web-2", MatchingUrl);
		firstHost.Enqueue(first.Task);
		secondHost.Enqueue(second.Task);
		await context.Coordinator.SetRulesAsync([CreateRule()], CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-1", firstHost, CancellationToken.None);
		await context.Coordinator.RegisterAsync("web-2", secondHost, CancellationToken.None);
		await CoordinatorContext.PumpAsync();

		var dispose = context.Coordinator.DisposeAsync().AsTask();
		await dispose.WaitAsync(TimeSpan.FromSeconds(1));
		context.MarkCoordinatorDisposed();
		first.TrySetException(new InvalidOperationException("first failure"));
		second.TrySetException(new InvalidOperationException("second failure"));
		await context.AdvanceAsync(TimeSpan.FromMinutes(1));

		firstHost.EvaluationCount.ShouldBe(1);
		secondHost.EvaluationCount.ShouldBe(1);
	}

	private static async Task WaitForNextStatusAsync(
		WebMonitorCoordinator coordinator,
		WebMonitorStatus expected)
	{
		TaskCompletionSource observed =
			new(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnStatusChanged(object? sender, WebMonitorStatusChangedEventArgs args)
		{
			if (args.Status == expected)
			{
				observed.TrySetResult();
			}
		}

		coordinator.StatusChanged += OnStatusChanged;
		try
		{
			await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
		}
		finally
		{
			coordinator.StatusChanged -= OnStatusChanged;
		}
	}

	private static WebMonitorRule CreateRule(
		string id = "rule-1",
		bool enabled = true,
		string urlPattern = "^https://builds\\.test/") =>
		new(
			id,
			id,
			enabled,
			urlPattern,
			PollIntervalSeconds: 5,
			Activity: new WebMonitorExtractor(
				".busy",
				WebMonitorValueSource.Exists,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null),
			Revision: new WebMonitorExtractor(
				".build",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null));

	private static async Task<bool> CompletesAsCanceledWithinAsync(Task task)
	{
		try
		{
			await task.WaitAsync(TimeSpan.FromSeconds(1));
			return false;
		}
		catch (OperationCanceledException)
		{
			return true;
		}
		catch (TimeoutException)
		{
			return false;
		}
		catch
		{
			return false;
		}
	}

	private static WebMonitorSnapshot CreateSnapshot(
		WebMonitorCompiledRule rule,
		Uri url,
		bool? activity,
		string? revision,
		bool unread = false) =>
		new(
			"web-1",
			WebMonitorUrl.Normalize(url).AbsoluteUri,
			rule.Source.Id,
			rule.Fingerprint,
			activity,
			revision,
			unread,
			new DateTimeOffset(2026, 7, 24, 8, 0, 0, TimeSpan.Zero));

	private sealed class CoordinatorContext : IAsyncDisposable
	{
		private readonly TemporaryDirectory _temporaryDirectory = TemporaryDirectory.Create();
		private string _root => _temporaryDirectory.Path;
		private bool _coordinatorDisposed;

		public CoordinatorContext(
			Action<Action>? dispatcher = null,
			Action<string>? beforePresentationMutation = null)
		{
			Paths = new AppPaths(_root);
			Store = new WebMonitorSnapshotStore(Paths);
			Time = new ManualTimeProvider(
				new DateTimeOffset(2026, 7, 24, 9, 0, 0, TimeSpan.Zero));
			Coordinator = new WebMonitorCoordinator(
				Store,
				Time,
				dispatcher
				?? (action =>
				{
					DispatchCount++;
					action();
				}),
				beforePresentationMutation);
			Coordinator.StatusChanged += (_, args) => Statuses.Add(args);
			Coordinator.DiagnosticChanged += (_, args) => Diagnostics.Add(args);
			Coordinator.LiveDiagnosticsChanged += (_, args) =>
				LiveDiagnostics.Add(args.Diagnostics);
			Coordinator.StableUrlChanged += (_, args) => StableUrls.Add(args);
		}

		public AppPaths Paths { get; }
		public WebMonitorSnapshotStore Store { get; }
		public ManualTimeProvider Time { get; }
		public WebMonitorCoordinator Coordinator { get; }
		public List<WebMonitorStatusChangedEventArgs> Statuses { get; } = [];
		public List<WebMonitorDiagnosticEventArgs> Diagnostics { get; } = [];
		public List<WebMonitorDiagnostics> LiveDiagnostics { get; } = [];
		public List<WebMonitorStableUrlChangedEventArgs> StableUrls { get; } = [];
		public int DispatchCount { get; private set; }

		public async Task AdvanceAsync(TimeSpan amount)
		{
			Time.Advance(amount);
			await PumpAsync();
		}

		public static async Task PumpAsync()
		{
			for (var index = 0; index < 20; index++)
			{
				await Task.Yield();
				await Task.Delay(1);
			}
		}

		public async Task<WebMonitorSnapshot> WaitForSnapshotAsync(
			string webPageId,
			Func<WebMonitorSnapshot, bool> condition)
		{
			for (var index = 0; index < 100; index++)
			{
				var snapshot =
					await Store.LoadAsync(webPageId, CancellationToken.None);
				if (snapshot is not null && condition(snapshot))
				{
					return snapshot;
				}

				await Task.Delay(5);
			}

			var finalSnapshot =
				await Store.LoadAsync(webPageId, CancellationToken.None);
			finalSnapshot.ShouldNotBeNull();
			condition(finalSnapshot).ShouldBeTrue();
			return finalSnapshot;
		}

		public void MarkCoordinatorDisposed() => _coordinatorDisposed = true;

		public async ValueTask DisposeAsync()
		{
			if (!_coordinatorDisposed)
			{
				await Coordinator.DisposeAsync();
			}

			await _temporaryDirectory.DisposeAsync();
		}
	}

	private sealed class FakeWebPageHost : IWebPageHost
	{
		private readonly Queue<Func<Task<WebMonitorEvaluation>>> _evaluations = [];
		private readonly List<(int MinimumCount, TaskCompletionSource Completion)> _evaluationWaiters = [];
		private readonly Lock _evaluationSignalLock = new();
		private int _concurrentEvaluations;

		public FakeWebPageHost(string id, Uri? source)
		{
			Id = id;
			Source = source;
		}

		public string Id { get; }
		public Uri? Source { get; private set; }
		public List<WebMonitorDomQuery?> Queries { get; } = [];
		public int EvaluationCount => Queries.Count;
		public int MaximumConcurrentEvaluations { get; private set; }
		public event EventHandler<Uri>? SourceChanged;
		public event EventHandler<string>? TitleChanged;
		public event EventHandler? NavigationStarted;
		public event EventHandler? NavigationCompleted;
		public event EventHandler<string>? NavigationFailed;
		public event EventHandler<Uri>? NewWindowRequested;

		public void Enqueue(Uri url, bool? activity, string? revision) =>
			Enqueue(
				Task.FromResult(
					new WebMonitorEvaluation(
						url,
						new WebMonitorObservation(activity, revision))));

		public void EnqueueProbe(Uri url) =>
			Enqueue(Task.FromResult(new WebMonitorEvaluation(url, Observation: null)));

		public void Enqueue(Task<WebMonitorEvaluation> evaluation) =>
			_evaluations.Enqueue(() => evaluation);

		public void EnqueueException(Exception exception) =>
			_evaluations.Enqueue(() => Task.FromException<WebMonitorEvaluation>(exception));

		public void SetSource(Uri source)
		{
			Source = source;
			SourceChanged?.Invoke(this, source);
		}

		public async Task<WebMonitorEvaluation> EvaluateMonitorAsync(
			WebMonitorDomQuery? query,
			CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			List<TaskCompletionSource> completed = [];
			lock (_evaluationSignalLock)
			{
				Queries.Add(query);
				for (var index = _evaluationWaiters.Count - 1; index >= 0; index--)
				{
					var waiter = _evaluationWaiters[index];
					if (Queries.Count >= waiter.MinimumCount)
					{
						completed.Add(waiter.Completion);
						_evaluationWaiters.RemoveAt(index);
					}
				}
			}

			foreach (var completion in completed)
			{
				completion.TrySetResult();
			}

			var concurrent = Interlocked.Increment(ref _concurrentEvaluations);
			MaximumConcurrentEvaluations = Math.Max(MaximumConcurrentEvaluations, concurrent);
			try
			{
				if (_evaluations.Count == 0)
				{
					throw new InvalidOperationException("No monitor evaluation result was queued.");
				}

				return await _evaluations.Dequeue()();
			}
			finally
			{
				Interlocked.Decrement(ref _concurrentEvaluations);
			}
		}

		public Task WaitForEvaluationCountAsync(int minimumCount)
		{
			ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCount);
			lock (_evaluationSignalLock)
			{
				if (Queries.Count >= minimumCount)
				{
					return Task.CompletedTask;
				}

				TaskCompletionSource completion =
					new(TaskCreationOptions.RunContinuationsAsynchronously);
				_evaluationWaiters.Add((minimumCount, completion));
				return completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
			}
		}

		public Task NavigateAsync(Uri uri, CancellationToken cancellationToken)
		{
			cancellationToken.ThrowIfCancellationRequested();
			Source = uri;
			SourceChanged?.Invoke(this, uri);
			return Task.CompletedTask;
		}

		public Task ReloadAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task ShowAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task HideAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task FocusAsync(CancellationToken cancellationToken) =>
			Task.CompletedTask;

		public Task<WebPageDocumentFragment> ReadDocumentHtmlAsync(
			WebPageDocumentRange range,
			CancellationToken cancellationToken) =>
			throw new NotSupportedException();

		public ValueTask DisposeAsync() => ValueTask.CompletedTask;

		// Keep the full production event surface in the fake even though the coordinator
		// receives lifecycle facts explicitly from the shell.
		public void RaiseNavigationStarted() => NavigationStarted?.Invoke(this, EventArgs.Empty);
		public void RaiseNavigationCompleted() => NavigationCompleted?.Invoke(this, EventArgs.Empty);
		public void RaiseNavigationFailed(string message) => NavigationFailed?.Invoke(this, message);
		public void RaiseTitleChanged(string title) => TitleChanged?.Invoke(this, title);
		public void RaiseNewWindowRequested(Uri uri) => NewWindowRequested?.Invoke(this, uri);
	}

}
