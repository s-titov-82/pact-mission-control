# Cross-agent review over the Pact MCP

Created: 2026-10-23.

Status: direction agreed in conversation on 2026-10-23. Replaces the scenario
engine for the review loop and tidies the agent-control (MCP) layer.

## Why

- `pact_request_review` does not work: the hidden reviewer it creates never
  becomes ready ("the session never became ready to receive input", seen on
  build `0.1.12+6a9b200`). The reviewer is invisible, so the user cannot see
  where it stalls, and review launch writes nothing to the application log.
- The review loop runs on a scenario engine with its own UI (setup dialog,
  run journal), task/response files in `.pact-reviews`, a step watchdog, input
  locks and a pause machine — about two thousand lines, and most of the
  scenario glitches. There is exactly one scenario kind (`reviewLoop`).
- The user's direction: a scenario is not a rigid harness with its own UI but
  a skill for the MCP. The skill describes the protocol; the author and the
  reviewer take turns and hand their conclusions to each other **through
  Pact**, never directly.
- Each MCP tool is described in three places joined only by its name string:
  the schema (`AgentControlToolCatalog` / `OrchestratorToolCatalog`), argument
  parsing and dispatch (`AgentControlToolRouter`, 545 lines, mostly hand
  parsing), and the agent instructions (`PactMcpSkill.md`).

## Goals

1. **An agent can ask another model for a review and it works.** The author
   calls `pact_request_review`; Pact creates the reviewer as an ordinary
   visible tab in the author's project, **without switching to it** — the user
   notices it by its tab status icon.
2. **The review is a relay, not an engine.** Pact wakes the side whose turn it
   is with a short trigger typed into its terminal; the agent fetches the full
   message through MCP and hands its answer back through MCP. Pact checks who
   may answer and the iteration limit, keeps a log, and nothing else.
3. **The review log is saved and shown.** The exchange is written to
   `.pact/reviews/<file>.md` inside the project as it happens; when the review
   ends Pact opens the file in the Docs pane so the user does not have to jump
   between tabs.
4. **The reviewer tab closes itself** when the review ends normally (approved,
   or the iteration limit reached). On failure or cancellation it stays, so the
   user can see where it stopped.
5. **`pact_open_document(path)`** lets an agent open a project Markdown file
   (for example a plan it just wrote) in the Docs pane and **switches the user
   to it**. (Unlike `pact_open_web_tab`, which creates its tab without
   selecting it.)
6. **The manual review button becomes a Quick Action**: a default prompt
   template that asks the selected agent to request a review. No setup dialog.
7. **One description per MCP tool**: name, schema, argument parsing and
   handler live together; the two catalogs and the router are built from those
   descriptions. `tools/list` no longer blocks on the UI thread.

## Non-goals

- A kanban board and a project-manager orchestrator (backlog).
- Day/week summaries across projects from session transcripts (backlog).
- Diff viewing.
- New orchestrator tools beyond adapting the review ones (see below).
- Surviving a Pact restart in the middle of a review: review state is in
  memory, as runs are today. The log file already written stays.

## Anti-goals

- **Agents talking to each other directly.** Every turn goes through Pact,
  which enforces participants and the limit.
- **Pasting long messages into a TUI.** Codex on Windows receives pasted text
  key by key; long relayed text is slow and gets truncated. Only a short,
  fixed trigger is typed; the content travels through MCP.
- **Losing the tuned review prompts.** The user's `scenarios.json` holds
  carefully tuned reviewer prompts (plan review, code review) with reviewer
  instruction variants (strict, allow minors). They stay, editable as today.
- **Rolling back a reviewer that failed to start.** Today a failed reviewer is
  deleted; the new reviewer tab stays with its screen so the user sees why.

## Constraints

- Delivery into a terminal goes through `SessionRuntimeCoordinator.DeliverPromptAsync`
  (the serialized per-session path); nothing writes to a terminal around it.
