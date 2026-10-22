# Scenario Pause State Machine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make a scenario run's state and pause reason one tested value in Core, split `ScenarioRunService.cs`, hold a response that arrives during a manual pause until Resume, and drive the scenario gateway straight from `SessionRuntimeCoordinator`.

**Architecture:** A pure `ScenarioPauseMachine` in `Pact.Core.Scenarios` returns a new `ScenarioPauseState` plus effects for each event; `ScenarioRunHandle` applies them under its lock and keeps only async plumbing. The 1557-line file is split by class first, with no behavior change, so the later diffs stay readable.

**Tech Stack:** .NET 10, C#, NUnit + Shouldly, Avalonia 12.

**Spec:** `docs/superpowers/specs/2026-10-22-scenario-pause-design.md`

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false` — 0 warnings, 0 errors.
- Tests: `dotnet test tests/<project> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`. One heavy build or test run at a time.
- Core stays free of tasks, locks and timers (`AGENTS.md` architecture rules).
- No path resends a trigger. A manual pause never ends without the user's Resume.
- Unchanged values: watchdog 15 min, delivery retry 250 ms, completion notice 30 s.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. The spec is committed together with Task 1.
- `docs/backlog/2026-10-09-development-directions.md` has an unrelated local edit: never stage it.

## Review Focus

1. Pause pressed while a trigger is being delivered, and the response arrives right after: the run must stay paused until Resume (Task 4 test `Pause_during_delivery_then_response_waits_for_resume`).
2. Abort while a response is held in a manual pause: the run must end Aborted, not hang (Task 4 test `Abort_while_a_response_is_held_ends_the_run`).
3. Pause pressed during an attention pause: every bound tab unlocks and the run reports `manual` to the orchestrator (Task 3 test `Escalated_pause_reports_manual_kind_and_unlocks_all`).
4. Soft stop pressed during a manual pause with a held response: after Resume the run ends without sending another trigger (Task 4 test `Soft_stop_during_held_response_ends_after_resume_without_sending`).
5. A bound session whose terminal exits between the liveness check and the delivery: the delivery reports NotRunning and the run pauses for attention instead of throwing (Task 5 test `Delivery_to_an_exited_session_reports_not_running`).

---

### Task 1: The pause state machine in Core

**Files:**
- Create: `src/Pact.Core/Scenarios/ScenarioPauseMachine.cs` (the state record, the effects enum, the transition record, and the static machine)
- Create: `src/Pact.Core/Scenarios/ScenarioPauseKind.cs`
- Move: `ScenarioPauseRequestStatus` from `src/Pact.Presentation/Services/ScenarioRunService.cs` to `src/Pact.Core/Scenarios/ScenarioPauseRequestStatus.cs` (namespace `Pact.Core.Scenarios`; fix usings in `ScenarioRunViewModel.cs`, `OrchestratorHost.cs`, `ScenarioRunServiceTests.cs`)
- Test: `tests/Pact.Core.Tests/Scenarios/ScenarioPauseMachineTests.cs`
- Commit with: `docs/superpowers/specs/2026-10-22-scenario-pause-design.md`, this plan

**Interfaces:**
- Produces:
  - `public enum ScenarioPauseKind { None, Manual, Attention }`
  - `public enum ScenarioAttentionReason { NothingSent, Unconfirmed, Watchdog }` (in `ScenarioPauseKind.cs`)
  - `[Flags] public enum ScenarioPauseEffects { None = 0, Changed = 1, JournalManualPause = 2, JournalAttention = 4, SignalResume = 8, InterruptWait = 16, HoldResponse = 32 }`
  - `public sealed record ScenarioPauseState(ScenarioRunState State, ScenarioPauseKind PauseKind, ScenarioAttentionReason? AttentionReason, string? StuckSessionId, bool PauseRequested, bool StopRequested)` with `public static ScenarioPauseState Initial` (Running, None, null, null, false, false) and `public bool IsPaused => State == ScenarioRunState.Paused;`. `AttentionReason` is non-null exactly when `PauseKind == Attention`.
  - `public readonly record struct ScenarioPauseTransition(ScenarioPauseState State, ScenarioPauseEffects Effects, ScenarioPauseRequestStatus? Status = null)`
  - `public static class ScenarioPauseMachine` with:
    - `ScenarioPauseTransition RequestPause(ScenarioPauseState state)`
    - `ScenarioPauseTransition ReachBoundary(ScenarioPauseState state)`
    - `ScenarioPauseTransition NeedAttention(ScenarioPauseState state, string? sessionId, ScenarioAttentionReason reason)`
    - `ScenarioPauseTransition DeliverySucceeded(ScenarioPauseState state)`
    - `ScenarioPauseTransition ResponseArrived(ScenarioPauseState state)`
    - `ScenarioPauseTransition Resume(ScenarioPauseState state)` — rejected = same state, `Effects.None`
    - `ScenarioPauseTransition RequestSoftStop(ScenarioPauseState state)`
    - `ScenarioPauseTransition Finish(ScenarioPauseState state, ScenarioRunState terminal)`
    - `IReadOnlyList<string> UnlockedSessions(ScenarioPauseState state, IEnumerable<string> boundSessionIds)`

- [ ] **Step 1: Write the failing tests** — one per row of the spec's transition table. Exact expectations:

```csharp
// Running → pending request, wait interrupted
var t = ScenarioPauseMachine.RequestPause(ScenarioPauseState.Initial);
t.State.ShouldBe(ScenarioPauseState.Initial with { PauseRequested = true });
t.Status.ShouldBe(ScenarioPauseRequestStatus.Requested);
t.Effects.ShouldBe(ScenarioPauseEffects.Changed | ScenarioPauseEffects.InterruptWait);

