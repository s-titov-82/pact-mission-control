# One prompt sender with confirmed stages

Created: 2026-10-14. Revised the same day after review.

Status: design agreed in conversation on 2026-10-14; step 3 of
[the architecture analysis](../../backlog/2026-10-14-architecture-analysis.md).

## Outcome

Every prompt that Pact puts into a session goes through one sender with the same
stages, the same refusal rules, and the same confirmation. The sender never
resends text. It repeats only Enter, and only when a fresh look at the screen
shows the text still waiting in the composer. A scenario never resends a step
trigger: the response file decides whether the step finished.

Success criteria:

- Template, selection, paste-command, MCP, workstation-lock, and scenario
  prompts behave identically for refusal and confirmation.
- Pact's own Enter is no longer evidence that the agent took the prompt.
- No code path pastes the same prompt twice.
- A whole delivery, from paste to result, holds the session against every other
  programmatic sender.
- Sending to a session that is not running either starts it and waits for real
  readiness (manual actions), or refuses and leaves the start to the caller
  (MCP, scenarios, Pact notices). The fixed 600 ms delay is gone.
- Each delivery writes one diagnostic trace line.

## What is wrong today

- `ActivityEpoch` rises on every submit written by Pact
  (`TerminalTabStatusEngine.OnUserInput` starts an activity on Enter), and
  `IsBusy` is the tab indicator that this activity turns on. The scenario
  confirms delivery by `ActivityEpoch > before`, so it confirms its own Enter.
- Retained screen facts are not fresh enough to confirm anything. A snapshot is
  stamped when the host receives it, not when the web view captured it, so one
  captured before Enter can arrive after it. The web view also skips a stable
  snapshot identical to the previous one, so a dropped Enter that changes
  nothing on screen produces no new evidence at all.
- A bracketed paste is followed by Enter without checking that the text landed.
  Only the clipboard path (SGR-mouse clients) waits for the paste. The "quiet
  and empty composer, so repaste" repair compensates for this.
- Refusal differs by channel: manual actions check only the scenario lock;
  `OrchestratorDispatcher` checks lock and question before the write turn;
  the workstation-lock prompt checks the question in the shell; the scenario
  refuses on busy, question, and unsent text.
- Confirmation and repair run outside the session's input turn, so another
  programmatic sender can write between the Enter and the repair.
- The scenario resends a trigger after an unconfirmed write
  (`ScenarioIterationContext.SendAsync`) and again from the step watchdog
  (`ReviewLoopScenarioProgram` recovery callback).
- A manual send to a stopped session waits a fixed 600 ms after first output.

## Decisions

1. **Refusal is the same for everyone:** a pending question, and a scenario
   lock held by someone else. A busy agent and text in the composer never
   refuse: the agent queues what it is given, and whoever sent it asked for it.
2. **A scenario sending to its own sessions** is refused only by a question.
3. **Not running:** manual actions start the session and wait for readiness;
   every other sender is refused with "session is not running". MCP gets a new
   `pact_start_session` tool so the calling agent can start it first.
4. **One delivery at a time per session.** The whole cycle (admission, paste,
   landing, Enter, confirmation, repair) holds a per-session delivery gate.
   Keyboard input is not held by the gate (see Design).
5. **Confirmation** is a fresh observation, taken after Pact's Enter, showing
   an empty composer (`PromptIsEmpty == true`). `null` never confirms. Busy is
   not required, whether the agent was idle or already working: with the paste
   shown before Enter, an empty composer after it means the text left the
   composer. This also covers an idle agent that answered so fast that no busy
   frame was captured.
6. **Repair** is one more Enter, only when a fresh observation shows the
   composer still holding text (`PromptIsEmpty == false`) after the
   confirmation timeout. Codex can take an Enter arriving right after a burst of
   input as a newline inside the paste; a later Enter submits. Nothing else is
   repeated. No repaste anywhere.
7. **Sessions without a composer model** (PowerShell, custom commands; their
   profiles never report `PromptIsEmpty`) end with a weaker, explicit result,
   `Submitted`: the paste was shown and Enter written, and nothing more can be
   known. No repair. Hermes support is out of scope.
8. **Scenario after an unconfirmed write:** pause for attention, keep watching
   the response file, never resend. Resuming the pause by hand does not resend
   either.
9. **Step watchdog:** after 15 minutes without a response file, pause for
   attention (the stuck session is unlocked, as attention pauses do today) and
   keep waiting. A valid response file re-locks and continues. The recovery
   resend is removed.
