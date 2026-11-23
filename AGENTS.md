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
  process start. Session launch and catalog browsing must leave the user's
  global agent configuration untouched.
- The skills editor may update only the skill settings selected by the user
  in global agent configuration. Before writing, show the affected files and
  profiles and require the user to select Apply. Preserve unrelated settings.

## Orchestrator slot

- The current desktop composition leaves the orchestrator and Hermes launch
  entries hidden. Retain existing Hermes settings and saved sessions on disk.
  The contracts below apply to the retained implementation when enabled.
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
  rewriting of newlines, Enter, Esc and Ctrl+letter keys belongs in C#, where
  session kind and terminal mode are known.
- Output remains batched at 33 ms for the presented session and 100 ms for
  hidden sessions, with an activation flush. Cursor blink is active only for
  the selected presented terminal.
- Resize delivery is serialized and latest-request-wins. Hermes may still
  redraw or scale poorly after Pact delivers the correct final dimensions; do
  not claim rendering parity from transport evidence.

Use VS Code's xterm.js/ConPTY integration as the first behavioral reference for
terminal-engine problems; Windows Terminal is useful for concepts but uses a
different renderer.

## Review and prompt delivery invariants

- Reviews use the fixed author/reviewer turn protocol through `ReviewRelay`.
  `Settings/scenarios.json` supplies the review criteria, default strictness,
  and any saved per-type round limit. The default is five rounds, and the
  request can override both strictness and the limit.
- Participants read an assigned turn with `pact_get_review_turn` and answer it
  with `pact_submit_review_turn` using the returned turn ID. A reviewer may
  submit `approved: true`. The short terminal trigger only prompts the
  participant to read the assigned turn. The turn protocol advances the
  review. Both sessions remain visible and editable.
- `SessionRuntimeCoordinator.DeliverPromptAsync` holds the session for the
  whole delivery. A busy session queues the trigger, and an existing draft
  does not block it. The startup readiness budget covers first output, settle
  time and folder trust, answered at most once per process. Wait for the paste
  to appear before Enter. A fresh question stops submission. Only a fresh
  capture of the empty composer confirms submission. Repeat Enter once if the
  composer still holds the sent text. A delivery that may have written input
  is never repasted. Retry only when no input was written.
- A failed or uncertain notification leaves the turn available and shows
  attention. Its owning participant acknowledges the notification by reading
  that turn. Ending a review cancels its trigger before relaying another turn.
  Close or pause a participating project by ending its reviews first.
- Accepted turns are logged in the project's `.pact/reviews/`. Completion
  notices are best-effort. Startup may clean abandoned legacy
  `.pact-reviews` directories.

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
- Before handing a task to the user for manual verification, publish the
  current changes as a launch-ready, framework-dependent `win-x64` build in
  `artifacts/local-diag`. Verify that `Pact.App.Avalonia.exe` is present and
  include its path in the handoff.
- Run `git diff --check HEAD -- <paths>` for touched docs and whitespace.
- The worktree may be dirty. Preserve unrelated user and generated changes.
- Use `apply_patch` for manual text edits.
