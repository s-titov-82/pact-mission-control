# Styled terminal snapshots and composer classification

Created: 2026-10-09. Revised: 2026-10-12 after the accompanying review.

Status: architectural direction approved; scope narrowed by the review's
2026-10-12 discussion outcome. Implementation follows a reviewed plan.

## Outcome and scope

Reliably distinguish Codex and Claude composer suggestions from unsent input by
preserving xterm cell styling. Improve structural recognition of the current
composer and provider menus, while preserving the existing work-state heuristics.
The immediate consumer is the existing delivery gate's `PromptIsEmpty` value.

This change implements styled screen snapshots and relevant AO structural
heuristics. Native hooks, hook transport/trust, native-event reducers, and submit
correlation are deferred. Delivery/scenario refactoring and response-file
transport changes are separate tasks. Verify existing delivery as a consumer of
the new composer evidence without redesigning it here.

The accompanying `2026-10-09-harness-events-and-styled-classification-review.md`
preserves hook and delivery suggestions for future work. Its statements about
hook support and trust are not prerequisites for this task.

The working tree contains unrelated terminal transport and WebView changes.
Implementation must extend the current files without reverting those changes.

## Architectural decision

Keep `TerminalTabStatusEngine` as the single status owner and
`TerminalTabStatusCoordinator` as its membership/routing/UI boundary.
Keep the existing `IAgentScreenProfile` selection by `AgentKind`; add styled
observations to that contract rather than introducing a hook-capability catalog
for deferred work. Shared composer helpers remain stateless and reusable by
additional harness profiles.

Core owns immutable snapshots, their plain-text projection and stateless
screen/composer interpretation. Presentation routes snapshots and projects the
existing verdict. Avalonia extracts agent-neutral data from the already-live
xterm grid. No second status reducer, VT emulator, endpoint, process, launch
injection, settings switch or package dependency is needed.

## Rendered screen contract

Replace the text-only browser boundary with an immutable screen snapshot:

- physical rows with their text and wrapped-row flags;
- compact Dim ranges with offsets measured consistently in UTF-16;
- Dim only, captured for the bottom 15 physical rows; higher rows have unknown styling;
- viewport dimensions and the existing stable/unstable capture fact.

Ranges must correspond to the text produced by xterm cells, including wide
characters, combining characters, surrogate pairs, empty cells, trailing spaces
and wrapped rows. Skip width-zero continuation cells without duplicating a wide
character; coalesce adjacent ranges with identical attributes.
Capture is anchored to `buffer.active.baseY`, independent of user scrollback.
Logical text remains a deterministic projection for existing regexes and message
extraction. A snapshot with no style information represents unknown styling,
not a proof that all characters have ordinary styling. Null Dim ranges mean
unknown styling; an empty array means the row was captured and has no dim text. Malformed
ranges cannot prove Empty or Draft: reject/discard invalid style metadata and
retain text-only classification with unknown styling.

Snapshot deduplication includes text, relevant styles and structure. Turning a
dim suggestion into ordinary input with identical characters must publish a new
observation. Preserve the existing stable debounce, early unstable Busy evidence,
and reset of the snapshot baseline at submission boundaries.

Browser capture remains agent-neutral. Locating prompt areas and interpreting
their styles belong to Core's harness profiles. Retained observations remain in
memory; logs contain metadata only.

## Composer interpretation

Classify the current composer as Empty, Draft or Unknown independently of work
state. Whitespace and dim suggestion text within an identified composer are
empty input. Ordinary visible input under a complete style capture is a draft.
Missing structure or styling needed to disambiguate visible text yields Unknown.
Keep text-only callers supported: non-empty text alone cannot become a Draft
verdict for Codex or Claude.

Identify current prompt rows and provider boundaries structurally. Exclude
footer chrome and old prompt rows from transcript history. Inspect continuation
rows so multiline drafts are included. Foreground color alone is not the dim
attribute. Provider labels may be excluded only by a narrowly documented rule.
A selected menu option is not an editable prompt; composer evidence is Unknown
while a question/permission frame occupies that region.

Composer evidence never starts or ends activity. An empty composer never resolves
a permission request or marks an unread completion by itself. Existing delivery
consumers retain their independent busy/question/lock gates and recovery policy.
Expose content-free structural evidence for diagnostics; do not add draft text
to diagnostics or logs.

## Heuristic changes

Adapt AO's structural recognition of the current composer, selected numbered
options with sibling options and navigation hints, and footer boundaries.
Ignore completed menus when a newer ordinary composer appears below them.
Support constrained viewports conservatively, using prompt styling where useful.

Recognize active interruption hints both beside the current status and, where
provider layout supports it, in the footer below the composer. AO includes this
footer layout for Claude; its occurrence in our running sessions has not been
established. Treat it as a supported layout variant, not a mandatory marker.

Preserve our recognition of localized/variable work labels, background work,
completion summaries, idle compaction, interruptions/API failures and structured
Codex questions. Broaden structural evidence without replacing these rules with
AO's narrower idle-only recovery detector or adding its periodic polling.
A generic separator alone must not overrule current Busy or permission evidence.
Preserve lifecycle, unstable-frame, unread and resume behavior in the status
engine; use concrete layout fixtures for work-state changes.

## Verification and documentation

- Exercise real xterm cell attributes in browser behavior tests: suggestion to
  identical draft text, style-only changes, wrapping, wide/combining characters,
  physical-row reconstruction, hidden sessions and scrolled viewports.
- Test decoder/projection roundtrips and missing/malformed style metadata;
  non-empty text without usable styling cannot yield false Empty/Draft evidence.
- Test profile observations with realistic current/old menus and footer layouts;
  include blank/dim/draft and multiline composers, partial/constrained frames,
  and our existing work, completion, error and structured-question cases.
- Test work and composer independently: Busy with an empty composer; a parent
  waiting for background agents remains Busy; composer changes cannot complete
  activity or bypass question/lock gates. Exercise existing delivery recovery
  decisions with reliable styled evidence without refactoring their policy.
- Run bounded targeted .NET suites and the relevant native WebView/ConPTY gate.
  Observe actual suggestions/drafts in separate visible test sessions without
  restarting the user's live agents. Separate synthetic and native evidence.
- Update architecture/onboarding and user guidance alongside implementation,
  describing styled snapshots and nullable composer evidence, not deferred hooks.

Run one heavy .NET command at a time: build with `-m:2 -nr:false -v q`
and tests with `-m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.
Do not commit this specification or the implementation plan by themselves.

## Evidence

Reference checkout: `D:\Personal\agent-orchestrator`, commit
`fdb8c5c461e82278ce44c83dcf8725a607365cb3`.

- Scope decision: accompanying review, "Итог обсуждения (2026-10-12)".
- AO styled composer: `backend/internal/adapters/agent/terminalui/composer.go`.
- AO screen layouts: Claude `terminal_surface.go`; Codex `terminal_activity.go`.
- Pact ownership: `TerminalTabStatusEngine`, `TerminalTabStatusCoordinator`.
- Pact text loss: `WebAssets/terminalHost.js`, `captureScreenText`.
- Existing consumer: `SessionRuntimeCoordinator` gates/recovery using `PromptIsEmpty`.
