using Pact.Core.Agents;
using Pact.Core.Sessions;
using Pact.Presentation.Services;
using Pact.Presentation.ViewModels;

namespace Pact.Presentation.Tests.Services;

public sealed class TerminalTabStatusCoordinatorTests
{
	private static readonly DateTimeOffset T0 = new(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);

	[Test]
	public void Registration_projects_initial_lifecycle_and_cached_global_facts()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		coordinator.SetSelectedSession("session-1", T0);
		coordinator.SetWindowFacts(true, true, T0);
		var session = CreateSession(status: SessionStatus.Stopped);

		coordinator.RegisterSession(session);

		session.Indicator.ShouldBe(TerminalTabIndicator.Paused);
		coordinator.OnLifecycleChanged("session-1", SessionStatus.Running, T0.AddSeconds(1));
		coordinator.OnUserInput("session-1", "\r", T0.AddSeconds(2));
		coordinator.OnScreenSnapshot("session-1", "Worked for 1s", T0.AddSeconds(3));
		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Selection_updates_previous_and_next_engine()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		coordinator.SetWindowFacts(true, true, T0);
		var first = CreateSession("first");
		var second = CreateSession("second");
		coordinator.RegisterSession(first);
		coordinator.RegisterSession(second);
		coordinator.OnUserInput("first", "\r", T0);
		coordinator.OnScreenSnapshot("first", "Worked for 1s", T0.AddSeconds(1));
		coordinator.OnUserInput("second", "\r", T0);
		coordinator.OnScreenSnapshot("second", "Worked for 1s", T0.AddSeconds(1));
		first.Indicator.ShouldBe(TerminalTabIndicator.Unread);
		second.Indicator.ShouldBe(TerminalTabIndicator.Unread);

		coordinator.SetSelectedSession("first", T0.AddSeconds(2));

		first.Indicator.ShouldBe(TerminalTabIndicator.None);
		second.Indicator.ShouldBe(TerminalTabIndicator.Unread);

		coordinator.SetSelectedSession("second", T0.AddSeconds(3));

