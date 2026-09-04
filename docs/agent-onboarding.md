# Agent Onboarding

This file contains the current implementation contracts that are easiest to
miss when changing PACT:> Mission Control. Start with [Architecture](architecture.md)
for ownership and dependency direction and [Development](development.md) for
commands.

## Avalonia Product Head

Avalonia 12 is the single product head and owns the stable
`%APPDATA%/Pact` profile:

```powershell
dotnet run --project src/Pact.App.Avalonia/Pact.App.Avalonia.csproj
```

The app owns a process lease derived from that root. A custom root may be
supplied with `--data-root <absolute-path>` for isolated tests; the app refuses
to open a root already leased by another process.

The Avalonia head owns the complete main shell, terminal/browser lifecycle,
notes, prompt actions, settings, project/session creation, scenario setup and
journal, Git popup, and commit/push/branch dialogs.

Root-level agent instructions live in `AGENTS.md`. `CLAUDE.md` only redirects Claude users there.

## Product Goal

PACT:> Mission Control is a Windows-first Avalonia desktop cockpit for visible
coding-agent terminal sessions. It embeds real TUI processes through ConPTY and
native WebView2/xterm.js, keeps project/session state, and adds explicit
human-triggered prompt actions.

The app must not become a headless API proxy for subscription TUI tools. Raw
selection and manual insertion actions do not submit. Prompt and shell template
actions submit only when their Auto-submit option is enabled; scenario actions
are the separate automated path. The scenario journal panel may temporarily
hide the WebView for inspection, but scenario actions still target already-live
TUI sessions through ConPTY.

## Current Architecture

Project ownership and dependency direction are documented in
[Architecture](architecture.md). This onboarding keeps the behavioral
contracts that are easy to break while changing those components.

Web-tab monitoring preserves the same ownership boundary: Core owns typed rules,
rule validation/fingerprints, the DOM codec, and per-page state transitions;
Infrastructure owns disposable snapshots; Presentation owns loaded-tab
registrations, polling, navigation gates, and status coordination; Avalonia is
the thin WebView DOM adapter and shell/lifecycle integration. Rules are generic
configuration, never TeamCity- or GitLab-specific C# branches.

The app has one terminal WebView host with one xterm instance per live session.
Switching sessions changes which xterm instance is visible; it does not stop the
other runtime controllers or rebuild their terminal contents. Saved browser
pages use native WebView2 hosts alongside the terminal host.

Terminal tab status has two explicit layers. Core's `TerminalTabStatusEngine`
owns all evidence and the priority
`Failed -> Paused -> InputRequested -> Busy -> Unread -> None` for one session.
Presentation's `TerminalTabStatusCoordinator` owns engine membership and routes
successful controller input, lifecycle/start mode, stable screen snapshots,
selection, and window facts, then dispatches the single projected
`TerminalTabIndicator` to `SessionViewModel`. Input containing Enter and resume
start are activity evidence; every submit restarts the activity cycle, normal
start is idle, lifecycle stop/exit pauses, and failure wins over every other
state. A settled empty composer ends startup activity without becoming unread;
a real completion marker still does become unread.

The browser-side terminal host waits for a one-shot 500 ms quiescence debounce
after xterm writes, captures the live screen from `buffer.active.baseY` rather
than the user's scrolled viewport, reconstructs wrapped physical rows into
logical lines, and posts only changed stable snapshots. Snapshot dedupe is
reset before each submitted activity, so a command returning to the same final
screen still reports completion while repeated redraws within that activity
remain deduplicated. The coordinator selects an `IAgentScreenProfile` per `AgentKind`:
Claude and Codex check Busy first, so `esc to interrupt` wins over any Done
marker on the same screen. Claude reports Done for a capitalized past-tense
verb with a duration (`Worked for 2m 30s`, `Cooked for 12s`, `Sautéed for 3s`),
since the verb changes but its shape does not; Codex reports Done for
`Worked for`, its usage hint, or the composer's long divider rule. The same
markers end the assistant message that each profile extracts, so a screen may
carry unrelated text between the message and its closing marker. The classifier
reports `InputRequested` with the pending question as its status line; folder
trust uses the shared `AgentScreenProfileBase.TrustPromptDescription` constant
across agents. It also reports `PromptIsEmpty`: `true` means delivery may treat
the composer as empty, `false` means it contains reliably detected unsent text,
and `null` means the screen cannot answer. Claude intentionally treats every
visible prompt as empty because its pale placeholder and staged hints are copied
from the terminal as ordinary text and cannot be distinguished reliably from
user input. Its structural evidence therefore reports prompt presence without
inspecting composer content. Pwsh reports Done when the last non-empty line
matches `PS …>`; otherwise these profiles report Unknown. Hermes and Custom use the
quiescence fallback: a stable snapshot reports Done during active work, while
the engine ignores Done when idle. Raw PTY/display output is not status
evidence. For diagnostics and profile-rule tuning, inspect
`TerminalTabStatusEngine.LastScreenSnapshot`.

