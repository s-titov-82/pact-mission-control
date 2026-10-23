# One value for what the center pane shows

Created: 2026-10-23.

Status: direction agreed in conversation on 2026-10-23; step 6 of
[the architecture analysis](../../backlog/2026-10-14-architecture-analysis.md).

## Why

What the center pane shows is spread across five shell properties and two
private fields in `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`:

- `IsTerminalVisible`, `IsPausedItemVisible`, `CurrentDocsAndNotes`,
  `CurrentNoteDocument`, `SelectedScenarioRun`;
- `_loadingTerminalSessionId` and `_pendingTerminalFocusSessionId`.

About thirteen places set some of them by hand: the five `Select*` methods,
pausing a ROOT session or web page, `ToggleNotesAsync`, `CloseNoteAsync`,
`CloseScenarioRunAsync`, `RestoreSelectedHostAsync`,
`ShowSessionTerminalUnderBusyAsync` and shutdown. Each place has to remember to
reset the fields that belong to the other kinds of tab. Setters have side
effects: `IsTerminalVisible = false` also clears the loading surface. Then
`MainWindow.RefreshPaneVisibility` rebuilds which pane to show from those
fields with implicit priorities. The browser, for example, is shown when the
view model has a selected page and neither the terminal nor the paused
placeholder is visible.

The two focus bugs fixed on 2026-10-13 (`98c7075`, `99ab25c`) came from this
arrangement. The user has seen no presentation or focus problems since. This
is mainly an internal refactor that makes the next bug of this kind harder to
write. It also carries three small behavior changes the user asked for while
the design was discussed (see "Agreed behavior changes").

Success means:

- what is shown is one value, assigned whole;
- the terminal's loading state and its pending focus belong to that value, so
  they disappear when something else is shown;
- the window decides which pane is visible from that value alone, plus the
  view model's selected web page where it does so today;
- nothing the user can see changes, except the three agreed changes.

## Goals

### The value

A closed set of cases, named `PresentedSurface`, in
`src/Pact.App.Avalonia/Controllers/`:

| Case | Carries | Set by today's |
|---|---|---|
| `Terminal` | the session; whether its loading surface is up; whether focus is waiting for loading to clear | `IsTerminalVisible = true` |
| `Paused` | the paused ROOT item (session or web page) | `IsPausedItemVisible = true` |
| `Notes` | the note, its `DocsAndNotesWorkspaceViewModel`, its `ProjectNoteDocument` | `CurrentDocsAndNotes`/`CurrentNoteDocument` non-null |
| `ScenarioRun` | the run | `SelectedScenarioRun` non-null |
| `Web` | nothing; the page is `ViewModel.SelectedWebPage` | `SelectWebPageAsync` on a page that is not paused |
| `None` | nothing | all of the above false or null |

The controller exposes it as one property, `PresentedSurface`, with one
`PropertyChanged`. Each of the thirteen places assigns a whole new value
instead of setting fields.

The exact C# shape (records under an abstract base, a discriminated record, or
a small class with a kind enum) is the builder's call. Equality must let the
controller skip a `PropertyChanged` when nothing changed, as today's setters
do.

### The window

`MainWindow` listens to `PresentedSurface` instead of the four property names
it watches today, and computes pane visibility from it. Builder's call: put
that mapping in a pure static function so a table test can pin it without a
headless window.

The mapping must reproduce today's visible result for every state the
controller can reach:

| Surface | Terminal pane | Loading surface | Paused | Notes | Journal | Browser | Empty |
|---|---|---|---|---|---|---|---|
| `Terminal`, not loading | shown | hidden | | | | | |
| `Terminal`, loading | hidden | shown | | | | | |
| `Paused` | | | shown | | | | |
| `Notes` | | | | shown | | | |
| `ScenarioRun` | | | | | shown | | |
| `Web` | | | | | | shown if the view model has a selected page | shown otherwise |
| `None` | | | | | | shown if the view model has a selected page | shown otherwise |

Blank cells are hidden.

`None` with a selected web page shows the browser because the view model
selects a web page on its own in some paths, for example the first page at
load. `SelectingWebPageFromFreshStateShowsBrowserPane` covers this. The
window keeps refreshing when `ViewModel.SelectedWebPage` changes.

The other reactions in `OnControllerPropertyChanged` stay as they are, keyed
on the new value:

- `NotesPane.Workspace` and `RightActions.Workspace` follow the `Notes` case's
  workspace, or null;
- `ScenarioJournal.DataContext` follows the `ScenarioRun` case's run, or null;
- when the notes workspace becomes a different non-null value, the editor is
  focused at `DispatcherPriority.Loaded` if it is still current and visible.

### Loading and focus

- **Loading** is a flag on the `Terminal` case. Showing any other case drops
  it, which replaces the side effect in today's `IsTerminalVisible` setter.
  `TerminalLoadingChanged` fires exactly when the effective loading state
  changes from false to true or back, as today. Builder's call whether the
  event stays or the window derives loading from the surface; the test at
  `AvaloniaMainShellControllerTests.cs:177` subscribes to it, so if the event
  goes, that test reaches the same fact through the new value.
