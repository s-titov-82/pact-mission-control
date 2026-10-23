# Presented Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the shell's five "what is shown" properties and two private fields with one `PresentedSurface` value, and ship the three agreed behavior changes.

**Architecture:** `PresentedSurface` is a closed record hierarchy in `Pact.App.Avalonia.Controllers`. The controller assigns it whole through one private `Present(...)` method. The old properties become read-only and computed from it. `MainWindow` maps it to pane visibility through a pure `CenterPaneVisibility.For(...)` function.

**Tech Stack:** .NET 10, C#, Avalonia, NUnit + Shouldly, Avalonia.Headless.

**Spec:** `docs/superpowers/specs/2026-10-23-presented-surface-design.md`

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`.
- Tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`. Run one heavy build at a time.
- No timing-sleep tests. Wait on conditions and signals only; use `ManualTimeProvider` for deadlines.
- Existing test assertions do not change. A test may change only how it reaches what it already tests.
- No user-visible change beyond the spec's three agreed changes. That includes pane visibility, the loading surface, focus, status texts and the persisted `ActiveItemId`.
- `IsTerminalVisible`, `IsPausedItemVisible`, `CurrentDocsAndNotes`, `CurrentNoteDocument` and `SelectedScenarioRun` stay on the controller as read-only properties computed from `PresentedSurface`.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. Commit on `main`, no push.

## Review Focus

1. **Thread of a surface change.** Loading clears from `OnScreenSnapshotReceived`. If that runs off the UI thread, a `PropertyChanged(PresentedSurface)` raised there must not touch window controls directly. Today `OnTerminalLoadingChanged` posts to `Dispatcher.UIThread` for this reason. Expected: the window marshals surface changes to the UI thread when it is not on it. Pinned by Task 2, test `Loading_cleared_off_the_ui_thread_updates_panes_on_the_ui_thread`.
2. **Selecting the same loading terminal again** while it is still loading keeps the loading surface. It must not flash the pane or fire `TerminalLoadingChanged(false)`. Pinned by Task 2, test `Reselecting_a_loading_terminal_keeps_its_loading_surface`.
3. **Leaving notes for a session and coming back.** The notes workspace's `PropertyChanged` subscription and `InvalidateSelectionIfSourceChanged()` run on every change of the shown workspace, including leaving `Notes`. Expected: no stale subscription, and a selection action whose source was the notes is invalidated. Pinned by Task 2, test `Leaving_notes_unsubscribes_and_invalidates_a_notes_selection`.
4. **A pending focus after the surface moved on.** The user selects a starting terminal, then a web page before loading clears. Expected: no terminal focus when loading clears. Pinned by Task 2, test `Pending_focus_is_dropped_when_the_surface_moves_on`.
5. **Pausing a project whose selected item capture does not touch** (a web page, or a session that is not eligible). Expected: that item is persisted as the active item. Pinned by Task 4.

---

