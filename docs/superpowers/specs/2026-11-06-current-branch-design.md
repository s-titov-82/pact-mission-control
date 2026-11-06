# Current branch against tracking upstream

Superseded by [review scopes](2026-11-06-review-scopes-design.md): all aggregates
include local contents, and tracking comparison is now Unpushed. Retained below
as the historical design for the preceding implementation.

User requested Current branch next to Working tree and Choose commit, plus two
right-panel entries with file/+/- totals and navigation to the matching mode.
Tracking upstream is explicitly selected by the user as comparison base.

Compare resolved @{upstream} directly with resolved HEAD, using committed Git
objects only. Working tree remains HEAD versus local/index/untracked changes.
The comparison is of endpoints (not merge-base); behind/diverged branches show
the complete tree difference. Push commonly makes the branch comparison empty.
No fetch, checkout or index mutation. Missing/gone upstream, unborn or detached
HEAD have explanatory unavailable states, not fake zero counts or a main fallback.

Reuse the Git runner/parser/blob limits and one Changes observation loop. Keep
live Working tree and branch totals independent of the displayed source.
Branch view follows HEAD/upstream updates; individual-commit view stays frozen.
An open remark editor temporarily retains its branch pair so a ref update cannot
drop typed text; summary cards still refresh, and closing the editor releases it.
Resolve both ids before reading paths/blobs, discard mixed branch/ref reads and
guard publication by owner, source, comparison and file generation. Changing
comparison invalidates pending file reads. Preserve first-file and view modes.

Historical branch remarks capture target HEAD, exact upstream base and old path.
Their links reopen that immutable pair, even after either ref moved, instead of
silently substituting the target's first parent. Marker/editor identity includes
both historical ids. Send, draft count, Open/Resolved and cleanup remain shared
by the entire project; only placement depends on the displayed comparison.
Historical Markdown retains the existing local-resource policy.

Right-panel Working tree replaces Changes and remains clickable at zero;
Current branch has matching counts/status and explicitly opens branch mode.
The common project remark count is shown once, outside the two source entries.
No additional saved selection state, store, settings or poller. Publish only
artifacts/local-diag and retain unrelated dirty work; do not commit/push.

Validate real repositories (tracking absent/equal/ahead/behind/diverged, dirty
files excluded, ref move, rename/binary/large limits), branch/source races,
immutable remark provenance, both UI entries and shared send/cleanup regressions.