10. **Ctrl+V** stays on the keyboard path and is out of scope; it is broken
    today and goes to the backlog as a separate item.

Unlocking every session of the run on the watchdog, instead of only the stuck
one, waits for step 4 (pause state machine in Core), where it does not need a
new flag.

## Design

### Fresh observation: on-demand screen capture

`ITerminalWebViewHost` gets:

```csharp
Task<TerminalScreenSnapshot?> CaptureScreenAsync(string sessionId, CancellationToken cancellationToken);
```

The host first sends any output it still buffers for the session, then calls a
new `agentTerminal.captureScreen(sessionId, requestId)`. The web view drains
`pendingOutput` into xterm (all chunks, waiting for each write callback),
captures with `captureScreenSnapshot`, and posts
`{ type: 'screenCapture', sessionId, requestId, ... }`. Messages to the web
view are handled in order, so the capture reflects all output the host received
before the request. `null` when the session has no terminal in the web view.

The capture does not go through `scheduleSnapshot`, its deduplication, or the
status engine. The sender classifies it itself with the session's stateless
profile (`AgentScreenProfileSelector.ForKind(runtime.Kind)`), reading
`PromptIsEmpty` and an `InputRequested` verdict. No counters are added to the
status engine; its `ActivityEpoch` and `IsBusy` keep their tab-indicator
meaning and are no longer used for delivery.

### Core: the sender identity and outcomes

```csharp
public enum PromptSender { Outsider, ScenarioOwner }
```

`Outsider` is refused by a question and by a scenario lock. `ScenarioOwner` is
refused only by a question. Whether a stopped session may be started is a
separate `bool startIfNotRunning` argument, true only for manual actions.

`PromptDeliveryOutcome` becomes:

| Outcome | Meaning |
|---|---|
| `Confirmed` | A fresh capture after Enter showed an empty composer. With `submit: false`, the paste was shown. |
| `Submitted` | The session has no composer model; the paste was shown and Enter written. |
| `Unconfirmed` | Paste shown, Enter written (and repeated once if the composer still held text), but an empty composer was never seen. Replaces `Written`. |
| `PasteNotShown` | The paste never appeared, so Enter was not sent. |
| `RefusedQuestion` | A question is pending; `WriteAttempted` says whether the paste had already been written when it appeared. |
| `RefusedLocked` | A scenario owns the session. |
| `NotRunning` | No live process, and starting was not allowed or did not reach readiness. `StatusLine` gives the reason. |

`BlockedByBusy` and `BlockedByPendingInput` are removed.

### Presentation: `SessionRuntimeCoordinator.DeliverPromptAsync`

One public method replaces `SendPromptAsync` and
`WriteScenarioPromptAndSubmitAsync`:

```csharp
Task<PromptDeliveryResult> DeliverPromptAsync(
    string sessionId,
    string text,
    bool submit,
    PromptSender sender,
    bool startIfNotRunning,
    CancellationToken cancellationToken);
```

The coordinator reads session facts itself instead of taking delegates per
call. It is constructed with an `ISessionFacts` reader:

```csharp
public interface ISessionFacts
{
    SessionScreenState? ReadScreen(string sessionId);
    bool IsScenarioLocked(string sessionId);
    Task StartAsync(string sessionId, CancellationToken cancellationToken);
}
```

The shell implements it once (screen from `TerminalTabStatusCoordinator`,
lock and start from the existing session view model code). This removes the
`readScreenState`, `startRuntimeAsync`, and `waitForInitialOutputAsync`
delegates from every call. `ReadScreen` serves admission and readiness, which
judge the current state; confirmation uses `CaptureScreenAsync`.

**Two levels of exclusion.** `SessionRuntime` keeps the input queue from step 2
(`RunInputExclusiveAsync`), which makes each write atomic. It gains a delivery
gate (`RunDeliveryExclusiveAsync`), held for a whole delivery. Programmatic
senders take the gate, then use input units inside it. Keyboard input takes
only input units, so typing is never frozen for the confirmation wait. Keyboard
input written during a delivery ends it without repair: the runtime counts
keyboard writes, and a delivery that sees the count change after its Enter
returns `Unconfirmed` and writes nothing more, because the composer may now
hold the user's text.

Stages, all inside the delivery gate:

1. **Running.** If the session has no active controller: when
   `startIfNotRunning`, call `ISessionFacts.StartAsync`, then
   `WaitForSessionReadyAsync` (first output, settle, classifier `Done`, one
   answer to the trust dialog). Not ready → `NotRunning` with the readiness
   reason. Otherwise → `NotRunning` immediately.
2. **Admission** from `ReadScreen`: question → `RefusedQuestion`;
   `Outsider` and locked → `RefusedLocked`.
3. **Paste and landing,** one input unit. Both paste paths confirm landing
   before Enter with `ClientPasteConfirmation` on output: the clipboard path as
   today, the bracketed path the same way. Not shown within 5 s →
   `PasteNotShown`, no Enter. With `submit: false` the delivery ends here as
   `Confirmed`.
4. **Enter,** in the same input unit, after the settle delay. Immediately
   before writing Enter, a fresh capture is checked for a question: the paste
   landing can take up to 5 s, and an agent that was already working may open
   a question meanwhile, which Enter would answer instead of submitting. A
   question → `RefusedQuestion` with `WriteAttempted: true`,
   `SubmitAttempted: false`. A capture that does not answer → `Unconfirmed`
   with `SubmitAttempted: false`; Enter is not written blind. Then
   `EncodeEnter()`.
5. **Confirmation.** Sessions whose profile has no composer model →
   `Submitted`. Their profiles never report a question either, so stage 4
   skips the pre-Enter capture for them. Otherwise capture every poll interval (250 ms)
   until the confirmation timeout (10 s):
   - `PromptIsEmpty == true` → `Confirmed`;
   - a question verdict → `RefusedQuestion` with `WriteAttempted: true`,
     `SubmitAttempted: true`;
   - keyboard input since Enter → `Unconfirmed`;
   - a capture that does not answer → `Unconfirmed`, no repair.
6. **Repair,** once, on timeout, in a new input unit (still under the delivery
   gate). Inside the unit, immediately before writing Enter, the sender checks
   again: keyboard input since the first Enter → `Unconfirmed`, nothing
   written; a fresh capture shows a question → `RefusedQuestion`
   (`WriteAttempted: true`, `SubmitAttempted: true`); a fresh capture showing
   an empty composer means the first Enter was taken late → `Confirmed`; one
   that cannot tell (`null`, or no answer) → `Unconfirmed`. Only
   `PromptIsEmpty == false` leads to the second Enter. Only then is the
   second Enter written and stage 5 repeated with a fresh timeout. If that
   wait also times out → `Unconfirmed`.

**Bounding each capture.** Every `CaptureScreenAsync` call is limited to the
remaining budget of the stage that issued it (the confirmation timeout, or a
fixed 2 s for the pre-Enter checks), through a linked cancellation token. The
host keeps pending capture requests by `requestId`; a cancelled or timed-out
request is removed, and a `screenCapture` message that arrives for an unknown
`requestId` is ignored. A web view that never answers therefore ends the
delivery within its budget and releases the delivery gate.

`WaitForSessionReadyAsync` stays public for the review launch and reads the
screen through `ISessionFacts` too.

"Has a composer model" is a property of the profile:
`IAgentScreenProfile.ReadsComposer`, true for Codex and Claude.

### Trace

Every delivery writes one line through a `deliveryTrace` sink
(`Action<string>`, wired to the existing application log, metadata only):

```text
delivery session=<id> sender=<Outsider|ScenarioOwner> start=<none|ok|not-ready>
  gate-wait=<ms> admission=<ok|question|locked> paste=<bracketed|clipboard>
  shown=<ms|timeout> enter=<ms> confirmed=<ms|timeout|no-model>
  repair=<none|enter> keyboard=<yes|no> outcome=<outcome>
```

These are the delivery traces the backlog asked for, collected during manual
testing of this change.

### Callers

| Caller | Call |
|---|---|
| Templates, selection actions, paste command (`SendTextToSessionAsync`) | `Outsider`, start allowed; outcomes other than `Confirmed`/`Submitted` go to the status bar |
| `pact_send_message` | `Outsider`, no start; outcome maps to the MCP result (`input-requested`, `session-scenario-locked`, `session-not-alive`, `not-confirmed`, `paste-not-shown`) |
| Workstation lock prompt | `Outsider`, no start; outcome goes to the orchestrator slot state text |
| Scenario step trigger and terminal notice (`IScenarioTerminalGateway`) | `ScenarioOwner`, no start |
| `PasteAsync` (Ctrl+V command) | unchanged keyboard-equivalent path, out of scope |