// request pending + RequestPause → Unchanged, Effects.None
// request pending + ReachBoundary → Paused Manual, PauseRequested false, StuckSessionId null,
//   Effects Changed | JournalManualPause
// Running without request + ReachBoundary → same state, Effects.None
// request pending + NeedAttention("s1", Unconfirmed) → Paused Manual (request wins),
//   AttentionReason null, Changed | JournalManualPause
// Running + NeedAttention("s1", NothingSent) → Paused Attention, StuckSessionId "s1",
//   AttentionReason NothingSent, Changed | JournalAttention
// Paused Attention(Unconfirmed, "s1") + NeedAttention("s2", Unconfirmed) → same state, Effects.None
// Paused Attention(Unconfirmed, "s1") + NeedAttention("s1", Watchdog) → AttentionReason Watchdog,
//   StuckSessionId still "s1", Changed | JournalAttention
// Paused Manual + NeedAttention("s1", Watchdog) → same state, Effects.None
// Paused Attention + RequestPause → Paused Manual, StuckSessionId null, AttentionReason null,
//   Status Escalated, Changed
// Paused Manual + RequestPause → same state, Status Unchanged, Effects.None
// every transition out of Attention (DeliverySucceeded, ResponseArrived, Resume, Finish)
//   leaves AttentionReason null
// Paused Attention + DeliverySucceeded → Running None, Changed | SignalResume
// Paused Manual + DeliverySucceeded → same state, Effects.None
// Paused Attention + ResponseArrived → Running None, Changed | SignalResume
// Paused Manual + ResponseArrived → same state, Effects.HoldResponse
// Running + ResponseArrived → same state, Effects.None
// Paused (either) + Resume → Running None, StuckSessionId null, PauseRequested kept as it was,
//   Changed | SignalResume
// Running + Resume → same state, Effects.None
// Running + RequestSoftStop → StoppingAfterStep, StopRequested true, Changed
// Paused Manual + RequestSoftStop → stays Paused Manual, StopRequested true, Effects.None
// StoppingAfterStep + RequestPause and Completed + RequestPause → Status NotPausable, Effects.None
// Paused Attention + Finish(Failed) → State Failed, PauseKind None, StuckSessionId null,
//   PauseRequested false, Changed
// UnlockedSessions: Manual → all bound ["a","b"]; Attention "b" → ["b"];
//   Attention with null stuck → []; Running → []
```

Name each test after its row, e.g. `Pause_request_during_an_attention_pause_escalates_to_manual`.

- [ ] **Step 2: Run the tests and see them fail**

Run: build, then `dotnet test tests/Pact.Core.Tests ... --filter "FullyQualifiedName~ScenarioPauseMachineTests"`
Expected: compile errors for the missing types, then — once the types exist as stubs returning `new(state, None)` — assertion failures.

- [ ] **Step 3: Implement the types and transitions listed under Interfaces** so the table holds. `Finish` on an already terminal state returns it unchanged with `Effects.None`.

- [ ] **Step 4: Run Core tests**

Run: `dotnet test tests/Pact.Core.Tests ...`
Expected: all pass.

- [ ] **Step 5: Run Presentation and App builds/tests** (only the enum moved there).
Expected: build 0 warnings; Presentation and App suites pass.

- [ ] **Step 6: Commit** spec, plan, Core files, the moved enum and the using fixes: `feat: model the scenario pause as one state machine in Core`.

---

### Task 2: Split `ScenarioRunService.cs` by class

**Files:**
- Modify: `src/Pact.Presentation/Services/ScenarioRunService.cs` (keeps only `ScenarioRunService`)
- Create: `src/Pact.Presentation/Services/ScenarioIterationContext.cs`, `ScenarioRunHandle.cs`, `IScenarioProgram.cs` (with `IScenarioRunArtifactCleaner`), `ScenarioExpectedResponse.cs`

**Interfaces:**
- Produces: the same public and internal members as today, in the new files. No signature changes.

- [ ] **Step 1: Move each type verbatim** into its file; keep namespaces and doc comments; trim the usings each file needs.
- [ ] **Step 2: Build and run Presentation tests**
Expected: 0 warnings; every test passes with no test edits. A test edit here means the move changed behavior: undo and redo the move.
- [ ] **Step 3: Commit**: `refactor: give each scenario run class its own file`.

---

### Task 3: The run handle applies the machine

**Files:**
- Modify: `src/Pact.Presentation/Services/ScenarioRunHandle.cs`, `ScenarioIterationContext.cs`
- Modify: `src/Pact.Presentation/ViewModels/ScenarioRunViewModel.cs` (`UnlockAllSessionsWhilePaused` → `PauseKind`)
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaScenarioCoordinator.cs:378-397` (locks from `ScenarioPauseMachine.UnlockedSessions`), `OrchestratorHost.cs:491-493` (kind from `PauseKind`)
- Test: `tests/Pact.Presentation.Tests/Services/ScenarioRunServiceTests.cs`, `tests/Pact.Presentation.Tests/ViewModels/ScenarioRunViewModelTests.cs`, the seven test usages of `UnlockAllSessionsWhilePaused`

