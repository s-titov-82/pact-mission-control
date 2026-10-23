# Architecture

PACT:> Mission Control is a Windows-first Avalonia desktop application for
operating visible coding-agent terminal sessions. Avalonia is the only product
head.

## Dependency direction

```mermaid
flowchart LR
    App["Pact.App.Avalonia"] --> Presentation["Pact.Presentation"]
    App --> Infrastructure["Pact.Infrastructure"]
    App --> Core["Pact.Core"]
    Presentation --> Infrastructure
    Presentation --> Core
    Infrastructure --> Core
```

- `Pact.Core` contains records, state machines, validation, and other pure
  business rules.
- `Pact.Infrastructure` owns Windows and filesystem integration: ConPTY,
  settings stores, atomic writes, Git processes, and data-root housekeeping.
- `Pact.Presentation` owns UI-neutral orchestration, runtime coordinators, and
  view models.
- `Pact.App.Avalonia` composes the application and adapts Avalonia, native
  WebView2, and Windows UI boundaries.

Dependencies point inward as shown. Core never depends on Presentation,
Infrastructure, or the application head.

## Terminal and WebView boundaries

Each live terminal session owns one `TerminalController`, one bundled ConPTY
backend, and one xterm.js instance. Selecting another session changes which
xterm instance is presented; it does not stop background processes or rebuild
their terminal state. Prompt and scenario actions always target these already
live, user-visible sessions. The application is not a headless API proxy for
subscription TUI tools.

The Avalonia host communicates with the local xterm page through native
WebView2. Browser tabs use separate native WebView2 controllers. Loaded hidden
terminal and browser pages remain operational: the WebView2 environment adds
`--disable-background-timer-throttling`,
`--disable-renderer-backgrounding`, and
`--disable-backgrounding-occluded-windows`. This is deliberate because
background terminal output and web monitoring must continue while another tab
is selected. The tradeoff is higher CPU, memory, and power use than allowing
Chromium to suspend hidden content.

Those arguments do not change what a page is told: a hidden browser tab's
WebView2 controller is invisible, so the page sees `document.hidden` and gets no
animation frames, and pages such as TeamCity then pause their live updates.
A tab whose address matches an enabled monitoring rule therefore runs
`WebAssets/backgroundVisibility.js` at document creation: `WebMonitorCoordinator`
turns it on through `IWebPageHost.SetKeepsRunningWhileHiddenAsync` when the
confirmed address matches and off when no rule matches, which also gives the
already loaded page its real visibility back. A failed change is retried by the
next monitor pass. Other tabs pause normally while hidden. It reports the document as visible, withholds only the transition to
hidden (the transition back still reaches the page), and runs a pending
animation-frame callback on a timer while the tab is actually hidden. The engine
probe's `background-visibility` and `background-frames` checks cover it.

Terminal screen and viewport facts cross into C# as immutable snapshots. Screen
snapshots retain physical rows/wrapping and UTF-16 Dim ranges from the bottom 15
xterm rows, alongside the existing logical-text projection. The browser captures
agent-neutral cell metadata; Core profiles recognize composer boundaries and
distinguish dim suggestions from ordinary drafts. Uncaptured or invalid styling
remains unknown. Work state and composer contents stay independent. A working
agent repaints too often for the screen to settle, so while it repaints the
browser also posts an unsettled snapshot at most once a second: it may show the
agent busy and, when the whole composer frame is recognized, what the composer
holds, but never that work finished. Enter clears the composer fact until a
newer frame shows it.
Runtime coordinators serialize mutable session ownership, and UI-facing
projections cross an explicit UI dispatcher. Every write to a session's
terminal goes through `SessionRuntimeCoordinator` as one exclusive input unit
per session, run in queue order, so a paste and its Enter are never split by
another writer. Enter and Esc are encoded once, by `AgentKeyEncoder`, from the
session's agent kind and win32-input-mode state; the status engine uses the
same encoder to recognize a submit. A prompt delivery
(`SessionRuntimeCoordinator.DeliverPromptAsync`, the one sender for manual
actions, MCP, Pact notices, and scenarios) also holds a per-session delivery
gate for its whole cycle, so two programmatic senders never interleave; typed
input does not wait for it. The delivery confirms itself from
`ITerminalWebViewHost.CaptureScreenAsync`, a capture taken after pending output
has rendered and classified with the session's profile. Terminal output is batched before
the WebView boundary; resize requests are serialized and only the newest
request may publish a viewport.

