# Review scopes: Uncommitted, Unmerged, Unpushed

Supersedes the tracking-upstream-only Current branch design. The user approved
the three scenarios and the proposal in conversation on 2026-11-06.

## Intent and approaches

Review either the current local edits, the whole feature, or the work since
the remote tracking checkpoint. Every aggregate ends at current file contents,
including tracked staged/unstaged edits and nonignored untracked files.
Show net differences, not the concatenation of commit patches.

Considered keeping committed-only Current branch, changing it to merge-base
only, or providing three scopes. The user selected three scopes because the
first two approaches cannot cover both pre-merge and pre-push review.

| Scope | Base | Result |
| --- | --- | --- |
| Uncommitted | HEAD, empty tree for unborn HEAD | Current local contents |
| Unmerged | merge-base(HEAD, target branch) | Current local contents |
| Unpushed | Configured tracking ref, or Unmerged base when none is configured | Current local contents |

An explicitly configured but missing tracking ref is an unavailable state,
not permission to substitute another base. No common ancestor, ambiguous
multiple merge bases, unavailable target and detached/unborn branch scopes
are explained rather than shown as clean. Uncommitted remains usable.

## Target and remote state

Show Target with a branch chooser for Unmerged and for the Unpushed fallback.
Implementation choice: default to the remote default branch, preferring origin
when there are multiple remotes; otherwise a unique remote default. With no
remote default, try local main then master, excluding HEAD's current branch;
if no candidate resolves, require an explicit selection. Show the resolved ref
and base SHA. Never derive the merge target from tracking configuration;
the independently selected target may coincide with the tracking ref (main).

Manual selection is a branch ref, not arbitrary shell text. Retain it for the
current project's lifetime and per current branch; automatic selection remains
automatic after branch changes. Durable preference is outside this change.

No implicit network update. Remote refs reflect the last successful fetch/pull,
including updates performed by other tools. The user selected an explicit
Pull (rebase) action instead of a Fetch-only button. Route it through the existing
Git operation/log path: fetch and rebase the current branch onto its configured
tracking branch, then refresh on success or failure (fetch may have succeeded
before rebase failed). This is a deliberately distinct command labelled
`Pull (rebase)`, not the configurable Git panel `Pull`: it uses fixed
`pull --rebase --no-autostash` arguments, never the configured PullId arguments.
Explain that fixed strategy in its tooltip. Do not alter the existing Pull
command, default or user overrides. Respect its visibility preference through
the same IsPullVisible projection, so disabling Pull hides both entry points.
Require an attached branch with configured tracking and
a clean index/worktree; never auto-stash, discard local edits or resolve conflicts
automatically. Otherwise show the reason and let the user finish the local work
first. Expose conflicts through existing Git status, resolve/abort controls and
operation log; continuing a stopped rebase remains an explicit terminal action.
Failure
is visible; failed refresh retains previous data explicitly marked stale.
This explicit action may update refs/index/worktree; review reads do not mutate
them. No automatic pull, push or checkout, and no separate Fetch-only action.

## Presentation

Replace Working tree/Current branch source actions with Uncommitted, Unmerged,
Unpushed. Right panel has three matching entries with independent net file/+/-
counts, explicit unavailable/loading states, and navigation to the chosen scope.
Changing the displayed commit never changes these aggregate counters.

Unmerged/Unpushed have an inner selector: All changes, Uncommitted, and their
commits (newest first, bounded/paged). The commit set is HEAD history excluding
commits reachable from the scope base; for diverged tracking refs it contains
only local-side commits. A selected commit shows its first-parent diff, or empty
tree for a root commit; it is not an ownership filter on the aggregate patch.
Uncommitted in the inner selector shows HEAD versus local contents while
retaining the surrounding scope and commit list. Returning to All changes
restores its aggregate. Keep Choose commit for arbitrary SHA/ref/history.

Captured local remarks open a separate Fixed base source, outside scope/filter
selection: label it `Fixed base <sha> vs local` (or empty tree). Its base stays
fixed while local contents refresh through the existing observation loop. The
three summary cards still show their live scopes; their counters never become
fixed-base counters. Displayed file totals describe the fixed-base view. Entering
any scope or filter leaves this source; it cannot be silently rebased by refresh.

Merge-base is recalculated when refs move: it is a current common ancestor,
not a persisted historical creation point. The user chose to keep Unpushed as
the exact difference against the known tracking checkpoint, not merge-base with
that checkpoint. Describe it as `Exact difference from <tracking ref> to local`.
If HEAD is behind or diverged, show ahead/behind and a visible warning that
remote-only changes appear as local reversions in the aggregate and counters.
The commit selector still lists only local-side commits: it is a list of
individual patches, not a decomposition of this aggregate. A divergence test
must assert both the remote-only reversions and the local-only commit list.
The mode is not proof that a push is safe or has not occurred elsewhere. Explain
missing-tracking fallback directly in the source description.

## Content, remarks and races

Reuse existing working-text limits, diff parser and rename handling. Compute
paths and totals against the selected base; never sum Working tree and commit
counts (edits can cancel). Untracked files absent from the base are additions;
an untracked path that exists in the base must be compared to that base blob.
Text line totals include nonignored untracked additions/replacements. A staged
deletion plus an untracked file at the same path (git rm --cached) collapses to
one base-versus-disk entry, or disappears if identical. Ignored files stay
excluded. Stage contents alone do not override current disk
contents. Binary/oversized files remain listed with existing placeholders.

Aggregate remarks have a live local new side and immutable comparison-base SHA;
they must not pretend HEAD is an immutable target for unsaved work. New-side
placement uses existing captured text snapshots. Old-side anchors reference
their immutable captured base, independent of current HEAD or the currently
selected scope. In Send, MCP listing and off-diff placement, validate that the
captured base and its original rename path/line remain readable; do not compare
that SHA to current HEAD. Missing objects/path/text leave an explicit outdated
or unavailable result. In an on-screen diff, require matching base identity as
well. Describe removed lines as `base <sha>`, never `HEAD <sha>` merely because
the existing field used to hold HEAD. This rule applies to existing persisted
local anchors too; no new anchor version is required. Reopening an aggregate remark uses
its captured base versus current local contents, even if the live scope base
moved; explain this fixed-base view. Historical commit remarks retain both SHAs.
Check base identity for all shown diff modes, not only historical targets.
Send, drafts, Open/Resolved, and cleanup remain project-wide.

Reuse the current observation timer and source/file intent generations. Reject
results from old owners, branches, target choices, scopes, filters and refs.
An editor retains its displayed base/text until save/close or explicit source
departure; live aggregate counters may continue refreshing. Do not create new
pollers or persist terminal/screens/review history. Preserve unrelated changes.
Keep actual HEAD distinct from the comparison base in DTOs and view models.
Replace the superseded Current branch reader/state/events with review scopes;
retain the general immutable comparison reader. Generalize the existing editor
hold rather than adding another refresh or hold mechanism.

## Delivery

Update public docs and superseded specs. Run targeted real-Git, view-model,
remark and actual Avalonia control tests, plus a bounded build and existing
native integration gate. Inspect wide/narrow layout. Publish framework-dependent
win-x64 only into artifacts/local-diag. No commit/push without user instruction.
