# Shell Controller Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move five subsystems out of `AvaloniaMainShellController` into their own classes, with no behavior change. Launching a session process becomes one shared core that both regular sessions and the orchestrator call.

**Architecture:**
- The five new `internal sealed` classes live in `src/Pact.App.Avalonia/Controllers/`.
- Each new class receives specific services and delegates; none holds a reference back to the controller.
- The controller creates the classes, wires them together, and keeps one-line forwarders for every entry point that `MainWindow.axaml.cs`, `AgentControlHost` or tests call today.

**Tech Stack:** .NET 10, Avalonia 12, NUnit + Shouldly.

**Spec:** `docs/superpowers/specs/2026-10-22-shell-controller-split-design.md`

## Global Constraints

- **No behavior change a user can notice.** This covers:
  - MCP tool results and error texts;
  - orchestrator slot texts;
  - restart timing;
  - lock prompts;
  - review start and rollback messages;
  - resume capture;
  - status messages;
  - the order of shutdown steps.
- **No new class takes the controller**, and no class bundles the controller's services into one "shell services" object.
- **Existing tests keep their assertions.** A test may change only how it reaches the code.
- **Forwarders hold no logic.** Every entry point used by `MainWindow.axaml.cs`, `AgentControlHost` or tests stays on the controller as a one-line forwarder.
- **Build:** `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`.
- **Tests:** `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`. Run one heavy build or test at a time.
- **No timing-sleep tests.** Wait on conditions and signals only.
- **Commits:** commit this spec and this plan together with Task 1, never alone. End every commit message with `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- **Line numbers** below are from commit `30dc5ca` and are only a guide.

## How TDD applies to a move

This is a pure refactor, so the new tests are characterization tests: they pin today's behavior.

- **Write each new test before the move, and run it against the old code.**
  - Expected result: it **passes**.
  - Then prove the test can fail: change one expected string in the test, watch it fail, and change the string back. Record this in the ledger as `mutation-checked`.
- **After the move, the same test must pass unchanged.**
- **Tasks with no new test** rely on existing tests that already exercise the moved code. Each such task names those tests, and they must pass before and after the move.

## Review Focus

1. **Order of the regular-session launch.** In `ActivateRuntimeAsync`, the size is read and the options are built before the `try`, so a failure there must not stop the runtime or revoke the token. The shared core call stays inside the `try`.
2. **Order of the orchestrator start.** The steps run in this order:
   1. `CreateTerminalAsync`;
   2. read the size;
   3. build the environment;
   4. `_intentionalStop = false`;
   5. record the start time;
   6. call the core.

   Caller cancellation rethrows **without** stopping the runtime or changing the slot.
3. **Skill publication is read at launch time.** `PactSkillPublication` is assigned in `InitializeAsync`, after construction. The launcher must read the current value at each launch. The existing test `Publisher_failure_keeps_mcp_injection_and_records_one_diagnostic` pins this.
4. **Endpoint start time.** The agent-control endpoint must still start inside the controller constructor at the same point. `AgentControlAddress` is read lazily through `Func<Uri>`, never captured before `Start` returns.
5. **Shutdown flag in the orchestrator restart path.** `HandleOrchestratorExitedAsync` checks the shutdown flag before it restarts. It reads it via `IsShutdownBegun()`. The existing test `Application_shutdown_does_not_restart_the_slot` pins this.

---

### Task 1: `AgentControlToolRouter`

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/AgentControlToolRouter.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
  - remove lines 426–933, from `BuildAgentControlToolsList` through `ArgumentError`;
  - change the `AgentControlJsonRpc` construction at lines 214–221.
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/AgentControlCompositionTests.cs`, `tests/Pact.App.Avalonia.Tests/Controllers/OrchestratorCompositionTests.cs`
- Also commit: `docs/superpowers/specs/2026-10-22-shell-controller-split-design.md` and this plan.