### Task 1: The value and the pane mapping

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/PresentedSurface.cs`
- Create: `src/Pact.App.Avalonia/Views/CenterPaneVisibility.cs`
- Test: `tests/Pact.App.Avalonia.Tests/Views/CenterPaneVisibilityTests.cs`
- Also commit: `docs/superpowers/specs/2026-10-23-presented-surface-design.md`, `docs/backlog/2026-10-09-development-directions.md`

**Interfaces:**
- Produces:
  ```csharp
  public abstract record PresentedSurface
  {
      public sealed record Terminal(SessionViewModel Session, bool Loading, bool FocusWhenLoaded) : PresentedSurface;
      public sealed record PausedSession(SessionViewModel Session) : PresentedSurface;
      public sealed record PausedWebPage(WebPageViewModel WebPage) : PresentedSurface;
      public sealed record Notes(ProjectNoteViewModel Note, DocsAndNotesWorkspaceViewModel Workspace, ProjectNoteDocument Document) : PresentedSurface;
      public sealed record ScenarioRun(ScenarioRunViewModel Run) : PresentedSurface;
      public sealed record Web : PresentedSurface;
      public sealed record None : PresentedSurface;
  }
  internal readonly record struct CenterPaneVisibility(
      bool Terminal, bool TerminalLoading, bool Paused, bool Notes, bool Journal, bool Browser, bool Empty)
  {
      public static CenterPaneVisibility For(PresentedSurface surface, bool hasSelectedWebPage);
  }
  ```
  The type is public because the controller's property is public. View models compare by reference, which is the records' default for these members. Add a short summary on each case, saying what it shows.

- [ ] **Step 1: Write the failing table test** `CenterPaneVisibilityTests.Each_surface_shows_exactly_its_pane`. Use `[TestCaseSource]` with one row per line of the spec table:
  - Terminal not loading → `Terminal`;
  - Terminal loading → `TerminalLoading`;
  - PausedSession → `Paused`, and the same for PausedWebPage;
  - Notes → `Notes`;
  - ScenarioRun → `Journal`;
  - Web with a selected page → `Browser`; Web without one → `Empty`;
  - None with a selected page → `Browser`; None without one → `Empty`.

  Each row asserts that the named flag is true and every other flag is false. Build view models with the simplest constructors the existing tests use (`new SessionViewModel(record)` and similar).
- [ ] **Step 2: Run it.** Run: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`. Expected: build FAIL, `PresentedSurface` / `CenterPaneVisibility` not found.
- [ ] **Step 3: Implement both files** with the signatures above. `For` is a `switch` over the case.
- [ ] **Step 4: Run the test.** Run: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --filter FullyQualifiedName~CenterPaneVisibilityTests -- NUnit.NumberOfTestWorkers=2`. Expected: all rows PASS.
- [ ] **Step 5: Commit** the two source files, the test, the spec and the backlog edit: `refactor: add PresentedSurface and the center pane mapping`.

### Task 2: The controller owns the surface; the window draws from it

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`:
  - properties at :372–426;
  - the `Select*` methods at :609–782;
  - `ToggleNotesAsync` :1653, `PauseRootSessionAsync` :1734, `PauseRootWebPageAsync` :1820, `CloseNoteAsync` :1857, `CloseScenarioRunAsync` :1902;
  - shutdown :2083, `RestoreSelectedHostAsync` :2305, `ShowSessionTerminalUnderBusyAsync` :2491, `FocusTerminalAfterLoadingAsync` :2547, `SetTerminalLoading`/`ClearTerminalLoading` :2968–2999.
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs`: `OnControllerPropertyChanged` :1239–1292, `RefreshPaneVisibility` :1336, `OnTerminalLoadingChanged` :1392.
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`, `tests/Pact.App.Avalonia.Tests/Views/MainWindowPaneVisibilityHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 1's `PresentedSurface`, `CenterPaneVisibility.For`.
- Produces:
  - `public PresentedSurface PresentedSurface { get; }` on `AvaloniaMainShellController`, raising `PropertyChanged(nameof(PresentedSurface))`;
  - the five old properties are get-only and computed;
  - `TerminalLoadingChanged` keeps its signature and fires only when the effective loading state (`PresentedSurface is Terminal { Loading: true }`) flips.

- [ ] **Step 1: Write the failing tests** in `AvaloniaMainShellControllerTests`:
  - `Showing_a_web_page_drops_terminal_loading_and_pending_focus`:
    - select `session-2` with `startIfNeeded: true`, so it is loading;
    - subscribe to `TerminalLoadingChanged`;
    - `SelectWebPageAsync(web-1)`;
    - assert the changes are `[false]` and `PresentedSurface` is `PresentedSurface.Web`.
  - `Pending_focus_is_dropped_when_the_surface_moves_on`:
    - select loading `session-2`;
    - select `web-1`;
    - count `fixture.Host` focus calls (use the fake's existing focus record; add a counter to `FakeTerminalWebViewHost` if there is none);
    - raise a stable snapshot for `session-2`;
    - `await FocusTerminalAfterLoadingAsync()`;
    - assert there is no new terminal focus.
  - `Reselecting_a_loading_terminal_keeps_its_loading_surface`:
    - select loading `session-2`, record `TerminalLoadingChanged`;
    - select `session-2` again;
    - assert no further events and `PresentedSurface` is `Terminal { Loading: true }`.
  - `Leaving_notes_unsubscribes_and_invalidates_a_notes_selection`: show notes; open a selection action whose source is the notes (follow how existing notes-selection tests do it); select `session-1`; assert `IsNotesSelectionSource` is false and the selection actions are closed.
  - `Pausing_a_root_session_from_notes_shows_only_its_paused_placeholder`:
    - fixture with `firstResumeCommand: string.Empty` and a ROOT Codex session with an empty resume command, not paused. Build its `RootTabsRecord` like `Initialize_selects_paused_root_session_without_starting_it`, with an empty paused list;
    - select the ROOT session so it starts;
    - show project notes and select them;
    - `PauseRootSessionAsync(root)`;
    - assert `PresentedSurface` is `PausedSession` of that session, `CurrentDocsAndNotes` is null, `IsPausedItemVisible` is true, and `ViewModel.SelectedSession` is the root session.
  - `Pausing_a_root_session_from_notes_after_a_capture_timeout_shows_only_its_paused_placeholder`: same, with a ROOT resume command of `"codex resume"`, `firstResumeCommand: "codex resume"` (no exit response) and a `ManualTimeProvider` advanced 20 s after `WaitForTimerCreatedAsync`, as in `ShutdownUsesOneDeadlineForAllResumeCaptureAttemptsAndStillStopsSessions`. Same asserts.

  In `MainWindowPaneVisibilityHeadlessTests`, add `Loading_cleared_off_the_ui_thread_updates_panes_on_the_ui_thread`:
  - select a starting session;
  - raise its stable snapshot from `Task.Run`;
  - run `Dispatcher.UIThread.RunJobs()`;
  - assert `window.IsTerminalPaneVisible` is true and the loading surface is hidden. Add an `IsTerminalLoadingSurfaceVisible` accessor next to the existing `Is*PaneVisible` ones if it is missing.
- [ ] **Step 2: Run them.** Run the build, then: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AvaloniaMainShellControllerTests|FullyQualifiedName~MainWindowPaneVisibilityHeadlessTests" -- NUnit.NumberOfTestWorkers=2`. Expected: the build fails on `PresentedSurface` missing on the controller. After stubbing the property to return `new PresentedSurface.None()`, the new tests FAIL on their surface asserts. The two ROOT-pause tests FAIL on `CurrentDocsAndNotes.ShouldBeNull()`.
- [ ] **Step 3: Implement in the controller.**
  - Add `private void Present(PresentedSurface next)`. It returns if `next == current`. Otherwise it stores `next` and then:
    - if the shown notes workspace changed (`(current as Notes)?.Workspace` vs `(next as Notes)?.Workspace`), moves the `OnDocsAndNotesPropertyChanged` subscription and calls `InvalidateSelectionIfSourceChanged()`;
    - raises `PropertyChanged` for `PresentedSurface` and for each of the five computed properties whose value changed;
    - fires `TerminalLoadingChanged(isLoading)` if the effective loading flipped.
  - Replace every assignment listed in the spec's "Places that assign the value" table with `Present(...)`.
  - `SetTerminalLoading(id, bool)` and `ClearTerminalLoading()` become updates of the `Terminal` case's `Loading` flag, and only when the surface is `Terminal` for that session.
  - The focus step in `SelectSessionAsync` sets `FocusWhenLoaded` on the current `Terminal` case when it is for this session and loading.
  - `FocusTerminalAfterLoadingAsync` focuses only when the surface is `Terminal { FocusWhenLoaded: true }` for `_runtimeCoordinator.ActiveSessionId`. It clears the flag first.
  - Delete `_loadingTerminalSessionId` and `_pendingTerminalFocusSessionId`.
  - Shutdown keeps `InvokeUiActionAsync`: `Present(new None())` there, only when the surface is `Terminal`.
