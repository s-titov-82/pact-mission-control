using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;
using Pact.Core.Sessions;

namespace Pact.Core.Tests.Sessions;

public sealed class TerminalTabStatusEngineTests
{
	private static readonly DateTimeOffset T0 = new(2026, 7, 18, 9, 0, 0, TimeSpan.Zero);

	private sealed class ScriptedProfile : IAgentScreenProfile
	{
		public TerminalScreenVerdict Verdict { get; set; } = new TerminalScreenVerdict(TerminalScreenVerdictState.Unknown, string.Empty);
		public string? LastScreen { get; private set; }

		public TerminalScreenVerdict Classify(string screen)
		{
			LastScreen = screen;
			return Verdict;
		}
	}

	private static TerminalTabStatusEngine CreateEngine(
		SessionStatus lifecycle = SessionStatus.Running,
		bool selected = false,
		bool windowVisible = false,
		bool windowActive = false,
		IAgentScreenProfile? profile = null) =>
		new("session-1", AgentKind.Claude, profile ?? QuiescenceScreenProfile.Instance,
			lifecycle, selected, windowVisible, windowActive);

	[Test]
	[TestCase(SessionStatus.Stopped, TerminalTabIndicator.Paused)]
	[TestCase(SessionStatus.Exited, TerminalTabIndicator.Paused)]
	[TestCase(SessionStatus.Failed, TerminalTabIndicator.Failed)]
	[TestCase(SessionStatus.Starting, TerminalTabIndicator.None)]
	[TestCase(SessionStatus.Running, TerminalTabIndicator.None)]
	public void Construction_maps_initial_lifecycle(SessionStatus status, TerminalTabIndicator expected)
	{
		var engine = CreateEngine(status);

		engine.CurrentIndicator.ShouldBe(expected);
		engine.LastEventKind.ShouldBeNull();
		engine.LastEventAt.ShouldBeNull();
	}

	[Test]
	public void Construction_rejects_blank_session_id() => Should.Throw<ArgumentException>(() => new TerminalTabStatusEngine(
																	" ", AgentKind.Codex, QuiescenceScreenProfile.Instance,
																	SessionStatus.Running, false, true, true));

	[Test]
	public void Construction_rejects_null_profile() => Should.Throw<ArgumentNullException>(() => new TerminalTabStatusEngine(
																"session-1", AgentKind.Codex, null!, SessionStatus.Running, false, true, true));

	[Test]
	public void Resume_start_becomes_busy_and_normal_start_does_not()
	{
		var normal = CreateEngine();
		var resume = CreateEngine();

		normal.OnSessionStarted(TerminalStartMode.Normal, T0);
		resume.OnSessionStarted(TerminalStartMode.Resume, T0.AddSeconds(1));

		normal.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
		normal.ActivityInProgress.ShouldBeFalse();
		resume.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		resume.ActivityInProgress.ShouldBeTrue();
		resume.ActivityStartedAt.ShouldBe(T0.AddSeconds(1));
	}

	[Test]
	public void Only_input_containing_carriage_return_starts_activity()
	{
		var engine = CreateEngine();

		engine.OnUserInput("abc", T0);

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
		engine.LastUserInputAt.ShouldBe(T0);
		engine.LastUserCharacter.ShouldBe('c');

		engine.OnUserInput("\r", T0.AddSeconds(1));

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.ActivityStartedAt.ShouldBe(T0.AddSeconds(1));
	}

	[Test]
	public void A_submit_into_a_busy_session_starts_a_new_cycle()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("startup spinner", T0);
		var epochWhileBusy = engine.ActivityEpoch;

		engine.OnUserInput("\r", T0.AddSeconds(1));