**Interfaces:**
- Produces:
  - `internal sealed class AgentControlToolRouter(AgentControlDispatcher agentDispatcher, OrchestratorDispatcher orchestratorDispatcher, MainWindowViewModel viewModel, ReviewProfileProvider reviewProfiles, IUiTaskDispatcher uiTaskDispatcher, Func<string, Task> reportStatusAsync)`
  - `JsonNode BuildToolsList(bool orchestrator)`: returns `OrchestratorToolCatalog.BuildToolsListResult()` for the orchestrator, otherwise today's `BuildAgentControlToolsList` body.
  - `Task<AgentControlResultData> InvokeAsync(AgentControlToolCall call, CancellationToken cancellationToken)`: today's `InvokeAgentControlToolAsync` without the `dispatcher` parameter.
  - The argument parsers become private static members of the router, with bodies and texts unchanged.

- [ ] **Step 1: Write the characterization tests**

In `AgentControlCompositionTests`, use the existing `CompositionFixture` and `InvokeToolAsync`:

```csharp
[Test]
public async Task A_missing_required_argument_is_reported_by_name()
{
	await using CompositionFixture fixture = new();
	await fixture.InitializeAsync();

	var result = await fixture.InvokeToolAsync(CompositionFixture.SessionA, "pact_append_note", new JsonObject());

	result.IsError.ShouldBeTrue();
	result.Text.ShouldBe("Argument 'text' is required.");
}

[Test]
public async Task A_string_argument_of_another_type_is_rejected()
{
	// same setup; arguments { ["text"] = 5 }
	// result.Text.ShouldBe("Argument 'text' must be a string.");
}

[Test]
public async Task A_non_integer_iteration_count_is_rejected()
{
	// pact_request_review with scenarioId, reviewProfileId, target as strings and ["maxIterations"] = "many"
	// result.Text.ShouldBe("Argument 'maxIterations' must be an integer.");
}

[Test]
public async Task An_unknown_session_tool_is_refused()
{
	// tool "pact_no_such_tool", empty arguments
	// result.IsError.ShouldBeTrue(); result.Text.ShouldBe("Unknown tool 'pact_no_such_tool'.");
}
```

`ToolResult`'s member names are whatever the fixture defines. Read them and use them as they are.

In `OrchestratorCompositionTests`, add:

```csharp
[Test]
public async Task An_unknown_orchestrator_tool_is_refused()
{
	await using CompositionFixture fixture = new();
	await fixture.InitializeAsync();

	(await fixture.InvokeAsync(fixture.Credential, "pact_no_such_tool", new JsonObject()))
		.ShouldBe("Unknown tool 'pact_no_such_tool'.");
}

[Test]
public async Task A_missing_orchestrator_argument_is_reported_by_name()
{
	// pact_get_session with empty arguments → "Argument 'sessionId' is required."
}
```

- [ ] **Step 2: Run the new tests against the old code**

Run: build, then `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --filter "Name~_argument_|Name~_is_rejected|Name~_is_refused" -- NUnit.NumberOfTestWorkers=2`
Expected: the 6 new tests PASS. The filter may pick up other existing tests as well; those must pass too. Then do the mutation check described above, once for each test.

- [ ] **Step 3: Move the routing into `AgentControlToolRouter`**

In the controller constructor:
- Build the router after `_orchestratorDispatcher` exists.
- Pass `new AgentControlJsonRpc(caller => router.BuildToolsList(caller.IsOrchestrator), router.InvokeAsync)`.
- Keep `_reviewProfileProvider` and the `ReportStatusAsync` method group as they are.

- [ ] **Step 4: Run the App suite**

Run: build, then `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`
Expected: all pass. That is 614 tests: 608 + 6.

- [ ] **Step 5: Commit**

```bash
git add src/Pact.App.Avalonia/Controllers/AgentControlToolRouter.cs src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs tests/Pact.App.Avalonia.Tests/Controllers/AgentControlCompositionTests.cs tests/Pact.App.Avalonia.Tests/Controllers/OrchestratorCompositionTests.cs docs/superpowers/specs/2026-10-22-shell-controller-split-design.md docs/superpowers/plans/2026-10-22-shell-controller-split.md
git commit -m "refactor: route MCP tool calls outside the shell controller"
```