Selection, window visibility (`IsVisible && WindowState != Minimized`), and
window activation are facts supplied by the view; activation uses the foreground
root HWND so focus inside the WebView2 child still counts as app activity. Core
has no Presentation dependency. A completion is acknowledged whenever its tab
is selected in a visible active window, regardless of which fact changed last.
`HasUnreadCompletions`, taskbar attention, and the tree glyph are consumers of
that projection, never separate status owners.

Each review-loop step atomically writes its complete immutable prompt to
`.pact-reviews/<shortRunId>/pass-NNN-<role>-task.md` before terminal submission.
The terminal receives one short task-path trigger as one bracketed-paste backend
write followed by a separate Enter; the full prompt is never pasted or typed
into the composer. All other programmatic prompt bodies use the same one-write
bracketed-paste mechanism. A newly launched reviewer becomes ready on one
bounded budget covering its first output, settle delay, and at most one Enter
for the folder-trust dialog. Delivery is confirmed by the new activity cycle
after submit. Pact repairs a dropped submit with Enter alone when the composer
still holds the trigger, and repeats the paste only after the composer remains
empty while the agent is idle.

The expected response observer starts before delivery and never stops for
watchdog or Pause. Pact writes nothing while the terminal is busy, asking a
question, or explicitly holding pending input; Codex and Claude remain writable
unless another gate blocks them. Automatic attention unlocks the
affected terminal, keeps observing the response, and retries the idempotent
task-path trigger when safe. Manual Pause unlocks every involved terminal and
forbids new automatic writes until Resume, but a footer-complete expected
response clears Pause, re-locks the terminals, and advances immediately without
Resume. The assigned agent writes the matching unique
`pass-NNN-<role>-response.md` and ends its non-empty response with the exact
transport footer specified in the task.
Only that footer-complete file advances the step, and the footer is removed
before content becomes a later prompt, final result, or completion-marker input.
Tab indicators remain UI evidence and never establish scenario completion or
advance a step. `Paused` retains the task and response files. The run state and
journal exist only in memory while its pseudo-node can be shown; its journal and
final result are read-only rendered Markdown views, and no durable
scenario-state JSON is written alongside the exchange. Every terminal state —
`Completed`, `MaxIterationsReached`, `Aborted`, and `Failed` — cleans the same
exact Pact-owned run directory; startup removes abandoned `.pact-reviews`, while
generic `.reviews` is not reserved or cleaned by Pact. Review-loop scenarios are
fixed author/reviewer loops driven by `ReviewLoopScenarioProgram`;
`Settings/scenarios.json` stores definitions, templates, stop marker, max
iterations, and reviewer instruction presets. The completion marker remains the
only machine-checked consensus signal.
There is no scenario id allow-list; any `scenarios.json` entry with a known
`kind` (currently `reviewLoop`) appears in the UI and runs. Custom ids survive
a load; files that do not match the supported schema are reseeded from the
bundled defaults.

## Runtime Data

The default data root is `%APPDATA%/Pact`; `--data-root X` uses exactly `X`.
It has four top-level directories with explicit ownership:

- `Settings`: durable JSON (`projects.json`, `root-tabs.json`,
  `review-profiles.json`, `agent-control.json`, `orchestrator.json`,
  launch/prompt/web/scenario/git/recent settings, window layout, and
  appearance) plus `Settings/Notes/<key>.md`. Notes are keyed from the
  normalized project root and are never deleted by hiding the tab.
- `WebView`: one shared WebView2 user-data folder. Browser pages use its default profile so cookies and login state survive. Terminal xterm uses the named `PactTerminal` profile. At startup browser cleanup removes only `DiskCache` older than 72 hours; cookies, Local Storage, IndexedDB, CacheStorage, Service Workers, passwords, autofill, and settings remain. The terminal profile is cleared completely.
- `Logs`: disposable application logs, split by UTC day and 5 MiB segments; files older than 72 hours are removed by `LastWriteTimeUtc`. Browser-tab WebView lifecycle phases (`webview browser:<pageId> …`) are appended here so a tab that ends up in a bad native state stays diagnosable after its in-memory trace is gone. Scenario delivery also records metadata-only outcome changes and transport exceptions, including run, step, role, session, attempt, and write/submit flags. It never records prompt, response, status-line, or terminal-screen text, and consecutive identical delivery outcomes are coalesced.
- `Temp/Session`: disposable atomic-write staging and other run-local data; it
  is cleared best-effort at startup and shutdown.
