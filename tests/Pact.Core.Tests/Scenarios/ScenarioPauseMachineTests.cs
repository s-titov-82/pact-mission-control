using Pact.Core.Scenarios;

namespace Pact.Core.Tests.Scenarios;

public sealed class ScenarioPauseMachineTests
{
	private static readonly ScenarioPauseState Running = ScenarioPauseState.Initial;
	private static readonly ScenarioPauseState Requested = Running with { PauseRequested = true };
	private static readonly ScenarioPauseState Manual = Running with
	{
		State = ScenarioRunState.Paused,
		PauseKind = ScenarioPauseKind.Manual
	};
	private static readonly ScenarioPauseState Attention = Running with
	{
		State = ScenarioRunState.Paused,
		PauseKind = ScenarioPauseKind.Attention,
		AttentionReason = ScenarioAttentionReason.Unconfirmed,
		StuckSessionId = "s1"
	};

	[Test]
	public void Pause_request_while_running_is_retained_and_interrupts_the_wait()
	{
		var t = ScenarioPauseMachine.RequestPause(Running);

		t.State.ShouldBe(Requested);
		t.Status.ShouldBe(ScenarioPauseRequestStatus.Requested);
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.InterruptWait);
	}

	[Test]
	public void A_second_pause_request_changes_nothing()
	{
		var t = ScenarioPauseMachine.RequestPause(Requested);

		t.State.ShouldBe(Requested);
		t.Status.ShouldBe(ScenarioPauseRequestStatus.Unchanged);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void A_pending_request_pauses_at_the_boundary()
	{
		var t = ScenarioPauseMachine.ReachBoundary(Requested);

		t.State.ShouldBe(Manual);
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalManualPause);
	}

	[Test]
	public void A_boundary_without_a_request_changes_nothing()
	{
		var t = ScenarioPauseMachine.ReachBoundary(Running);

		t.State.ShouldBe(Running);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void A_pending_request_wins_over_attention()
	{
		var t = ScenarioPauseMachine.NeedAttention(Requested, "s1", ScenarioAttentionReason.Unconfirmed);

		t.State.ShouldBe(Manual);
		t.State.AttentionReason.ShouldBeNull();
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalManualPause);
	}

	[Test]
	public void Attention_while_running_pauses_with_the_stuck_session_and_reason()
	{
		var t = ScenarioPauseMachine.NeedAttention(Running, "s1", ScenarioAttentionReason.NothingSent);

		t.State.ShouldBe(Attention with { AttentionReason = ScenarioAttentionReason.NothingSent });
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalAttention);
	}

	[Test]
	public void Attention_while_stopping_after_the_step_pauses_and_keeps_the_stop()
	{
		var stopping = Running with { State = ScenarioRunState.StoppingAfterStep, StopRequested = true };

		var t = ScenarioPauseMachine.NeedAttention(stopping, "s1", ScenarioAttentionReason.Watchdog);

		t.State.ShouldBe(Attention with { AttentionReason = ScenarioAttentionReason.Watchdog, StopRequested = true });
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalAttention);
	}

	[Test]
	public void Attention_again_for_the_same_reason_changes_nothing()
	{
		var t = ScenarioPauseMachine.NeedAttention(Attention, "s2", ScenarioAttentionReason.Unconfirmed);

		t.State.ShouldBe(Attention);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Attention_for_another_reason_replaces_the_reason_and_keeps_the_session()
	{
		var t = ScenarioPauseMachine.NeedAttention(Attention, "s1", ScenarioAttentionReason.Watchdog);

		t.State.ShouldBe(Attention with { AttentionReason = ScenarioAttentionReason.Watchdog });
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.JournalAttention);
	}

	[Test]
	public void Attention_during_a_manual_pause_changes_nothing()
	{
		var t = ScenarioPauseMachine.NeedAttention(Manual, "s1", ScenarioAttentionReason.Watchdog);

		t.State.ShouldBe(Manual);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Pause_request_during_an_attention_pause_escalates_to_manual()
	{
		var t = ScenarioPauseMachine.RequestPause(Attention);

		t.State.ShouldBe(Manual);
		t.Status.ShouldBe(ScenarioPauseRequestStatus.Escalated);
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed);
	}

	[Test]
	public void Pause_request_during_a_manual_pause_changes_nothing()
	{
		var t = ScenarioPauseMachine.RequestPause(Manual);

		t.State.ShouldBe(Manual);
		t.Status.ShouldBe(ScenarioPauseRequestStatus.Unchanged);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void A_successful_delivery_ends_an_attention_pause()
	{
		var t = ScenarioPauseMachine.DeliverySucceeded(Attention);

		t.State.ShouldBe(Running);
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.SignalResume);
	}

	[Test]
	public void A_successful_delivery_keeps_a_manual_pause()
	{
		var t = ScenarioPauseMachine.DeliverySucceeded(Manual);

		t.State.ShouldBe(Manual);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void A_response_ends_an_attention_pause()
	{
		var t = ScenarioPauseMachine.ResponseArrived(Attention);

		t.State.ShouldBe(Running);
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.SignalResume);
	}

	[Test]
	public void A_response_during_a_manual_pause_is_held()
	{
		var t = ScenarioPauseMachine.ResponseArrived(Manual);

		t.State.ShouldBe(Manual);
		t.Effects.ShouldBe(ScenarioPauseEffects.HoldResponse);
	}

	[Test]
	public void A_response_while_running_changes_nothing()
	{
		var t = ScenarioPauseMachine.ResponseArrived(Running);

		t.State.ShouldBe(Running);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Resume_ends_either_pause_and_keeps_a_pending_request()
	{
		foreach (var paused in new[] { Manual, Attention, Manual with { PauseRequested = true } })
		{
			var t = ScenarioPauseMachine.Resume(paused);

			t.State.ShouldBe(Running with { PauseRequested = paused.PauseRequested });
			t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.SignalResume);
		}
	}

	[Test]
	public void Resume_while_running_is_rejected()
	{
		var t = ScenarioPauseMachine.Resume(Running);

		t.State.ShouldBe(Running);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Soft_stop_while_running_stops_after_the_step()
	{
		var t = ScenarioPauseMachine.RequestSoftStop(Running);

		t.State.ShouldBe(Running with { State = ScenarioRunState.StoppingAfterStep, StopRequested = true });
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed);
	}

	[Test]
	public void Soft_stop_during_a_manual_pause_is_recorded_and_the_pause_stays()
	{
		var t = ScenarioPauseMachine.RequestSoftStop(Manual);

		t.State.ShouldBe(Manual with { StopRequested = true });
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[TestCase(ScenarioRunState.StoppingAfterStep)]
	[TestCase(ScenarioRunState.Completed)]
	public void Pause_request_when_stopping_or_finished_is_not_pausable(ScenarioRunState state)
	{
		var stopped = Running with { State = state };

		var t = ScenarioPauseMachine.RequestPause(stopped);

		t.State.ShouldBe(stopped);
		t.Status.ShouldBe(ScenarioPauseRequestStatus.NotPausable);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Finish_clears_the_pause()
	{
		var t = ScenarioPauseMachine.Finish(Attention with { PauseRequested = true }, ScenarioRunState.Failed);

		t.State.ShouldBe(Running with { State = ScenarioRunState.Failed });
		t.Effects.ShouldBe(ScenarioPauseEffects.Changed);
	}

	[Test]
	public void Finish_of_a_finished_run_changes_nothing()
	{
		var done = Running with { State = ScenarioRunState.Completed };

		var t = ScenarioPauseMachine.Finish(done, ScenarioRunState.Failed);

		t.State.ShouldBe(done);
		t.Effects.ShouldBe(ScenarioPauseEffects.None);
	}

	[Test]
	public void Unlocked_sessions_follow_the_pause_kind()
	{
		string[] bound = ["a", "b"];

		ScenarioPauseMachine.UnlockedSessions(Manual, bound).ShouldBe(["a", "b"]);
		ScenarioPauseMachine.UnlockedSessions(Attention with { StuckSessionId = "b" }, bound).ShouldBe(["b"]);
		ScenarioPauseMachine.UnlockedSessions(Attention with { StuckSessionId = null }, bound).ShouldBeEmpty();
		ScenarioPauseMachine.UnlockedSessions(Running, bound).ShouldBeEmpty();
	}
}