`OrchestratorDispatcher.SendMessageAsync` keeps argument and self-target
validation; its question and lock checks move into the sender, so they run
under the delivery gate. `IsSessionAlive` stays as the early
`session-not-alive` answer.

### New MCP tool: `pact_start_session`

Orchestrator catalog only. Arguments: `session_id`. Starts a session that is
not running, the same way activating its tab does, then waits for readiness.
Results: `ok`; `already-running`; `session-not-found`;
`not-ready` with the reason (question text or "never became ready").

### Scenario

`ScenarioIterationContext.SendAsync`:

- `Confirmed`, `Submitted` → resume after delivery, return.
- `RefusedQuestion` with `WriteAttempted: false` → attention pause; retry after
  the question clears (nothing was written, so retrying is safe).
- `RefusedQuestion` with `WriteAttempted: true`, `Unconfirmed`,
  `PasteNotShown` → attention pause, return without retry. The step keeps
  waiting for its response file, which resumes the run.
- `NotRunning` → attention pause, retry after the session is alive again.
- `RefusedLocked` cannot happen for `ScenarioOwner`.

`WaitForResponseAsync` loses `recoverAfterTimeoutAsync`. On
`ScenarioStepTimeoutException` it pauses for attention and continues waiting.
`ReviewLoopScenarioProgram` drops the recovery callback and the
`initialDeliveryReady` plumbing. The response file stays authoritative: when it
arrives, any delivery still in progress is cancelled and the run resumes.

The pause flags (`StuckSessionId`, `UnlockAllSessionsWhilePaused`) are not
restructured here; that is step 4.

### Contract documents

The rules change, so these are updated in the same commit as the code:

- `AGENTS.md`, scenario rules: a run writes into a busy session and into a
  non-empty composer; only a question refuses. Confirmation by a fresh empty
  composer, Enter-only repair, no repaste, no resend after an unconfirmed write
  or the watchdog.
- `docs/architecture.md`, scenario delivery paragraph and the runtime
  coordinator paragraph: one sender, delivery gate, on-demand capture.
- `docs/agent-onboarding.md`, scenario delivery section and the log entry
  description (new trace line).
- `docs/backlog/2026-10-09-development-directions.md`: Ctrl+V item;
  `docs/backlog/2026-10-14-architecture-analysis.md`: step 3 marked done.

## Testing

Automated:

- Coordinator, with a fake `ISessionFacts`, fake host capture, and fake
  controller:
  - question refuses both senders; lock refuses only `Outsider`;
  - busy and non-empty composer do not refuse;
  - bracketed paste waits for its echo, and `PasteNotShown` writes no Enter;
  - a capture with an empty composer confirms; `null` does not;
  - Enter repair is written once, only after a capture showed text;
  - a question that appears while the paste is landing stops Enter
    (`RefusedQuestion`, write attempted, submit not attempted);
  - a question or keyboard input that appears before repair stops the second
    Enter;
  - a capture that never answers ends the delivery within its budget without
    Enter or repair, and its late answer is ignored;
  - no path writes the paste twice;
  - a second programmatic delivery waits for the first one's result;
  - keyboard input during confirmation ends the delivery without repair;
  - a profile without a composer model returns `Submitted`;
  - stopped session with `startIfNotRunning` waits for readiness and answers
    the trust dialog once; without it returns `NotRunning` and writes nothing.
- Scenario: `Unconfirmed` pauses without a second send; the watchdog pauses
  without a resend; a response file after the watchdog resumes and re-locks.
- Orchestrator dispatcher: outcome to MCP error-code mapping;
  `pact_start_session` results.

Live checks (manual, recorded in the trace lines):

- Landing heuristic: send to a busy Codex and a busy Claude; send when an
  earlier `[Pasted …]` placeholder is already on screen and gets redrawn.
  `ClientPasteConfirmation` accepts `[Pasted` or the last 12 normalized
  characters from any output, so a redraw could confirm a paste that has not
  landed. This is a known limit to measure, not a blocker; the heuristic is
  changed only if the traces show false landings.
- Codex Enter repair: a long trigger into Codex with win32-input-mode.
- Fast answer from an idle agent confirms without a busy frame.
- PowerShell session gets `Submitted`.

## Out of scope

- Ctrl+V (backlog item).
- Pause state machine and unlocking all run sessions on the watchdog (step 4).
- Typed question kind instead of comparing the trust status line (A5).
- Hermes composer support.
- Splitting the shell controller (step 5).