---

### Task 2: `SessionLauncher`

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/SessionLauncher.cs`
- Modify: `AvaloniaMainShellController.cs`
  - remove `ActivateRuntimeAsync` (3239–3333), `CreateAgentLaunchInjection` (3335–3370), `OnControllerExited` and `HandleControllerExitedAsync` (3787–3810);
  - remove the `_pactSkillPublication` and `_agentControlEnabled` fields;
  - change the callers of `ActivateRuntimeAsync`: lines 1137, 2377, 3153 and 3423.
- Test: existing tests only (listed in Step 1).

**Interfaces:**
- Produces:
  - `internal sealed class SessionLauncher(SessionRuntimeCoordinator runtimeCoordinator, ITerminalWebViewHost terminalHost, MainWindowViewModel viewModel, AgentControlTokenRegistry agentControlTokens, AppPaths appPaths, Func<Uri> agentControlAddress, bool agentControlEnabled, Func<string, IReadOnlyList<string>, Task<string?>> resolveCommandAsync, IUiTaskDispatcher uiTaskDispatcher, ObservedTaskGroup eventTasks, Action<string, string> onOutput, Func<string, Task> resetSnapshotBaselineAsync, Action<string> notifySessionDied, Func<string, Task> reportStatusAsync)`
  - `PactSkillPublication SkillPublication { get; set; } = PactSkillPublication.Empty;`
  - `Task<SessionStartPlan> ActivateAsync(SessionViewModel session, bool startIfNeeded, bool preferResumeCommand, CancellationToken cancellationToken)`: today's `ActivateRuntimeAsync`, with its order unchanged.
  - `Task StartProcessAsync(string sessionId, AgentKind kind, TerminalStartOptions options, Action<string, TerminalController> exited, CancellationToken cancellationToken)`: the shared core.
    - It builds the four handlers, keyed by `sessionId`:
      - output → `onOutput`;
      - writing → `resetSnapshotBaselineAsync` on `AgentKeyEncoder.IsSubmit`;
      - written → `TerminalTabStatuses.OnUserInput`;
      - viewport → `TerminalTabStatuses.OnViewportChanged`.
    - It calls `runtimeCoordinator.StartAsync`.
    - It does nothing else.
  - `ActivateAsync` calls `StartProcessAsync` inside its existing `try`. It passes its own regular-session exit handler, which is today's `OnControllerExited`/`HandleControllerExitedAsync` with `_scenarioCoordinator.NotifySessionDied` replaced by `notifySessionDied`.

- [ ] **Step 1: Run the existing tests that pin the launch path, before the move**

Run: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AgentControlCompositionTests|FullyQualifiedName~AvaloniaMainShellControllerTests" -- NUnit.NumberOfTestWorkers=2`

Expected: PASS. The tests that guard this task are:
- `RestartingASessionRevokesTheOldTokenAndInjectsAFreshOne`;
- `Ordinary_and_resume_commands_keep_the_base_command_and_receive_arguments_once`;
- `Publisher_failure_keeps_mcp_injection_and_records_one_diagnostic`;
- `Disabled_agent_control_adds_common_guidance_without_mcp_or_token`;
- the `SessionStatus.Exited` tests around `AvaloniaMainShellControllerTests.cs:2375`–`2396`.

- [ ] **Step 2: Create `SessionLauncher` and switch the controller to it**

Construction order in the controller constructor:
- Read `_settingsFileStore.ReadAgentControlEnabled()` before you construct the launcher. It is a settings read, so moving it earlier is safe.
- Create the launcher before `AgentControlHost`, with `() => AgentControlAddress`.
- `InitializeAsync` assigns `_launcher.SkillPublication` where it assigns `_pactSkillPublication` today.
- `OnOutputReceived` and `ResetSnapshotBaselineSafeAsync` stay in the controller and are passed as method groups.

- [ ] **Step 3: Run the App suite**