The right panel's selected-tab details are an event-driven projection of those
existing runtime facts. Terminal details use classifier verdict, composer and
content-free structural composer evidence, activity, viewport, lifecycle, and scenario-lock metadata; web details use the
saved resume URL plus the live monitor registration, matched rule, observation,
schedule, navigation, unread, and sanitized failure metadata. The projection
does not expose or persist a raw terminal-screen snapshot. External process
metrics have a separate, default-off Appearance preference. While both that
preference and selected-tab details are visible, the Windows infrastructure
reader samples either the selected live terminal's root PID and descendants or
an already-loaded selected WebView2 host. An in-memory UI monitor converts
cumulative processor time into machine-normalized CPU over two-second samples
and publishes combined working set and process count. WebView2 extended process
information attributes renderers exclusively rooted at the selected main frame
to the page; browser, GPU, utility, and renderers shared across root frames are
reported separately as shared runtime. Renderers belonging only to other tabs
are excluded. Switching away, hiding details, disabling the preference, or
losing the live host/PID stops the corresponding poller. Paused web tabs are
never loaded or resumed for metrics.

See [Bundled ConPTY](adr/0002-bundled-conpty.md) and
[Native WebView2 with xterm.js](adr/0006-native-webview2-and-xterm.md).

## Agent control boundary

Qualified terminal launch profiles receive an ephemeral per-session token and
the loopback-only MCP endpoint address. The token identifies the calling live
session, so notes and browser actions stay under that session's project or
ROOT owner. Tokens are revoked on stop, exit, and restart; endpoint shutdown
stops admission and drains accepted requests before terminal and WebView
resources are disposed.

At startup Pact atomically publishes its short agent guidance as owner-managed,
disposable Markdown under `Temp/Retained/PactSkills`. Launch composition adds a
conditional pointer, never the miniskill bodies, after selecting the direct
process or PowerShell route. Arguments remain structured until that point:
direct launches use Win32 argument quoting, while PowerShell launches use an
encoded script and literal argument array so spaces, dollar signs, backticks,
and other path metacharacters survive unchanged. Claude receives the pointer
through `--append-system-prompt`; Codex receives an invocation-level
`developer_instructions` override. This applies to ordinary, ROOT, reviewer,
restart, and resume launches. When agent control is disabled the pointer names
only the common guidance; when enabled it also names the MCP guidance and the
session receives the endpoint carriers. A publication failure is diagnostic
only: MCP injection continues, but Pact omits pointers to files it did not
confirm as published. Retained Markdown contains no endpoint or credential.

The ordinary endpoint exposes only owner-scoped, UI-equivalent actions:
read/append project Notes, revision-safely replace their complete text, create
a browser tab, and request a file-first review. ROOT callers have no project
Notes. It does not create a headless agent execution path or modify global
Codex or Claude configuration.
`Settings/review-profiles.json` owns reviewer commands; agent-kind launch
argument templates remain code-owned and shared with normal session launch.

The HTTP MCP transport advertises `tools.listChanged=true`. An authenticated
GET opens the server-sent-event notification channel, while JSON-RPC requests
remain authenticated POSTs. Initialize responses issue an opaque
`Mcp-Session-Id` bound to the bearer credential; supplied ids are validated
before routing. Claude's authenticated GET without that session header remains
accepted for client compatibility. After a Settings reload, a change to the
live scenario-id or review-profile-id set publishes
`notifications/tools/list_changed`, allowing connected ordinary sessions to
re-list tools without a terminal restart. There is no settings file watcher,
and display text, commands, ordering, and unrelated settings do not change the
tool catalog. The orchestrator uses a separate fixed catalog and receives no
such notification.

A single pinned Hermes orchestrator tier sits above ROOT. Its durable
credential exposes a separate cross-session MCP catalog for reading workspace,
session, screen, usage, and detailed active-review state, including the current
step, pause state, expected exchange file, and in-memory journal. It can submit
prompts to already-live unlocked sessions and cooperatively request or resume a
review pause.

The orchestrator has no project ownership, but its bounded resource tools can
read/append or revision-safely replace Notes for running projects. It can list
active and paused web tabs under running projects and ROOT, resume a tab in the
background without selecting it, and read an already-active host's live
`documentElement` HTML in bounded UTF-16 fragments. Paused projects are
excluded. There is no orchestrator Git, project Markdown/document, arbitrary
JavaScript, or scenario-start tool. Ordinary session credentials cannot
enumerate or invoke this catalog. The pinned row uses the same selected-terminal
highlight as ordinary terminal rows. Stable terminal screens, extracted last
messages, and review journals are retained in memory only.

## Projects, documents, and storage

`Settings/projects.json` owns project state and nested saved terminal and web
sessions. Project Markdown files remain in their repositories; Pact-owned
notes live under `Settings/Notes`.

