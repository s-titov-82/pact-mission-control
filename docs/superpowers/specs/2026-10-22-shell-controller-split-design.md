# Splitting the main shell controller

Created: 2026-10-22.

Status: direction agreed in conversation on 2026-10-22; step 5 of
[the architecture analysis](../../backlog/2026-10-14-architecture-analysis.md).

## Why

`src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` is 4449
lines and holds about ten independent subsystems. To change one of them (the
orchestrator, a review that an agent requested, MCP tool routing) you have to
read the whole file. Most of these subsystems can only be tested through the
whole controller. The session launch logic is also written twice: once for
regular sessions and once, copied, for the orchestrator. A fix to one copy can
miss the other.

The user reported no behavior problems in these areas. This is an internal
refactor. Success means:

- the five subsystems below live in their own classes, each with narrow
  dependencies;
- session launch exists once;
- nothing the user can see changes.

## Goals

Move five subsystems out of the controller. All the new classes go in
`src/Pact.App.Avalonia/Controllers/`.

| New class | What moves into it | Controller lines today |
|---|---|---|
| `AgentControlToolRouter` | `BuildAgentControlToolsList`, `InvokeAgentControlToolAsync`, `InvokeOrchestratorToolAsync`, the `TryGetRequiredString`/`TryGetOptionalString`/`TryGetOptionalInt32`/`ArgumentError` parsers | 426–933 |
| `SessionLauncher` | `ActivateRuntimeAsync`, `CreateAgentLaunchInjection`, the regular-session exit path (`OnControllerExited`, `HandleControllerExitedAsync`), and the one shared process start that the orchestrator also uses | 3239–3370, 3787–3810, and the copy in 1938–1989 |
| `OrchestratorSupervisor` | the orchestrator state fields (`_orchestratorRecord`, `_orchestratorSession`, `_workstationLockMonitor`, `_mainWindowHandle`, `_orchestratorIntentionalStop`, `_orchestratorConsecutiveFailures`, `_orchestratorStartedAt`, `OrchestratorSessionId`); `ApplyOrchestratorSettingsAsync`; `AttachWorkstationLockMonitor`; `RefreshWorkstationLockMonitor`; `IsOrchestratorRunning`; `StartOrchestratorAsync`; `StopOrchestratorAsync`; exit and restart handling; `OnWorkstationLockStateChanged`; `DeliverWorkstationLockPromptAsync`; `SendOrchestratorMessageAsync`; `RestoreOrchestratorAsync`; disposing the lock monitor at shutdown | 1780–2165, 3110–3114, 3160–3171 |
| `ReviewLaunchService` | `StartAgentRequestedReviewAsync`, `RollBackAgentReviewerAsync`, `FailedReviewStart` | 2304–2483 |
| `ResumeCaptureService` | `CaptureResumeCommandsBeforeStopAsync`, `IsResumeCaptureEligible`, `CaptureResumeCommandUnderDeadlineAsync`, `ResumeCaptureCancellation`, `TrySaveResumeCommandFromRecentOutputAsync`, `GracefulAgentExitTimeout` | 3653–3785 |

Line numbers are from commit 5b091ad and are only a guide.

Additional goals:

- **The controller creates and wires the new classes.** It keeps all UI
  command routing, tab presentation, selection actions, the web/monitor/Git
  wiring, and the shutdown order.
- **No new class gets a reference back to the controller.** Each one receives
  only what it uses: specific services (`SessionRuntimeCoordinator`,
  `ITerminalWebViewHost`, `MainWindowViewModel`, `AgentControlTokenRegistry`,
  `TimeProvider`, `IUiTaskDispatcher`, `ObservedTaskGroup`, the dispatchers)
  and delegates for behavior the controller still owns. Examples of such
  delegates: report a status message, check whether shutdown has begun, select
  a session tab, show a session's terminal, notify the scenario coordinator
  that a session died.
- **`AgentControlHost` stops holding the controller.** Today it takes `this`
  only to call `StartAgentRequestedReviewAsync`. It should take
  `ReviewLaunchService` instead, or a delegate to it.

## The shared launch, precisely

Both launch paths today call `SessionRuntimeCoordinator.StartAsync` with an
exit handler and the same four handlers:

- output goes to the controller's `OnOutputReceived(id, text)`;
- input writing calls `ResetSnapshotBaselineSafeAsync(id)` when
  `AgentKeyEncoder.IsSubmit(input)`;
- input written goes to `TerminalTabStatuses.OnUserInput`;
- a viewport change goes to `TerminalTabStatuses.OnViewportChanged`.

**The shared core is exactly this: build the four handlers and call
`StartAsync`.** It is one method on `SessionLauncher`. It takes the session id,
the agent kind, ready-made `TerminalStartOptions`, the exit handler and the
cancellation token. Both paths call it.

The core does not read the terminal size or build the options. Each caller
keeps doing that itself, in its current order:

- **A regular session** reads the size and builds the options before its
  `try`. Only the core call sits inside the `try`. The cleanup boundary stays
  where it is today.
- **The orchestrator** reads the size, then builds the environment, then
  resets the intentional-stop flag, then records the start time, and only then
  calls the core.

(Reviewer finding, 2026-10-22.)

Everything else also stays with its caller and must keep its current order
and behavior:

| | Regular session (`SessionLauncher`) | Orchestrator (`OrchestratorSupervisor`) |
|---|---|---|
| Command | `ShellProfileCommandPlanner.GetStartPlan`, then `CodexEmbeddedMode.Apply` with the MCP/instruction injection, then resolve. An empty result revokes the token and throws `"Session has no launch command."` | resolve `LaunchCommand` with no extra arguments. An empty result throws `"The launch command could not be resolved."` |
| Already running | resize the existing controller and return the plan | return early before resolving |
| Environment | the injection's environment variables | `PACT_MCP_URL` and `PACT_MCP_TOKEN` from the orchestrator credential |
| Terminal creation | done by the callers, as today | `CreateTerminalAsync` before the start, as today |
| Before start | none | reset the intentional-stop flag; record the start time |
| Exit handler | `OnControllerExited`: revoke the token, notify the scenario coordinator, mark the session Exited | `OnOrchestratorExited`: run the restart policy |
| After start | `UpdateSessionStatusAsync(Running)`; `OnSessionStarted(plan.Mode)` | `UpdateRecord(Running)`; `OnSessionStarted(Normal)`; slot shows "Running" |
| On failure | stop the runtime, revoke the token, rethrow | stop the runtime (an `AggregateException` if cleanup fails), remove the tab status, slot shows `Failed: …`. Caller cancellation is rethrown **without** any of this cleanup, as today. |

The output handler, `WriteOutputAsync` and `ResetSnapshotBaselineSafeAsync`
stay in the controller, because they write to the terminal view. The launcher
reaches them through delegates.

`_pactSkillPublication` is set during `InitializeAsync`, after construction.
The launcher must read the value that is current at launch time, not the one
from construction.

## Seams the builder will hit

- **The orchestrator session is read outside the supervisor.** These readers
  stay in the controller and read it through the supervisor's accessors (its
  session, its id, whether it is running):
  - `SelectOrchestratorAsync`, which selects the tab;
  - the `OrchestratorHost` lambda that reports the orchestrator id;
  - the soft-restart restorer's id check;
  - the restore-selection branch near line 3179;
  - the start-on-initialize branch in `InitializeAsync`, which needs
    `_terminalHostInitialized`.

  `ApplyOrchestratorSettingsAsync` also reads that flag. Pass it as a
  delegate or call a "terminal host ready" method on the supervisor; the
  choice is the builder's.
- **The constructor order has a cycle today.**
  - The router needs both dispatchers.
  - `OrchestratorHost` needs `SendOrchestratorMessageAsync`.
  - The supervisor and the launcher need `AgentControlAddress`, which exists
    only after the endpoint (which needs the router) has started.

  - Once `AgentControlHost` takes `ReviewLaunchService`, there is one more
    chain: endpoint → router → `AgentControlDispatcher` → `AgentControlHost`
    → `ReviewLaunchService` → `SessionLauncher` → endpoint address. The
    reviewer is started through `ActivateRuntimeAsync`.

  **How to break it:**
  - The launcher and the supervisor receive the address as a `Func<Uri>`.
  - They are created before the endpoint, together with
    `ReviewLaunchService`.
  - The endpoint still starts at the same point as today.

  (Reviewer finding, 2026-10-22. The other option was to create these classes
  after the endpoint. That would also need a deferred delegate for starting
  the reviewer, so it was dropped.)

  `SendOrchestratorMessageAsync` uses no orchestrator state. It may move
  into the supervisor or stay as a plain coordinator call.