Run: build, then the App suite command from Task 1, Step 4.
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Pact.App.Avalonia/Controllers/SessionLauncher.cs src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs
git commit -m "refactor: launch session processes from one SessionLauncher"
```

---

### Task 3: `OrchestratorSupervisor`

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/OrchestratorSupervisor.cs`
- Create: `src/Pact.App.Avalonia/Controllers/PromptDeliveryText.cs`
  - It holds `internal static string? DescribeUndelivered(PromptDeliveryResult result)`, moved verbatim from the controller.
- Modify: `AvaloniaMainShellController.cs`
  - remove the orchestrator fields (68–75) and lines 1780–2165 except `CreateOrchestratorSectionViewModel` and `SelectOrchestratorAsync`;
  - remove `RestoreOrchestratorAsync` (3160–3171) and the lock-monitor disposal (3110–3114);
  - update every remaining reader listed in the spec's "Seams" section;
  - change `SendTextToSessionAsync` to call `PromptDeliveryText.DescribeUndelivered`.
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/OrchestratorCompositionTests.cs`

**Interfaces:**
- Consumes: `SessionLauncher.StartProcessAsync` (Task 2).
- Produces:
  - `internal sealed class OrchestratorSupervisor(SessionRuntimeCoordinator runtimeCoordinator, ITerminalWebViewHost terminalHost, MainWindowViewModel viewModel, SessionLauncher launcher, AgentControlTokenRegistry agentControlTokens, Func<Uri> agentControlAddress, Func<string, IReadOnlyList<string>, Task<string?>> resolveCommandAsync, TimeProvider timeProvider, ObservedTaskGroup eventTasks, Func<string, Task> reportStatusAsync, Func<bool> isShutdownBegun, Func<bool> isTerminalHostInitialized) : IDisposable`
  - `const string SessionId = "pact-orchestrator";`
  - `OrchestratorRecord Record { get; }`
  - `SessionViewModel? Session { get; }`
  - `bool IsRunning { get; }`
  - `Task ApplySettingsAsync(OrchestratorRecord record, CancellationToken cancellationToken)`
  - `void AttachWorkstationLockMonitor(IntPtr windowHandle)`
  - `Task StartAsync(CancellationToken cancellationToken = default)`
  - `Task StopAsync()`
  - `Task DeliverWorkstationLockPromptAsync(bool locked)`
  - `Task<bool> RestoreAsync(CancellationToken cancellationToken)`
  - `Task<PromptDeliveryResult> SendMessageAsync(SessionViewModel target, string text, CancellationToken cancellationToken)`
  - `void Dispose()`: unsubscribes from and disposes the lock monitor, as lines 3110–3114 do today.
  - `StartAsync` keeps the order from Review Focus 2. It calls `launcher.StartProcessAsync(SessionId, AgentKind.Hermes, options, OnExited, ct)` in place of the copied handlers.
- Controller forwarders (one line each, logic-free):
  - `ApplyOrchestratorSettingsAsync`;
  - `AttachWorkstationLockMonitor`;
  - `IsOrchestratorRunning`;
  - `StartOrchestratorAsync`;
  - `StopOrchestratorAsync`;
  - `DeliverWorkstationLockPromptAsync`.

- [ ] **Step 1: Write the characterization test**

In `OrchestratorCompositionTests`:

```csharp
[Test]
public async Task Orchestrator_output_reaches_its_terminal_and_enter_resets_the_snapshot_baseline()
{
	await using CompositionFixture fixture = new();
	await fixture.InitializeAsync();

	fixture.OrchestratorBackend.EmitOutput("orchestrator says hi");
	// poll (condition, not sleep, 5 s budget) until
	// fixture.Host.WrittenOutput contains ("pact-orchestrator", text containing "orchestrator says hi")

	await fixture.Controller.WriteInputAsync("pact-orchestrator", "\r");

	fixture.Host.SnapshotBaselineResetSessions.ShouldContain("pact-orchestrator");
}
```

- [ ] **Step 2: Run it against the old code**

Run: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Orchestrator_output_reaches" -- NUnit.NumberOfTestWorkers=2`
Expected: PASS. Then do the mutation check: expect a different session id and watch the test fail.

