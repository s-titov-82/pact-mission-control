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
their terminal state. Prompt actions and review triggers always target these already
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
actions, MCP, Pact notices, and review triggers) also holds a per-session delivery
gate for its whole cycle, so two programmatic senders never interleave; typed
input does not wait for it. The delivery confirms itself from
`ITerminalWebViewHost.CaptureScreenAsync`, a capture taken after pending output
has rendered and classified with the session's profile. Terminal output is batched before
the WebView boundary; resize requests are serialized and only the newest
request may publish a viewport.

The right panel's selected-tab details are an event-driven projection of those
existing runtime facts. Terminal details use classifier verdict, composer and
content-free structural composer evidence, activity, viewport, and lifecycle metadata; web details use the
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
live review-template-id or review-profile-id set publishes
`notifications/tools/list_changed`, allowing connected ordinary sessions to
re-list tools without a terminal restart. There is no settings file watcher,
and display text, commands, ordering, and unrelated settings do not change the
tool catalog. The orchestrator uses a separate fixed catalog and receives no
such notification.

A single pinned Hermes orchestrator tier sits above ROOT. Its durable
credential exposes a separate cross-session MCP catalog for reading workspace,
session, screen, usage, and active-review state (participants, iteration, whose
turn, log file). It can submit prompts to already-live sessions.

The orchestrator has no project ownership, but its bounded resource tools can
read/append or revision-safely replace Notes for running projects. It can list
active and paused web tabs under running projects and ROOT, resume a tab in the
background without selecting it, and read an already-active host's live
`documentElement` HTML in bounded UTF-16 fragments. Paused projects are
excluded. There is no orchestrator Git, project Markdown/document, arbitrary
JavaScript, or review-start tool. Ordinary session credentials cannot
enumerate or invoke this catalog. The pinned row uses the same selected-terminal
highlight as ordinary terminal rows. Stable terminal screens, extracted last
messages are retained in memory only.

## Projects, documents, and storage

`Settings/projects.json` owns project state and nested saved terminal and web
sessions. Project Markdown files remain in their repositories; Pact-owned
notes live under `Settings/Notes`.

Markdown previews use the official `Markdown.Avalonia` renderer and its native
Avalonia controls, without a browser surface. The package and its transitive
rendering dependencies are pinned by the central package version and lock files.

`AttentionFeedViewModel` projects existing terminal, browser and review indicators
across running projects, ROOT and the enabled orchestrator. It observes current
owner membership and detaches removed sources; it has no journal, storage or
poller. `AttentionFeedView` is a separate, relocatable view currently hosted below
Settings in the right panel. Navigation revalidates the captured owner/target,
uses ordinary tab activation and then reveals its tree row without selecting it
again. Existing tab visibility and review acknowledgement rules clear automatic
attention. A separate `SessionRecord.IsMarkedForLater` bookmark (or the singular
orchestrator setting) remains until the user toggles it off with a double-click.
It never changes classifier state, and appears alongside other attention reasons.

`Settings/root-tabs.json` separately owns project-independent terminal and web
tabs, their last selected item, and explicit per-item pause state. ROOT is not a
sentinel project: it has no project close/pause lifecycle, Git panel, documents,
notes, or reviews. Its unpaused terminals may still participate in
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

## Agent reviews

An agent asks another agent to review its work over MCP; `ReviewRelay`
(Presentation) runs the review and owns its state. A session takes part in at
most one active review, so the session token alone identifies it. The relay
reserves the author before any await, creates the reviewer as a visible but
unselected tab of the author's project, starts it, and waits for readiness;
closing either tab, its process exiting or Pact shutting down while the
reviewer starts ends the review before the first turn.

Turns pass through Pact, never through files. `ReviewConversation` (Core) holds
the turn rules: the reviewer goes first; a reviewer answer either approves
(`approved: true`, Completed) or hands the turn to the author; an author answer
starts the next iteration or, at the limit, ends the review as
MaxIterationsReached. Each turn has an id; a retried submit of an accepted turn
returns its stored result and relays nothing, even after the end. Agents pull
their turn with `pact_get_review_turn`, which returns the message rendered from
the review template, the other side's last answer verbatim as `previousTurn`,
and the `turnId`; they hand it back with `pact_submit_review_turn`. Review
templates are the `scenarios.json` definitions; `ReviewMessageRenderer`
renders them and appends Pact's protocol paragraph, and the old response-file
placeholders render as a pointer to `previousTurn`.

The side whose turn it is gets one short trigger line typed into its terminal
through `DeliverPromptAsync`. A trigger belongs to its turn: accepting the
turn's answer, or ending the review, cancels the trigger and waits for its
delivery to finish before anything is relayed, so a trigger never arrives after
its turn moved on. A delivery that wrote nothing (a question was pending, or the
session was not running) is retried when the session's status next says it can
take a prompt; one that wrote anything is never retyped, since the agent can
always pull its turn. Nothing is locked: the user can type into either tab.