**Interfaces:**
- Consumes: everything Task 1 produces.
- Produces: `ScenarioRunHandle.PauseKind` (`ScenarioPauseKind`) and `ScenarioRunViewModel.PauseKind`, replacing `UnlockAllSessionsWhilePaused` on both; `ScenarioRunHandle.AttentionReason` and `ScenarioRunViewModel.AttentionReason` (`ScenarioAttentionReason?`). `PauseForBlockedDeliveryAsync` passes `NothingSent`, `PauseForUndeliveredAsync` passes `Unconfirmed`, `PauseForTimeoutAsync` passes `Watchdog`. `State`, `StuckSessionId`, `PauseRequested`, `RequestManualPause`, `Resume`, `TryResume` keep their signatures.

- [ ] **Step 1: Write the failing test** `Escalated_pause_reports_manual_kind_and_unlocks_all` in `ScenarioRunServiceTests`: drive a run into an attention pause (unconfirmed delivery, as `SendAsync_unconfirmed_delivery_pauses_for_attention_without_resending` does), call `RequestManualPause()`; assert `Status == Escalated`, `handle.PauseKind == ScenarioPauseKind.Manual`, `handle.StuckSessionId` is null, and `ScenarioPauseMachine.UnlockedSessions` over the handle's state returns every bound session. Rewrite the seven `UnlockAllSessionsWhilePaused` assertions to `PauseKind`. Add `Attention_pause_reports_its_reason`: an unconfirmed delivery gives `AttentionReason == Unconfirmed`; with a 100 ms watchdog and no response, the reason becomes `Watchdog` and the journal gains the watchdog line; after Resume the reason is null.
- [ ] **Step 2: Run it** — Expected: compile failure on `PauseKind`.
- [ ] **Step 3: Replace the handle's fields** `_state`, `_pauseRequested`, `UnlockAllSessionsWhilePaused`, `StuckSessionId`'s backing field and `_softStopRequested` with one `ScenarioPauseState _pause`. Every public mutator and the `PauseIfRequestedAsync` / `PauseForAttentionAsync` / `TryResume` / `WaitWhileManualPauseAsync` / `WaitForRetryOpportunityAsync` paths call the machine under `_sync` and act on the effects outside the lock: `Changed` → `RaiseStateChanged`, `JournalManualPause` → journal `"paused by user"` (Info), `JournalAttention` → journal the reason (Warning), `SignalResume` → complete `_resumeRequested`, `InterruptWait` → cancel `_pauseInterruption`. Keep `_resumeRequested` and `_pauseInterruption` as plumbing. In this task `ResumeAfterResponse` applies `ScenarioPauseMachine.Resume`, so a response still ends a manual pause as today; Task 4 switches it to `ResponseArrived`.
- [ ] **Step 4: Point the view model, `AvaloniaScenarioCoordinator.ApplyRunLockState` and `OrchestratorHost.ProjectRun`** at `PauseKind`; the coordinator computes locked/unlocked ids with `ScenarioPauseMachine.UnlockedSessions`.
- [ ] **Step 5: Build; run Presentation and App suites**
Expected: 0 warnings; all pass, including every existing pause test unchanged except the `PauseKind` rename and any journal-count assertion that now sees the spec's new watchdog-after-unconfirmed line.
- [ ] **Step 6: Commit**: `refactor: drive scenario pauses through the Core state machine`.