- **Pending focus** is a flag on the `Terminal` case. It is set where
  `_pendingTerminalFocusSessionId` is set today: in the focus step of
  `SelectSessionAsync`, only if that session's terminal is loading. When
  loading clears, `FocusTerminalAfterLoadingAsync` focuses the terminal only
  if the surface is still `Terminal` for that session, focus is still
  pending, and `_runtimeCoordinator.ActiveSessionId` is that session. It then
  drops the flag. Today's extra check of `ActiveSessionId` stays.
- Today the window's general refresh sets the terminal pane's visibility
  without regard to loading, and only the loading handler hides it. In
  practice the pane is hidden whenever the terminal is loading, because the
  loading event follows every switch to a loading terminal. The table above
  makes that the single rule. (My call: the difference is not something a
  user can see.)

### Places that assign the value

Line numbers are from commit 6a9b200 and are only a guide.

| Place | Today | New value |
|---|---|---|
| `SelectSessionAsync` (:609), paused item | `IsTerminalVisible = false`, `IsPausedItemVisible = true` | `Paused(session)` |
| `SelectSessionAsync`, live or starting | `IsTerminalVisible = true`, then loading set or cleared | `Terminal(session, loading: startsRuntime)`; the focus step and the post-activation "no controller" check update the flags on the same case |
| `SelectWebPageAsync` (:692) | paused → paused placeholder; otherwise terminal and paused false | `Paused(page)` or `Web` |
| `SelectNoteAsync` (:754) | sets document and workspace after the refresh | `Notes(note, workspace, document)`, assigned after `RefreshAsync` as today |
| `SelectScenarioRunAsync` (:771) | sets the run, clears the rest | `ScenarioRun(run)` |
| `ToggleNotesAsync` (:1653), closing with nothing selected | clears notes, `IsTerminalVisible = false` | `None` |
| `PauseRootSessionAsync` (:1734), `PauseRootWebPageAsync` (:1820), when the item is selected | paused placeholder | `Paused(item)` |
| `CloseNoteAsync` (:1857) | clears notes, then restores | `None`, then restore |
| `CloseScenarioRunAsync` (:1902) | clears the run if it is the one shown, then restores | `None` if the surface is that run, then restore |
| `RestoreSelectedHostAsync` (:2305) | "terminal visible and active session matches" check; else-branch clears everything | the check reads the `Terminal` case; the else-branch assigns `None` |
| `ShowSessionTerminalUnderBusyAsync` (:2491) | `ViewModel.SelectedSession = session`, `IsTerminalVisible = true` | `Terminal(session)`, keeping the loading and focus flags if the surface was already `Terminal` for that session, otherwise both false |
| Shutdown (:2083) | `IsTerminalVisible = false` | `None` if the surface is `Terminal`; otherwise unchanged |

`ShowSessionTerminalUnderBusyAsync` runs during resume capture: when Pact
closes, when a project is paused or closed, and when a ROOT session is paused.
Pausing a ROOT session has no busy overlay. Today it can leave notes or a run
set while also making the terminal visible, so two panes are visible at once.
The new value cannot hold both. That is agreed behavior change 1 below.

### Agreed behavior changes

Agreed with the user on 2026-10-23, after a reviewer found that resume capture
changes what is shown without the busy overlay (reviewer finding, 2026-10-23).

1. **Resume capture shows only the agent's terminal.** Resume capture stays
   visible on purpose: while Pact waits for an agent to exit, the user sees
   that agent's terminal and can tell where it is stuck. Capture selects the
   session and shows `Terminal(session)`, as today. Notes or a scenario
   journal that were shown before no longer stay visible next to it.
   - **ROOT session pause, from any surface** (notes, journal, web, another
     terminal): after capture and stop, the surface is `Paused(session)` and
     the session stays selected. A capture that times out ends the same way,
     because `CaptureBeforeStopAsync` reports a timeout and the pause
     continues, as today. A capture that throws, or that the caller cancels,
     stops the pause where it is, as today: the surface stays
     `Terminal(session)` with nothing else beside it.
   - **Project pause and project close:** after capture the surface is what
     the code produces today with notes and journal dropped. Choosing what to
     show afterwards is out of scope (see Non-goals).
2. **The paused placeholder shows what is paused.** It replaces today's pause
   icon, "Paused" and the hint about the Resume button:
   - black background;
   - the Pact logo in the center (`/Assets/AppIcon.ico`, or a larger asset of
     the same logo if one exists);
   - below it, for an agent session: `Paused — <agent kind>, <working
     directory>`;
   - for a web page: `Paused — <page address>`.

   The `Paused` case carries the item, so the window reads these facts from
   it. Builder's call: keep the Resume hint as a smaller second line or drop
   it.