- [ ] **Step 3: Create `OrchestratorSupervisor` and `PromptDeliveryText`, and switch the controller to them**

Construction order in the controller constructor:
- Create the supervisor after the launcher and before `OrchestratorHost`.
- `OrchestratorHost` receives:
  - `supervisor.SendMessageAsync`;
  - `() => supervisor.Session is null ? null : OrchestratorSupervisor.SessionId`.
- The soft-restart restorer receives:
  - `supervisor.RestoreAsync`;
  - an id check against `OrchestratorSupervisor.SessionId`.

Elsewhere in the controller:
- `InitializeAsync` keeps its start-on-initialize branch and reads `Record` and `IsRunning` from the supervisor.
- `SelectOrchestratorAsync` and the restore-selection branch read `Session` and `IsRunning` from the supervisor.
- The shutdown sequence calls `supervisor.StopAsync` and `supervisor.Dispose()` at the same points where it stops the orchestrator and disposes the lock monitor today.

- [ ] **Step 4: Run the App suite**

Expected: all pass, including every test in `OrchestratorCompositionTests`.

- [ ] **Step 5: Commit**

```bash
git add src/Pact.App.Avalonia/Controllers/OrchestratorSupervisor.cs src/Pact.App.Avalonia/Controllers/PromptDeliveryText.cs src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs tests/Pact.App.Avalonia.Tests/Controllers/OrchestratorCompositionTests.cs
git commit -m "refactor: supervise the orchestrator outside the shell controller"
```

---

### Task 4: `ReviewLaunchService`

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/ReviewLaunchService.cs`
- Modify:
  - `AvaloniaMainShellController.cs`: remove lines 2304–2483;
  - `src/Pact.App.Avalonia/Controllers/AgentControlHost.cs`: the constructor and line 138.
- Test: existing tests only.

**Interfaces:**
- Consumes: `SessionLauncher.ActivateAsync` (Task 2).
- Produces:
  - `internal sealed class ReviewLaunchService(MainWindowViewModel viewModel, AvaloniaScenarioCoordinator scenarioCoordinator, ReviewProfileProvider reviewProfiles, SessionRuntimeCoordinator runtimeCoordinator, ITerminalWebViewHost terminalHost, SessionLauncher launcher, AgentControlTokenRegistry agentControlTokens, Action throwIfRejectingInput)`
  - `Task<ReviewStartOutcome> StartAgentRequestedReviewAsync(string projectId, string authorSessionId, RequestReviewRequest request, CancellationToken cancellationToken)`: today's body, with its `CA2000` suppression attribute moved along with it.
  - `AgentControlHost(ReviewLaunchService reviews, MainWindowViewModel viewModel, IUiTaskDispatcher uiDispatcher)` replaces the `AvaloniaMainShellController shell` parameter.
  - The controller keeps `StartAgentRequestedReviewAsync` as a forwarder, because `AgentRequestedReviewStartTests` calls it.

- [ ] **Step 1: Run the guarding tests before the move**

Run: the App suite with `--filter "FullyQualifiedName~AgentRequestedReviewStartTests|FullyQualifiedName~AgentControlCompositionTests"`.

Expected: PASS. The tests that guard this task are:
- `Reviewer_that_never_becomes_ready_is_rolled_back`;
- `AFailedReviewerStartLeavesNoSessionControllerOrToken`;
- `ConcurrentReviewRequestsForOneProjectStartExactlyOneRun`;
- `ASecondRequestHeldAtTheReservationStageIsToldAStartIsInProgress`;
- `ASecondRequestAgainstARunningReviewIsToldItsRunId`;
- `AnAgentRequestedReviewLeavesTheSelectionWhereItWas`.

- [ ] **Step 2: Create `ReviewLaunchService` and switch `AgentControlHost` and the controller to it**

Create the service after the launcher and `_scenarioCoordinator`, and before `AgentControlHost`. Pass `ThrowIfRejectingInput` as the method group.

- [ ] **Step 3: Run the App suite**

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Pact.App.Avalonia/Controllers/ReviewLaunchService.cs src/Pact.App.Avalonia/Controllers/AgentControlHost.cs src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs
git commit -m "refactor: start agent-requested reviews from ReviewLaunchService"
```