---

### Task 4: A response during a manual pause waits for Resume

**Files:**
- Modify: `src/Pact.Presentation/Services/ScenarioIterationContext.cs` (`WaitForResponseAsync` no longer resumes; new `AcceptResponseAsync`), `ScenarioRunHandle.cs` (`ResumeAfterResponse` → `ResponseArrived`)
- Modify: `src/Pact.Presentation/Services/Scenarios/ReviewLoopScenarioProgram.cs` (`ExchangeAsync`, around line 282)
- Modify docs (same commit): `AGENTS.md:122-128`, `docs/architecture.md`, `docs/agent-onboarding.md:146-154`, `docs/guide/en/reviews-and-orchestrator.md:26-30`, `docs/guide/ru/reviews-and-orchestrator.md`, `docs/guide/ru/terminal-workflows.md` (and `docs/guide/en/terminal-workflows.md` if it states the old rule), `docs/help/en/settings-scenarios.md`, `docs/help/ru/settings-scenarios.md` — every sentence saying a valid response clears a manual Pause without Resume now says: the response is read and stops further delivery at once, and the run continues after Resume. Find them with `grep -rniE "without (requiring )?Resume|clears (the )?Pause|без .*Resume|продолж" AGENTS.md docs --include=*.md` and read each hit.
- Test: `tests/Pact.Presentation.Tests/Services/ScenarioRunServiceTests.cs`, `tests/Pact.Presentation.Tests/Services/Scenarios/ReviewLoopScenarioProgramTests.cs`