- Readiness of a freshly started reviewer is `WaitForSessionReadyAsync`: a settled
  screen with verdict Done or a recognized empty composer, not busy, no question; one
  trust-dialog answer. A fresh agent shows no completion marker, so the empty composer
  (read from a settled screen, not a repainting frame) is what says it can take a prompt.
- A terminal created hidden starts at the size the host picked for its process (the
  last fitted size, or the default before any); the page never chooses a size of its own.
- The participant is identified by the session token of the MCP call
  (`call.SessionId`), never by an argument.
- An MCP-less session cannot take part: MCP comes from the profile's
  agent-control argument template. Pact refuses to start a review when the
  author or the chosen reviewer profile has no agent-control injection, with
  an error that says so.
- The Docs pane shows only Markdown inside the project
  (`ProjectMarkdownCatalog`). `.pact` is not in its ignored directory list, so
  `.pact/reviews/*.md` appears under Common.
- `.pact/` gets its own `.gitignore` containing `*`, the same way
  `ReviewExchangeDirectory` keeps `.pact-reviews` out of git today. Pact does
  not edit the repository's own `.gitignore`.
- Build and test limits from `AGENTS.md`/user rules (≤2 msbuild nodes, two
  NUnit workers, one heavy build at a time); no timing-sleep tests.

## The review relay

### Review templates (kept settings)

`scenarios.json` and its settings section stay; each `reviewLoop` definition
becomes a **review template**. Its fields keep their meaning as message
wrappers:

| Field | Used for |
|---|---|
| `startPromptTemplate` (`{target}` …) | the reviewer's first message |
| `firstFeedbackTemplate` | the author's message carrying the reviewer's first findings |
| `authorReturnTemplate` | the reviewer's message carrying the author's answer |
| `feedbackTemplate` | the author's message carrying later findings |
| reviewer instructions + default id | appended to the reviewer's messages as today |
| `maxIterations` | default iteration limit |
| `defaultTarget` | dropped (2026-10-24): the agent that asks for a review always names its scope; an old value in the file is ignored |
| `stopMarker` | no longer needed: approval is an explicit flag (see below). Kept in the file, ignored. |

The reviewer-instruction composition stays as the engine renders it today
(`ReviewLoopScenarioProgram`); the builder moves that rendering, it does not
redesign it.

**The previous turn's content never depends on the template.** Today the
feedback templates do not carry the other side's answer; they point at a file
through `{reviewResultFile}` / `{authorFeedbackFile}` and tell the agent to
read it. Those files are gone. Therefore:

- `pact_get_review_turn` always returns the other side's last submitted text
  verbatim in its own field (`previousTurn`), separate from the rendered
  wrapper, whatever the template says.
