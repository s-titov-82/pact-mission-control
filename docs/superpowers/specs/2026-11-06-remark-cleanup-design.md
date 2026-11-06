# Finish a remark review and clear its list

User-approved interaction, 2026-11-06: after resolving the last open remark,
ask “Ревью завершено? Очистить список замечаний?”. No keeps the remarks;
an explicit cleanup button lets the user finish later.

## Approach

The agreed completion signal replaces speculative Git/merge detection and
time-based retention. Reuse the existing RemarkBook, source identity and atomic
store; no separate review entity, store, settings or timer. User correction on
2026-11-06 supersedes the initial builder choice to split by source: a review list
is the entire PROJECT book, across Working tree and all commits. Send and its
draft count include pending messages and typed replies of every open thread.
Only line placement and per-file markers depend on the selected source. Other
projects remain untouched; no source filtering is exposed on send/cleanup APIs.

The question appears inline under the remark buttons, with project-wide count,
when a previously observed list becomes entirely resolved and has no drafts,
uncertain/sending messages or typed replies. It may follow user or agent resolve.
Do not ask merely on loading already-resolved records or switching sources.
Background completion waits for the owning project's remark panel to be displayed.

No dismisses the pending question. Refresh, navigation and restart do not ask
again for unchanged closed records. New/reopened activity can lead to another
completion offer. “Очистить замечания…” explicitly shows the same question later;
it is enabled only for a nonempty entirely resolved project list without pending
messages/replies. It remains available after No and after restart.

Yes removes the captured list only if every thread and message still matches
the offered snapshot under the book's mutation gate. Any added/reopened/changed
thread invalidates the operation: remove nothing and explain why. Typed replies
also prevent clearing; relevant editors are disabled for the short save so new
typing cannot be lost during persistence. Failure keeps all records retryable.
Ids are never reused. Existing shared text snapshots are unchanged by this
increment; cleanup removes thread records from remarks.json.

## Validation

Pin last-resolve transition, user/agent resolution, startup/source switching,
No and explicit retry, a mixed Working tree / multiple-commit list, pending delivery/typed replies,
concurrent added/reopened/replied threads, atomic save failure and id continuity.
Exercise the inline controls headlessly and publish only artifacts/local-diag.
Preserve unrelated dirty work; no commit or push without user request.
