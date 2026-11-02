# Review delivery attention

## Intent and evidence

On 2026-10-29 the author notifications for reviews `b45de3ba` (13:08 Moscow)
and `a73af28c` (15:46 Moscow) returned `PasteNotShown`, with a write attempted
and no Enter. The relay correctly avoided repasting but left both agents waiting
without a visible explanation. The old logs cannot establish why Claude did not
show the clipboard paste.

## Agreed behavior

The user chose to preserve the current turn and show a noticeable warning so
the review can continue manually. A failed or unconfirmed notification must
appear on both participants' existing review badges and session rows, with the
reason and instructions to ask the agent whose turn it is to continue the review.
The same issue is exposed in the existing orchestrator review summary.
The project row also shows the warning, including when the project is collapsed;
its tooltip names the session whose turn needs continuation. Do not auto-expand it.

Fetching the current turn by its owning agent acknowledges the notification:
clear the warning, including when an earlier delivery finishes afterwards.
Polling by the waiting agent must not acknowledge the other agent's turn.
Successful delivery, advancing the turn, and ending the review clear the issue.
Never add a retry after a possible write. Preserve cancellation/draining and
the existing no-write retry behavior. Keep attention in memory only; do not
persist terminal text or add a timer, store, modal dialog, or recovery tool.

## Approaches

- **Chosen:** retain the turn with a visible warning and manual continuation.
  This preserves the exchange and avoids duplicate terminal input.
- End the review on a notification failure: rejected by the user because manual
  continuation should remain possible.
- Automatically repaste after a possible write: incompatible with delivery safety.

## Implementation choices

Reuse `ReviewRelay.Changed`, `ReviewSummary`, `SessionReviewBadge`, and the
existing UI dispatch. The badge becomes an exclamation mark in the danger color,
and the session row displays `Review needs attention`. The existing badge click
continues to open the log. The existing shell badge refresh also updates the
project's aggregate warning, without adding item-event subscriptions. Tooltips
name the side to continue and the MCP call the agent should make.
Do not change clipboard delivery based on conjecture.