**Interfaces:**
- Consumes: `ScenarioPauseMachine.ResponseArrived`, `ScenarioPauseEffects.HoldResponse` (Task 1); the handle from Task 3.
- Produces:
  - `public Task ScenarioIterationContext.AcceptResponseAsync(string stepId, CancellationToken cancellationToken)` — applies `ResponseArrived`: an attention pause resumes; a manual pause journals `"response received; waiting for Resume"` (Info) once and waits for the resume signal; otherwise returns at once.
  - `WaitForResponseAsync` returns as soon as a valid response is read and changes no pause state on its own.
  - `ExchangeAsync` order: the response wait completes → cancel and drain the delivery (as today) → `await context.AcceptResponseAsync(captureStepId, cancellationToken)` on both branches → journal `read ...` and return. Receiving the file therefore stops delivery before any wait for Resume.

- [ ] **Step 1: Write the failing tests.** No sleeps (`AGENTS.md` forbids timing-sleep tests): every wait is on a signal — a `TaskCompletionSource` completed from `FakeScenarioTerminalGateway.PromptSentAsync`, `ScenarioRunHandle.JournalEntryAdded` (the `"response received; waiting for Resume"` line) or `StateChanged`, each bounded by `.WaitAsync(TimeSpan.FromSeconds(5))`. Count sends per task: only `Sent` entries whose prompt contains the completed step's `TaskPath`; the next step may send its own trigger.
  - `Response_during_manual_pause_cancels_a_waiting_delivery_and_waits_for_resume` (reviewer finding 1), in `ReviewLoopScenarioProgramTests`: gateway returns `NotRunning` (`WriteAttempted: false`) so the delivery sits in its retry; wait for the first send; `RequestManualPause()`; write a valid response; wait for the waiting journal line — by then the delivery is cancelled and drained — and record the sends for that task; assert `State == Paused`, `PauseKind == Manual`; `Resume()`; wait for the next step's first send (a prompt with the next `TaskPath`); assert the sends for the completed task are unchanged.
  - `Response_during_manual_pause_is_used_only_after_resume`: pause during the response wait, write the response, wait for the waiting journal line; assert `State == Paused`, `PauseKind == Manual`, the program has not received the response, and the line occurs once; `Resume()`; assert the program receives exactly that response and the completed task was sent once.
  - `Pause_during_delivery_then_response_waits_for_resume` (Review Focus 1): `PromptSentAsync` blocks on a gate; pause while it is blocked; release it; write the response; same assertions as above.
  - `Abort_while_a_response_is_held_ends_the_run` (Review Focus 2): after the waiting line, `Abort()`; `Completion` finishes (5 s bound) with `State == Aborted`.
  - `Soft_stop_during_held_response_ends_after_resume_without_sending` (Review Focus 4): after the waiting line, `RequestSoftStop()`; assert the run is still `Paused`/`Manual`; `Resume()`; `Completion` finishes with `State == Aborted` and the total send count is unchanged.
  - Rewrite `RequestPause_during_response_wait_keeps_observing_and_clears_pause_without_resending`, `Manual_pause_and_watchdog_never_resend_and_the_response_completes`, `ReviewLoop_manual_pause_during_delivery_accepts_response_without_resume_or_resend` (and any other the change turns red): each now calls `Resume()` before expecting progress and keeps its no-resend assertion.
- [ ] **Step 2: Run them** — Expected: the new tests fail (the run proceeds without Resume, or a soft stop ends the pause); the rewritten ones fail the same way.
- [ ] **Step 3: Implement** `AcceptResponseAsync`, remove the resume from `WaitForResponseAsync`, and call `AcceptResponseAsync` in `ExchangeAsync` after the delivery is cancelled and drained. Move `ThrowIfSoftStopRequested()` out of `WaitForResponseAsync` into the end of `AcceptResponseAsync`, after any wait for Resume, so a soft stop never ends a manual pause on its own.
- [ ] **Step 4: Update the docs listed above.**
- [ ] **Step 5: Run Presentation and App suites** — Expected: all pass.
- [ ] **Step 6: Commit** code, tests and docs: `fix: keep a manual scenario pause until the user resumes`.