---

### Task 5: `ResumeCaptureService`

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/ResumeCaptureService.cs`
- Modify: `AvaloniaMainShellController.cs`
  - remove `GracefulAgentExitTimeout` (line 41) and lines 3653–3785, except `ShowSessionTerminalUnderBusyAsync`, which stays;
  - change the callers at lines 2174, 2744 and 3069.
- Test: existing tests only.

**Interfaces:**
- Produces:
  - `internal sealed class ResumeCaptureService(SessionRuntimeCoordinator runtimeCoordinator, MainWindowViewModel viewModel, TimeProvider timeProvider, Func<SessionViewModel, Task> showSessionTerminalAsync, Func<string, Task> reportStatusAsync)`
  - `Task CaptureBeforeStopAsync(IEnumerable<SessionViewModel> sessions, CancellationToken cancellationToken)`: today's `CaptureResumeCommandsBeforeStopAsync`, with the 20 s deadline, the 75 ms submit gap, the 100 ms poll and the status texts unchanged.

- [ ] **Step 1: Run the guarding tests before the move**

Run: the App suite with `--filter "FullyQualifiedName~AvaloniaMainShellControllerTests"`.
Expected: PASS. This includes the `/exit`, "Resume capture timed out" and "Capturing resume session id" tests around `AvaloniaMainShellControllerTests.cs:2590`–`2680`.

- [ ] **Step 2: Create `ResumeCaptureService` and switch the three callers**

Pass `ShowSessionTerminalUnderBusyAsync` and `ReportStatusAsync` as method groups.

- [ ] **Step 3: Run the App suite**

Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Pact.App.Avalonia/Controllers/ResumeCaptureService.cs src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs
git commit -m "refactor: capture resume commands in ResumeCaptureService"
```

---

### Task 6: Docs and the full suite

**Files:**
- Modify: `docs/backlog/2026-10-14-architecture-analysis.md`. In step 5 under "Порядок рефакторинга", append `*Сделано 2026-10-22: … спека docs/superpowers/specs/2026-10-22-shell-controller-split-design.md.*`, in the same style as steps 1–4.
- Modify: `docs/architecture.md`, near line 326 ("The shell controller owns terminal/session shutdown"). Add one paragraph that names each new App-layer class and its responsibility:
  - `AgentControlToolRouter`;
  - `SessionLauncher`, the single process-start core for sessions and the orchestrator;
  - `OrchestratorSupervisor`;
  - `ReviewLaunchService`;
  - `ResumeCaptureService`.

- [ ] **Step 1: Edit both documents as described above**

- [ ] **Step 2: Run every suite, one at a time**

Run: build, then `dotnet test` for `tests/Pact.Core.Tests`, `tests/Pact.Presentation.Tests`, `tests/Pact.Infrastructure.Tests` and `tests/Pact.App.Avalonia.Tests`, using the Global Constraints command.

Expected: everything passes. Before this plan the counts were Core 531, Presentation 841 and App 608; App is now 608 + 7.

If an unrelated failure appears, run that test alone and record it in the ledger. Known flakes:
- `UpdatePackageStoreTests.Invalid_setup…("oversized")`;
- `Automatic_checks_start_after_30_seconds_and_then_run_hourly`.

- [ ] **Step 3: Check the controller size**

Run: `wc -l src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
Expected: roughly 3000 lines or fewer. Record the number in the ledger.

- [ ] **Step 4: Commit**

```bash
git add docs/backlog/2026-10-14-architecture-analysis.md docs/architecture.md
git commit -m "docs: record the shell controller split"
```