3. **A paused project remembers what the user had selected.**
   `PauseWorkspaceAsync` passes `ViewModel.SelectedSession?.Record.Id` to the
   view model as the project's active item. That runs after capture, which has
   selected the last captured session. Read the active item before capture
   starts and pass that instead, so resuming the project returns to where the
   user was. If the user had a web page or the notes selected, that item's id
   is passed. Today only a selected session's id is passed; the builder checks
   that `MainWindowViewModel.PauseWorkspaceAsync` accepts a web page or note
   id as the active item, and passes null for anything it does not accept.

### Side effects that must survive

`CurrentDocsAndNotes` has a setter that subscribes to the workspace's
`PropertyChanged` (`OnDocsAndNotesPropertyChanged`) and calls
`InvalidateSelectionIfSourceChanged()`. Both must still run when the shown
notes workspace changes, including when the surface leaves `Notes`.

## Non-goals

- **Selection in the view model.** `SelectedSession`, `SelectedWebPage`,
  `SelectedProjectNote`, `SelectedScenarioRun` and `ActiveItemId` already
  behave as one value: each setter clears the others
  (`MainWindowViewModel.cs:233–395`). They stay as they are. The analysis
  doc's idea that selection changes only through `PresentedSurface` is
  dropped. The shell already changes selection only from its `Select*`
  methods. (Agreed with the user on 2026-10-23.)
- **`CurrentGitPanel`.** It belongs to the selected project, not to the
  center pane.
- **Selection actions** (`ClearSelectionActions`, capture, source validity).
  They keep reading what they read today.
- **What to show after closing a tab, or after closing or pausing a project.**
  Today a closed tab is replaced by its neighbour by index, and a paused
  project leaves the surface as capture left it. Both stay as they are. The
  user plans project and app dashboards to switch to instead (backlog,
  `2026-10-09-development-directions.md`, "Отложено: дашборды").
- **Capture terminals not being visible at Pact close.** The user saw no
  terminal switching during the last close. That is a separate bug in the
  backlog, not part of this step.
- **The "focus after an MCP tab while the window is inactive" case** from the
  analysis doc. No code handles it separately, the user has not seen it since
  `98c7075`, and it cannot be reproduced here. If it comes back, it is a new
  bug.
- **Renaming public or internal entry points** used by `MainWindow`,
  `EngineProbeRunner` or tests.

## Anti-goals

- **Any change a user can see** beyond the three agreed ones: which pane is
  visible, the loading surface, where focus lands, status texts, persisted
  `ActiveItemId`.
- **A second source of truth.** If the old properties stay, they are computed
  from `PresentedSurface` and cannot be set.
- **Changing what existing tests assert.**

## Constraints

- **Existing tests keep their assertions.** They read `IsTerminalVisible`,
  `IsPausedItemVisible`, `CurrentDocsAndNotes`, `CurrentNoteDocument` and
  `SelectedScenarioRun` on the controller, and `EngineProbeRunner` reads
  `IsTerminalVisible`. Builder's call: keep these as read-only properties
  computed from `PresentedSurface`, raising their own `PropertyChanged` when
  their computed value changes. That keeps test churn at zero.
- **New tests:**
  - a table test of the pane mapping, one row per case in the table above,
    with and without a selected web page for `Web` and `None`;
  - showing anything other than a loading terminal drops loading and pending
    focus, and `TerminalLoadingChanged` fires false once;
  - a pending focus is not applied after the surface moved to another session
    or kind;
  - pausing a live, capture-eligible ROOT session while notes are shown ends
    with exactly `Paused(session)`, the session selected and the notes pane
    hidden; the same when capture times out;
  - the paused placeholder shows `Paused — <kind>, <directory>` for a session
    and `Paused — <address>` for a web page;
  - pausing a project while a web page or a session that capture will not
    select is selected persists that item as the project's active item, not
    the last captured session.

  Check `AvaloniaMainShellControllerTests` and
  `MainWindowPaneVisibilityHeadlessTests` first, and add only what they don't
  already cover.
- **Build and test resource limits** from `AGENTS.md` apply:
  - build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q
    -p:BuildInParallel=false`;
  - tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false
    -- NUnit.NumberOfTestWorkers=2`;
  - one heavy build at a time.
- **No timing-sleep tests.** Wait on conditions and signals only.
- **Commits.** Commit this spec together with the first code commit, not
  alone.

## Suggested order

1. The `PresentedSurface` type and the pure pane mapping, with the table test.
   No caller yet.
2. The controller holds `PresentedSurface`. The old properties become
   computed. Every place in the table assigns the new value. The window
   switches to the mapping.
3. Loading and pending focus move into the `Terminal` case. The two private
   fields go away.
4. The paused placeholder (change 2).
5. The paused project's active item read before capture (change 3).
6. Docs: mark step 6 done in the analysis doc. Add a sentence to
   `docs/architecture.md` next to the step-5 paragraph.

Change 1 falls out of step 2; its tests belong there. Each step leaves the
build and every suite green.

## Left to the builder

- The C# shape of `PresentedSurface` and the mapping's return type.
- Whether `TerminalLoadingChanged` stays as an event.
- Helper names, and whether the thirteen assignments go through one private
  `Present(...)` method that also runs the notes-workspace side effects.
