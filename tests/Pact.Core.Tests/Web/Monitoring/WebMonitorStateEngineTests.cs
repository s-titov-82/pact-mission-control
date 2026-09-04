using Pact.Core.Web.Monitoring;

namespace Pact.Core.Tests.Web.Monitoring;

public sealed class WebMonitorStateEngineTests
{
	private static readonly Uri Url =
		new("https://builds.example/jobs?branch=main#overview");

	private static readonly DateTimeOffset T0 =
		new(2026, 7, 24, 9, 0, 0, TimeSpan.Zero);

	[TestCase(false, null, true, null, false, true)]
	[TestCase(true, "1842", false, "1842", true, false)]
	[TestCase(false, "3", false, "4", true, false)]
	public void Observe_applies_known_transition_matrix(
		bool previousActivity,
		string? previousRevision,
		bool currentActivity,
		string? currentRevision,
		bool expectedUnread,
		bool expectedActivity)
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(previousActivity, previousRevision),
			T0);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(currentActivity, currentRevision),
			T0.AddMinutes(1));

		transition.Snapshot!.Unread.ShouldBe(expectedUnread);
		transition.Snapshot.Activity.ShouldBe(currentActivity);
		transition.Snapshot.Revision.ShouldBe(currentRevision);
		transition.Status.ShouldBe(expectedActivity
			? WebMonitorStatus.Activity
			: expectedUnread
				? WebMonitorStatus.Unread
				: WebMonitorStatus.None);
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Observe_first_observation_establishes_baseline_without_unread()
	{
		var engine = CreateLoadedEngine();

		var transition = engine.Observe(
			Url,
			CreateRule(),
			new WebMonitorObservation(Activity: true, Revision: "1842"),
			T0);

		transition.Status.ShouldBe(WebMonitorStatus.Activity);
		transition.SnapshotChanged.ShouldBeTrue();
		transition.Snapshot.ShouldNotBeNull();
		transition.Snapshot.WebPageId.ShouldBe("web-1");
		transition.Snapshot.Url.ShouldBe("https://builds.example/jobs?branch=main");
		transition.Snapshot.Activity.ShouldBe(true);
		transition.Snapshot.Revision.ShouldBe("1842");
		transition.Snapshot.Unread.ShouldBeFalse();
		transition.Snapshot.ObservedAt.ShouldBe(T0);
	}

	[Test]
	public void Observe_compatible_restored_snapshot_participates_in_comparison()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		engine.Restore(CreateSnapshot(
			rule,
			activity: true,
			revision: "1842",
			unread: false));

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "1842"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.Unread);
		transition.Snapshot!.Unread.ShouldBeTrue();
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Restore_projects_retained_unread_before_an_observation()
	{
		var rule = CreateRule();
		WebMonitorStateEngine engine = new("web-1");
		var snapshot = CreateSnapshot(
			rule,
			activity: false,
			revision: "1842",
			unread: true);
		engine.Restore(snapshot);

		var transition = engine.SetPresentationFacts(
			loaded: false,
			selected: false,
			windowVisible: false,
			windowActive: false);

		transition.Status.ShouldBe(WebMonitorStatus.Unread);
		transition.Snapshot.ShouldBeSameAs(snapshot);
		transition.SnapshotChanged.ShouldBeFalse();
	}

	[TestCase("web-page-id-null")]
	[TestCase("web-page-id-blank")]
	[TestCase("web-page-id-mismatch")]
	[TestCase("url-null")]
	[TestCase("url-blank")]
	[TestCase("url-relative")]
	[TestCase("url-fragment")]
	[TestCase("rule-id-null")]
	[TestCase("rule-id-blank")]
	[TestCase("fingerprint-null")]
	[TestCase("fingerprint-blank")]
	[TestCase("observed-at-default")]
	public void Restore_treats_malformed_snapshot_as_absent(string malformation)
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		var malformed = MakeMalformedSnapshot(
			CreateSnapshot(
				rule,
				activity: false,
				revision: "1842",
				unread: true),
			malformation);

		engine.Restore(malformed);

		var loaded = engine.SetPresentationFacts(
			loaded: true,
			selected: false,
			windowVisible: false,
			windowActive: false);
		var unloaded = engine.SetPresentationFacts(
			loaded: false,
			selected: false,
			windowVisible: false,
			windowActive: false);

		loaded.Status.ShouldBe(WebMonitorStatus.None);
		loaded.Snapshot.ShouldBeNull();
		loaded.SnapshotChanged.ShouldBeFalse();
		unloaded.Status.ShouldBe(WebMonitorStatus.Paused);
		unloaded.Snapshot.ShouldBeNull();
		unloaded.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void Observe_rule_without_activity_extractor_treats_activity_as_false()
	{
		var rule = CreateRule(hasActivityExtractor: false);
		var engine = CreateLoadedEngine();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: null, Revision: "3"),
			T0);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: null, Revision: "4"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.Unread);
		transition.Snapshot!.Activity.ShouldBe(false);
		transition.Snapshot.Revision.ShouldBe("4");
		transition.Snapshot.Unread.ShouldBeTrue();
	}

	[Test]
	public void Observe_revision_change_while_activity_true_does_not_create_unread()
	{
		var engine = CreateLoadedEngine();
		var rule = CreateRule();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "3"),
			T0);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "4"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.Activity);
		transition.Snapshot!.Unread.ShouldBeFalse();
		transition.Snapshot.Revision.ShouldBe("4");
	}

	[Test]
	public void Observe_unknown_values_do_not_create_events_or_overwrite_known_values()
	{
		var engine = CreateLoadedEngine();
		var rule = CreateRule();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: null, Revision: null),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.None);
		transition.Snapshot!.Activity.ShouldBe(false);
		transition.Snapshot.Revision.ShouldBe("3");
		transition.Snapshot.Unread.ShouldBeFalse();
		transition.Snapshot.ObservedAt.ShouldBe(T0);
		transition.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void Observe_updates_known_fields_independently()
	{
		var engine = CreateLoadedEngine();
		var rule = CreateRule();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0);

		var activityTransition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: null),
			T0.AddMinutes(1));
		var revisionTransition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: null, Revision: "4"),
			T0.AddMinutes(2));

		activityTransition.Snapshot!.Activity.ShouldBe(true);
		activityTransition.Snapshot.Revision.ShouldBe("3");
		revisionTransition.Status.ShouldBe(WebMonitorStatus.Activity);
		revisionTransition.Snapshot!.Activity.ShouldBe(true);
		revisionTransition.Snapshot.Revision.ShouldBe("4");
		revisionTransition.Snapshot.Unread.ShouldBeFalse();
	}

	[TestCase(true, true, true, false)]
	[TestCase(false, true, true, true)]
	[TestCase(true, false, true, true)]
	[TestCase(true, true, false, true)]
	public void Observe_suppresses_new_unread_only_when_all_active_view_facts_are_true(
		bool selected,
		bool windowVisible,
		bool windowActive,
		bool expectedUnread)
	{
		var rule = CreateRule();
		WebMonitorStateEngine engine = new("web-1");
		engine.SetPresentationFacts(
			loaded: true,
			selected,
			windowVisible,
			windowActive);
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "1842"),
			T0);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "1842"),
			T0.AddMinutes(1));

		transition.Snapshot!.Unread.ShouldBe(expectedUnread);
		transition.Status.ShouldBe(expectedUnread
			? WebMonitorStatus.Unread
			: WebMonitorStatus.None);
	}

	[TestCase(false, true, true)]
	[TestCase(true, false, true)]
	[TestCase(true, true, false)]
	public void SetPresentationFacts_does_not_acknowledge_if_any_active_view_fact_is_false(
		bool selected,
		bool windowVisible,
		bool windowActive)
	{
		(var engine, _) = CreateUnreadEngine();

		var transition = engine.SetPresentationFacts(
			loaded: true,
			selected,
			windowVisible,
			windowActive);

		transition.Status.ShouldBe(WebMonitorStatus.Unread);
		transition.Snapshot!.Unread.ShouldBeTrue();
		transition.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void SetPresentationFacts_acknowledges_and_returns_persistable_snapshot_change()
	{
		(var engine, _) = CreateUnreadEngine();

		var transition = engine.SetPresentationFacts(
			loaded: true,
			selected: true,
			windowVisible: true,
			windowActive: true);

		transition.Status.ShouldBe(WebMonitorStatus.None);
		transition.Snapshot!.Unread.ShouldBeFalse();
		transition.Snapshot.ObservedAt.ShouldBe(T0.AddMinutes(1));
		transition.SnapshotChanged.ShouldBeTrue();

		WebMonitorStateEngine restored = new("web-1");
		restored.Restore(transition.Snapshot);
		restored.SetPresentationFacts(
				loaded: true,
				selected: false,
				windowVisible: true,
				windowActive: true)
			.Status
			.ShouldBe(WebMonitorStatus.None);
	}

	[Test]
	public void SetPresentationFacts_acknowledgement_predicate_does_not_depend_on_loaded()
	{
		(var engine, _) = CreateUnreadEngine();

		var transition = engine.SetPresentationFacts(
			loaded: false,
			selected: true,
			windowVisible: true,
			windowActive: true);

		transition.Status.ShouldBe(WebMonitorStatus.Paused);
		transition.Snapshot!.Unread.ShouldBeFalse();
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Observe_existing_unread_survives_later_activity_and_reappears_after_it_stops()
	{
		(var engine, var rule) = CreateUnreadEngine();

		var activity = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "1842"),
			T0.AddMinutes(2));
		var completed = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "1842"),
			T0.AddMinutes(3));

		activity.Status.ShouldBe(WebMonitorStatus.Activity);
		activity.Snapshot!.Unread.ShouldBeTrue();
		completed.Status.ShouldBe(WebMonitorStatus.Unread);
		completed.Snapshot!.Unread.ShouldBeTrue();
	}

	[Test]
	public void Projection_priority_is_activity_then_unread_then_paused_then_none()
	{
		(var engine, var rule) = CreateUnreadEngine();

		var unreadPaused = engine.SetPresentationFacts(
			loaded: false,
			selected: false,
			windowVisible: false,
			windowActive: false);
		var activityPaused = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "1842"),
			T0.AddMinutes(2));
		WebMonitorStateEngine idle = new("web-2");
		var paused = idle.SetPresentationFacts(
			loaded: false,
			selected: false,
			windowVisible: false,
			windowActive: false);
		var none = idle.SetPresentationFacts(
			loaded: true,
			selected: false,
			windowVisible: false,
			windowActive: false);

		activityPaused.Status.ShouldBe(WebMonitorStatus.Activity);
		unreadPaused.Status.ShouldBe(WebMonitorStatus.Unread);
		paused.Status.ShouldBe(WebMonitorStatus.Paused);
		none.Status.ShouldBe(WebMonitorStatus.None);
	}

	[TestCase("url", false)]
	[TestCase("url", true)]
	[TestCase("rule", false)]
	[TestCase("rule", true)]
	[TestCase("fingerprint", false)]
	[TestCase("fingerprint", true)]
	public void Observe_incompatible_restored_baseline_is_dropped_without_creating_unread(
		string mismatch,
		bool retainedUnread)
	{
		var rule = CreateRule();
		var restored = CreateSnapshot(
			rule,
			activity: true,
			revision: "3",
			unread: retainedUnread);
		restored = mismatch switch
		{
			"url" => restored with { Url = "https://builds.example/other" },
			"rule" => restored with { RuleId = "other-rule" },
			"fingerprint" => restored with { RuleFingerprint = "other-fingerprint" },
			_ => throw new ArgumentOutOfRangeException(nameof(mismatch))
		};
		var engine = CreateLoadedEngine();
		engine.Restore(restored);

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "4"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(retainedUnread
			? WebMonitorStatus.Unread
			: WebMonitorStatus.None);
		transition.Snapshot!.Unread.ShouldBe(retainedUnread);
		transition.Snapshot.Activity.ShouldBe(false);
		transition.Snapshot.Revision.ShouldBe("4");
		transition.Snapshot.RuleId.ShouldBe(rule.Source.Id);
		transition.Snapshot.RuleFingerprint.ShouldBe(rule.Fingerprint);
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Observe_fragment_only_observation_url_keeps_restored_baseline_compatible()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		engine.Restore(CreateSnapshot(
			rule,
			activity: true,
			revision: "1842",
			unread: false));

		var transition = engine.Observe(
			new Uri("https://builds.example/jobs?branch=main#new"),
			rule,
			new WebMonitorObservation(Activity: false, Revision: "1842"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.Unread);
		transition.Snapshot!.Url.ShouldBe(
			"https://builds.example/jobs?branch=main");
	}

	[Test]
	public void Observe_malformed_restored_snapshot_starts_without_retained_unread()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		engine.Restore(CreateSnapshot(
			rule,
			activity: true,
			revision: "3",
			unread: true) with
		{
			Url = "not an absolute URL"
		});

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "4"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.None);
		transition.Snapshot!.Unread.ShouldBeFalse();
		transition.Snapshot.Activity.ShouldBe(false);
		transition.Snapshot.Revision.ShouldBe("4");
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Observe_rule_without_activity_ignores_impossible_restored_activity()
	{
		var rule = CreateRule(hasActivityExtractor: false);
		var engine = CreateLoadedEngine();
		engine.Restore(CreateSnapshot(
			rule,
			activity: true,
			revision: "3",
			unread: false));

		var transition = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: null, Revision: "3"),
			T0.AddMinutes(1));

		transition.Status.ShouldBe(WebMonitorStatus.None);
		transition.Snapshot!.Activity.ShouldBe(false);
		transition.Snapshot.Unread.ShouldBeFalse();
		transition.SnapshotChanged.ShouldBeTrue();
	}

	[Test]
	public void Observe_unchanged_observation_does_not_claim_snapshot_change()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		var baseline = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0);

		var unchanged = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0.AddMinutes(1));

		unchanged.Snapshot.ShouldBeSameAs(baseline.Snapshot);
		unchanged.Snapshot!.ObservedAt.ShouldBe(T0);
		unchanged.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void Observe_rejects_default_timestamp_without_mutating_state()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		var baseline = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0);

		Should.Throw<ArgumentOutOfRangeException>(() => engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "4"),
			default));

		var unchanged = engine.SetPresentationFacts(
			loaded: true,
			selected: false,
			windowVisible: true,
			windowActive: true);
		unchanged.Status.ShouldBe(WebMonitorStatus.None);
		unchanged.Snapshot.ShouldBeSameAs(baseline.Snapshot);
		unchanged.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void SetPresentationFacts_without_acknowledgement_does_not_claim_snapshot_change()
	{
		var rule = CreateRule();
		var engine = CreateLoadedEngine();
		var baseline = engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "3"),
			T0);

		var transition = engine.SetPresentationFacts(
			loaded: false,
			selected: false,
			windowVisible: true,
			windowActive: true);

		transition.Status.ShouldBe(WebMonitorStatus.Paused);
		transition.Snapshot.ShouldBeSameAs(baseline.Snapshot);
		transition.SnapshotChanged.ShouldBeFalse();
	}

	[Test]
	public void Constructor_rejects_blank_page_id() => Should.Throw<ArgumentException>(() => new WebMonitorStateEngine(" "));

	[TestCase("web/page")]
	[TestCase(@"web\page")]
	public void Constructor_rejects_page_id_with_directory_separator(string webPageId) => Should.Throw<ArgumentException>(() => new WebMonitorStateEngine(webPageId));

	private static WebMonitorStateEngine CreateLoadedEngine()
	{
		WebMonitorStateEngine engine = new("web-1");
		engine.SetPresentationFacts(
			loaded: true,
			selected: false,
			windowVisible: true,
			windowActive: true);
		return engine;
	}

	private static (
		WebMonitorStateEngine Engine,
		WebMonitorCompiledRule Rule) CreateUnreadEngine()
	{
		var engine = CreateLoadedEngine();
		var rule = CreateRule();
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: true, Revision: "1842"),
			T0);
		engine.Observe(
			Url,
			rule,
			new WebMonitorObservation(Activity: false, Revision: "1842"),
			T0.AddMinutes(1));
		return (engine, rule);
	}

	private static WebMonitorSnapshot CreateSnapshot(
		WebMonitorCompiledRule rule,
		bool? activity,
		string? revision,
		bool unread) => new WebMonitorSnapshot(
			"web-1",
			"https://builds.example/jobs?branch=main",
			rule.Source.Id,
			rule.Fingerprint,
			activity,
			revision,
			unread,
			T0);

	private static WebMonitorSnapshot MakeMalformedSnapshot(
		WebMonitorSnapshot snapshot,
		string malformation) => malformation switch
		{
			"web-page-id-null" => snapshot with { WebPageId = null! },
			"web-page-id-blank" => snapshot with { WebPageId = " " },
			"web-page-id-mismatch" => snapshot with { WebPageId = "web-2" },
			"url-null" => snapshot with { Url = null! },
			"url-blank" => snapshot with { Url = " " },
			"url-relative" => snapshot with { Url = "/jobs?branch=main" },
			"url-fragment" => snapshot with
			{
				Url = "https://builds.example/jobs?branch=main#overview"
			},
			"rule-id-null" => snapshot with { RuleId = null! },
			"rule-id-blank" => snapshot with { RuleId = " " },
			"fingerprint-null" => snapshot with { RuleFingerprint = null! },
			"fingerprint-blank" => snapshot with { RuleFingerprint = " " },
			"observed-at-default" => snapshot with { ObservedAt = default },
			_ => throw new ArgumentOutOfRangeException(nameof(malformation))
		};

	private static WebMonitorCompiledRule CreateRule(bool hasActivityExtractor = true)
	{
		WebMonitorRule rule = new(
			"build-monitor",
			"Build monitor",
			Enabled: true,
			UrlPattern: @"^https://builds\.example/jobs\?branch=main$",
			PollIntervalSeconds: 30,
			Activity: hasActivityExtractor
				? new WebMonitorExtractor(
					".running",
					WebMonitorValueSource.Exists,
					AttributeName: null,
					MatchPattern: null,
					CaptureGroup: null)
				: null,
			Revision: new WebMonitorExtractor(
				".revision",
				WebMonitorValueSource.Text,
				AttributeName: null,
				MatchPattern: null,
				CaptureGroup: null));
		return WebMonitorRuleCompiler.Compile(rule);
	}
}