`Settings/root-tabs.json` separately owns project-independent terminal and web
tabs, their last selected item, and explicit per-item pause state. ROOT is not a
sentinel project: it has no project close/pause lifecycle, Git panel, documents,
notes, or scenario ownership. Its unpaused terminals may still participate in
manual prompt and selection actions.

`Settings/agent-control.json` owns the fixed loopback endpoint port.
`Settings/orchestrator.json` owns the singular slot, its durable credential,
launch command, lock/unlock prompts, and the two opt-in switches. Provisioning
writes the same endpoint and credential into the dedicated Hermes profile
`.env`; no global Codex or Claude configuration is changed.

`Settings/appearance.json` owns the theme and whether selected-tab details are
shown. The form editor preserves unknown JSON fields so future appearance
options can coexist with older application versions.

Terminal and browser order is persisted inside each owning project or ROOT
record. Reordering never transfers ownership or mixes terminal and browser
groups. Terminal HTTP(S) link activation crosses the terminal host boundary as
a session-scoped event; the shell creates a saved browser page under that
session's existing project or ROOT owner.

Editable documents expose one immutable persistence projection:

- `Clean` — the current buffer is persisted;
- `Dirty` — local changes are waiting to be saved;
- `Saving` — persistence is in progress;
- `Conflict` — disk changed since the local revision;
- `Failed` — the last save failed and local edits remain retryable.

The data root is `%APPDATA%/Pact` unless `--data-root <absolute-path>` is
supplied. It has exactly four top-level directories:

- `Settings` — durable JSON and notes;
- `WebView` — shared WebView2 user data;
- `Logs` — bounded disposable logs;
- `Temp` — session staging and owner-managed disposable snapshots.

The process lease is a named operating-system mutex derived from the normalized
data-root path. No lock file is stored under `Settings` or elsewhere in the
profile.

## Scenario transport

