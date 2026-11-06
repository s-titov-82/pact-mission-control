# ROOT Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans for native execution, or superpowers:subagent-driven-development if the user chooses delegation. Steps use checkbox syntax for tracking.

**Goal:** Open the global project dashboard from ROOT and return to predictable screens after closing or pausing the current context.

**Architecture:** Presentation projects the existing project/session models into dashboard cards. The shell owns an explicit PresentedSurface.Dashboard and navigation intent; Git uses a lightweight status query through the existing reader/runner/parser. The existing startup checkpoint and soft-restart selection carry dashboard selection without inventing a project owner.

**Tech Stack:** .NET 10, C#, Avalonia 12, NUnit/Shouldly, bundled ConPTY and native WebView2 unchanged.

**Spec:** [Approved design](../specs/2026-11-03-root-dashboard-design.md).

**Plan review:** Claude Opus approved through Pact on 2026-11-04, review
`96eee39981124f3c86d7a8b5c8277bfa`, iteration 2. All 5 findings (3 major,
2 minor) were addressed in this plan; no findings remain. This approves the
plan only: implementation and its tests have not run.

**Implementation:** implemented and locally verified 2026-11-04. Full source review was explicitly authorized and started through Claude Opus/Pact. All seven findings are resolved; Claude Opus approved iteration 2 on 2026-11-04. No commit/push performed.

- [x] Independent final code review: Claude Opus approved iteration 2; no open findings.

## Global Constraints