		engine.ActivityEpoch.ShouldBeGreaterThan(epochWhileBusy);
		engine.ActivityStartedAt.ShouldBe(T0.AddSeconds(1));
	}

	[Test]
	public void Typing_without_a_carriage_return_starts_nothing()
	{
		var engine = CreateEngine();
		var epoch = engine.ActivityEpoch;

		engine.OnUserInput("abc", T0);

		engine.ActivityEpoch.ShouldBe(epoch);
	}

	[Test]
	public void Screen_evidence_does_not_restart_a_running_cycle()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("busy", T0);
		var epoch = engine.ActivityEpoch;

		engine.OnScreenSnapshot("still busy", T0.AddSeconds(1));

		engine.ActivityEpoch.ShouldBe(epoch);
	}

	[Test]
	public void Empty_input_records_observation_without_creating_input_evidence()
	{
		var engine = CreateEngine();

		engine.OnUserInput(string.Empty, T0);

		engine.LastEventKind.ShouldBe(TerminalTabEventKind.UserInput);
		engine.LastEventAt.ShouldBe(T0);
		engine.LastUserInputAt.ShouldBeNull();
		engine.LastUserCharacter.ShouldBeNull();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Snapshot_busy_verdict_starts_activity()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty) };
		var engine = CreateEngine(profile: profile);

		engine.OnScreenSnapshot("esc to interrupt", T0);

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.ActivityStartedAt.ShouldBe(T0);
		engine.LastScreenSnapshot.ShouldBe("esc to interrupt");
	}

	[Test]
	public void An_empty_composer_ends_a_startup_activity_without_marking_it_unread()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("startup spinner", T0);

		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Unknown,
			string.Empty,
			string.Empty,
			PromptIsEmpty: true);
		engine.OnScreenSnapshot("idle composer", T0.AddSeconds(1));

		engine.ActivityInProgress.ShouldBeFalse();
		engine.HasUnreadCompletion.ShouldBeFalse();
		engine.CurrentStatus.PromptIsEmpty.ShouldBe(true);
	}

	[Test]
	public void A_completion_marker_still_marks_the_tab_unread()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("busy", T0);

		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Done,
			"Worked for 3s",
			string.Empty,
			PromptIsEmpty: true);
		engine.OnScreenSnapshot("finished", T0.AddSeconds(1));

		engine.ActivityInProgress.ShouldBeFalse();
		engine.HasUnreadCompletion.ShouldBeTrue();
	}

	[Test]
	public void A_composer_holding_text_never_ends_an_activity()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("busy", T0);

		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Unknown,
			string.Empty,
			string.Empty,
			PromptIsEmpty: false);
		engine.OnScreenSnapshot("queued text", T0.AddSeconds(1));

		engine.ActivityInProgress.ShouldBeTrue();
	}

	[Test]
	public void Diagnostics_snapshot_contains_stable_classifier_and_delivery_facts()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.InputRequested,
				"Approve changes?",
				PromptIsEmpty: false)
		};
		var engine = CreateEngine(profile: profile);
		engine.OnViewportChanged(215, 37, T0);

		engine.OnScreenSnapshot("question", T0.AddSeconds(1));

		var diagnostics = engine.CurrentDiagnostics;
		diagnostics.SessionId.ShouldBe("session-1");
		diagnostics.TerminalKind.ShouldBe(AgentKind.Claude);
		diagnostics.LifecycleStatus.ShouldBe(SessionStatus.Running);
		diagnostics.VerdictState.ShouldBe(TerminalScreenVerdictState.InputRequested);
		diagnostics.VerdictDescription.ShouldBe("Approve changes?");
		diagnostics.Indicator.ShouldBe(TerminalTabIndicator.InputRequested);
		diagnostics.PromptIsEmpty.ShouldBe(false);
		diagnostics.InputRequested.ShouldBeTrue();
		diagnostics.ActivityInProgress.ShouldBeFalse();
		diagnostics.ActivityEpoch.ShouldBe(0);
		diagnostics.Columns.ShouldBe(215);
		diagnostics.Rows.ShouldBe(37);
		diagnostics.LastClassificationAt.ShouldBe(T0.AddSeconds(1));
	}

	[Test]
	public void Prompt_only_change_raises_diagnostics_without_indicator_change()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Unknown,
				PromptIsEmpty: true)
		};
		var engine = CreateEngine(profile: profile);
		List<TerminalClassifierDiagnostics> changes = [];
		engine.DiagnosticsChanged += (_, args) => changes.Add(args.Diagnostics);
		engine.OnScreenSnapshot("empty", T0);

		profile.Verdict = profile.Verdict with { PromptIsEmpty = false };
		engine.OnScreenSnapshot("has text", T0.AddSeconds(1));

		changes.Count.ShouldBe(2);
		changes[0].Indicator.ShouldBe(TerminalTabIndicator.None);
		changes[0].PromptIsEmpty.ShouldBe(true);
		changes[1].Indicator.ShouldBe(TerminalTabIndicator.None);
		changes[1].PromptIsEmpty.ShouldBe(false);
	}

	[Test]
	public void Input_request_ends_activity_and_shows_its_own_indicator()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Cogitating")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("busy", T0);

		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.InputRequested,
			AgentScreenProfileBase.TrustPromptDescription);
		engine.OnScreenSnapshot("trust", T0.AddSeconds(1));

		var status = engine.CurrentStatus;
		status.InputRequested.ShouldBeTrue();
		status.Indicator.ShouldBe(TerminalTabIndicator.InputRequested);
		status.StatusLine.ShouldBe(AgentScreenProfileBase.TrustPromptDescription);
		engine.ActivityInProgress.ShouldBeFalse();
	}

	[Test]
	public void A_mid_repaint_snapshot_never_parks_the_tab_on_a_question()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.InputRequested, "Approve?")
		};
		var engine = CreateEngine(profile: profile);

		engine.OnScreenSnapshot("half-drawn", T0, stable: false);

		engine.CurrentStatus.InputRequested.ShouldBeFalse();
	}

	[Test]
	[TestCase(TerminalScreenVerdictState.Done, "Worked for 3s")]
	[TestCase(TerminalScreenVerdictState.Unknown, "")]
	[TestCase(TerminalScreenVerdictState.Busy, "Cogitating")]
	public void The_next_settled_verdict_clears_the_question_and_its_status_line(
		TerminalScreenVerdictState next,
		string description)
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.InputRequested, "Approve?")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("question", T0);

		profile.Verdict = new TerminalScreenVerdict(next, description);
		engine.OnScreenSnapshot("answered", T0.AddSeconds(1));

		var status = engine.CurrentStatus;
		status.InputRequested.ShouldBeFalse();
		status.Indicator.ShouldNotBe(TerminalTabIndicator.InputRequested);
		status.StatusLine.ShouldNotBe("Approve?");
		engine.CurrentDescription.ShouldBe(description);
	}

	[Test]
	public void A_dead_process_is_not_waiting_for_anyone()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.InputRequested, "Approve?")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("question", T0);

		engine.SetLifecycleStatus(SessionStatus.Exited, T0.AddSeconds(1));

		engine.CurrentStatus.InputRequested.ShouldBeFalse();
	}

	[Test]
	public void Snapshot_done_verdict_ends_activity_and_marks_unread_in_background()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty) };
		var engine = CreateEngine(profile: profile);
		engine.OnUserInput("run\r", T0);
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty);

		engine.OnScreenSnapshot("Worked for 2m", T0.AddSeconds(30));

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Unread);
		engine.ActivityInProgress.ShouldBeFalse();
	}

	[Test]
	public void Unstable_snapshot_busy_verdict_starts_activity()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty) };
		var engine = CreateEngine(profile: profile);

		engine.OnScreenSnapshot("* Thinking (esc to interrupt)", T0, stable: false);

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.ActivityStartedAt.ShouldBe(T0);
	}

	[Test]
	public void Unstable_snapshot_done_verdict_does_not_end_activity()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty) };
		var engine = CreateEngine(profile: profile);
		engine.OnUserInput("run\r", T0);

		engine.OnScreenSnapshot("half-drawn frame without busy marker", T0.AddSeconds(1), stable: false);

		engine.ActivityInProgress.ShouldBeTrue();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.HasUnreadCompletion.ShouldBeFalse();
	}

	[Test]
	public void Snapshot_done_verdict_while_idle_is_noop()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty) };
		var engine = CreateEngine(profile: profile);

		engine.OnScreenSnapshot("PS D:\\> ", T0);

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
		engine.HasUnreadCompletion.ShouldBeFalse();
	}

	[Test]
	public void Snapshot_unknown_verdict_changes_nothing()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty) };
		var engine = CreateEngine(profile: profile);
		engine.OnUserInput("run\r", T0);
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Unknown, string.Empty);

		engine.OnScreenSnapshot("resize redraw", T0.AddSeconds(1));

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.LastScreenSnapshot.ShouldBe("resize redraw");
	}

	[Test]
	public void Snapshot_ignored_when_lifecycle_not_running()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty) };
		var engine = CreateEngine(lifecycle: SessionStatus.Exited, profile: profile);

		engine.OnScreenSnapshot("esc to interrupt", T0);

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Paused);
		profile.LastScreen.ShouldBeNull();
	}

	[Test]
	public void Stable_snapshot_retains_message_from_the_classified_verdict()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Busy,
				"Working",
				"half way through")
		};
		var engine = CreateEngine(profile: profile);

		engine.OnScreenSnapshot("screen text", T0, stable: true);

		engine.LastMessage.ShouldBe("half way through");
		engine.LastMessageIsCurrent.ShouldBeTrue();
		engine.LastStableScreen.ShouldBe("screen text");
	}

	[Test]
	public void Unstable_snapshot_is_ignored_for_screen_retention()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Busy,
				"Working",
				"settled")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("settled screen", T0, stable: true);

		engine.OnScreenSnapshot("mid repaint", T0.AddSeconds(1), stable: false);

		engine.LastStableScreen.ShouldBe("settled screen");
		engine.LastMessage.ShouldBe("settled");
		engine.LastMessageIsCurrent.ShouldBeTrue();
	}

	[Test]
	public void Stable_snapshot_without_a_message_keeps_the_previous_message_as_stale()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Busy,
				"Working",
				"first")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("one", T0, stable: true);
		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Busy,
			"Working",
			string.Empty);

		engine.OnScreenSnapshot("two", T0.AddSeconds(1), stable: true);

		engine.LastMessage.ShouldBe("first");
		engine.LastMessageIsCurrent.ShouldBeFalse();
		engine.LastStableScreen.ShouldBe("two");
	}

	[Test]
	public void New_recognised_message_becomes_current_again()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Busy,
				"Working",
				"first")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("one", T0, stable: true);
		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Busy,
			"Working",
			string.Empty);
		engine.OnScreenSnapshot("two", T0.AddSeconds(1), stable: true);
		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Done,
			"Worked for 3s",
			"second");

		engine.OnScreenSnapshot("three", T0.AddSeconds(2), stable: true);

		engine.LastMessage.ShouldBe("second");
		engine.LastMessageIsCurrent.ShouldBeTrue();
	}

	[Test]
	public void Non_running_process_retains_no_screen_state()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(
				TerminalScreenVerdictState.Busy,
				"Working",
				"ignored")
		};
		var engine = CreateEngine(lifecycle: SessionStatus.Stopped, profile: profile);

		engine.OnScreenSnapshot("screen", T0, stable: true);

		engine.LastMessage.ShouldBeEmpty();
		engine.LastStableScreen.ShouldBeEmpty();
	}

	[Test]
	public void Snapshot_done_in_selected_visible_active_window_acknowledges_immediately()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty) };
		var engine = CreateEngine(
			selected: true, windowVisible: true, windowActive: true, profile: profile);
		engine.OnUserInput("run\r", T0);

		engine.OnScreenSnapshot("done screen", T0.AddSeconds(10));

		engine.HasUnreadCompletion.ShouldBeFalse();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Snapshot_null_throws()
	{
		var engine = CreateEngine();

		Should.Throw<ArgumentNullException>(() => engine.OnScreenSnapshot(null!, T0));
	}

	[Test]
	[TestCase(false, true, true)]
	[TestCase(true, false, true)]
	[TestCase(true, true, false)]
	public void Completion_remains_unread_when_any_acknowledgement_fact_is_false(
		bool selected,
		bool visible,
		bool active)
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty) };
		var engine = CreateEngine(
			selected: selected, windowVisible: visible, windowActive: active, profile: profile);
		engine.OnUserInput("run\r", T0);

		engine.OnScreenSnapshot("done screen", T0.AddSeconds(1));

		engine.HasUnreadCompletion.ShouldBeTrue();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Unread);
	}

	[Test]
	[TestCase("select")]
	[TestCase("window")]
	public void Any_event_order_clears_unread_once_all_acknowledgement_facts_are_true(string lastFact)
	{
		var selected = lastFact != "select";
		var visible = lastFact != "window";
		var active = lastFact != "window";
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty) };
		var engine = CreateEngine(
			selected: selected, windowVisible: visible, windowActive: active, profile: profile);
		engine.OnUserInput("run\r", T0);
		engine.OnScreenSnapshot("done screen", T0.AddSeconds(1));
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Unread);

		if (lastFact == "select")
		{
			engine.SetSelected(true, T0.AddSeconds(2));
		}

		if (lastFact == "window")
		{
			engine.SetWindowFacts(true, true, T0.AddSeconds(2));
		}

		engine.HasUnreadCompletion.ShouldBeFalse();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Lifecycle_overrides_activity_and_clears_it()
	{
		var engine = CreateEngine(selected: false);
		engine.OnUserInput("\r", T0);

		engine.SetLifecycleStatus(SessionStatus.Failed, T0.AddSeconds(1));

		engine.ActivityInProgress.ShouldBeFalse();
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Failed);

		engine.SetLifecycleStatus(SessionStatus.Exited, T0.AddSeconds(2));
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Paused);
	}

	[Test]
	public void Viewport_updates_evidence_only_and_rejects_invalid_dimensions()
	{
		var engine = CreateEngine();

		engine.OnViewportChanged(120, 40, T0);

		engine.LastColumns.ShouldBe(120);
		engine.LastRows.ShouldBe(40);
		engine.LastEventKind.ShouldBe(TerminalTabEventKind.ViewportChanged);
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.None);
		Should.Throw<ArgumentOutOfRangeException>(() => engine.OnViewportChanged(0, 40, T0));
		Should.Throw<ArgumentOutOfRangeException>(() => engine.OnViewportChanged(120, 0, T0));
	}

	[Test]
	public void Indicator_changed_fires_only_for_enum_changes_and_allows_reentry()
	{
		ScriptedProfile profile = new() { Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Unknown, string.Empty) };
		var engine = CreateEngine(profile: profile);
		List<TerminalTabIndicator> changes = [];
		engine.IndicatorChanged += (_, args) =>
		{
			changes.Add(args.Indicator);
			_ = engine.LastScreenSnapshot;
			if (args.Indicator == TerminalTabIndicator.Busy)
			{
				engine.SetWindowFacts(false, false, T0.AddSeconds(10));
			}
		};

		engine.OnUserInput("a", T0);
		engine.OnUserInput("\r", T0.AddSeconds(1));
		engine.OnScreenSnapshot("still working", T0.AddSeconds(2));
		engine.SetLifecycleStatus(SessionStatus.Stopped, T0.AddSeconds(3));

		changes.ShouldBe([TerminalTabIndicator.Busy, TerminalTabIndicator.Paused]);
	}

	[Test]
	public void Unknown_description_updates_text_without_changing_status()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Thinking")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("busy", T0);
		List<TerminalTabIndicatorChangedEventArgs> changes = [];
		engine.IndicatorChanged += (_, args) => changes.Add(args);
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Unknown, "Scrolled");

		engine.OnScreenSnapshot("unknown", T0.AddSeconds(1));
		profile.Verdict = new TerminalScreenVerdict(
			TerminalScreenVerdictState.Unknown,
			string.Empty);
		engine.OnScreenSnapshot("unknown without text", T0.AddSeconds(2));

		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Busy);
		engine.CurrentDescription.ShouldBe("Scrolled");
		changes.ShouldHaveSingleItem().Description.ShouldBe("Scrolled");
	}

	[Test]
	public void Same_status_replaces_description_only_when_new_text_is_non_empty()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Thinking")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnScreenSnapshot("first", T0);
		List<TerminalTabIndicatorChangedEventArgs> changes = [];
		engine.IndicatorChanged += (_, args) => changes.Add(args);
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Working");

		engine.OnScreenSnapshot("second", T0.AddSeconds(1));
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, string.Empty);
		engine.OnScreenSnapshot("third", T0.AddSeconds(2));

		engine.CurrentDescription.ShouldBe("Working");
		changes.ShouldHaveSingleItem().Description.ShouldBe("Working");
	}

	[Test]
	public void New_status_replaces_description_even_when_new_text_is_empty()
	{
		ScriptedProfile profile = new()
		{
			Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Busy, "Thinking")
		};
		var engine = CreateEngine(profile: profile);
		engine.OnUserInput("run\r", T0);
		engine.OnScreenSnapshot("busy", T0.AddSeconds(1));
		List<TerminalTabIndicatorChangedEventArgs> changes = [];
		engine.IndicatorChanged += (_, args) => changes.Add(args);
		profile.Verdict = new TerminalScreenVerdict(TerminalScreenVerdictState.Done, string.Empty);

		engine.OnScreenSnapshot("done", T0.AddSeconds(2));

		engine.CurrentDescription.ShouldBe(string.Empty);
		changes.ShouldHaveSingleItem().Description.ShouldBe(string.Empty);
		engine.CurrentIndicator.ShouldBe(TerminalTabIndicator.Unread);
	}
}
