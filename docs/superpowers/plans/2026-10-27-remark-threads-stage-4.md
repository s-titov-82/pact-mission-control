# Remark threads on diff lines (stage 4) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The user leaves remarks on lines of the Changes diff, sends every draft to an agent in one prompt, and the agent answers and resolves them over MCP; threads keep their line while the file changes and turn visibly outdated when they cannot.

**Architecture:** Core gets the remark model with a derived thread state, a line diff and the locator that carries an anchor to the current file, and the prompt composer. Infrastructure stores threads and file snapshots under `.pact/remarks`. Presentation gets `RemarkBook` (one per project, thread-safe, persisted, raises `Changed`), `RemarkPlacer`, the MCP tools and `ChangesRemarksViewModel` for the Changes tab. The App adds a remark margin to the diff editor, a thread overlay under the clicked line, a send bar, counts in the file list and the right column, and the wiring.

**Tech Stack:** .NET 10, Avalonia 12 + AvaloniaEdit 12 (headless tests), NUnit + Shouldly.

**Spec:** `docs/superpowers/specs/2026-10-26-project-screen-changes-design.md`, section "4. Remark threads" only. Stage 1 is built (`docs/superpowers/plans/2026-10-26-project-screen-stage-1.md`); stages 2 and 3 are not, and nothing here depends on them.