- `Temp/Retained`: disposable owner-managed caches and is not blanket-cleared.
  `Temp/Retained/WebMonitoring/<webPageId>.json` is the monitoring owner’s
  atomic snapshot cache. Startup sweeps malformed and orphaned snapshots; each
  owner defines its own validity and cleanup.
  `Temp/Retained/AgentControl/pact-mcp.json` is the generated agent-control
  configuration, rewritten at every launch that needs it.
  `Temp/Retained/PactSkills/PactMcpSkill.md` and
  `Temp/Retained/PactSkills/PactCommonSkill.md` are atomically published,
  Pact-owned session guidance. Housekeeping may remove stale Pact-owned files
  there, but user-authored settings and notes never belong in this directory.
  The Markdown contains no bearer token, endpoint, or other credential.

Pact does not persist terminal output or scenario journals. Terminal history after
resume belongs to the external TUI, while the live xterm buffer supports the
current process. There are no Pact `sessions`, transcript, or `scenario-runs`
directories.

## Agent control connection

Sessions reach Pact's tools through one loopback MCP endpoint; the bearer token
identifies the session, so there is no per-session endpoint. The launch
arguments that carry that connection follow the **agent kind**, not the launch
profile: `AgentControlArgumentTemplates.For` holds Claude's `--mcp-config` file
and Codex's `-c mcp_servers.pact.*` overrides, and kinds absent from it launch
unconnected. Profiles carry no control arguments, and sessions record no
profile reference — a saved session's kind is all the connection needs, so any
session of a supported kind is connected on its next start or resume.

Each launch revokes the session's previous token and issues a new one, then
materializes only the carriers its template names: `{configPath}` writes
`Temp/Retained/AgentControl/pact-mcp.json`, `{endpointUrl}` carries the address,
and `{tokenEnvVar}` names the environment variable holding the token.

The credential lives only in the child environment. The configuration document
names `${PACT_AGENT_CONTROL_TOKEN}` rather than the credential itself, so it
holds no secret, is identical for every session, and one retained file serves
them all — staging it per session would have cleared it out from under live
agents at the next startup.
It must declare `"type": "http"`: an agent that cannot tell a remote server from
a local one rejects the whole configuration. Agent-owned configuration files
outside the Pact data root are never edited.

`Settings/agent-control.json` owns the fixed loopback port and an `enabled`
switch; a missing or unreadable file keeps the connection on.

Pact also appends a short conditional instruction to every Codex or Claude
launch it owns: ordinary project sessions, ROOT sessions, reviewer-created
sessions, restarts, and resumes all pass through the same composition path.
The instruction points at retained files instead of inlining their bodies into
the system context or later prompts. With agent control enabled it says to read
`PactMcpSkill.md` before first use of the `pact` MCP server and to read
`PactCommonSkill.md` for Pact behavior or missing-tool questions. With control
disabled it names only the common file. Neither file is read unconditionally.

The selected profile's command and account/model flags remain authoritative.
Pact appends structured arguments only after choosing direct process launch or
the PowerShell fallback. Direct arguments use Win32 quoting; the fallback uses
an encoded PowerShell script with a literal argument array, preserving spaces,
quotes, dollar signs, backticks, and other path metacharacters. Claude carries
the instruction inline through `--append-system-prompt`. Codex carries it as an
invocation-level `developer_instructions` value, overriding the same key from
an explicitly selected Codex config profile without replacing any other
profile values.

Skill publication is attempted before external settings and terminals are
initialized. If it fails, Pact records a diagnostic and continues to inject
the MCP connection, but does not emit instruction paths that were not confirmed
as published.

An ordinary project credential can read its exact Notes text and opaque
revision, append text, or replace the complete buffer using that revision.
Replacement may intentionally use empty text; a stale revision returns a
conflict without overwriting either side. ROOT credentials have no project
Notes. Browser creation and file-first review requests remain owner-scoped.