- [ ] **Step 4: Implement in the window.**
  - `OnControllerPropertyChanged` handles `nameof(PresentedSurface)` instead of the four names. It runs inline when `Dispatcher.UIThread.CheckAccess()` and posts otherwise.
  - `NotesPane.Workspace`, `RightActions.Workspace`, `ScenarioJournal.DataContext` and the editor focus at `DispatcherPriority.Loaded` keep their current behavior, keyed on the `Notes` and `ScenarioRun` cases.
  - `RefreshPaneVisibility` applies `CenterPaneVisibility.For(surface, ViewModel.SelectedWebPage is not null)` to `TerminalPane`, `TerminalLoadingSurface`, `PausedPane`, `NotesPane`, `ScenarioJournal`, `BrowserPane` and `EmptyPane`, and keeps `BrowserPane.Page = ViewModel.SelectedWebPage`.
  - `OnTerminalLoadingChanged` keeps only the focus hook: when `!isLoading`, it calls `FocusTerminalAfterLoadingAsync`.
- [ ] **Step 5: Run the App tests.** Run: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`. Expected: all PASS, including the existing visibility, loading (`Terminal_loader_covers_a_starting_runtime_until_its_first_stable_screen`), paused and notes tests, unchanged. Known flakes that pass alone: `An_unexpected_exit_restarts_the_enabled_slot` and `StayingHiddenAfterNavigationRequestsNoNativeRepaint`.
- [ ] **Step 6: Commit:** `refactor: present the center pane from one PresentedSurface value`.

### Task 3: The paused placeholder shows what is paused

**Files:**
- Create: `src/Pact.App.Avalonia/Views/PausedPlaceholderText.cs`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml:24–34`, `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs` (`RefreshPaneVisibility`)
- Test: `tests/Pact.App.Avalonia.Tests/Views/PausedPlaceholderTextTests.cs`, `MainWindowPaneVisibilityHeadlessTests`