The scenario coordinator owns the single active review slot per project,
including reservations while an agent-requested reviewer terminal is starting.
The shell owns creation and rollback of that terminal; the view model owns
neither lifecycle. Runs reach terminals through `RuntimeScenarioGateway`, which
calls `SessionRuntimeCoordinator` directly (delivery as the scenario owner,
Esc, liveness from the runtime's controller) and never waits for the UI thread.
A run's state is one `ScenarioPauseState` value with pure transitions in
`Pact.Core.Scenarios.ScenarioPauseMachine`: the run state, the pause kind
(manual or attention), the attention reason (nothing sent, unconfirmed,
watchdog), the stuck session, and which bound sessions are unlocked.

Scenario journals are in memory. Each review-loop step atomically publishes
one immutable task file below
`.pact-reviews/<run>/pass-NNN-<role>-task.md`. The terminal receives only the
short task-path instruction as one paste and then a separate Enter. The paste is
bracketed, or, for a client that captures the mouse, a clipboard paste the
client makes itself on a right click, after a left click at the end of the
composer draft that clears a history selection; Enter then waits until the output shows
it. Only a pending question refuses a run's write; a busy agent queues the
trigger, and text already in the composer does not block it. Codex and Claude
distinguish composer suggestions and drafts using captured Dim metadata and
structural boundaries; text-only or incomplete observations remain unknown. A
freshly launched reviewer reaches readiness through one bounded budget covering
its first output, a settle delay, and the folder-trust dialog, which Pact
answers with Enter exactly once. Readiness requires a stable screen with the
classifier verdict `Done` or a recognized empty composer (a fresh agent shows no
completion marker), and no activity or question; composer content does not
determine activity. A fresh capture
is checked for a question immediately before Enter. Delivery is confirmed only
when a fresh capture after Enter shows an empty composer; Pact's own Enter is
never evidence. If a capture still shows the text, Pact repeats Enter once; it
never pastes twice. Sessions whose profile reads no composer (PowerShell,
custom commands) end as submitted once the paste showed and Enter was written.

Bounded application logs retain metadata-only scenario delivery decisions and
transport exceptions so a later retry stall can be reconstructed. Entries name
the run, step, iteration, role, session, attempt, outcome, and whether write or
submit was attempted; they never include prompt, response, status-line, or
terminal-screen text. Identical consecutive delivery outcomes are coalesced.

The response observer starts before delivery and remains active through every
watchdog and pause state. A delivery that wrote nothing (a question was already
pending, or the session was not running) is retried once the terminal is safe.
Once anything reached the terminal, an unconfirmed delivery pauses for attention
and is never resent; the response file decides. The step watchdog likewise only
pauses for attention and keeps waiting. A complete footer-valid response is
authoritative: it cancels any delivery still in progress before the run may
use it, ends an attention pause, re-locks the terminals, and advances the run.

Manual Pause releases every involved terminal and suppresses all new automatic
writes until explicit Resume, but it does not stop response observation. A
response completed during manual Pause is read, stops the delivery, and is kept
(`ScenarioIterationContext.AcceptResponseAsync`); the run advances with it only
after Resume. Watchdog attention releases only the terminal that needs
attention. Paused runs retain their files; the run
state and journal remain in memory rather than introducing a second durable
state file. The final result and journal are rendered as read-only Markdown
views. Completion, maximum-iteration, abort, and failure states remove only that
run's Pact-owned directory, and startup removes abandoned `.pact-reviews`.
Generic `.reviews` directories are never owned by Pact.

## Update and soft-restart boundary

The process-wide update coordinator checks only the fixed public GitHub
repository's stable `vMAJOR.MINOR.PATCH` releases. It checks at startup and then
hourly, honors GitHub rate-limit reset time, and never downloads an asset until
the user accepts the release offer. Downloaded Setup and checksum assets are
bounded, streamed, verified, and atomically staged below
`Temp/Retained/Updates`; update state is not durable product configuration.

Applying an update uses the same restoration overlay as the diagnostic soft
restart. The shell captures only live tab identities, selection, unread state,
and available agent resume data, then writes a one-time ticket at the path
derived from a cryptographic restart id. Active terminal work, any active or
paused review, or begun shutdown blocks handoff. Safety is checked again after
the updater copy and ticket are ready; only a successfully started helper may
enter the confirmed graceful-shutdown path.

`Pact.Updater.exe` is copied out of the installation before launch. It waits for
the source Pact process and then requires both `Pact.App.Avalonia.exe` and
`conpty/OpenConsole.exe` to accept exclusive read/write opens in the same poll
iteration. Setup receives the running executable's parent through an explicit
`/DIR`, never a compiled-in assumption about the installation directory. The
installer disables its implicit previous-directory override; its code-backed
interactive default reads the current-user AppId `InstallLocation` and falls
back to `%LOCALAPPDATA%\Programs\Pact Mission Control`. The helper forbids
Windows restart, records one terminal outcome in the same ticket, deletes the
staged Setup, and relaunches the exact Pact path.

Restoration consumes only the ticket derived from the supplied restart id and
does so once. Persisted tabs remain the durable base; the overlay starts the
previously active terminal and browser set, restores unread markers and
selection, and starts the orchestrator through its separate provisioned path.
An agent terminal with both its command template and an extracted id resumes
that conversation; with the template alone it resumes into the agent's own
conversation picker. Without a usable template it cold-starts and the bounded
summary reports that fallback rather than omitting the tab. Paused tabs remain paused. A failed Setup
uses `UpdateNotApplied`, allowing the previous executable to consume the same
snapshot and report the sanitized failure category.

## Lifetime ownership

The application bootstrap owns startup cancellation and the close-during-start
boundary. The shell controller owns terminal/session shutdown; runtime
coordinators own their controllers; dependency injection owns registered
singletons such as web monitoring. Shutdown drains observed event tasks,
aborts scenarios, flushes documents, captures agent resume commands under one
shared deadline, stops session backends, disposes WebViews, and finally
releases the data-root mutex.

The shell controller composes and routes; subsystems it used to hold live in
their own App-layer classes, each given only the services and delegates it
uses. `AgentControlToolRouter` parses MCP tool arguments and routes session and
orchestrator calls to their dispatchers. `SessionLauncher` starts session
processes: the launch command and MCP injection for regular sessions, and one
`StartProcessAsync` core that wires output, submit, input and viewport handlers
for every process, the orchestrator included. `OrchestratorSupervisor` owns
the orchestrator slot's settings, start, stop, restart policy and workstation
lock prompts. `ReviewLaunchService` starts agent-requested reviews and rolls
the reviewer back on failure. `ResumeCaptureService` captures resume commands
before sessions stop.

What the center pane shows is one `PresentedSurface` value on the shell
controller: a terminal, a paused ROOT item, notes, a scenario journal, the
browser, or nothing. The controller assigns it whole, and the terminal case
also carries its loading state and a focus request that waits for loading to
clear, so showing anything else drops both. `MainWindow` draws the panes from
that value through `CenterPaneVisibility`. Selection stays in the view model.

## Non-goals

- A cross-platform product head.
- A hidden or headless agent API.
- Durable terminal transcripts or scenario journals.
- Windows Terminal rendering parity.