The endpoint declares `tools.listChanged=true`. A bearer-authenticated GET
opens an SSE stream for server notifications; accepted JSON-RPC calls continue
to use POST. The initialize response returns an opaque `Mcp-Session-Id` derived
for that bearer, and a supplied id must validate against the same bearer before
the request is routed. Claude may open its authenticated GET stream without the
session header, matching its current client transport behavior.

`ReloadExternalSettingsAsync` fingerprints only sorted scenario ids and review
profile ids after both live snapshots have reached their success, retained, or
fallback state. If that fingerprint changes, ordinary connected clients
receive `notifications/tools/list_changed` and can re-run `tools/list` without
restarting their terminal. A failed review-profile read retains the prior
snapshot; malformed review-profile JSON follows the reader's empty-snapshot
contract. A malformed scenario file installs built-in defaults, and notifies
only when those ids differ from the previous live set. Display names, commands,
ordering, and unrelated settings do not notify. There is no external file
watcher: hand edits take effect only at the Settings reload boundary or after
application restart. The orchestrator catalog is fixed separately and is not
subscribed to these notifications.

## Orchestrator

The pinned row above ROOT owns exactly one Hermes session. It has a selectable
terminal and cross-session MCP rights, but no project ownership. It can inspect
projects, ROOT, live session state, retained stable screens, subscription
usage, and active reviews. Detailed review state includes the current step,
manual or attention pause, pending pause request, expected agent/task/response
file, and in-memory journal. Pause is cooperative at a safe boundary; Resume
continues an established pause and does not cancel a merely pending request.
Prompts still use the same visible-terminal path as a human.

The orchestrator can read, append, and revision-safely replace Notes only for
projects in `Workspaces`; `PausedWorkspaces` are not targets. It can list
active or paused saved web tabs under those running projects and ROOT. Resuming
a tab loads it in the background without changing selection or focus. HTML
inspection requires an already-active host and returns best-effort live
`documentElement` fragments in UTF-16 code units, defaulting to 100,000 and
capped at 200,000 per call. If the URL or `totalLength` changes while paging,
the caller must discard prior fragments and restart at offset zero.

There is no orchestrator Git, project Markdown/document, arbitrary JavaScript,
or scenario-start tool. Ordinary session credentials cannot enumerate or
invoke the orchestrator tools. Selecting the pinned row gives it the same
accent background, accent border, and leading edge as an ordinary selected
terminal; selecting another item restores its tier styling.

`Settings/orchestrator.json` owns the singular slot record: launch command,
working directory, durable bearer credential, lock/unlock prompts, and the
orchestrator and workstation-lock switches. Both switches default to off.
Disabling the orchestrator stops the process and immediately clears its
credential from the endpoint without deleting either settings or its Hermes
profile. Reissuing the credential rewrites the profile `.env` and invalidates
the old credential in the running shell.

Provisioning is explicit through the Settings `Orchestrator` section. It first
requires the installed `hermes` CLI to create the named `pact` profile; Pact
does not invent a parallel profile layout. `HermesHome.ResolveRoot` mirrors
Hermes' root rules:

- on Windows the native root is `%LOCALAPPDATA%\hermes`, not `~/.hermes`;
- on other platforms the native root is `~/.hermes`;
- blank or absent `HERMES_HOME` uses that native root;
- a `HERMES_HOME` outside the native root replaces it;
- if that value names `<root>/profiles/<name>`, the owning root is
  `<root>`; a value already inside the native root still resolves to the
  native root.

The dedicated profile therefore lives at
`<Hermes root>/profiles/pact`. Pact owns only its MCP block in `config.yaml`,
the generated `SOUL.md`, `.env`, and
`skills/pact-status-report/SKILL.md`. Provisioning preserves unrelated YAML
keys, backs up user-owned template/configuration content before replacing it,
and reports each artifact separately. The `.env` contains the loopback
endpoint and durable bearer credential; do not copy it into ordinary launch
profiles or persist it in terminal output.

The slot starts only when provisioned and enabled. Unexpected process exit
uses bounded restart backoff. Tier Stop, disabling the switch, and application
shutdown are intentional stops and never restart it. Windows session-change
notifications are registered only while both the slot and lock detection are
enabled. Lock/unlock prompts are submitted once to the live slot; a missed
delivery becomes visible tier state rather than an exception.

Stable screens, extracted agent messages, and their current/stale marker live
only in the per-session status engine. Stopping or removing a session discards
that state; never add it to `orchestrator.json`, logs, transcripts, or scenario
journals. Scenario-locked sessions remain readable but reject orchestrator
messages through the normal prompt lock.