---

### Task 5: The gateway over the runtime coordinator

**Files:**
- Create: `src/Pact.Presentation/Services/RuntimeScenarioGateway.cs`
- Delete: `src/Pact.Presentation/Services/MainWindowScenarioGateway.cs`, `tests/Pact.Presentation.Tests/Services/MainWindowScenarioGatewayTests.cs`
- Modify: `src/Pact.Presentation/Services/IScenarioTerminalGateway.cs` (drop `GetSessionLabel`), `ScenarioRunService.cs` (`Start` gains labels), `ScenarioRunHandle.cs`, `ScenarioIterationContext.cs` (label from the handle)
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaScenarioCoordinator.cs` (constructor, `Start` call), `AvaloniaMainShellController.cs` (remove `SendScenarioPromptAndSubmitAsync`, `FindScenarioSession`, `SendScenarioEscapeAsync`, `IsScenarioSessionActive`; pass `_runtimeCoordinator`)
- Modify: the four test fakes implementing `GetSessionLabel` (remove the member)
- Test: `tests/Pact.Presentation.Tests/Services/RuntimeScenarioGatewayTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class RuntimeScenarioGateway(SessionRuntimeCoordinator coordinator) : IScenarioTerminalGateway` — `SendPromptAsync` → `coordinator.DeliverPromptAsync(sessionId, prompt, submit: true, PromptSender.ScenarioOwner, startIfNotRunning: false, ct)`; `SendEscapeAsync` → `coordinator.SendEscapeAsync(sessionId)`; `IsSessionAlive` → `coordinator.TryGetActiveController(sessionId, out _, out _)`.
  - `ScenarioRunService.Start(..., int maxIterations, IReadOnlyDictionary<string, string>? sessionLabels = null)`; the journal names a session by its label, falling back to its id.
  - `AvaloniaScenarioCoordinator(MainWindowViewModel, AppPaths, ScenarioDefinitionStore, SessionRuntimeCoordinator runtimeCoordinator, IUiTaskDispatcher, Func<ScenarioRunViewModel, Task>, Func<string, Task>, ObservedTaskGroup)`; at `Start` it builds labels from `_viewModel.FindSession(id)?.Title` on the UI thread.

- [ ] **Step 1: Write the failing tests** in `RuntimeScenarioGatewayTests`, reusing the delivery fixture style of `SessionRuntimeCoordinatorTests` (fake facts, recording host, Codex runtime):
  - `A_trigger_is_delivered_as_the_scenario_owner_without_starting`: a scenario-locked session (`facts.Locked = true`) still receives the paste and Enter; result `Confirmed`.
  - `Delivery_to_an_exited_session_reports_not_running` (Review Focus 5): detach the controller; result `NotRunning`, `WriteAttempted` false, no exception, no start requested.
  - `Liveness_follows_the_runtime_controller`: true while attached, false after detach.
  - In `ScenarioRunServiceTests`: `The_journal_names_a_session_by_its_label` — `Start` with labels `{ "session-1": "Review" }` journals `sending trigger to reviewer [Review]`.
- [ ] **Step 2: Run them** — Expected: compile failure on `RuntimeScenarioGateway` and the new `Start` parameter.
- [ ] **Step 3: Implement** the gateway, the label parameter, the coordinator constructor change; remove the old gateway, its tests, the four shell controller members and `GetSessionLabel` from the interface and fakes.
- [ ] **Step 4: Build; run Presentation and App suites** — Expected: 0 warnings; all pass.
- [ ] **Step 5: Update docs**: `docs/architecture.md` scenario section — the gateway calls the coordinator directly, and the pause is one Core state machine (kind and attention reason). Mark step 4 done in `docs/backlog/2026-10-14-architecture-analysis.md` the way steps 1–3 are marked (stage only those hunks; the directions file is not touched).
- [ ] **Step 6: Commit**: `refactor: send scenario triggers through the runtime coordinator directly`.