		second.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Metadata_only_evidence_dispatches_diagnostics_without_changing_indicator()
	{
		List<Action> dispatched = [];
		TerminalTabStatusCoordinator coordinator = new(dispatched.Add);
		var session = CreateSession();
		coordinator.RegisterSession(session);
		dispatched.Count.ShouldBe(2);
		foreach (var action in dispatched)
		{
			action();
		}

		dispatched.Clear();

		coordinator.OnUserInput("session-1", "abc", T0);
		dispatched.ShouldBeEmpty();
		coordinator.OnViewportChanged("session-1", 120, 40, T0.AddSeconds(1));

		dispatched.ShouldHaveSingleItem()();
		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Unknown_and_removed_sessions_ignore_late_events()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession();
		coordinator.RegisterSession(session);
		coordinator.RemoveSession("session-1");

		coordinator.OnUserInput("missing", "\r", T0);
		coordinator.OnUserInput("session-1", "\r", T0);
		coordinator.OnScreenSnapshot("session-1", "Worked for 1s", T0.AddSeconds(1));

		session.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Snapshot_routes_to_registered_engine_with_kind_profile()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession(kind: AgentKind.Claude);
		coordinator.RegisterSession(session);
		coordinator.OnUserInput("session-1", "run\r", T0);

		coordinator.OnScreenSnapshot(
			"session-1",
			"Worked for 2m\n? for shortcuts",
			T0.AddSeconds(5));

		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);
		session.StatusDescription.ShouldBe("Worked for 2m");
	}

	[Test]
	public void Unstable_snapshot_does_not_complete_activity_through_coordinator()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession(kind: AgentKind.Pwsh);
		coordinator.RegisterSession(session);
		coordinator.OnUserInput("session-1", "dir\r", T0);

		coordinator.OnScreenSnapshot("session-1", @"PS D:\Work> ", T0.AddSeconds(1), stable: false);

		session.Indicator.ShouldBe(TerminalTabIndicator.Busy);

		coordinator.OnScreenSnapshot("session-1", @"PS D:\Work> ", T0.AddSeconds(2));

		session.Indicator.ShouldBe(TerminalTabIndicator.Unread);
	}

	[Test]
	public void Snapshot_for_unknown_session_is_ignored()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());

		coordinator.OnScreenSnapshot("missing", "text", T0);
	}

	[Test]
	public void Try_get_screen_state_returns_the_registered_session_state()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession("session-a", kind: AgentKind.Claude);
		coordinator.RegisterSession(session);

		coordinator.OnScreenSnapshot(
			"session-a",
			ClaudeScreenWithAssistantText("done here"),
			T0);

		coordinator.TryGetScreenState("session-a", out var state).ShouldBeTrue();
		state.Screen.ShouldContain("done here");
		state.LastMessage.ShouldBe("done here");
		state.LastMessageIsCurrent.ShouldBeTrue();
	}

	[Test]
	public void Try_get_screen_state_projects_pending_input_and_activity_cycle()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession("session-a", kind: AgentKind.Claude);
		coordinator.RegisterSession(session);
		coordinator.OnUserInput("session-a", "\r", T0);

		coordinator.OnScreenSnapshot(
			"session-a",
			"Some options selectors\nEnter to select\n"
			+ "──────────────────────────────\n"
			+ "> pending answer\n"
			+ "──────────────────────────────",
			T0.AddSeconds(1));

		coordinator.TryGetScreenState("session-a", out var state).ShouldBeTrue();
		state.InputRequested.ShouldBeTrue();
		state.StatusLine.ShouldBe("Enter to select");
		state.PromptIsEmpty.ShouldBe(true);
		state.ActivityEpoch.ShouldBeGreaterThan(0);
		state.IsBusy.ShouldBeFalse();
	}

	[Test]
	public void Try_get_screen_state_returns_false_after_session_removal()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession("session-a");
		coordinator.RegisterSession(session);
		coordinator.OnScreenSnapshot("session-a", "screen", T0);

		coordinator.RemoveSession("session-a");

		coordinator.TryGetScreenState("session-a", out _).ShouldBeFalse();
	}

	[Test]
	public void Diagnostics_are_relayed_as_complete_snapshots_for_the_registered_session()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var session = CreateSession("session-a", kind: AgentKind.Claude);
		List<TerminalClassifierDiagnostics> changes = [];
		coordinator.DiagnosticsChanged += (_, args) => changes.Add(args.Diagnostics);
		coordinator.RegisterSession(session);

		coordinator.OnViewportChanged("session-a", 215, 37, T0);

		changes.Count.ShouldBe(2);
		changes[^1].SessionId.ShouldBe("session-a");
		changes[^1].Columns.ShouldBe(215);
		changes[^1].Rows.ShouldBe(37);
		coordinator.TryGetDiagnostics("session-a", out var current).ShouldBeTrue();
		current.ShouldBe(changes[^1]);
	}

	[Test]
	public void Queued_old_engine_change_cannot_update_replacement_registration()
	{
		List<Action> dispatched = [];
		TerminalTabStatusCoordinator coordinator = new(dispatched.Add);
		var oldSession = CreateSession();
		coordinator.RegisterSession(oldSession);
		dispatched.Count.ShouldBe(2);
		foreach (var action in dispatched)
		{
			action();
		}

		dispatched.Clear();
		coordinator.OnUserInput("session-1", "\r", T0);
		var staleUpdates = dispatched.ToArray();
		staleUpdates.Length.ShouldBe(2);
		dispatched.Clear();
		coordinator.RemoveSession("session-1");
		var replacement = CreateSession();
		coordinator.RegisterSession(replacement);

		foreach (var staleUpdate in staleUpdates)
		{
			staleUpdate();
		}

		oldSession.Indicator.ShouldBe(TerminalTabIndicator.None);
		replacement.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	[Test]
	public void Indicator_change_updates_only_matching_session_and_busy_timestamp()
	{
		TerminalTabStatusCoordinator coordinator = new(action => action());
		var first = CreateSession("first");
		var second = CreateSession("second");
		coordinator.RegisterSession(first);
		coordinator.RegisterSession(second);

		coordinator.OnUserInput("first", "\r", T0);

		first.Indicator.ShouldBe(TerminalTabIndicator.Busy);
		first.BusySince.ShouldBe(T0);
		second.Indicator.ShouldBe(TerminalTabIndicator.None);
	}

	private static SessionViewModel CreateSession(
		string id = "session-1",
		SessionStatus status = SessionStatus.Running,
		AgentKind kind = AgentKind.Codex) => new SessionViewModel(new SessionRecord(
			id,
			kind,
			id,
			@"D:\Work",
			"codex",
			null,
			status,
			T0,
			T0));

	private static string ClaudeScreenWithAssistantText(string message) =>
		$"● {message}\n✻ Worked for 3s\n╭──╮\n│ > │\n╰──╯";
}