- ROOT header opens the dashboard; no Home button. Expander and add buttons retain independent actions.
- Only open projects, in tree order; only agent sessions with an attached live runtime in the card's agent list.
- Existing unread, Read later, review, process and document ownership remain authoritative.
- Changes count unique staged/unstaged/untracked paths; unknown/error must not appear clean.
- Git reconciliation while visible: 5 seconds, at most two concurrent queries, no overlapping query per project.
- Explicit UI dispatch; version/owner/root checks after asynchronous work; no timing sleeps in tests.
- Startup restores existing live ids; selecting the dashboard never starts extra processes.
- Public APIs require XML documentation. No dependencies or persisted terminal text added.
- Preserve the pending backlog edit. Commit design/plan only together with implementation, and only when authorized.
- One heavy command at a time. Build: `rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`.
- Restore only if needed: `rtk dotnet restore Pact.slnx --disable-parallel --locked-mode`.
- Tests use existing VSTest/NUnit setup, `--no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.

## Review Focus

1. A slow close/resume capture finishes after another selection: preserve the newer surface and do not launch a replacement (Task 3).
2. Dirty Notes or a selected browser when ROOT is opened: flush through existing ownership, hide native host, retain unread/bookmarks (Tasks 2, 5).
3. Paused/deleted project or changed root during Git I/O: reject the old result and detach subscriptions (Task 2).
4. Missing/legacy checkpoint selection: show dashboard without cold-starting a saved adjacent session (Task 4).
5. Nested ROOT controls and nested card actions: exactly one intended action, including keyboard activation (Task 5).

## Task 1: Lightweight Git summary

**Files:**
- Modify `src/Pact.Infrastructure/Git/GitWorkingTreeReader.cs`.
- Modify `tests/Pact.Infrastructure.Tests/Git/GitWorkingTreeReaderTests.cs` and existing reader fakes implementing the interface.

**Interfaces:** Add `Task<int?> ReadChangedFileCountAsync(string root, CancellationToken cancellationToken)` to `IGitWorkingTreeReader` and its implementation. Null means unavailable. Existing full changes/diff methods keep their contracts.

- [x] Add failing behavioral tests: clean returns `0`; staged+unstaged same file plus untracked returns `2`; untracked nested files are counted individually; rename is one path; unborn HEAD works; nonrepository/failing status returns null.
- [x] Run the affected Infrastructure fixture and confirm the missing behavior fails.
- [x] Implement through existing `GitCliRunner`, `GitStatusParser`, `--no-optional-locks`, porcelain v2 and `--untracked-files=all`. Count distinct paths; do not run diff/numstat for this method.
- [x] Verify those cases and retain existing full-reader behavior. Any fake implementations fail explicitly for unused summary reads or provide a controlled result, rather than silently reporting clean.

## Task 2: Dashboard cards and bounded refresh

**Files:**
- Create `src/Pact.Presentation/ViewModels/RootDashboardViewModel.cs`.
- Create `src/Pact.Presentation/ViewModels/ProjectDashboardCardViewModel.cs`.
- Create `tests/Pact.Presentation.Tests/ViewModels/RootDashboardViewModelTests.cs`.
- Create `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.Dashboard.cs`; modify shell composition and disposal in `AvaloniaMainShellController.cs`.
- Modify `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs` and `src/Pact.App.Avalonia/Controllers/PresentedSurface.cs`; extend their existing model/controller tests.

**Interfaces:**
- `RootDashboardViewModel` consumes open `WorkspaceViewModel` collection, `IGitWorkingTreeReader`, `Func<SessionViewModel, bool> isLiveAgent`, UI dispatcher and `TimeProvider` using repository injection conventions.
- Exposes `IReadOnlyList<ProjectDashboardCardViewModel> Cards`, `bool IsEmpty`, `Task ActivateAsync(CancellationToken)`, `void Deactivate()`, `void RefreshAgents()` and `Dispose()`.
- Cards expose `WorkspaceViewModel Workspace`, `IReadOnlyList<SessionViewModel> Agents`, `string ChangesText`, `bool HasGitRepository`; navigation keeps the original owner/target identity.
- Shell exposes `RootDashboardViewModel Dashboard`, `bool IsDashboardVisible`, `Task OpenDashboardAsync(CancellationToken cancellationToken = default)`.
- This task defines `MainWindowViewModel.SelectDashboard()` (clears all selected owner/item fields and updates action targets without selecting a replacement) and `PresentedSurface.Dashboard`. Task 3 consumes these APIs. Actual view/visibility mapping remains in Task 5; no forward dependency on Task 3 is required to compile Task 2.

- [x] Add failing tests with controlled reader completions and existing fake clock: card order/membership; no live agent despite saved Running; shell/custom/browser excluded; existing status/bookmark changes project through original SessionViewModel; no agents still leaves a card.
- [x] Add deterministic tests: initial Checking, clean/dirty/error text, no-Git omission, max concurrency `2`, no per-root overlap, 5-second reconciliation, no new refresh while hidden, no stale publication after root change/pause/removal/reopen/disposal.
- [x] Implement cards as projections and one visibility-owned reconciliation loop. Use Task 1's lightweight summary; detach removed owners and bound queued work. Gate UI publication by generation, current owner/root and disposed/visible state.
- [x] Shell supplies actual runtime membership with existing `HasActiveTerminalProcess` and agent-kind checks; refresh membership from existing session lifecycle/status events, including process exit. No independent poller for agent status.
- [x] Add `PresentedSurface.Dashboard`, clear selected item/owner via `MainWindowViewModel.SelectDashboard()`, hide browser and selection popover, flush current document, cancel obsolete activation, publish explicit surface. Switching away deactivates refresh; no runtime stops and no unread acknowledgement.
- [x] Run new Presentation tests and shell tests for hidden native host, dirty-document flush, retained unread/Read later and disabled session-target actions.

## Task 3: Deterministic close/pause transitions

**Files:**
- Modify `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs`.
- Modify `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` and `.Dashboard.cs`.
- Modify `src/Pact.App.Avalonia/Controllers/ResumeCaptureService.cs` and `.ProjectScreen.cs` only where operation-owned presentation must carry/check the navigation intent; extend the existing resume-capture tests.
- Extend `tests/Pact.Presentation.Tests/ViewModels/MainWindowViewModelTests.cs`.
- Extend `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`.

**Interfaces:** Append optional `bool selectReplacement = true` after the existing cancellation argument on `RemoveSessionAsync`, `RemoveWebPageAsync`, `RemoveWorkspaceAsync`, `PauseWorkspaceAsync`, `HideNotesTabAsync`, and propagate to private ROOT removal. Existing callers keep their behavior; shell close/pause/legacy Notes-hide paths pass false. Consume Task 2's `SelectDashboard()`.

Navigation ownership uses a shell intent generation separate from `_projectNavigationVersion`. Explicit navigation requests (tree, card, document/tab selection, ROOT) invalidate the previous operation's intent; internal `Present`, `SelectedSession`, `ReleaseProjectScreen` and host restoration do not. Capture the original surface and owner/item identities before the first await, then carry the operation's intent through resume presentation and final return. Extend `ResumeCaptureService.CaptureBeforeStopAsync` with an optional operation-scoped `Func<SessionViewModel, Task>` presentation override, using the existing constructor delegate when absent (shutdown keeps its current behavior). The operation override validates intent before changing selection/surface and before showing the native terminal. If a newer navigation request has superseded it, capture may continue without taking over presentation.

- [x] Pin failures for selected terminal/browser close: project goes Overview; ROOT goes Dashboard; no second runtime starts. Selected project close/pause goes Dashboard, including a project with no tabs.
- [x] Pin the retained compatibility path `ToggleNotesAsync`/`HideNotesTabAsync`: hiding a selected legacy Notes item goes Overview without starting a runtime; hiding a background legacy item preserves the current screen. Notes are no longer in `WorkspaceViewModel.TreeItems`, so do not restore the removed Notes tree row or toggle UI to exercise this path.
- [x] Pin failures for background close/pause: original terminal, browser or project tab remains selected/presented; no unrelated document is flushed as a side effect of fallback.
- [x] Use controlled close/persistence/resume-capture completions: select another context while awaiting; after release assert the newer selection/surface persists. Include same-owner switch, switch to ROOT, and closing an active review participant whose relay ends asynchronously.
- [x] Add explicit current/background project-pause cases with a live Claude/Codex session that requires resume capture. Starting on another project's terminal, browser, Docs, or Dashboard must return to that exact original context after internal capture presentation; starting inside the paused project must end at Dashboard. Repeated internal Present/SelectedSession changes must not invalidate the operation's own intent. A real later navigation must win, including against a delayed native ShowTerminal callback.
- [x] Suppress model-level replacement in these shell operations; clear removed selection only if it is still selected after persistence. While the operation still owns intent, restore the captured original surface/selection for a background close/pause (without cold start), or show Overview/Dashboard for a removed current context. Never infer supersession from an arbitrary selection difference or `_projectNavigationVersion` change: resume capture and cleanup themselves change both. Do not call generic `RestoreSelectedHostAsync` on the transient capture-selected session. If intent was superseded, leave the newer context untouched, including during subsequent capture/cleanup callbacks. An error/cancellation restores the still-valid original context only while that intent is current; otherwise preserve the new choice.
- [x] Preserve ROOT per-item Pause placeholder and existing review-abort/termination sequencing. Do not change stop semantics or generic restoration into an implicit session start.
- [x] Run affected model and controller tests; update previous tests that intentionally asserted the superseded adjacent-selection policy only where shell behavior changes.

## Task 4: Checkpoint and restart selection

**Files:**
- Modify `src/Pact.Core/Updates/SoftRestartSelection.cs`.
- Modify `src/Pact.Infrastructure/Updates/SoftRestartTicketStore.cs` and `src/Pact.Infrastructure/Storage/SessionRestorationStore.cs` for explicit selection validation.
- Modify `src/Pact.App.Avalonia/Controllers/SoftRestartSnapshotBuilder.cs`, `AvaloniaMainShellController.StartupState.cs`, `AvaloniaMainShellController.cs`.
- Extend `tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartSnapshotBuilderTests.cs`, `SoftRestartRestorerTests.cs`, `SessionRestorationCheckpointTests.cs`, `AvaloniaMainShellControllerTests.cs`.
- Extend `tests/Pact.Infrastructure.Tests/Storage/SessionRestorationStoreTests.cs` and `Updates/SoftRestartTicketStoreTests.cs` where their validation covers selection.

**Interfaces:** Append optional `bool IsDashboard = false` to `SoftRestartSelection`. Snapshot builder consumes an optional `Func<bool> isDashboardVisible`; captures `new SoftRestartSelection(null, null, IsDashboard: true)` before item fallbacks. No schema version bump for this optional additive field. Update `SoftRestartTicketStore.ValidateCollections` explicitly: dashboard selection is valid only with null project/item/tab/path; the existing owner-or-item requirement applies only when `IsDashboard == false`. Dashboard combined with any owner/item/tab/path is rejected, not silently given precedence. Ordinary checkpoint loading applies the same selection-shape rule (invalid checkpoint yields no restoration); null selection and legacy non-dashboard shapes retain compatibility. Ticket validation still rejects malformed tickets, without rejecting a valid dashboard ticket and losing its live ids.

- [x] Assert dashboard capture has null project/item, retains live ids/unread independently, and round-trips in checkpoint and update ticket. Legacy absent field remains false; existing project Docs/path and paused ROOT restoration still work.
- [x] Assert a dashboard ticket with live ids passes validation and restores those ids; dashboard+owner, dashboard+item, dashboard+tab/path are invalid; non-dashboard selection with neither owner nor item remains invalid. Checkpoint invalid-shape handling follows its load-as-null policy.
- [x] Assert ordinary and soft restore select Dashboard when explicitly saved; missing/deleted/paused selected owner falls back to Dashboard while preserving the existing restoration failure summary. No checkpoint does not cold-start a saved adjacent session.
- [x] Replace `InitializeAsync`'s no-ticket/no-checkpoint selection branch: retain configured orchestrator autostart, but remove `SelectSessionAsync(startIfNeeded: true)`, `SelectWebPageAsync` and `SelectWorkspaceAsync` driven by model-loaded ActiveItemId; call `OpenDashboardAsync` instead. A saved model selection is not evidence of an active runtime or successfully restored surface. Pin this behavior with a saved ActiveItemId pointing to an unstarted terminal and a saved browser, asserting zero extra process starts/host loads.
- [x] Implement dashboard selection routing before owner/item lookup. After a checkpoint/ticket restore, retain selection only if `RestoreSelectionAsync` succeeded; null, missing or failed selection falls back to `OpenDashboardAsync` while retaining existing active-id restoration and failure reporting. Explicit dashboard selection must cause checkpoint publication even though all item fields are null.
- [x] Run storage and restoration fixtures; assert selected-dashboard startup does not acknowledge unread before final selection and does not clear manual bookmarks.

## Task 5: ROOT interaction, view and final integration

**Files:**
- Create `src/Pact.App.Avalonia/Views/RootDashboardView.axaml` and `.axaml.cs`.
- Modify `Views/ProjectTreeView.axaml`, `.axaml.cs`, `Views/MainWindow.axaml`, `.axaml.cs`, `Views/CenterPaneVisibility.cs` under `src/Pact.App.Avalonia`.
- Create `tests/Pact.App.Avalonia.Tests/Views/RootDashboardViewHeadlessTests.cs`; extend `ProjectTreeHeaderHeadlessTests.cs`, `CenterPaneVisibilityTests.cs`.
- Update `README.md`, `README.ru.md`, `docs/architecture.md`, `docs/agent-onboarding.md`, `docs/backlog/2026-10-25-next-steps.md`, approved spec and this plan as implemented results warrant.

**Interfaces:** `ProjectTreeView.RootDashboardRequested` event; `RootDashboardView` emits typed workspace/session/Changes navigation events and `AddProjectRequested`. MainWindow routes through existing observed action wiring and shell APIs. `CenterPaneVisibility` adds explicit `Dashboard` flag, never exposing Browser/Terminal simultaneously.

- [x] Add failing headless tests: header/keyboard activation opens Dashboard; add/expander controls do not; current highlight follows Dashboard and leaves on another surface. Card title, session and Changes actions each emit only their intended event; removed owner/target cannot navigate.
- [x] Implement responsive wrapping cards with scroll, existing theme tokens, focusable controls and English text. Empty screen uses the existing add/open project action. No extra Home button, duplicated attention feed, shell list or new MCP action.
- [x] Wire explicit Dashboard center visibility and native host hiding, event detachment and disposal. Reuse standard project/session activation and tree reveal behavior for card actions; revalidate target ownership at activation time.
- [x] Check headless layout at narrow/wide widths and keyboard traversal; capture a standalone rendered screenshot if supported. Run relevant controller, tree, visibility, dashboard and restoration fixtures, then the bounded native WebView/soft-restart gate with an isolated data root. Record any unavailable native/visual gate as NOT-RUN with reason.
- [x] Update documentation and backlog: first item implemented, Ctrl+V becomes next; retain completed feature summary and remaining agreed order. Record actual verification evidence and unresolved limits.
- [x] Run `rtk git diff --check HEAD -- <touched paths>`, inspect accumulated diff including second-order selection and subscription behavior. No standalone spec/plan commit.

## Verification commands and execution notes

NUnit/VSTest was confirmed from global.json, Directory.Build.props, Directory.Packages.props and tests/Directory.Build.props; no run-tests overlay exists. Use this command shape with the task's fixture names:

```powershell
rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~GitWorkingTreeReaderTests" -- NUnit.NumberOfTestWorkers=2
rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~RootDashboardViewModelTests|FullyQualifiedName~MainWindowViewModelTests" -- NUnit.NumberOfTestWorkers=2
rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AvaloniaMainShellControllerTests|FullyQualifiedName~SoftRestart|FullyQualifiedName~SessionRestorationCheckpointTests|FullyQualifiedName~CenterPaneVisibilityTests|FullyQualifiedName~RootDashboardViewHeadlessTests|FullyQualifiedName~ProjectTreeHeaderHeadlessTests" -- NUnit.NumberOfTestWorkers=2
```

Run commands sequentially; if headless fixture aggregation exceeds the existing heap cap, split by fixture and report the limitation. Additional storage tests must run when Task 4 changes their contract. Test names/counts and exit codes are required evidence; this plan is not evidence of execution.

Self-review: each approved spec section maps to Tasks 1–5; ownership/navigation races and cold-start fallback are explicitly tested. The tasks share shell/selection interfaces, so native execution in this session is recommended. A final independent accumulated-diff review follows implementation.