**Interfaces:**
- Consumes: `PresentedSurface.PausedSession`, `PresentedSurface.PausedWebPage`.
- Produces: `internal static class PausedPlaceholderText { public static string For(PresentedSurface surface); }`. It returns `""` for any case other than the two paused ones.

- [ ] **Step 1: Write the failing tests:**
  - `A_paused_session_names_its_kind_and_directory`: a Codex session with working directory `C:\work\repo` → `"Paused — Codex, C:\work\repo"`. Use the agent kind's display name if the tree already has one; otherwise `AgentKind.ToString()`. Pin whichever you use.
  - `A_paused_web_page_names_its_address`: `ResumeUrl` `https://example.test/wiki` → `"Paused — https://example.test/wiki"`.
  - Headless test `Paused_root_item_shows_the_logo_and_its_caption`: with the ROOT session paused (fixture as in `Initialize_selects_paused_root_session_without_starting_it`), the `PausedPane` caption `TextBlock` text equals `PausedPlaceholderText.For(surface)`.
- [ ] **Step 2: Run them.** Expected: build FAIL, `PausedPlaceholderText` not found.
- [ ] **Step 3: Implement.**
  - In `MainWindow.axaml`, `PausedPane` gets a black background, an `Image` of `/Assets/AppIcon.png` at about 96 px, and a caption `TextBlock` named `PausedCaption`.
  - Keep the Resume hint as a smaller muted second line.
  - `RefreshPaneVisibility` sets `PausedCaption.Text = PausedPlaceholderText.For(surface)`. The separator is the em dash `—`.
- [ ] **Step 4: Run the App tests.** Expected: all PASS.
- [ ] **Step 5: Commit:** `feat: show what is paused on the paused placeholder`.

### Task 4: A paused project remembers what the user had selected

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs:1342–1357` (`PauseWorkspaceAsync`)
- Test: `AvaloniaMainShellControllerTests`

- [ ] **Step 1: Write the failing test** `Pausing_a_project_keeps_the_item_selected_before_capture_as_its_active_item`:
  - fixture `firstResumeCommand: string.Empty`, so `session-1` is a live, eligible Codex session;
  - initialize; `SelectWebPageAsync(web-1)`;
  - `PauseWorkspaceAsync(workspace)`;
  - assert the stored project's `ActiveItemId` is `"web-1"`. Read it through `fixture.Store`'s document the same way existing pause tests read persisted state.
- [ ] **Step 2: Run it.** Expected: FAIL, `ActiveItemId` is `"session-1"`, the last captured session.
- [ ] **Step 3: Implement.** At the top of `PauseWorkspaceAsync`, before `InvalidateSelectionOwner`, read `ViewModel.SelectedSession?.Record.Id ?? ViewModel.SelectedWebPage?.Record.Id ?? ViewModel.SelectedProjectNote?.Record.Id`. Pass it to `ViewModel.PauseWorkspaceAsync`. `ProjectStructurePersistenceCoordinator.PauseProjectAsync` already drops an id the project does not own.
- [ ] **Step 4: Run the App tests.** Expected: all PASS, including `PausingWorkspaceCapturesConcreteResumeIdBeforeStoppingCodex` and the other `PauseWorkspaceAsync` tests unchanged.
- [ ] **Step 5: Commit:** `fix: keep the user's selection as a paused project's active item`.

### Task 5: Docs

**Files:**
- Modify: `docs/backlog/2026-10-14-architecture-analysis.md` (step 6 at :301), `docs/architecture.md` (the step-5 paragraph after "Lifetime ownership")

- [ ] **Step 1:** In the analysis doc, mark step 6 *Сделано 2026-10-23*. Name `PresentedSurface` and `CenterPaneVisibility`. Note that view-model selection stays as it was, and link the spec. Add one sentence to `docs/architecture.md`: the center pane is drawn from the controller's single `PresentedSurface`, which also carries the terminal's loading and pending-focus state.
- [ ] **Step 2: Run the full suites.** Run the build, then `dotnet test` for `tests/Pact.Core.Tests`, `tests/Pact.Presentation.Tests`, `tests/Pact.Infrastructure.Tests`, `tests/Pact.Updater.Tests` and `tests/Pact.App.Avalonia.Tests`, one at a time. Expected: all PASS.
- [ ] **Step 3: Commit:** `docs: record the presented surface`.