When a turn notification fails or its delivery is unconfirmed, both participants
show `Review needs attention` with a danger-colored badge and recovery guidance.
The project row also shows attention, so a collapsed project cannot hide the warning.
The turn stays available for manual continuation; a possible write is never
repasted. Fetching that turn with `pact_get_review_turn` by its owning participant
acknowledges the notification and clears attention, including a late delivery
failure. A waiting participant's poll does not acknowledge the other side's turn.
The issue is an in-memory part of the existing review summary, not a journal or
terminal transcript.

Every review keeps a Markdown log in `.pact/reviews/` of the author's project
(header, each accepted turn as submitted, footer), appended after each turn;
`.pact/.gitignore` ignores the folder. On Completed or MaxIterationsReached the
reviewer tab closes the way the user would close it; on Failed or Aborted it
stays. Every end opens the log in the project's Docs pane and tells the author
with a last trigger, except during shutdown. Pausing or closing a project ends
the reviews its sessions take part in. Startup removes `.pact-reviews`
directories left by older versions; generic `.reviews` directories are never
owned by Pact.

The trigger uses the same delivery as every other prompt. The paste is
bracketed, or, for a client that captures the mouse, a clipboard paste the
client makes itself on a right click, after a left click at the end of the
composer draft that clears a history selection; Enter then waits until the output shows
it. Only a pending question refuses a write; a busy agent queues the
trigger, and text already in the composer does not block it. Codex and Claude
distinguish composer suggestions and drafts using captured Dim metadata and
structural boundaries; text-only or incomplete observations remain unknown. A
freshly launched reviewer reaches readiness through one bounded budget covering
its first output, a settle delay, and the folder-trust dialog, which Pact
answers with Enter exactly once. Readiness requires a stable screen with the
classifier verdict `Done` or an empty composer recognized on that settled screen
(a fresh agent shows no completion marker; a repainting frame does not count),
and no activity or question; composer content does not
determine activity. Each launch starts without the previous process's
readiness evidence. A fresh capture
is checked for a question immediately before Enter. Delivery is confirmed only
when a fresh capture after Enter shows an empty composer; Pact's own Enter is
never evidence. If a capture still shows the text, Pact repeats Enter once; it
never pastes twice. Sessions whose profile reads no composer (PowerShell,
custom commands) end as submitted once the paste showed and Enter was written.

Bounded application logs retain metadata-only review facts: start, readiness,
each trigger delivery outcome and whether anything was written, each accepted
turn and the end state. They never include prompt, answer, status-line, or
terminal-screen text.

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

Update restoration consumes only the ticket derived from the supplied restart id
and does so once. Ordinary startup uses `Settings/session-state.json`; a valid
update ticket takes precedence. Both use `SessionRestorationState` and the same
restorer. The checkpoint contains only runtime ids, global selection, terminal
unread ids, and orchestrator running intent. Event-driven writes are serialized
and skip equal projections. Startup holds writes until restoration completes;
shutdown freezes and flushes the checkpoint before resume capture or stopping
processes can change selection and lifecycle state. Unexpected exits recover the
last complete atomic checkpoint. Busy, questions, readiness and composer facts
always require fresh screen evidence; no terminal text is persisted.

Persisted tabs remain the durable base; the overlay starts the
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
aborts active reviews, flushes documents, captures agent resume commands under one
shared deadline, stops session backends, disposes WebViews, and finally
releases the data-root mutex.

The shell controller composes and routes; subsystems it used to hold live in
their own App-layer classes, each given only the services and delegates it
uses. `AgentControlToolRouter` sends each MCP request to the session or
orchestrator tool set (`SessionTools`, `OrchestratorTools` in Presentation),
where each tool's name, description, schema, argument parsing and dispatcher call
live in one `AgentControlTool`; listing reads settings snapshots
(`ScenarioDefinitionsSnapshot`, `ReviewProfileProvider.Current`) and never waits
for the UI thread. `SessionLauncher` starts session
processes: the launch command and MCP injection for regular sessions, and one
`StartProcessAsync` core that wires output, submit, input and viewport handlers
for every process, the orchestrator included. `OrchestratorSupervisor` owns
the orchestrator slot's settings, start, stop, restart policy and workstation
lock prompts. `ReviewRelay` (Presentation) runs agent-requested reviews: it
starts the reviewer as a visible unselected tab, hands turns between the two
agents over MCP (`pact_get_review_turn`, `pact_submit_review_turn`), nudges the
side whose turn it is with a short trigger, keeps `.pact/reviews/*.md` and opens
it in Docs when the review ends; `ReviewRelayHost` gives it the shell
operations. `ResumeCaptureService` captures resume commands
before sessions stop.

What the center pane shows is one `PresentedSurface` value on the shell
controller: a terminal, a paused ROOT item, notes, the browser, or nothing. The controller assigns it whole, and the terminal case
also carries its loading state and a focus request that waits for loading to
clear, so showing anything else drops both. `MainWindow` draws the panes from
that value through `CenterPaneVisibility`. Selection stays in the view model.

## Non-goals

- A cross-platform product head.
- A hidden or headless agent API.
- Durable terminal transcripts.
- Windows Terminal rendering parity.