## Packaging

The only supported package is framework-dependent `win-x64`
(`SelfContained=false`). Run `tools/Publish-Pact.ps1`; it cleans only
`artifacts/publish/win-x64` and fails if the result contains PDBs, Linux/macOS,
win-x86 or win-arm64 runtime folders, or exceeds 50 MiB. Do not add another RID
until the bundled ConPTY payload exists for that architecture.

## Right Panel Prompt Actions

- Selection actions use one contextual cursor popover over the center pane.
  Terminal mouse selection anchors it to the xterm completion point; Claude
  OSC 52 copy uses its supplied point when available and otherwise falls back
  to the center pane. Notes mouse selection uses an editor-local point, while
  keyboard selection falls back to the center pane when Avalonia exposes no
  reliable selected-range rectangle.
- xterm publishes a completed selection only from its own mouse-up handling,
  after the pointer release the popover anchors to, so the browser host holds
  the release point and completes the selection when xterm publishes it. Never
  complete a selection from the pointer release alone.
- The popover position travels as popup anchor offsets. An empty
  `PlacementRect` is discarded by the platform positioner, which then places
  the popup at the placement target's origin.
- Agents that own the mouse keep their selection outside xterm and inside a
  native web view, where neither a selection change nor an Avalonia light
  dismiss can report that it is gone. The browser host reports a dismissal when
  the user presses or types inside a terminal, and the shell closes a popover
  belonging to that session. The pointer anchor survives the mouse reports such
  an agent sends through the input channel and is dropped on typed input.
- The right panel remains independent while the popover is open: its `Actions`
  block and `Usage limits` stay visible and usable.
- The popover offers a visible `Raw selection` / prompt-template list for
  templates whose body contains `{selectedText}`. Its compact target tree shows
  only the source-owner ROOT or project group. `More targets…` expands to the
  complete eligible ROOT/project/session tree across unpaused ROOT tabs and
  active projects; web pages, paused ROOT sessions, and scenario-locked
  sessions are excluded.
- Selection actions paste into a chosen visible terminal without pressing
  Enter, or append into eligible project Notes without overwriting existing
  text. A selection from Notes can target a terminal; a terminal selection can
  target another project's Notes.
- The right panel `Quick actions` block shows current-session actions only.
  Agent sessions (`codex`, `claude`, `hermes`) show prompt actions and paste
  without Enter. `pwsh` shows terminal-command actions and submits them with
  Enter.
- Below `Quick actions`, selected terminals show classifier verdict and
  description, derived indicator, composer/input-request state, content-free
  composer evidence, activity epoch, viewport, lifecycle, working directory,
  scenario lock, and classification time. Selected web tabs show their full resume URL, browser state, monitor
  status, matched rule, last observation/revision, unread state, poll schedule,
  navigation, and the last sanitized monitor error. Updates are event-driven;
  raw terminal-screen snapshots are not exposed or persisted.
- `Appearance -> Show selected tab details` hides only that facts block.
  Transient action and error status stays independent.
- `Appearance -> Show external process metrics` is default-off and effective
  only while selected-tab details are visible. For the selected live terminal,
  it shows the root PID and process-tree count, combined working set, and CPU
  normalized to total logical-processor capacity. For a selected loaded web
  tab it separately shows renderers attributable only to that page and the
  shared WebView2 browser/GPU/utility or multi-page renderer processes. A
  renderer owned only by another tab is excluded. Working set is available on
  the first snapshot; CPU shows `Sampling…` until the second two-second sample.
  The data remains in memory. Selection changes, stopped sessions, hiding
  details, and disabling the option stop the corresponding polling. A paused
  or unloaded web tab is reported as `Not loaded` and is never resumed for
  metrics. A failed Windows or WebView2 snapshot is shown as unavailable
  without affecting the terminal or browser.
- Paused projects are restored from the left `Projects` header menu, not from
  the right panel.

## Docs & Notes

The project-tree pseudo-tab is labeled `Docs & Notes`. The center pane has three
primary tabs: `Notes`, `Common MD's`, and `Docs`. `Common MD's` covers every
project Markdown file outside `docs`; `Docs` covers the complete `docs` tree,
including `docs/superpowers`. Files never appear in more than one group.