**Decided with the user on 2026-10-27 (overrides the spec's "an input under the line"):** the diff is an AvaloniaEdit editor now, which cannot open space between lines. A thread opens in an overlay that lies over the diff right under its line and follows scrolling. A gutter column next to the line numbers shows a "+" on the hovered line and a marker on lines with threads; a click there opens the overlay. A click in the text keeps selecting text.

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`. One heavy build at a time. Read the output: a failed build leaves old binaries, and the tests then lie.
- Tests: `dotnet test tests/<Project> --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 5m --filter "<filter>" -- NUnit.NumberOfTestWorkers=2`.
- Analyzers fail the build: CS1591 (public members need XML docs), IDE0005, IDE0011 (braces), IDE0055, IDE0061, CA1826, CS0067 (unused event). Tabs, file-scoped namespaces, `field` properties as in the surrounding code.
- Ids are `C1`, `C2`, … per project, never reused, also after a thread is discarded.
- Storage: `.pact/remarks/remarks.json` and `.pact/remarks/snapshots/<sha256 hex>.txt`; `.pact/.gitignore` with `*` is written when missing. Writes go through `AtomicFileWriter`.
- Prompt head lines (fixed by the spec): new side `C3 · src/X.cs:121 · <text>`; old side `C3 · src/X.cs, removed line 121 of HEAD <7-char id> · <text>`; an outdated thread gets ` (outdated)` right after its id; the captured line follows quoted as `> <text>`.
- Delivery goes through `SessionRuntimeCoordinator.DeliverPromptAsync(sessionId, text, submit: true, PromptSender.Outsider, startIfNotRunning: true, ct)`. Outcome mapping: `IsSent` → Sent; `!WriteAttempted` → back to Draft; `WriteAttempted && !IsSent` → Uncertain.
- UI copy (English like the rest of the UI): bar `N remarks to send` (`1 remark to send`), button `Send`; `Other remarks (K)`; states `Draft`, `Sent`, `Delivery uncertain`, `Answered`, `Resolved`, mark `Outdated`; thread buttons `Add`, `Save`, `Discard`, `Resolve`, `Reopen`, `Send again`, `Mark as sent`; right column `remarks: N`; no session: `No session in this project to send to`.
- Threads, files and MCP calls may come from any thread; UI-facing state changes go through the UI poster, like `ProjectChangesViewModel`.
- Logs never carry remark text or file contents.
- Commits on `main` are allowed; never push. Trailer: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. The diff on screen is older than the file on disk when the user adds a remark (an agent edited the file meanwhile): the remark must not capture a line the user did not see. Expected: refused with `The file changed; wait for the diff to refresh.` Pinned in Task 7 (`A_remark_on_a_line_the_file_no_longer_has_is_refused`).
2. `remarks.json` that cannot be parsed: remarks must not vanish silently. Expected: the file is set aside as `remarks.json.broken-<yyyyMMddHHmmss>`, the book starts empty, and the status line says so. Pinned in Task 3 and Task 7.
3. An agent reply arrives while the user types a reply in the open overlay: the typed text must stay. Pinned in Task 8 (`An_agent_reply_keeps_the_text_being_typed`).
4. A file with CRLF line endings: the snapshot and the current file are compared after removing `\r`, so line endings never outdate a remark. Pinned in Task 2.
5. A huge file or edit: the line diff gives up past its edit budget and the remark turns outdated instead of freezing the UI. Pinned in Task 2.

---

### Task 1: Remark model and thread state

**Files:**
- Create: `src/Pact.Core/Remarks/RemarkThread.cs`
- Test: `tests/Pact.Core.Tests/Remarks/RemarkThreadTests.cs`

**Interfaces:**
- Produces (namespace `Pact.Core.Remarks`):
  - `enum RemarkSide { New, Old }` — Old is a removed line of the compared commit; context and added lines are New.
  - `record RemarkAnchor(string Path, RemarkSide Side, int Line, string LineText, IReadOnlyList<string> Before, IReadOnlyList<string> After, string? BaseCommit, string? SnapshotId)` — `Line` 1-based in the captured revision (New) or in `BaseCommit` (Old); `BaseCommit` null means the empty tree; `SnapshotId` only for New.
  - `enum RemarkAuthor { User, Agent }`, `enum RemarkDelivery { Draft, Sending, Sent, Uncertain }` (agent messages are `Sent`).
  - `record RemarkMessage(string Id, RemarkAuthor Author, string Text, DateTimeOffset At, RemarkDelivery Delivery, string? SessionId)`.
  - `enum RemarkThreadState { Draft, Sent, DeliveryUncertain, Answered, Resolved }`.
  - `record RemarkThread(string Id, RemarkAnchor Anchor, IReadOnlyList<RemarkMessage> Messages, bool IsResolved)` with `RemarkThreadState State`, `RemarkMessage? Draft` (the single user Draft message, if any) and `string? LastSessionId` (session of the newest sent user message).

State rule, in this order: `IsResolved` → Resolved; an Uncertain user message with no agent message after it → DeliveryUncertain; a Draft message → Draft; the newest message by the agent → Answered; otherwise Sent.

- [ ] **Step 1: Write the failing tests** — one test per rule, built from small message lists:
  - `A_new_remark_is_a_draft` (one user Draft → Draft).
  - `A_sending_or_sent_remark_is_sent` (Sending → Sent; Sent → Sent).
  - `An_agent_message_after_the_users_answers_it` (user Sending + agent → Answered; the user message stays Sending, the state still says Answered).
  - `A_reply_drafted_after_an_answer_is_a_draft` (user Sent, agent, user Draft → Draft).
  - `Uncertain_delivery_shows_until_the_agent_answers` (user Uncertain → DeliveryUncertain; user Uncertain + agent → Answered).
  - `Resolved_wins_over_everything` (IsResolved with a Draft → Resolved).
  - `The_last_session_is_the_one_the_newest_sent_message_went_to`.
- [ ] **Step 2: Run, expect FAIL** (types missing). Filter `FullyQualifiedName~RemarkThreadTests`.
- [ ] **Step 3: Implement the records and the derived properties.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat: model remark threads and derive their state`

---

### Task 2: Line diff and anchor locator

**Files:**
- Create: `src/Pact.Core/Remarks/LineDiff.cs`, `src/Pact.Core/Remarks/RemarkLocator.cs`
- Test: `tests/Pact.Core.Tests/Remarks/RemarkLocatorTests.cs`

**Interfaces:**
- Consumes: `RemarkAnchor` (Task 1).
- Produces:
  - `static class LineDiff { int?[]? MapUnchanged(IReadOnlyList<string> before, IReadOnlyList<string> after, int maxEdits = 4000) }` — for each index of `before`, the index in `after` when the line is unchanged in a minimal line diff (Myers), else null; returns null when the diff needs more than `maxEdits` edits.
  - `static class RemarkLocator`:
    - `IReadOnlyList<string> SplitLines(string text)` — split on `\n`, drop `\r`, no phantom last line after a final newline.
    - `int? LocateNew(RemarkAnchor anchor, IReadOnlyList<string> captured, IReadOnlyList<string> current)` — the anchor's 1-based line in `current`, or null when outdated.
    - `bool IsOldValid(RemarkAnchor anchor, string? headCommit)` — true only while `headCommit == anchor.BaseCommit` (ordinal; null equals null).

`LocateNew` algorithm (the spec fixes it; the code is not determined by the tests alone):
1. `map = LineDiff.MapUnchanged(captured, current)`; null → outdated.
2. Window = the anchor line with `Before.Count` lines above and `After.Count` below, read from `captured`. Find every start index in `captured` where that same sequence of lines occurs.
3. More than one occurrence: if any line of any occurrence has `map[i] == null` → outdated.
4. Otherwise `map[Line - 1]` → that index + 1, or outdated when null.

- [ ] **Step 1: Write the failing tests**
  - `An_unchanged_line_follows_lines_inserted_above` (captured `a,b,c`, current `x,a,b,c`, anchor on `b` (line 2) → 3).
  - `A_changed_line_is_outdated` (`a,b,c` → `a,B,c`, anchor `b` → null).
  - `A_deleted_line_is_outdated`.
  - `Deleting_the_first_of_two_identical_blocks_outdates_a_remark_on_it` (the spec's case: captured `x; p,q,r; p,q,r; y`, anchor on the `q` of the first block with one line of context each side; current `x; p,q,r; y` → null, although the surviving `q` now sits at the anchor's old line number).
  - `Deleting_an_unrelated_line_keeps_a_remark_in_a_repeated_block` (same file, delete `x` → anchor moves up by one).
  - `Crlf_and_lf_versions_of_the_same_text_keep_the_anchor` (`SplitLines("a\r\nb\r\n")` vs `SplitLines("a\nb\n")` → same line).
  - `An_edit_beyond_the_budget_outdates_instead_of_scanning` (`MapUnchanged` with `maxEdits: 2` over 3 changed lines → null; `LocateNew` → null).
  - `An_old_side_remark_is_valid_only_on_its_commit` (`IsOldValid` true on `abc`, false on `def`, true null/null, false null/`abc`).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement `LineDiff` (Myers O(ND) with the edit budget) and `RemarkLocator`.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat: carry remark anchors to the current file and mark them outdated`

---

### Task 3: Remark storage

**Files:**
- Create: `src/Pact.Infrastructure/Remarks/RemarkFileStore.cs`
- Create: `src/Pact.Infrastructure/Storage/PactFolder.cs` (move the `.pact/.gitignore` write out of `ReviewLogWriter.CreatePath`, which then calls it)
- Test: `tests/Pact.Infrastructure.Tests/Remarks/RemarkFileStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 records.
- Produces:
  - `static class PactFolder { void EnsureGitIgnored(string projectRoot) }`.
  - `record RemarkFile(int NextNumber, IReadOnlyList<RemarkThread> Threads, string? SetAsideFile = null)` — `NextNumber` starts at 1.
  - `sealed class RemarkFileStore(string projectRoot)` with `Task<RemarkFile> LoadAsync(CancellationToken)`, `Task SaveAsync(RemarkFile, CancellationToken)`, `Task<string> SaveSnapshotAsync(string text, CancellationToken)` (returns the lower-case SHA-256 hex of the UTF-8 text; writes only when missing) and `Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string id, CancellationToken)` (null when missing; lines through `RemarkLocator.SplitLines`).
- JSON: System.Text.Json, camelCase, enums as strings, `{ "version": 1, "nextNumber": n, "threads": [...] }`.

- [ ] **Step 1: Write the failing tests**
  - `Threads_and_the_next_number_survive_a_save_and_load` (two threads with messages of both authors).
  - `A_missing_file_loads_empty_with_number_one`.
  - `Saving_writes_the_gitignore_of_the_pact_folder` (`.pact/.gitignore` contains `*`).
  - `An_unreadable_file_is_set_aside_not_lost` (write `{broken`; load → empty, `SetAsideFile` names an existing `remarks.json.broken-*` holding `{broken`, and `remarks.json` is gone).
  - `A_snapshot_is_stored_once_by_its_hash` (same text twice → same id, one file; `LoadSnapshotLinesAsync` gives the lines).
  - Keep `ReviewLogWriterTests` green (they cover the moved `.gitignore` write).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement `PactFolder`, switch `ReviewLogWriter` to it, implement `RemarkFileStore`.**
- [ ] **Step 4: Run `RemarkFileStoreTests` and `ReviewLogWriterTests`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: store remark threads and file snapshots in the project`

---

### Task 4: RemarkBook, the project's remarks in memory

**Files:**
- Create: `src/Pact.Presentation/Services/Remarks/RemarkBook.cs`, `src/Pact.Presentation/Services/Remarks/RemarkBooks.cs`
- Test: `tests/Pact.Presentation.Tests/Services/Remarks/RemarkBookTests.cs`

**Interfaces:**
- Consumes: Tasks 1 and 3.
- Produces (namespace `Pact.Presentation.Services.Remarks`):
  - `sealed class RemarkBook(string projectRoot, RemarkFileStore store, TimeProvider time)`:
    - `string ProjectRoot`; `Task<IReadOnlyList<string>?> LoadSnapshotLinesAsync(string snapshotId, CancellationToken)` (passes through to the store).
    - `event EventHandler? Changed` — raised on the calling thread after a change is saved.
    - `IReadOnlyList<RemarkThread> Threads` — immutable snapshot; `string? SetAsideFile` from the load.
    - `Task EnsureLoadedAsync(CancellationToken)` — loads once; every other method awaits it first.
    - `Task<RemarkThread> AddAsync(RemarkAnchor anchor, string? capturedText, string text, CancellationToken)` — assigns `C{NextNumber}`, stores the snapshot for a New anchor and sets `SnapshotId`, and adds one user Draft message.
    - `Task SaveDraftAsync(string threadId, string text, CancellationToken)` — creates or replaces the thread's single Draft; whitespace text removes the draft, and a thread left with no messages is removed (its id is not reused).
    - `Task ResolveAsync(string threadId, CancellationToken)`, `Task ReopenAsync(string threadId, CancellationToken)`.
    - `Task<RemarkReply> AgentReplyAsync(string threadId, string text, bool resolve, CancellationToken)`; `record RemarkReply(bool Found, IReadOnlyList<string> OpenIds)`. Resolve sets `IsResolved`; without it a resolved thread is reopened by the answer.
    - `Task<RemarkBatch?> BeginSendAsync(string sessionId, CancellationToken)` — every Draft message (of unresolved threads) becomes Sending with that session; null when there is none. `record RemarkBatch(string SessionId, IReadOnlyList<RemarkBatchItem> Items)`, `record RemarkBatchItem(RemarkThread Thread, RemarkMessage Message)` (the thread as it was before this message).
    - `Task CompleteSendAsync(RemarkBatch batch, PromptDeliveryResult result, CancellationToken)` — changes only the batch's messages, by message id, and only while they are still Sending (the mapping is in Global Constraints).
    - `Task RetryAsync(string threadId, CancellationToken)` (Uncertain → Draft), `Task MarkSentAsync(string threadId, CancellationToken)` (Uncertain → Sent).
  - `sealed class RemarkBooks(TimeProvider time) { RemarkBook For(string projectRoot) }` — one book per full root path, case-insensitive, each over a `new RemarkFileStore(projectRoot)`.
- All mutations run under one `SemaphoreSlim` and save before `Changed` is raised.

- [ ] **Step 1: Write the failing tests** (temporary project folder, real `RemarkFileStore`):
  - `Ids_count_up_and_are_not_reused_after_a_discard` (add C1, C2; discard C2's only draft; add → C3; reload a new book from disk → next is C4).
  - `A_new_side_remark_keeps_a_snapshot_of_the_file`.
  - `Begin_send_takes_every_draft_and_only_drafts`.
  - `A_late_delivery_result_does_not_undo_an_answer` (begin send; agent reply; complete with `Unconfirmed` sent → state Answered).
  - `A_late_delivery_result_does_not_undo_a_resolve` (same with `resolve: true` → Resolved).
  - `A_reply_drafted_during_delivery_stays_a_draft` (begin send; draft a reply on the same thread; complete with Confirmed → the new reply is still Draft, the batched message is Sent).
  - `Nothing_written_puts_the_messages_back_to_draft` (`NotRunning`, `WriteAttempted: false`).
  - `Written_but_not_submitted_is_delivery_uncertain_and_can_be_retried_or_marked_sent`.
  - `An_unknown_id_lists_the_open_ones` (`AgentReplyAsync("C9", ...)` → `Found: false`, `OpenIds` `["C1"]` when C1 is open and C2 resolved).
  - `Changes_are_saved_before_changed_is_raised` (in the handler, a new `RemarkFileStore` load sees the change).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement `RemarkBook` and `RemarkBooks`.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat: keep a project's remark threads with sending and agent replies`

---

### Task 5: Placing threads and composing the prompt

**Files:**
- Create: `src/Pact.Presentation/Services/Remarks/RemarkPlacer.cs`, `src/Pact.Core/Remarks/RemarkPrompt.cs`
- Test: `tests/Pact.Presentation.Tests/Services/Remarks/RemarkPlacerTests.cs`, `tests/Pact.Core.Tests/Remarks/RemarkPromptTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4.
- Produces:
  - `record RemarkPlacement(int? Line, bool IsOutdated)` (Presentation) and `static class RemarkPlacer` with `Task<RemarkPlacement> PlaceAsync(RemarkBook book, RemarkThread thread, string? headCommit, CancellationToken)`. New side: read the current file under `book.ProjectRoot` (missing → outdated) and the snapshot through `book.LoadSnapshotLinesAsync` (missing → outdated), then `LocateNew`. Old side: `IsOldValid` → its own line, else outdated.
  - `record RemarkPromptEntry(RemarkThread Thread, RemarkMessage Message, int Line, bool IsOutdated)` and `static class RemarkPrompt { string Compose(IReadOnlyList<RemarkPromptEntry> entries) }` (Core). `Line` is the current line when placed, else the captured one.

Prompt layout (wording is the builder's call; the head lines are fixed in Global Constraints):

```
Remarks from the user on the uncommitted changes. Answer every id with pact_reply_remark (id, text; resolve: true when it is done); pact_list_remarks shows them again.

C3 · src/X.cs:121 · <text>
> <captured line>

C4 (outdated) · src/Y.cs, removed line 7 of HEAD abc1234 · reply
> <captured line>
you: <earlier user message>
agent: <earlier agent message>
you: <new text>
```

A reply has `reply` in place of the text in its head line, then the earlier messages (Draft ones excluded) and the new one. Lines after the first in a message are indented by two spaces. The empty tree reads `of the empty tree`.

- [ ] **Step 1: Write the failing tests**
  - `RemarkPromptTests`: `A_first_remark_has_its_head_line_and_the_quoted_line`, `An_old_side_remark_names_the_commit`, `The_empty_tree_is_named`, `An_outdated_thread_is_labelled`, `A_reply_lists_the_earlier_messages_above_the_new_one`, `Multi_line_text_is_indented` — exact strings.
  - `RemarkPlacerTests`: `A_new_side_remark_follows_its_line_in_the_current_file`, `A_deleted_file_outdates_a_new_side_remark`, `An_old_side_remark_outdates_when_head_moves` (the spec's "old side after a commit" case).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS.**
- [ ] **Step 5: Commit** — `feat: place remark threads in the current file and compose the remark prompt`

---

### Task 6: MCP tools `pact_reply_remark` and `pact_list_remarks`

**Files:**
- Create: `src/Pact.Presentation/Services/AgentControl/RemarkTools.cs`
- Modify: `src/Pact.Presentation/Services/AgentControl/SessionTools.cs` (new `RemarkTools remarks` parameter, its two tools appended)
- Modify: `src/Pact.Presentation/Services/AgentControl/AgentControlDispatcher.cs` (add `Task<(string? Root, AgentControlResult? Refusal)> ResolveProjectRootAsync(string sessionId, CancellationToken)`, using `TryGetOwner`, the project-owner validation the notes tools use, and `GetProjectRootAsync`)
- Modify: `src/Pact.Infrastructure/AgentControl/PactSkills/PactMcpSkill.md` (a "Remarks" section)
- Test: `tests/Pact.Presentation.Tests/Services/AgentControl/SessionToolsTests.cs`, `tests/Pact.Presentation.Tests/Services/AgentControl/RemarkToolsTests.cs`, `tests/Pact.Infrastructure.Tests/AgentControl/PactSkillPublisherTests.cs`

**Interfaces:**
- Consumes: `RemarkBooks`, `RemarkBook`, `RemarkPlacer` (Tasks 4–5).
- Produces: `sealed class RemarkTools(AgentControlDispatcher dispatcher, RemarkBooks books, Func<string, CancellationToken, Task<string?>> readHead, Func<string, Task> reportStatusAsync)` with `IReadOnlyList<AgentControlTool> Create()`. `readHead(projectRoot)` gives the HEAD commit, or null when unborn.
  - `pact_reply_remark` — required `id` and `text` strings, optional boolean `resolve`. An unknown id is refused with code `unknown-remark`: `No remark C9 in this project. Open remarks: C1, C3.` (or `There are no open remarks.`). Success text `Reply recorded.`; status `Agent action: remark answered`.
  - `pact_list_remarks` — optional `state` enum `open` (default, not resolved), `draft`, `sent`, `uncertain`, `answered`, `resolved`, `all`. Returns a JSON array of `{ id, path, side, line, lineText, baseCommit, outdated, state, messages: [{ author, text }] }`; Draft messages and threads that hold only a Draft are left out.
  - ROOT and orchestrator callers are refused like the notes tools.

- [ ] **Step 1: Write the failing tests**
  - `BuildToolsList_lists_every_session_tool` gains both names.
  - `RemarkToolsTests` (fake `IAgentControlHost` like the notes tests, real book on a temporary root): `A_reply_answers_the_thread`, `A_reply_with_resolve_resolves_it`, `An_unknown_id_is_refused_with_the_open_ids`, `The_list_leaves_out_drafts_and_marks_outdated_threads`, `A_root_session_is_refused`.
  - `Published_guidance_documents_all_new_notes_review_and_web_tools` gains `pact_reply_remark` and `pact_list_remarks`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement; skill text: when Pact sends remarks, answer each id with `pact_reply_remark`, `resolve: true` once it is done; `pact_list_remarks` recovers them.**
- [ ] **Step 4: Run the three test classes, expect PASS.**
- [ ] **Step 5: Commit** — `feat: let agents answer and list remark threads over MCP`

---

### Task 7: ChangesRemarksViewModel

**Files:**
- Create: `src/Pact.Presentation/ViewModels/ChangesRemarksViewModel.cs`, `src/Pact.Presentation/ViewModels/RemarkThreadViewModel.cs`
- Modify: `src/Pact.Presentation/ViewModels/ProjectChangesViewModel.cs` (`ChangedFileViewModel` gets INotifyPropertyChanged and `int OpenRemarks`)
- Test: `tests/Pact.Presentation.Tests/ViewModels/ChangesRemarksViewModelTests.cs`

**Interfaces:**
- Consumes: Tasks 4–5, `ProjectChangesViewModel` (`Files`, `SelectedFile`, `DiffRows`, `HeadCommit`, `ProjectRoot`).
- Produces:
  - `record SessionTarget(string SessionId, string Title)`.
  - `sealed class ChangesRemarksViewModel(ProjectChangesViewModel changes, RemarkBook book, Func<IReadOnlyList<SessionTarget>> sessions, Func<string, string, CancellationToken, Task<PromptDeliveryResult>> deliver, Action<Action> postToUi) : INotifyPropertyChanged, IDisposable`:
    - `IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> ThreadsByRow` — threads placed on rows of the shown diff (row index in `DiffRows`); recomputed when the rows, the selected file, HEAD or the book change.
    - `IReadOnlyList<RemarkThreadViewModel> Elsewhere` — threads of any file that are outdated or whose line is not in the shown rows, newest id first.
    - `int OpenCount` (threads not resolved), `int DraftCount`, `string BarText`, `string? StatusText`, `bool CanSend`, `string? SendBlockedReason`, `IReadOnlyList<SessionTarget> SendTargets`.
    - `bool CanRemarkOn(int row)` — added and context rows (New side, line = `NewNumber`) and removed rows (Old side, line = `OldNumber`, `BaseCommit` = `HeadCommit`); never hunk headers.
    - `Task<RemarkThreadViewModel?> AddRemarkAsync(int row, string text, CancellationToken)` — for New, reads the file from disk and refuses (`StatusText` = `The file changed; wait for the diff to refresh.`, returns null) unless line `NewNumber` equals the row's text; context is up to 3 lines each side from the file (New) or from the neighbouring rows that have an `OldNumber` (Old).
    - `Task SendAsync(string sessionId, CancellationToken)` — `BeginSendAsync`, place each item, `RemarkPrompt.Compose`, `deliver`, `CompleteSendAsync`; `StatusText` from `PromptDeliveryText.DescribeUndelivered`, or `Delivery was not confirmed.` for Unconfirmed. `PromptDeliveryText` moves from `src/Pact.App.Avalonia/Controllers` to `src/Pact.Presentation/Services` (namespace `Pact.Presentation.Services`, now `public`); its App callers only change their `using`.
    - Sets `OpenRemarks` on every `ChangedFileViewModel` in `Files` whenever the files or the book change.
    - Reports `book.SetAsideFile` once in `StatusText`: `The remarks file could not be read; it was kept as <name>.`
  - `sealed class RemarkThreadViewModel : INotifyPropertyChanged` — `Id`, `StateText`, `IsOutdated`, `LocationText` (`src/X.cs:121` or `src/X.cs, removed line 121`), `Messages` (`RemarkMessageViewModel(string AuthorText, string Text, string? DeliveryText)`; author `You`/`Agent`), `DraftText` (two-way), and `SaveDraftAsync`, `DiscardDraftAsync`, `ResolveAsync`, `ReopenAsync`, `RetryAsync`, `MarkSentAsync`, with `CanResolve`, `CanReopen`, `CanRetry`. One instance per thread id lives as long as the thread, so a refresh updates it in place and never resets `DraftText` while it differs from the stored draft.
  - `SendTargets` is the `sessions()` list; `CanSend` needs `DraftCount > 0` and at least one target, and `SendBlockedReason` is `No session in this project to send to` when there is none.

- [ ] **Step 1: Write the failing tests** (fake reader from the stage 1 tests, temporary root, immediate UI poster, gated `deliver` built on a `TaskCompletionSource`):
  - `A_remark_on_an_added_line_is_placed_on_its_row`.
  - `A_remark_on_a_removed_line_is_old_side_against_head`.
  - `Hunk_headers_take_no_remark`.
  - `A_remark_on_a_line_the_file_no_longer_has_is_refused`.
  - `An_outdated_thread_moves_to_elsewhere` (change the line on disk, refresh → the row has no thread, `Elsewhere` holds it with `IsOutdated`).
  - `Counts_follow_the_book` (`OpenCount`, `DraftCount`, `BarText` `2 remarks to send`, the file's `OpenRemarks`).
  - `Send_delivers_one_prompt_with_every_draft` (the delivered text holds both head lines; after Confirmed both threads are Sent).
  - `An_agent_answer_during_delivery_stays_answered` (gate the delivery, `AgentReplyAsync` meanwhile, release with Unconfirmed → Answered and `StatusText` `Delivery was not confirmed.`).
  - `Sending_is_blocked_without_a_session`.
  - `A_set_aside_remarks_file_is_reported`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect PASS; run all of `Pact.Presentation.Tests`.**
- [ ] **Step 5: Commit** — `feat: place remark threads on the diff and send them from the Changes tab`

---

### Task 8: Remark margin and thread overlay

**Files:**
- Create: `src/Pact.App.Avalonia/Views/Diff/DiffRemarkMargin.cs`
- Create: `src/Pact.App.Avalonia/Views/Remarks/RemarkThreadView.axaml(.cs)` (one thread: header with id, state, `Outdated`; messages; draft box; buttons) and `src/Pact.App.Avalonia/Views/Remarks/RemarkOverlay.axaml(.cs)` (the threads of one row plus, for a new remark, a text box with `Add`; ✕ in the corner)
- Modify: `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml(.cs)`
- Test: `tests/Pact.App.Avalonia.Tests/Views/ProjectScreenViewHeadlessTests.cs`

**Interfaces:**
- Consumes: `ChangesRemarksViewModel`, `RemarkThreadViewModel` (Task 7). The view gets the remarks through `ProjectScreenViewModel.Remarks` (new nullable constructor parameter, wired in Task 10).
- Produces:
  - `sealed class DiffRemarkMargin : AbstractMargin` with `SetMarks(Func<int, bool> canRemark, IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> threads)`, `event EventHandler<int>? RowClicked` (row index = document line − 1) and `MarkOf(int line)` → `"+"` (hovered and remarkable), `"thread"`, or `""` for tests. The marker colour follows the worst state of the row: Uncertain (orange), Draft (muted), Sent (accent), Answered (green), Resolved (faint).
  - The overlay is a `Canvas` in the same grid cell as `DiffEditor`, above it. `ProjectScreenView` places it at `top = TextView.GetVisualTopByDocumentLine(line + 1) - TextView.ScrollOffset.Y`, or above the line when it would cross the bottom; `left` is the text area's left edge, width `min(640, available)`. It follows `TextView.ScrollOffsetChanged` and hides while its line is scrolled out. It closes on ✕, Esc, a file switch, or when its row's thread disappears.

- [ ] **Step 1: Write the failing headless tests** (FluentTheme plus the AvaloniaEdit style include, as in the existing editor tests):
  - `The_margin_marks_rows_with_threads`.
  - `Clicking_the_margin_opens_the_overlay_under_its_line` (the overlay's top equals the line's bottom within 1 px).
  - `The_overlay_follows_scrolling` (scroll by 3 lines → the top moves up by 3 line heights; scrolled out → hidden).
  - `Adding_a_remark_from_the_overlay_creates_a_draft`.
  - `An_agent_reply_keeps_the_text_being_typed` (type into the draft box, `AgentReplyAsync` on the book → the message appears, the box keeps its text).
  - `Esc_closes_the_overlay`.
  - `A_click_in_the_text_still_selects` (a drag in the text area selects and opens nothing).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run `ProjectScreenViewHeadlessTests`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: open remark threads under diff lines`

---

### Task 9: Send bar, other remarks and counts

**Files:**
- Modify: `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml(.cs)` (bar in the Changes panel: `BarText`, `StatusText`, `Other remarks (K)` button with a flyout of `RemarkThreadView`s, `Send` button that sends straight away for one target and opens a `MenuFlyout` of session titles for several, disabled with `SendBlockedReason` as its tooltip)
- Modify: `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml(.cs)` (file rows show `OpenRemarks` when above 0; the Changes entry gets a `remarks: N` line; new `ChangesRemarksViewModel? Remarks` property)
- Test: `tests/Pact.App.Avalonia.Tests/Views/ProjectScreenViewHeadlessTests.cs`, `tests/Pact.App.Avalonia.Tests/Views/RightActionsPanelHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 7 and Task 8.
- Produces: `RightActionsPanel.Remarks` (set by `MainWindow` in Task 10).

- [ ] **Step 1: Write the failing headless tests**
  - `The_bar_counts_drafts_and_sends_to_the_only_session`.
  - `With_several_sessions_send_asks_which_one` (the flyout lists both titles; picking one delivers to it).
  - `Send_is_disabled_without_a_session` (tooltip text).
  - `Other_remarks_lists_outdated_threads`.
  - `The_changes_entry_shows_open_remarks` and `A_file_row_shows_its_open_remarks`.
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run both test classes, expect PASS.**
- [ ] **Step 5: Commit** — `feat: send remarks from the Changes tab and count them in the right column`

---

### Task 10: Wiring in the shell

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` (own a `RemarkBooks`; pass `RemarkTools` to `SessionTools.Create`; `readHead` through `WorkingTreeReader.ReadChangesAsync(...).HeadCommit`)
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.ProjectScreen.cs` (create a `ChangesRemarksViewModel` with each project's change list and dispose it with the list in `DropProject` and `DisposeProjectScreens`; `CurrentRemarks` beside `CurrentChanges`; `sessions` = the workspace's sessions; `deliver` = `DeliverPromptAsync` as in Global Constraints)
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs` (`RightActions.Remarks = EngineProbeController.CurrentRemarks`)
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/AgentControlCompositionTests.cs`, `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces: `AvaloniaMainShellController.CurrentRemarks` (`ChangesRemarksViewModel?`), raised with `CurrentChanges`.

- [ ] **Step 1: Write the failing tests**
  - Composition: `An_agent_reply_reaches_the_open_changes_tab` (a remark in project A's book; `pact_reply_remark` from session A → the screen's thread is Answered, on the UI poster).
  - Composition: `A_session_of_another_project_cannot_answer` (session B gets `unknown-remark` for A's id).
  - Controller: `Remarks_follow_the_shown_project` (`CurrentRemarks` belongs to the project of the shown session or screen and is null outside git).
  - Controller: `Sending_remarks_delivers_to_the_picked_session` (fake runtime delivery records the session id and text).
- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Build; run the full suites of `Pact.Core.Tests`, `Pact.Infrastructure.Tests`, `Pact.Presentation.Tests`, `Pact.App.Avalonia.Tests`; expect PASS (name any failure, and rerun a known load-flaky test alone).**
- [ ] **Step 5: Commit** — `feat: wire remark threads into the shell and the agent tools`

---

### After the last task

- Publish to `artifacts/local-diag` for a live check (`dotnet publish src/Pact.App.Avalonia/Pact.App.Avalonia.csproj -c Release --runtime win-x64 --self-contained false --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false -p:UsedAvaloniaProducts= -o artifacts/local-diag`).
- Codex review of the whole range (`pact_request_review`, `codex-med`, `code-review`).
- Mark stage 4 done in `docs/backlog/2026-10-25-next-steps.md`.
