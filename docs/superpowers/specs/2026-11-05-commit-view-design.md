# Viewing an individual commit in Changes

2026-11-06 user correction supersedes source-specific Send/draft scope in this
original design: Send includes pending messages and typed replies of all open
project threads, and review cleanup covers the entire project book. Only line
placement and per-file markers follow the selected source. See
2026-11-06-remark-cleanup-design.md for the current completion/cleanup contract.

2026-11-05. Next implementation item after the Ctrl+V correction; the latter
remains available for native-agent acceptance. This extends the existing Changes
screen, not the terminal, Git mutation commands or the review relay.

Technical review: Claude Opus approved iteration 2 through Pact, review
`e83977841aad423abda7ec27b8f18182`. After that review, the user challenged the
need for downgrade protection; the sidecar proposal was removed in favor of a
single remark file and an explicit unsupported downgrade boundary. This storage
simplification was not part of the reviewer's approval. The user authorized
implementation of this revised single-file design on 2026-11-05.

## Agreed scope

The backlog requests selecting a commit and viewing its changes. The user
explicitly confirmed that remarks remain available and belong to the selected
commit. Working-tree remarks and their existing behavior must be preserved.

## Proposed interaction

- Changes starts in **Working tree**, as today. Its header gains a source selector
  and a **Choose commit…** action. Selected commit mode displays short SHA,
  subject and comparison base, with author/date available in the picker/details.
- The picker lists the latest 50 commits from HEAD, newest first, with **Load
  more** for older entries. A SHA/ref input can select a commit outside that page.
  Every input resolves to a full immutable commit SHA before content is loaded.
- A commit is compared with its first parent; the header makes this explicit for
  merges. The initial commit compares with the empty tree. No ranges, branch
  graph, parent selector or Git write operations in this increment.
- Keep the existing file list, first-file selection, Diff / Whole file / Rendered
  controls, folding and size/binary/deletion placeholders. Historical text comes
  from Git objects, even when the working file differs or no longer exists.
- In historical Markdown, local images (relative or absolute/file URLs) become
  explicit unavailable-resource placeholders, retaining alt text. Do not read
  them from the working tree or materialize revision assets. Local file links
  are disabled with an explanation; same-document headings and HTTP(S) links keep
  their existing behavior. Reuse code-aware preprocessing so image syntax inside
  code is unchanged. The deferred general broken-image feature stays deferred.
- Returning to Working tree restores live updates. A retained project view can
  remember its selected source for this app run; no new restart/settings state.
- Right-column/dashboard file and line counts still describe uncommitted changes;
  their Changes actions explicitly open Working tree. The separate right-column
  **remarks: N** count remains the total of open project threads across sources.
  Clicking the project screen's Changes tab may retain its last source.

These UI details are implementation proposals, not earlier user decisions.

## Remarks and persistence

- A historical remark captures target commit SHA, parent/base SHA, file identity,
  old/new side, line and the displayed text/context. Renames preserve the identity
  of the old path as well as the destination when resolving old-side content.
- Existing remark records without a target commit retain Working tree meaning;
  do not infer their scope from HEAD or migrate them into a historical commit.
- Open/Resolved lists remain project-wide, with Working tree or commit SHA badges
  and an action to open the thread's source/file. Historical threads and agent
  replies therefore remain discoverable from Working tree after restart. Editing
  an off-source thread first opens its source; failed navigation retains drafts.
  Row markers, per-file counters and Send/draft count apply only to the selected
  source. Switching retains unsent messages/reply drafts without sending another
  source's drafts.
- Remark ids stay unique within the existing project book. Agent listing/replies
  remain project-owned; historical entries and outgoing prompts include the full
  target SHA, comparison base and side so the agent knows what is being reviewed.
  Working-tree prompt wording remains accurate for working-tree entries.
- An editor opened on a captured diff cannot save onto another commit merely
  because path and parent happen to match. Source SHA participates in captured
  diff identity and stale-gesture checks. Historical placements do not drift
  with later working-tree edits or a moving branch.
- Historical placement uses the captured line in its immutable revision, with
  `outdated: false`, for **all** RemarkPlacer consumers: Send/prompts,
  `pact_list_remarks`, and Elsewhere/off-screen threads. Neither disk contents nor
  current HEAD participate. An unavailable Git object is a source-load/navigation
  error, not grounds to reinterpret the thread as a working-tree remark.
- Keep all threads in the existing `remarks.json`, with optional target SHA on
  historical anchors and the existing project-wide id counter/save lifecycle.
  New builds read old records without a target SHA as Working tree. Opening and
  saving this new data in pre-feature builds is unsupported: those readers ignore
  format versions and can drop historical metadata. Do not add storage or counter
  coordination to support downgrade or alternating old/new builds. Concurrent
  writers from separate Pact processes remain outside this feature's contract.

## Implementation boundaries

Reuse the existing Git runner, diff parser, text limits, diff/rendered controls,
remark book and MCP reply mechanism. Do not introduce another Git process engine,
remark-store service, sidecar, transcript, history database or polling loop.
Reuse the existing book mutation/save lifecycle and keep failed saves retryable.

The current Changes VM serves both the screen and the right-column working-tree
summary. Preserve those live facts separately from the selected historical
display; polling may update the summary without replacing committed file rows.
Immutable commit content need not be periodically reread. A revision-reader
contract can live beside the current working-tree reader without changing its
uncommitted-change contract or adding throwing defaults to existing fakes.

Every asynchronous publication is guarded by project/root, source SHA and file
selection generation. Switching project/source/file or disposing invalidates old
responses; a load failure must not leave mismatched headers, rows and remarks.
Resolve refs using argument lists with option boundaries. Parse paths losslessly
including spaces and renames; bound history pages and Git-blob reads before
materializing large files. No checkout, index mutation, clean or reset.

## Validation

- Temporary Git repositories: ordinary, root and merge commits; additions,
  deletions, renames, Unicode/space paths; historical text differs from disk.
- Source switch during list/diff/blob/remark reads; working-tree refresh during
  committed display; return to Working tree; project/root replacement and dispose.
- Historical remarks on both sides survive restart/reply/resolve, remain separate
  from working-tree and other-commit remarks, and appear with explicit SHA in MCP
  and outgoing prompts. Assert exact line and outdated=false in Send, MCP and
  Elsewhere while HEAD differs from target and working files changed/deleted.
  Include sibling commits with the same parent/path and off-source drafts.
- From Working tree after restart, discover and open a historical thread and its
  agent reply; project-wide remark totals and source-specific Send stay distinct.
- Read existing working-tree remarks unchanged; round-trip mixed historical and
  working-tree threads through the new reader/writer with unique ids and exact
  SHA/base/side/messages. Downgrade round trips are not an acceptance requirement.
- Historical Markdown local images/file links never read or open working-tree
  resources; placeholders retain alt text, code syntax is untouched and heading
  navigation cannot jump after a source change.
- Actual Avalonia picker/source/file/remark interaction and rendered/full-file
  modes; right-column counts/actions still use working-tree facts.
- Resource-bounded builds/tests, relevant native integration gate and publish to
  the existing `artifacts/local-diag`. Preserve pending unrelated changes and do
  not commit this design without its implementation.