Document selection happens in the `Documents` tree under Scenarios in the right
panel. The tree is visible only while the documentation pane is open on
`Common MD's` or `Docs`; it is hidden for `Notes` and for other center-pane
surfaces. On its first activation, `Common MD's` opens the root `README.md` when
one exists, and later activations restore the last selected common document.
`Docs` opens no document until the user chooses one, then restores that choice.
This per-group selection memory lasts only for the current application session
and is not persisted.

The explicit `Preview | Editor` segmented control shows the current mode.
Notes defaults to Editor, project files default to Preview, and each document's
manual choice is retained while the pane remains open.

Project Markdown files are edited in place. They use revision-aware atomic
autosave: if disk changed while the local buffer was dirty, saving stops and
the pane offers `Reload from disk` or `Save mine`. A clean active document is
reloaded automatically after an external change. App-owned Notes retain their
existing `%APPDATA%/Pact/Settings/Notes/<key>.md` persistence. Completing a
non-empty mouse or keyboard selection opens the shared center-pane selection
popover without changing autosave, caret, or per-document selection ownership;
an empty completion closes it.

## Important Terminal Compatibility Decisions

- Usage-limit rows preserve their last successfully parsed limits across refresh failures. `?` shows the latest raw source response from the current attempt and is hidden when that response is empty, while `!` independently shows the latest execution or parse error; both can appear together. A resolvable Claude command is authoritative for that profile and never falls back to shared statusline data after a failed or unparseable attempt.
- Terminal tab status is event-driven; stable snapshots use the browser host's one-shot quiescence debounce described above. Do not add a second status timer or mutate UI glyph flags from terminal callbacks.
- Terminal output is batched before crossing the C#-to-WebView bridge: 33 ms for the active session and 100 ms for background sessions, with immediate flush on activation. The browser receives these prebatched writes without a second delay, retains xterm callback backpressure, and exposes passive C#/JS performance snapshots. Cursor blinking is enabled only while the selected terminal pane is visible in the active, non-minimized window.
- Right-click inside xterm is handled by `terminalHost.js`: with selection it copies, without selection it requests paste from the application host. Browser context menus are disabled.
- Clipboard paste is written by the C# session runtime as bracketed paste without
  Enter. CRLF and standalone CR are normalized to LF before the backend write;
  do not route clipboard text through browser-side `term.paste()`.
- xterm HTTP(S) links publish the source session id to the application host.
  The shell opens a saved native browser tab under the same project or ROOT;
  non-HTTP(S) and stale-session requests are rejected.
- `Send selected text` reads selection from the active WebView only. The browser-side host keeps a local `lastSelectedText` cache because moving focus to an application button can clear live selection before C# asks for it.
- `TerminalDisplayOutputFilter` preserves alternate screen and mouse-tracking sequences so agent TUIs can own their view and wheel scrolling. It strips display-side clear-scrollback (`ESC[3J`) and full terminal reset (`RIS`) sequences that break wrapper UX.
- Do not reintroduce transcript preload. Agent TUIs own resumed history, and prior transcript replay produced duplicated mixed frames.

## Settings UI

The right-side gear opens `SettingsWindow`, a form-based editor
(`src/Pact.App.Avalonia/Views/Settings/`) over left-navigation sections: Root
tabs, Projects, Paused projects, Launch profiles, Review profiles,
Orchestrator, Web link templates, Web monitoring rules, Prompt templates, Git
helpers, Scenarios, Recent directories, and Appearance. Selecting a section
shows a top tab strip and a per-type form below.

Editable files, one per section (excluding Projects, which edits live runtime state instead of a settings file):

- `projects.json` (Projects section — see below)
- `root-tabs.json` (Root tabs section — see below)
- `shell-profiles.json` (Launch profiles)
- `review-profiles.json` (Review profiles)
- `orchestrator.json` (Orchestrator)
- `prompt-templates.json` (Prompt templates)
- `web-link-templates.json` (Web link templates)
- `web-monitor-rules.json` (Web monitoring rules)
- `scenarios.json` (Scenarios)
- `git-helpers.json` (Git helpers)
- `recent-directories.json` (Recent directories)
- `appearance.json` (Appearance)

For the file-backed sections, saves round-trip through a node-preserving JSON
pipeline: unknown properties and array entries the form doesn't recognize
survive a save untouched. Loading `scenarios.json` for the settings window
never reseeds or rewrites the file, unlike
`ScenarioDefinitionStore.LoadAsync`'s malformed-file reseed behavior. "Save
section" writes only the active section's file and sets the status line to
`Saved <label> (N items).`; each section validates its own fields before
saving.

