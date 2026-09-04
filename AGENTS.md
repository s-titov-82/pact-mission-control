# Repository agent instructions

## Start here

Before making non-trivial changes, read:

1. `README.md` for the product overview and public commands.
2. `docs/architecture.md` for ownership and dependency direction.
3. `docs/agent-onboarding.md` for terminal, scenario, storage, and settings
   contracts that are difficult to infer from code.

The current code and these public documents are the source of truth.

## Product boundaries

- Avalonia 12 is the only Windows-first product head.
- Terminal sessions are visible TUI processes running through the bundled
  ConPTY and native WebView2/xterm.js. Prompt and scenario actions must target
  already-live sessions; do not introduce a headless agent API.
- Switching sessions must not stop background terminal processes.
- The supported package is framework-dependent `win-x64`.

## Runtime storage

The default data root is `%APPDATA%/Pact`; `--data-root` accepts an absolute
isolated root. It has exactly four top-level directories:

- `Settings` for durable JSON and notes;
- `WebView` for WebView2 user data;
- `Logs` for bounded disposable logs;
- `Temp` for session staging and owner-managed disposable snapshots.

`Settings/projects.json` owns project state and nested saved sessions.
`Settings/review-profiles.json` owns reviewer launch commands and their
agent-control argument templates.
`Settings/agent-control.json` owns the fixed loopback MCP port, and
`Settings/orchestrator.json` owns the singular orchestrator slot.
Application startup and profile copying use the same named operating-system
mutex; do not add a lock file under `Settings` or elsewhere in the data root.
Do not persist terminal transcripts or scenario journals.

## Agent control channel

- The MCP endpoint binds loopback only. Authentication tokens are per-session
  and are revoked when that session stops, exits, or restarts.
- Tokens route requests to the owning project or ROOT tab; they do not isolate
  same-user agents from one another.
- Project-session tokens can read and append Notes or replace their complete
  text with an expected revision. ROOT has no project Notes.
- Requests do not show per-action confirmation prompts. They perform only
  actions already available in the UI and target visible terminal sessions.
- Pact injects endpoint configuration into qualified launch profiles at
  process start. It never writes to the user's global agent configuration.

## Orchestrator slot

- The pinned orchestrator is one singular Hermes session above ROOT. It may
  inspect every live session, submit prompts, inspect and control active
  reviews, edit Notes for running projects, and list active or paused web tabs
  under running projects and ROOT. It may resume a web tab in the background
  and read bounded live HTML, but paused projects are excluded.
- The orchestrator has no project ownership, Git, project Markdown, arbitrary
  JavaScript, or scenario-start tool. Its selected row uses the same current
  terminal highlight as ordinary sessions.
- Its durable credential is stored in `Settings/orchestrator.json` and in the
  provisioned Hermes profile `.env`. Ordinary session tokens never receive
  orchestrator tools.
- Both the slot and workstation-lock detection default to off. An intentional
  stop, disabling the slot, or application shutdown must not trigger restart.
- Retained stable screens and extracted agent messages remain in memory only;
  never persist either as orchestrator state.

## Terminal invariants

- Use the bundled ConPTY only; there is no inbox ConPTY fallback.
- Preserve alternate-screen and mouse-tracking VT sequences. Claude exports
  internal selection through OSC 52; do not remove the host clipboard handler.
- Keep the browser-side terminal host agent-neutral. Codex win32-input-mode
  newline rewriting belongs in C#, where session kind and terminal mode are
  known.
- Output remains batched at 33 ms for the presented session and 100 ms for
  hidden sessions, with an activation flush. Cursor blink is active only for
  the selected presented terminal.
- Resize delivery is serialized and latest-request-wins. Hermes may still
  redraw or scale poorly after Pact delivers the correct final dimensions; do
  not claim rendering parity from transport evidence.

Use VS Code's xterm.js/ConPTY integration as the first behavioral reference for
terminal-engine problems; Windows Terminal is useful for concepts but uses a
different renderer.

## Scenario invariants

- `ReviewLoopScenarioProgram` is the fixed author/reviewer flow. Definitions,
  templates, marker, iteration limit, and reviewer text come from
  `Settings/scenarios.json`.
- Each step atomically publishes one immutable
  `.pact-reviews/<run>/pass-NNN-<role>-task.md`. Terminal input from a scenario
  step is only the short task-path instruction followed by a separate Enter.
  A run writes only into a session that is idle, holding no question, with an
  empty composer, which a freshly launched session reaches through one bounded
  budget covering its first output, a settle delay, and the folder-trust dialog
  — answered with a single Enter, once per launched session. Delivery is
  confirmed by a new activity cycle, and a submitted prompt always starts one.
  A dropped submit is repaired with Enter alone once the composer is seen still
  holding the trigger; a paste is repeated only after the composer stays empty
  while the agent is idle. A detected question or unsent text blocks a
  programmatic send, and the run pauses naming what happened and whether
  anything was typed.
  The single exception is one final completion notice, delivered when a run
  reaches a terminal state: it carries no task, expects no response, and
  advances nothing, so routing it through a task file would force the run
  directory to outlive the run. Its delivery is best-effort and a rejected
  submission is journaled as a delivery failure, never reported as delivered.
- The matching response must be non-empty and end with the exact supplied
  transport footer. Only a footer-complete file advances the run.
- Terminal busy/idle is UI evidence and never scenario completion. Busy delays
  safe prompt delivery without advancing or failing the step.
- The response-file observer starts before prompt delivery and remains active
  through delivery retries, watchdog attention, and manual pause. Automatic
  attention retries delivery when the terminal becomes safe. Manual Pause
  blocks all new automatic terminal writes until Resume, but an already
  appearing valid response clears the pause, re-locks the involved sessions,
  and advances the run without requiring Resume.
- Paused runs retain their exchange files and resume the same wait. Completed,
  maximum-iteration, aborted, and failed runs remove only their own Pact-owned
  directory; startup removes abandoned `.pact-reviews`, never generic
  `.reviews`.
- Involved sessions are input-locked but remain visible and scrollable. App or
  project close aborts active runs before stopping sessions.

## Settings and documents

- The form-based Settings window is the supported editor. File-backed sections
  preserve unknown JSON nodes on save; project edits apply through live runtime
  state.
- Project Markdown uses revision-aware atomic autosave. A disk revision change
  must surface a conflict instead of overwriting either side; failed saves keep
  local edits retryable.
- Preserve the `Clean`, `Dirty`, `Saving`, `Conflict`, and `Failed` document
  state model and explicit UI dispatch boundaries.

## Resource-bounded .NET commands

This machine may be shared. Run one heavy command at a time and keep the
following limits:

```powershell
dotnet restore Pact.slnx --disable-parallel --locked-mode
dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
dotnet test <project> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
```

If `rtk` is installed, it may prefix commands as a local output wrapper; it is
not required by the project.

## Working rules

- Do not commit preliminary design specifications or implementation plans on
  their own. Commit specs and plans only together with the implementation code
  written from them.
- Write XML documentation for public classes, methods, properties, and other
  public API. Explain the contract or non-obvious constraint, not the name.
- Prefer targeted behavioral tests and the relevant native integration gate.
  Do not add source-text, private-shape, or timing-sleep tests.
- Run `git diff --check HEAD -- <paths>` for touched docs and whitespace.
- The worktree may be dirty. Preserve unrelated user and generated changes.
- Use `apply_patch` for manual text edits.
