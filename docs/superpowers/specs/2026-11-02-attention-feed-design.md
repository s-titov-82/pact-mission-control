# Current attention feed

Status: approved by the user on 2026-11-02. Implement this feature first, with no
event history; include terminal errors and review-delivery failures as well as
unread output and input requests. Busy and manually paused tabs are excluded.
Independent builder check: nothing important left.

## Intent and agreed placement

Show the tabs that need the user's attention now, across running projects and
ROOT. The user's project Notes specify a block in the right panel, below
Settings and above Changes. Each clickable card shows its project/ROOT label,
tab name and attention indicator. A card disappears when its tab no longer
needs attention. Clicking opens that tab and reveals its row in the main tree,
expanding and scrolling the tree when needed.

Use **Needs attention** as the proposed heading; **Attention** is a shorter
alternative. `Waiting response` would not describe unread output well.

## Proposed behavior

- Terminals: include existing `InputRequested` and `Unread` indicators.
- Web pages: include existing unread monitoring state. Activity and loading
  alone do not require attention.
- Also include terminal `Failed` and `SessionReviewBadge.HasDeliveryIssue`.
  The latter is a failure warning, not merely participation in an active review.
- Include running-project tabs even when their project is collapsed, ROOT
  tabs, and the enabled orchestrator slot if its session qualifies. Labels
  distinguish project ownership, ROOT and Orchestrator.
- Exclude paused projects and manually paused ROOT tabs. Ordinary Busy and
  Paused indicators do not create cards. Loading a page does not create a card.
- One card per tab even if several reasons apply. Show all relevant reasons
  in its tooltip. Reuse existing indicator appearance where it fits.
- Proposed ordering: questions first, failures next, unread last; within a
  group keep project/tree order. This avoids cards jumping when unrelated
  output arrives. No timestamps or event-order store are required.
- Keep the block globally available when switching between terminals, web,
  Docs and project Changes. Hide it when empty. Bound its height and scroll
  its contents so the other right-panel sections remain reachable.
- A selected tab can remain listed while its existing indicator remains set.
  Clicking a card does not clear questions, failures or review warnings by
  itself. Existing selection/visibility acknowledgement rules remain owners
  of unread clearing.
- Use normal tab activation semantics. Verify that the captured target still
  belongs to its running owner before navigation; a stale card cannot resume
  a paused project or resurrect a removed tab. Tree revelation must not cause
  a second activation through selection events.

## Implementation approach

Recommended: a small Presentation projection over the existing tab view
models, plus a separate Avalonia feed view. It owns the current card collection
and observes membership/indicator/title changes, but owns no terminal state.
The shell routes a card's activation to the existing selection methods and
asks ProjectTreeView to reveal that same target. Existing UI dispatch
boundaries apply to every update.

An independent event journal or new status bus would duplicate existing state
and require acknowledgement rules the user has explicitly deferred. Do not
introduce either for this feature. Do not parse terminal output a second time.

Keep source subscriptions bounded to current owners. Reconcile on project,
ROOT and orchestrator membership changes, detach replaced/removed sources,
and dispose subscriptions with the owning view model. Match ownership by
owner and tab identity, not equal titles; renamed/replaced tabs must update
correctly. The view accepts the projection and emits navigation requests,
without depending on RightActionsPanel or a particular panel location.

No history, storage file, toast, `pact_report`, kanban, new timer or background
poller. The existing project-expansion persistence path may save an expansion
caused by explicit card navigation, as it does for ordinary tree expansion.

## Acceptance checks

Behavioral tests must prove: add/update/remove on attention transitions;
terminal and web inclusion/exclusion; one card for multiple reasons; stable
ordering; project/ROOT/orchestrator labels; rename and identity replacement;
pause/close/removal cleanup; late updates from detached owners ignored;
selected-tab acknowledgement still owned by existing state.

Rendered Avalonia tests must prove visible cards and empty hiding, availability
on different main surfaces, bounded scrolling, and a click that opens the
target once and reveals it inside a collapsed/offscreen tree. Exercise a stale
card whose tab or owner was removed before the click.

Use targeted deterministic tests, no source-text/private-shape/timing-sleep
tests. One heavy .NET command at a time; use the repository build/test resource
limits. Preserve the pending official Markdown package migration. Commit this
design only together with its implementation, never on its own.