Web monitoring rules are evaluated only for web tabs whose WebView host has
actually been loaded. Restored or paused pages have no host and no polling;
their retained unread state can still project from the snapshot. A loaded page
uses the first matching enabled rule in file order, rebaselines after navigation
or a rule-fingerprint change, and projects `Activity -> Unread -> Paused ->
None` (the existing loading glyph temporarily hides that projection). The two
seeded TeamCity and GitLab examples are disabled and contain `CHANGE-ME-`
hostname markers; a rule with an unreplaced marker cannot be enabled. Use
**Test on current tab** against a loaded page before enabling a customized rule.

An event is suppressed and an existing unread is acknowledged only when the
page is selected and the Pact window is both visible and active. Retained unread
survives unload and a URL/rule-fingerprint rebaseline; acknowledgement persists
to its snapshot. Closing the tab, or reaching a confirmed stable URL with no
matching enabled rule, clears unread and removes the stale snapshot.
When a loaded page becomes the selected, visible page in the active window,
the coordinator forces its next DOM evaluation immediately so the presented
status cannot remain behind content that WebView has already refreshed. While
the page stays actively viewed its rule interval is additionally clamped to two
seconds: many sites only refresh their DOM once they become visible, so the
single immediate evaluation still reads the stale document, and the real change
would otherwise be observed one rule interval later — after the user has left
the tab, where it would be recorded as unread instead of seen.

The Projects section doesn't read or write `projects.json` directly: edits go through diff-based `MainWindowViewModel.UpdateProjectSettingsAsync`/`UpdateSessionSettingsAsync`, which write only the fields that changed against live runtime state. Its "+" creates a new project via a directory picker. There is no project or session delete from settings. A session that is locked by a running scenario shows its fields read-only.

The Root tabs section uses the same live diff-based editing boundary for
terminal title, working directory, launch command, and resume command, plus web
page title and URL. Every ROOT terminal exposes its working directory,
including agent kinds. The row edit button deep-links directly to that ROOT
item.

The project tab and each session tab show a compact one-line read-only
`InfoLine` (id, status, timestamps for a project; id, kind, status for a
session) above the editable fields. A session's Working directory field is
hidden for agent-kind sessions (`Codex`/`Claude`/`Hermes` —
`SessionSettingsItemViewModel.ShowWorkingDirectorySetting` is true only for
`Pwsh`/`Custom`), since those always run in the project's own directory;
validation skips the directory-exists check when the field is hidden. The
project's Sessions block is hidden when the project has no sessions
(`ProjectItemViewModel.HasSessions`).

Prompt template's "Send by default" field is relabeled "Auto-submit" in the form (JSON property name is still `sendByDefault`); it controls whether the matching manual prompt action sends Enter after inserting the text. The prompt template Body editor (and every other multiline field) wraps text (`TextWrapping="Wrap"`, no horizontal scrollbar) instead of scrolling horizontally; the Body editor's grid row also uses a star height so it stretches to fill remaining vertical space instead of a fixed min-height box. The Recent directories section has an "Add directory" button that opens a folder picker and appends the picked path as a new line; save trims, deduplicates, and retains at most 20 entries.

Each settings section has a help topic in
`Pact.Presentation.Settings.SettingsHelpContent`. A single "?" button in the
section header opens `SettingsHelpWindow`, a small read-only modal showing that
section's `(Title, Body)` as plain wrapped text. The English bodies in
`SettingsHelpContent` are the runtime source; `docs/help/en/*.md` mirrors them
for translators, and `docs/help/ru/*.md` contains Russian translations. The
documentation files are not read by the app.

Other window behavior: "Open raw JSON" opens the active section's underlying
file in the OS-associated external editor (prompting to save first if the
section is dirty). "Revert" discards in-progress edits and reloads the active
section from disk or, for Projects, from live runtime state after confirming
discard when dirty. Ctrl+S saves the active section; Esc closes the window;
switching sections or closing with unsaved changes prompts to discard.

Two card buttons deep-link into this window: the project card's ✎ opens that
project's Projects tab; the terminal card's ✎ opens the full session editor
with that session selected and the Title field focused. After a settings save,
`MainWindow.ReloadExternalSettingsAsync` rebuilds
`ExternalGitHelperResolver` and clears cached git-panel view models, so
git-helper and project `RootPath` edits take effect without an app restart.
External raw edits to `projects.json` are applied on the next app start.

## Project/Workspace Model

The UI still uses some workspace language internally and in services, but persisted state is project-centered:

