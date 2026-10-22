# Scenario pause as one state machine

Created: 2026-10-22.

Status: design agreed in conversation on 2026-10-22; step 4 of
[the architecture analysis](../../backlog/2026-10-14-architecture-analysis.md).

## Why

The user stopped using scenarios because they behaved unpredictably. The last
straw was a run that paused and resent a trigger while a slow reviewer was still
working; step 3 removed every resend. What is left is a pause that nobody can
read in one place: five flags in `ScenarioRunHandle` (`_state`,
`_pauseRequested`, `_resumeRequested`, `UnlockAllSessionsWhilePaused`,
`StuckSessionId`) and transitions spread over `RequestManualPause`,
`PauseIfRequestedAsync`, `PauseForAttentionAsync`, `TryResume`,
`WaitWhileManualPauseAsync` and `WaitForRetryOpportunityAsync`. The goal is a run
whose state and the reason it stands still are one value with tested
transitions, so the next fix is a change to a table, not to a thread race.

A later, separate project will redesign the run screen (today it is a wall of
journal text mixing technical detail with answers). It will read this state;
it is not part of this step.

## Goals

- One pure state machine in `Pact.Core.Scenarios` owns the run state, the pause
  kind and reason, the pending manual-pause request, the stuck session, and the
  set of unlocked sessions. Every transition is a pure function tested without
  threads, timers or terminals.
- `ScenarioRunHandle` applies transitions under its lock and keeps only the
  async plumbing (the resume signal, the wait interruption, events).
- `ScenarioRunService.cs` is split so each class has its own file: the service
  (run registry and session locks), `ScenarioIterationContext` (send, wait for a
  response, journal), `ScenarioRunHandle` (lifecycle, finalization, completion
  notice), and the small public types it declares today.
- The scenario gateway calls `SessionRuntimeCoordinator` directly instead of
  going through `MainWindowScenarioGateway`'s delegates and the shell
  controller's lambdas. Liveness and delivery no longer hop to the UI thread.

## One behavior change (decided by the user)

A response file that arrives while the run is paused **by the user** no longer
resumes the run. The run reads and keeps the response, journals that it arrived
and is waiting for Resume, and continues with that response only after the user
resumes. A response during an **attention** pause still resumes the run, as
today.

Receiving a valid response and being allowed to use it are separate. A valid
response file stops any further delivery of that step's trigger at once, even
during a manual pause; only the run's progress waits for Resume. A delivery
that has written nothing yet and is waiting to retry must never send the
trigger after its response has arrived.

User-facing and agent-facing documents that describe the old rule change with
the code: `AGENTS.md`, `docs/architecture.md`, `docs/agent-onboarding.md`,
`docs/guide/{en,ru}/reviews-and-orchestrator.md`,
`docs/guide/ru/terminal-workflows.md` (and its `en` counterpart if it says the
same), `docs/help/{en,ru}/settings-scenarios.md`.

Everything else behaves as today, except that an attention pause whose reason
changes (the watchdog fires after an unconfirmed delivery) now journals the new
reason instead of staying silent.

## The states and transitions (today's behavior, made explicit)

Run states stay those of `ScenarioRunState`: Running, Paused, StoppingAfterStep,
and the terminal Completed, MaxIterationsReached, Aborted, Failed.

A paused run has a kind:

- **Manual** — the user pressed Pause. Every bound session is unlocked. No new
  trigger is sent until the user resumes.
- **Attention** — Pact needs the user. Only the stuck session is unlocked (or
  none, when the stuck session is unknown); the others stay locked. Reasons:
  - *nothing sent*: a question is open or the session is not running; delivery
    is retried every 250 ms and the run resumes on its own once it succeeds;
  - *unconfirmed*: the trigger may have reached the terminal; it is never
    resent and the run waits for the response file;
  - *watchdog*: 15 minutes without a response; nothing is resent and the run
    goes on waiting for the response file.

Transitions:

| From | Event | To |
|---|---|---|
| Running | user Pause | Running with a pending manual request (status `Requested`); a response wait in progress is interrupted at once |
| Running, request pending | user Pause | unchanged (`Unchanged`) |
| Running, request pending | safe boundary (before a send, after a delivery, after a response wait) | Paused Manual |
| Running, request pending | attention needed | Paused Manual (the request wins) |
| Running | attention needed | Paused Attention with the stuck session |
| Paused Attention | attention needed again, same reason | unchanged, no new journal line |
| Paused Attention | attention needed again, other reason (e.g. watchdog after unconfirmed) | stays Attention with the new reason and journals it; the stuck session stays |
| Paused Attention | user Pause | Paused Manual (`Escalated`) |
| Paused Manual | user Pause | unchanged (`Unchanged`) |
| Paused Attention | delivery succeeded | Running |
| Paused Manual | delivery succeeded | unchanged |
| Paused Attention | response arrived | Running |
| Paused Manual | response arrived | unchanged; the response is held until Resume (**new**) |
| Paused (either) | user Resume | Running; a pending request is not cancelled |
| not Paused | user Resume | rejected (`Resume` throws, `TryResume` returns false) |
| Running | soft stop | StoppingAfterStep |
| Paused (either) | soft stop | stays Paused with the stop recorded; after Resume the run ends at its next check |
| any non-terminal | abort or failure | the pending resume wait is cancelled; finalize follows |
| StoppingAfterStep or terminal | user Pause | rejected (`NotPausable`) |
| any non-terminal | finalize | the terminal state; nothing unlocked |

The attention reason (nothing sent, unconfirmed, watchdog) is part of the
state: set on entering Attention, replaced as above, and cleared on any
transition out of Attention, including escalation to Manual.

Unlocked sessions are derived from the state: Manual → all bound sessions,
Attention → the stuck session, otherwise none. The run never decides locks any
other way.

## Where things go

- `Pact.Core.Scenarios`: the state machine (an immutable state value and
  transition functions returning the new state plus what the handle must do,
  e.g. "journal this", "signal resume", "interrupt the wait"), the pause kind,
  and the attention reason. Core stays free of tasks and locks.
- `Pact.Presentation.Services`: `ScenarioRunService.cs`,
  `ScenarioIterationContext.cs`, `ScenarioRunHandle.cs`, and the small public
  types (`IScenarioProgram`, `IScenarioRunArtifactCleaner`,
  `ScenarioExpectedResponse`, `ScenarioPauseRequestStatus`) in their own files.
- The handle exposes the pause kind instead of `UnlockAllSessionsWhilePaused`;
  `ScenarioRunViewModel`, `AvaloniaScenarioCoordinator` and `OrchestratorHost`
  read the kind. `StuckSessionId` stays.
- The gateway: one Presentation class over `SessionRuntimeCoordinator`
  (`DeliverPromptAsync` as `ScenarioOwner` without starting, `SendEscapeAsync`,
  liveness from the runtime's active controller). `MainWindowScenarioGateway`
  and the shell controller's `SendScenarioPromptAndSubmitAsync`,
  `SendScenarioEscapeAsync`, `IsScenarioSessionActive` go away.

## Non-goals

- The run screen and the journal's wording.
- Changes to delivery, the watchdog duration, the 250 ms retry, or the review
  loop's step logic.
- Persisting runs across restarts.

## Anti-goals

- Any path that resends a trigger.
- A manual pause that the run leaves on its own.
- Locks that disagree with the pause state the UI shows.

## Testing

- Core: one test per row of the transition table, plus the derived unlocked
  sessions for each state.
- Presentation: the existing `ScenarioRunServiceTests` stay green except the
  tests that expect a response to end a manual pause
  (`RequestPause_during_response_wait_keeps_observing_and_clears_pause_without_resending`,
  `Manual_pause_and_watchdog_never_resend_and_the_response_completes`,
  `ReviewLoop_manual_pause_during_delivery_accepts_response_without_resume_or_resend`,
  and any other the change turns red); each is rewritten to the new rule and
  keeps its no-resend assertion. A new test proves a response held during a
  manual pause is used after Resume and not before.
- The gateway: a delivery reaches `DeliverPromptAsync` as `ScenarioOwner` with
  `startIfNotRunning: false`; liveness reads the runtime, not the view model.

## Left to the builder

- Names of the state machine's types and how effects are represented.
- How the session label reaches the gateway, as long as it does not block on
  the UI thread.
- How the split files group the small types.
- Whether liveness needs a thread-safe accessor on the coordinator; today
  `TryGetActiveController` is already called off the UI thread by
  `DeliverPromptAsync`.