- `{reviewResultFile}` and `{authorFeedbackFile}` render as a fixed phrase
  pointing at that field (for example "the `previousTurn` text returned with
  this message").
- Every rendered message ends with a fixed protocol paragraph from Pact:
  the previous turn is in `previousTurn`; ignore any instruction above to read
  or write review files; hand the answer back with `pact_submit_review_turn`
  and this message's `turnId`. The user's template text is not rewritten.
- For the reviewer, the paragraph also says: wherever the instructions above
  say to emit the completion marker, call `pact_submit_review_turn` with
  `approved: true` instead and do not write the marker; when approving, the
  text is the final run summary (today's wording in
  `ReviewLoopScenarioProgram.BuildReviewerTaskProtocol`, kept). The
  `{stopMarkerPrefix}`/`{stopMarkerSuffix}` placeholders keep rendering as
  today so the user's reviewer instructions still read sensibly.
- The shipped defaults (`src/Pact.Infrastructure/Defaults/scenarios-default.json`)
  are reworded to the new protocol; the user's own `scenarios.json` is left
  as it is and relies on the two rules above.

### Tools (session side)

- `pact_request_review(scenarioId, reviewProfileId, target, maxIterations?)` —
  the caller becomes the author. Keeps today's arguments and enums. A ROOT
  session is refused (no project to hold the reviewer tab and the log). Pact:
  1. refuses if the caller already takes part in an active review in either
     role (a session belongs to at most one active review at a time; this
     replaces today's one-per-project slot). Because of this rule the
     session token alone identifies the review in the two tools below;
  2. creates the reviewer session from the profile as a visible tab in the
     author's project, not selected (`select: false`, but visible in the tab
     list, not hidden);
  3. starts it and waits for readiness;
  4. on readiness, writes the log header, sets the turn to the reviewer and
     delivers the trigger; returns the review id.
  If readiness fails, the review ends as Failed: the tool returns the error
  with the reviewer's last status line, the reviewer tab stays.
- `pact_get_review_turn()` — returns, for the calling participant: the review
  id, the review state, the iteration, whether it is the caller's turn, the
  rendered message, `previousTurn` (the other side's last submitted text), and
  a `turnId` when it is the caller's turn. A caller that took part in no
  review, current or just finished, gets an error. After the review has ended
  it returns the end state, the reason, and the final text (the reviewer's
  approval or last findings), with no `turnId`: nothing more can be
  submitted.
- `pact_submit_review_turn(turnId, text, approved?)` — the caller hands over
  its turn. Pact checks the `turnId`:
  - the current turn of the caller: accepted. From the reviewer,
    `approved:true` ends the review as Completed; otherwise the text goes to
    the author. From the author, the text goes to the reviewer, unless the
    iteration limit is reached, which ends the review as MaxIterationsReached
    (the author's last answer is still logged). `approved` from the author is
    an error;
  - a turn of the caller that was already accepted: the stored success result
    of that turn is returned again and nothing is relayed (a retried call
    after a lost result is harmless). This rule wins over the next one: it
    holds after the review has ended too, so repeating the final
    `approved:true` returns its original result;
  - anything else (unknown id, the other side's turn, a new answer to an
    ended review): an error that names the current state.

The trigger typed into the terminal is short and fixed, for example
`Pact: your review turn is ready — call pact_get_review_turn.` It is the same
text whichever side receives it.

### Lifecycle

- **States:** Running, Completed, MaxIterationsReached, Failed, Aborted.
- **Delivery of a trigger** uses the orchestrator's policy: a busy session is
  fine (Codex and Claude queue input), a session showing a question is not.
  What happens next depends on the `PromptDeliveryResult`:
  - **nothing was written** (`NotRunning`, or `RefusedQuestion` with
    `WriteAttempted: false`): the trigger stays pending and the whole
    delivery is retried when that session's status next becomes Done
    *(my call: no timer; the status change is the signal)*;
  - **text was written** (`RefusedQuestion` with `WriteAttempted: true`,
    `Unconfirmed`, or any other outcome after a write): the trigger is never
    typed again — the same rule as scenario delivery today. The trigger text
    stays in the agent's composer or was submitted unconfirmed; the review
    keeps waiting, the outcome goes to the application log, and the user can
    press Enter in that tab. The agent can always pull its turn with
    `pact_get_review_turn`, so a lost trigger never loses content;
  - `Confirmed` / `Submitted`: done.
- **A trigger belongs to its turn.** Accepting a turn's answer cancels that
  turn's pending or in-flight trigger delivery and waits for it to finish
  before relaying (an agent may pull and answer its turn before the trigger
  arrives). Ending the review cancels the deliveries of all its turns the same
  way; the final notice to the author is a separate delivery started after
  that. A trigger therefore never arrives after its turn changed or the
  review ended.
- **Failed:** the reviewer's or the author's process exits while the review is
  running, or the reviewer never becomes ready.
- **Aborted:** the user closes the reviewer tab or the author tab, or Pact
  shuts down.
- **On Completed / MaxIterationsReached:** write the log footer, close the
  reviewer tab (the same path as the user closing it), open the log in the
  Docs pane of the author's project and switch to it.
- **On Failed / Aborted:** write the footer with the reason; the reviewer tab
  stays (if it still exists); open the log the same way.
- **On every end state the author is told**, if its session is still running:
  one last trigger is delivered (same delivery rules), and
  `pact_get_review_turn` returns the end state, reason and final text as
  described above. Shutdown is the exception: nothing is delivered while Pact
  closes.
- No input locks, no pause, no step watchdog. The user can type into either
  tab at any time.

### Log file

- Path: `.pact/reviews/<yyyy-MM-dd-HHmm>-<template id>-<short review id>.md`
  in the author's project root.
- Header: template name, target, author and reviewer tab titles and agent
  kinds, iteration limit, start time.
- One section per turn: who, iteration, time, the text as submitted (not the
  rendered wrapper).
- Footer: end state, reason, time.
- Written incrementally (append after each turn), so a crash keeps what
  happened.

### Diagnostics

Review start, readiness wait outcome, each trigger delivery outcome, each
turn submission and the end state are written to the application log
(no message text, as the delivery traces already do). The readiness failure
seen today is reproduced first with these diagnostics and a visible reviewer;
its cause is fixed in the new launch path, whatever it turns out to be.

### Orchestrator

`pact_list_active_runs` and `pact_get_review_run` become views over active
reviews (state, participants, iteration, whose turn, log path).
`pact_pause_review` and `pact_resume_review` are removed together with the
pause machine.

### What is removed

The scenario engine and its UI: `ScenarioRunService`, `ScenarioRunHandle`,
`ScenarioIterationContext`, `ScenarioPauseMachine`, `ReviewLoopScenarioProgram`,
`RuntimeScenarioGateway`/`IScenarioTerminalGateway`, `ReviewExchangeDirectory`
and `.pact-reviews`, the setup dialog (`ScenarioSetupViewModel`), the run
journal view (`ScenarioJournalView`) and the `ScenarioRun` surface case,
scenario input locks, `ReviewLaunchService` (its job moves into the relay).
The builder deletes what becomes unreferenced; anything still needed by the
review templates settings stays.

## `pact_open_document(path)`

- `path` is relative to the calling session's project root (absolute paths
  inside the root are accepted too).
- Refused: ROOT sessions (no Docs pane), a path outside the root (after
  resolving `..` and reparse points), a non-`.md` file, a missing file.
- Effect: select the caller's project, show its Docs/Notes pane on the
  section that contains the file (Docs for `docs/…`, Common otherwise),
  expand the tree to it and open it. The user is switched there.
- The review log uses the same operation.

## Quick Action

A default prompt template "Request review" is added to the shipped
`prompt-templates.json` defaults: it asks the agent to request a cross-agent
review through Pact and to ask the user for the template, reviewer profile and
target if they are not clear. The user edits it like any template. Existing
user template files are not modified; defaults only seed a missing
`prompt-templates.json`, so an existing user adds the template in Settings
(the final report gives its text).

## One description per tool

- A tool is one object: name, description, input schema, argument parsing
  into a typed request, and the handler. Session tools and orchestrator tools
  are two lists of such objects; `tools/list` and dispatch are derived from
  them.
- Enumerated arguments that depend on live settings (scenario ids, review
  profile ids) are read from snapshots that do not require a blocking
  UI-thread call.
- `PactMcpSkill.md` is updated to the new tool set and describes the review
  protocol: on a trigger, call `pact_get_review_turn`, do the work, hand back
  with `pact_submit_review_turn`; the reviewer approves only when it has no
  remaining findings that the template's instructions require fixed.
- The Hermes orchestrator templates that list tool names are updated.

## Order

1. One description per tool (no behavior change) and `tools/list` off the UI
   thread.
2. `pact_open_document`.
3. The review relay with visible reviewer, diagnostics, log file and tab
   closing; reproduce and fix the readiness failure here.
4. Remove the scenario engine and its UI; adapt the orchestrator review tools.
5. Quick Action template, `PactMcpSkill.md`, Hermes templates, docs
   (`docs/architecture.md`, backlog notes).

## Left to the builder

- Where the relay lives (a Presentation service next to
  `SessionRuntimeCoordinator` is the expected home; it must not need the
  shell controller beyond creating/closing tabs and opening a document).
- Exact trigger wording, log formatting, review id format.
- How the template text is migrated (see Review templates).
