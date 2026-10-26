# Project screen, change review and agent files

Created: 2026-10-26.

Status: direction agreed in conversation on 2026-10-26. Covers section 2 of
the [plan](../../backlog/2026-10-25-next-steps.md) and lays the ground for
section 3 (project screen and dashboards).

## Why

Three moments where the user leaves Pact today only to look at files:

1. **Before a commit.** An agent finished a task; the user wants to read what
   it changed and tell it what to fix.
2. **Edited descriptions.** An agent rewrote some Markdown; the user wants the
   list of changed documents, not the whole Docs tree.
3. **A file the agent points at.** An agent wrote a script into its
   scratchpad or Temp and names it; the user wants to read it without
   Explorer.

In all three the user reads; edits still go through the agent. The value is
in staying in Pact and in sending remarks back to the agent in one prompt,
with answers coming back per remark, the way a GitLab review works.

## Goals

- A **project screen** in the center pane with tabs: Overview, Changes,
  Review logs.
- A **Changes** tab: the uncommitted changes of the project's repository as a
  file list and a hunk diff, refreshing by itself while agents work.
- **Remark threads** on diff lines: each remark gets an id, all open remarks
  go to an agent in one prompt, the agent answers and resolves them over MCP,
  the user reopens them if needed.
- A **rendered mode for Markdown** diffs next to the plain diff.
- **Agent files**: an agent adds a link to any file (inside or outside the
  project) to a list in the right column; a click shows the file read-only in
  a temporary panel over the terminal.
- A **reworked right column** with a "Changes · N" entry that leads to the
  Changes tab.

## Non-goals

- Editing, staging, discarding or committing from the Changes tab. Commit
  stays in the git popup.
- Viewing a specific commit or a range of commits (later, see Stages).
- Filling the Overview tab with a dashboard (section 3 of the plan).
- Moving Docs into the project screen. The Docs pane stays as it is; only the
  review logs leave it.
- Syntax highlighting of code in the diff (nice later, not needed now).

## Anti-goals

- A remark that silently disappears or silently points at a different line
  after the file changed. Drift must be visible ("outdated"), never hidden.
- The Changes view running git so often that it slows the agents' own git
  calls or the machine. Refresh is throttled.