- `ProjectRecord` includes project status, active item id, notes, `GitLabRepoId`, `TeamCityProjectId`, nested `SessionRecord[]`, and nested `WebPageRecord[]`.
- `ProjectRecord.NotesTab` marks the visible Notes tab; its body text lives outside `projects.json` under `notes/`.
- The visible pseudo-tab is labeled `Docs & Notes`; project Markdown tabs are discovered from `RootPath` and are not persisted in `projects.json`.
- Pausing a project closes its sessions and marks the project paused.
- Pausing or closing a project, pausing a ROOT terminal, closing a live terminal
  tab, or closing Pact prompts only when the affected session has a live runtime
  controller. Saved `Running`/`Starting` status alone is not confirmation
  evidence.
- Restoring uses per-session `ResumeCommand` when available; otherwise it falls back through shell profile resume templates. New profile sessions store the profile `resumeCommandTemplate` immediately, and graceful shutdown later replaces it with a concrete resume id when capture succeeds.
- Web pages are project items restored from their saved resume/current URL. Prompt and scenario target lists remain terminal-session-only.
- Switching selected sessions does not stop the previous session.

## ROOT tabs

The expanded `ROOT` section is a separate, collapsible tree above uppercase
`PROJECTS`. Its `+` and `@` actions use the same launch-profile and web-link
menus as project cards, but ROOT items are stored in `root-tabs.json` and never
acquire a fake project id. A new ROOT terminal uses the existing Windows user
profile directory as its initial working directory.

Both `@` menus end with `Custom URL...`, which accepts an exact absolute HTTP(S)
address. Terminal and browser rows can be reordered by drag-and-drop only
within the same type and owner; the saved order is updated before the tree is
changed, and failed persistence leaves the visible order untouched.

ROOT has no group pause or close. Each ROOT terminal or browser page has its own
Pause/Resume action. Pausing captures a terminal resume command and stops its
runtime, or unloads the browser host while retaining its monitoring snapshot.
The selected row remains selected and the center shows `Paused`. Selecting a
paused row only selects it; it never resumes it. Only the explicit Resume
button restarts or reloads the item.

ROOT terminals are valid manual quick-action, prompt, send-selection, and
selection-action targets while unpaused. They are never scenario participants
and have no Git, project Markdown, or notes ownership.

## Git Helper Popup

Git repository projects show a `⎇` button on the project card. The popup reads repository state with headless `git` child processes in the project root, never through agent terminal sessions. It shows remote, branch, ahead/behind, working-tree counters, a persistent streaming operation log, common git actions, a rebase-onto-master helper scenario, and conflict resolve/abort actions. The popup stays open while an action-owned modal dialog collects options, so the same operation log remains visible when the command starts.

External GUI helpers such as TortoiseGit are declared in `git-helpers.json` as executable probes plus argument templates. Unresolved helpers are hidden; Resolve falls back to `git mergetool -y` when no configured helper provides a resolve action.

Terminal resize delivery is a shared, agent-independent contract. The browser host
fits the visible xterm and publishes its positive cell dimensions; each live
`TerminalController` serializes backend resize calls and only the newest request may
publish the viewport. Automated coverage exercises the JavaScript bridge, rapid
WebView events, superseded controller requests, and an exact `101x37` boundary in the
real bundled ConPTY.

## Known Caveats

- Hermes still redraws/scales awkwardly during resize even when Pact's xterm
  and bundled ConPTY reach the correct final dimensions. Do not describe
  deterministic delivery as Hermes rendering parity.
- The terminal compatibility layer is pragmatic, not perfect Windows Terminal parity.
- Browser-cache cleanup is intentionally profile-API based; do not replace it with raw Chromium filesystem deletion.

## Useful Verification Commands

The ordinary Avalonia test suite needs Node.js 22+ on `PATH` for the
terminal-host behavior checks; it does not need an npm install.

```powershell
node --version
dotnet restore Pact.slnx --disable-parallel --locked-mode
dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1
```

When using `BaseOutputPath` for test/build isolation, write under `.tmp` and remove `.tmp` only after verifying the resolved path is inside the workspace.

## Documentation Map

- [Documentation index](README.md)
- [English user guide](guide/en/README.md)
- [Russian user guide](guide/ru/README.md)
- [Architecture](architecture.md)
- [Configuration](configuration.md)
- [Development](development.md)
- [Release verification](release-verification.md)
- [Architecture decisions](adr/)
- [Current manual protocols](manual-tests/)
- [English settings help](help/en/README.md)
- [Russian settings help](help/ru/README.md)