## Non-goals

- **Selection actions** (capture, source validity, routes; roughly lines
  1258–1610). They stay in the controller. They are tied to tab presentation
  and focus, which is step 6 (`PresentedSurface`).
- **Tab presentation, loading and focus.** Step 6.
- **The web monitor, process metrics, soft restart, Git panels and the
  clipboard.** These are already partly extracted. Leave them alone.
- **`DescribeUndelivered`.** The orchestrator lock prompt and
  `SendTextToSessionAsync` both use it. Put it wherever both can reach it.
  Changing its wording is out of scope.
- **Renaming or reshaping public and internal entry points** used by
  `MainWindow.axaml.cs` or by tests.

## Anti-goals

These would make the work a failure even if it compiles:

- **Any change a user can notice.** That includes:
  - MCP tool results and error texts;
  - orchestrator slot texts;
  - restart timing;
  - lock prompts;
  - the review start and rollback messages;
  - resume capture behavior;
  - status messages;
  - the order of shutdown steps.
- **New classes that take the whole controller,** or a "shell services" bag
  that recreates it.
- **Changing what existing tests assert.**

## Constraints

- **Tests.** Existing App, Presentation, Core and Infrastructure tests must
  pass. Their assertions must not change.
  - A test may change only how it reaches the code it already tests. For
    example, it may go through a forwarding member, or construct the new
    class directly.
  - Builder's call: keep one-line forwarding members on the controller for
    every entry point that `MainWindow.axaml.cs`, `AgentControlHost` or tests
    use today, such as `StartOrchestratorAsync`, `StopOrchestratorAsync`,
    `IsOrchestratorRunning`, `DeliverWorkstationLockPromptAsync` and
    `StartAgentRequestedReviewAsync`. That keeps test churn at zero. A
    forwarder holds no logic.
- **Seam tests.** Add tests where a move creates a new contract that the
  controller tests reach only indirectly:
  - argument parsing in `AgentControlToolRouter`: a missing required
    argument, a wrong type, arguments that are not an object, an unknown
    tool;
  - the orchestrator launch through the shared `SessionLauncher` core: a
    Submit key resets the snapshot baseline, and output reaches the output
    delegate.

  Check `AgentControlCompositionTests` and `OrchestratorCompositionTests`
  first. Add only the cases they don't already cover.
- **Build and test resource limits** from `AGENTS.md` apply:
  - build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q
    -p:BuildInParallel=false`;
  - tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false
    -- NUnit.NumberOfTestWorkers=2`;
  - one heavy build at a time.
- **No timing-sleep tests.** Wait on conditions and signals only.
- **Commits.** One commit per extracted class is a natural boundary. Commit
  this spec together with the first one, not alone.

## Suggested order

1. `AgentControlToolRouter`. Pure routing, the fewest dependencies.
2. `SessionLauncher`, with the regular-session path only.
3. `OrchestratorSupervisor`, which removes the launch copy by calling the
   `SessionLauncher` core.
4. `ReviewLaunchService`, with `AgentControlHost` switched to it.
5. `ResumeCaptureService`.
6. Docs: mark step 5 done in the analysis doc. Add a short paragraph to
   `docs/architecture.md` naming the new App-layer classes next to the
   existing "shell controller owns terminal/session shutdown" text.

Each step leaves the build and every suite green.

## Left to the builder

- Exact constructor signatures and delegate shapes. Prefer specific
  `Func`/`Action` delegates over new interfaces unless an interface already
  exists.
- File-local helper records and their names.
- Whether reading the shutdown flag through the existing locked
  `IsShutdownBegun()` replaces today's unlocked read of `_shutdownBegun` in
  the orchestrator exit path. Builder's call: yes. A user cannot observe the
  difference.