- The agent-file panel resizing the terminal (a PTY resize reflows the
  agent's screen). It lies over the terminal instead.

## Stages

Built and shipped in this order; each stage is usable on its own.

1. Project screen, Changes tab without remarks, review logs moved, right
   column reworked.
2. Agent files.
3. Rendered Markdown mode.
4. Remark threads.
5. Later, out of this spec's plans: viewing a commit, the Overview dashboard.

## 1. Project screen and the Changes tab

### Opening it

- A user click on a project row in the tree opens that project's screen in
  the center pane. Today such a click only selects the project for the git
  popup; it now also presents the screen. The git popup button keeps
  selecting the project without opening the screen.
- The project screen is a new presented surface (next to Terminal, Notes,
  Web in `PresentedSurface`), carrying the project and the active tab.
- A click on the project row always opens the Overview tab. The other tabs
  are reached directly: the right-column Changes entry opens Changes, the
  review badge and review-log openings open Review logs. Inside the screen
  the user switches tabs freely.
- Closing a tab, pausing a project etc. keep today's fallback behaviour;
  making the project screen the fallback belongs to section 3.

### Tabs

- **Overview.** A placeholder for now: project name, root path, current
  branch. *(my call: enough to make the tab not look broken.)*
- **Changes** (only for git repositories). Header shows the file count,
  e.g. "Changes 5".
- **Review logs.** Moved here from the Docs pane: the files directly in
  `.pact/reviews`, newest first, shown read-only with the same Markdown
  preview Docs uses. The Reviews tab is removed from Docs; Docs is back to
  Notes, Common MD's and Docs.
  - The review badge click and the end-of-review "open the log" now open the
    project screen on this tab with that log selected.
  - `pact_open_document` on a review log also lands here. *(my call)*

### The Changes tab

- **What it shows:** every path `git status` reports — modified, added,
  deleted, renamed, untracked, conflicted — compared with HEAD. Staged and
  unstaged changes are one list against HEAD; a file whose index differs
  from HEAD carries a small "staged" mark. Untracked files are shown as
  wholly added. In a repository without commits, compare with the empty
  tree.
- **Layout:** file list on the left (status letter, file name, folder in
  muted text; remark count once stage 4 exists), diff of the selected file
  on the right. Unified diff, grouped by hunk, old and new line numbers in
  the gutter, added and removed lines tinted.
- **Binary files** show a one-line placeholder instead of a diff. Very large
  diffs are cut with a "show all" link. *(builder picks the threshold)*
- **Freshness:** the list and the open diff refresh by themselves while the
  screen or the right-column entry is visible. The baseline is
  reconciled, not only event-driven: while visible, a throttled periodic
  `git status` runs (it alone catches a `git reset --soft`, a branch-ref
  move, or a linked worktree whose `HEAD` and index live outside the project
  root). File changes under the project root, window activation and opening
  the screen only bring that run forward. At most one git status run per
  project every couple of seconds; nothing runs while neither the screen nor
  the entry is visible. *(builder picks the numbers.)* Verify with a soft
  reset on a clean tree and with a linked worktree.
  When the open file changes, its diff is redrawn in place, keeping the
  scroll position near where it was. When it leaves the list (committed or
  reverted), the selection moves to its neighbour.
- Git is run through the existing `GitCliRunner` with `--no-optional-locks`,
  like the git popup, so Pact never holds the index lock an agent needs.

### Right column

Order, top to bottom:

1. The header row keeps the Settings and "Restart and update" buttons; the
   "Actions" title is removed. *(my call: the buttons have to live
   somewhere.)*
2. Quick actions.
3. A separator.
4. The **Changes entry**: "Changes · 5" with "+84 −12" under it (and
   "remarks: N" once stage 4 exists). A click opens the project screen on
   the Changes tab. With a clean tree it reads "No uncommitted changes". For
   a project that is not a git repository the entry is hidden. It belongs to
   the project of the currently shown session, web page or screen.
5. The **Agent files** list (stage 2), right under the Changes entry.
6. Free space.
7. The terminal diagnostics block.
8. A separator.
9. Usage limits.

The Documents tree that appears in the right column while Docs is shown stays
as it is.

## 2. Agent files

- New MCP tool, working name `pact_add_file_link(path, title?)`. `path` is
  absolute, or relative to the session's working directory. Any location is
  allowed: the panel is read-only and only shows the file to the user.
  The file must exist when the link is added; the refusal says so.
- The link belongs to the calling session. Links are stored with the session
  record, so they come back when Pact restarts and the same session is
  resumed; they are dropped when the session is closed. Adding a path that is
  already in the list moves it to the top instead of duplicating it. The user
  can remove a link with a small ✕. *(my call; also a cap of about 20 links
  per session, oldest dropped.)*
- The right column shows the links of the currently shown session: file name
  and a short location ("scratchpad", "Temp", "project", or the parent
  folder).
- **Opening:** a click shows the file in a panel lying over the right part of
  the terminal, about 60% of the center width, full height. Read-only
  monospaced text (Markdown rendered with the Docs preview), header with file
  name and full path, ✕ to close; Esc closes it too. The terminal keeps
  running underneath and is not resized.
  - The terminal is a WebView (a native window), so an ordinary Avalonia
    overlay cannot draw over it. The panel needs its own top-level surface
    (a `Popup` like the selection-actions popover) or an equivalent. *(the
    builder picks the mechanism)*
  - The file is read when the panel opens. A missing file shows "File not
    found" in the panel and the link stays. Large and binary files get the
    same treatment as in the diff.
- The tool does not open the panel by itself; the agent only adds the link.

## 3. Rendered Markdown mode

- For a `.md` file the diff header has a switch: **Diff** (default) and
  **Rendered**.
- Rendered shows the new version through the Docs Markdown preview, with
  changed blocks marked: added and modified blocks with a coloured bar at the
  side, removed blocks as a thin collapsed marker that expands to the old
  text. Block granularity (paragraph, list item, heading, code block) is the
  builder's call.
- Remarks (stage 4) are made in the Diff mode only.

## 4. Remark threads

### Writing remarks

- Clicking a diff line (or a gutter "+") opens an input under it. A remark is
  anchored to: file path, side (new or old line), line number, the line's
  text, a few lines of context, and the comparison it was written against
  (the HEAD commit id, or "empty tree").
- Each remark gets a project-wide id `C1`, `C2`, … that never repeats.
- Remarks are stored in the project (`.pact/remarks.json` or similar under
  the already git-ignored `.pact/`) and survive restarts. *(file name is the
  builder's call)*

### Thread states

- **Draft** — written, not sent yet.
- **Sent** — delivered to an agent, waiting for an answer.
- **Delivery uncertain** — text may have reached the terminal but the
  delivery cannot say it was submitted (see Sending). Shown with a warning.
- **Answered** — the agent replied without resolving.
- **Resolved** — the agent or the user resolved it. Collapsed by default.
- Orthogonal **outdated** mark. Relocation is conservative: a remark moves
  only when its identity is unambiguous, otherwise it is outdated.
  - New-side remark: the remark keeps a snapshot of the file as it was when
    written (the captured revision). Its position is carried by a line diff
    from the captured revision to the current file, never by searching for
    its text. It survives only if its line is an unchanged line in that
    diff; if the line is deleted or changed, it is outdated.
  - Repeated blocks: a line diff cannot tell "the first of two identical
    blocks was deleted" from "the second was". So if the anchor's line plus
    context occurs more than once in the captured revision, any deletion or
    change to any of those identical regions makes the anchor outdated, even
    when a surviving copy now sits at the same line number.
  - Old-side remark (a removed or changed line of HEAD): valid only while
    HEAD is still the commit it was written against. Once HEAD moves it is
    outdated.
  - An outdated thread stays in the list with its captured file, side, line
    and text, shown apart from the diff. Verify the old-side-after-commit
    case, and the case with two adjacent identical blocks: a remark is on
    the first, the first is deleted, and the remark must be outdated even
    though the surviving copy now holds its line number and is the only
    text-and-context match.
- The user can reply in a thread (the reply is a new draft in it), resolve,
  or reopen.

### Sending

- A bar at the bottom of the Changes tab: "N remarks" and **Send**.
  - One session in the project: Send delivers straight to it.
  - Several: Send opens a list of the project's sessions to pick from.
- What goes out: every draft remark and every draft reply, as one prompt
  through the shared delivery (the same path other Pact prompts use). One
  block per thread with a head line and the captured line:
  - new side: `C3 · src/X.cs:121 · <remark text>`;
  - old side: `C3 · src/X.cs, removed line 121 of HEAD <short id> · <remark text>`;
  - an outdated thread is labelled `(outdated)` in its head line;
  - under the head line, the captured line text quoted, so the agent never
    has to guess from the current file;
  - the thread's earlier messages above the new one when it is a reply;
  - a short header telling the agent to answer each id with
    `pact_reply_remark`.
  *(exact wording is the builder's call; ids, side, line and captured text
  are fixed)*
- The batch is a snapshot: only the messages it contained change state when
  delivery ends. Replies drafted during delivery stay drafts. The outcome
  maps from `PromptDeliveryResult`:
  - `IsSent` (Confirmed, Submitted, Unconfirmed) → the messages are sent;
    for Unconfirmed the bar says delivery was not confirmed, but nothing is
    resent automatically.
  - Nothing written (`WriteAttempted` false: NotRunning, a refusal before
    the paste) → the messages stay drafts and the bar shows the reason.
  - Written but not submitted (`WriteAttempted` true, `IsSent` false) →
    **Delivery uncertain**. Plain Send skips these; the thread offers
    "send again" and "mark as sent".
- Delivery state lives on the messages; the thread state is derived from
  them. A late delivery result therefore never turns an Answered or Resolved
  thread (an agent reply that came over MCP before the delivery returned)
  back into Sent. Verify these orderings with a gated fake delivery and a
  fake MCP reply, not with delays.
- The session the batch went to is remembered per thread.

### Agent side (MCP)

- `pact_reply_remark(id, text, resolve?)` — appends the agent's answer to the
  thread; `resolve: true` marks it Resolved. Unknown id → refusal listing the
  open ids. Any session of the same project may answer. *(my call)*
- `pact_list_remarks(state?)` — the project's threads with ids, file, side,
  line, captured line text, the comparison commit, the outdated mark, state
  and messages, so an agent that lost the prompt can recover.
  *(my call)*
- An answer arriving while the Changes tab is open shows up in place; the
  right-column entry's remark count counts threads not resolved.

## What is left to the builder

- Refresh throttling numbers, the large-diff threshold, the agent-file cap.
- How the agent-file panel is layered over the WebView.
- Markdown block diffing granularity.
- Storage file names and formats, as long as remarks live in the project's
  `.pact/` and agent links live with the session record.
- Exact prompt wording for remark delivery and tool descriptions.
