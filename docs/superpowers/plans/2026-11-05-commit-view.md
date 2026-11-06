# Commit View Implementation Plan

Superseded scope detail (user correction 2026-11-06): Send and draft counts are
project-wide across Working tree and commits; only row/file markers are
source-specific. Review cleanup likewise covers the complete project book.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** View an immutable commit in Changes and review its lines without mixing working-tree content or remark scopes.

**Architecture:** Reuse the Git runner/parser, Changes display and existing remark book. A revision-reader interface supplies commit metadata/blobs; Changes retains live working-tree facts separately from its displayed source. Historical anchors add optional SHA/path metadata to the same remarks.json.

**Tech Stack:** .NET 10, Avalonia 12, existing Git CLI runner, NUnit/Shouldly.

**Spec:** `docs/superpowers/specs/2026-11-05-commit-view-design.md`, approved by user for implementation on 2026-11-05 after the single-file simplification.

## Global Constraints

- Single `remarks.json`; old records mean Working tree. Downgrade writes unsupported. No sidecar, new store, Git engine or polling loop.
- First-parent comparison; root commit against empty tree; history pages of 50.
- Historical blobs and both sides of remarks belong to exact full SHAs; no checkout/index mutation.
- Right-column file/line counts remain working-tree facts; remarks total remains project-wide.
- Reuse diff, whole-file and Markdown controls. Historical local images become placeholders and local file links are disabled.
- Existing dirty feature checkout and user changes are retained. No automatic commits, worktrees or new publish directories. Publish to `artifacts/local-diag` only.
- One heavy command at a time. Build `-m:2 -nr:false -v q -p:BuildInParallel=false`; tests `--no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.

## Review Focus

1. Refs move during picker/history reads: resolve to immutable SHA before diffing; Task 1 test.
2. Live status completes during commit selection: update only live summary, never committed rows; Task 2 test.
3. Sibling commits share path/parent: stale editors and row mapping still distinguish target SHA; Task 3 test.
4. Historical thread outside shown file with different HEAD: prompt/MCP/Elsewhere keep exact line and outdated=false; Task 3 test.
5. A delayed local heading/link from an old source must not navigate the new source; Task 4 test.

## Task 1: Read commit snapshots through the existing Git runner

**Files:** create `src/Pact.Infrastructure/Git/IGitCommitReader.cs`, `GitWorkingTreeReader.Commits.cs`; modify `GitWorkingTreeReader.cs` to partial/implement the new interface; create `tests/Pact.Infrastructure.Tests/Git/GitCommitReaderTests.cs`.

**Interfaces:** `GitCommitInfo(Id, Parents, Author, AuthoredAt, Subject)` with `BaseCommit` (first parent or null); `CommitChanges(Commit, Files, AddedLines, RemovedLines)`.
`IGitCommitReader` produces `ReadCommitsAsync(root, skip, count, ct)`, `ResolveCommitAsync(root, revision, ct)`, `ReadCommitChangesAsync(root, commit, ct)`, `ReadCommitDiffAsync(root, entry, commit, ct)`, `ReadCommitTextAsync(root, path, commitId, ct)`.

- [x] Write temporary-repository tests: `Root_commit_reads_added_files_and_immutable_text`, `Merge_uses_first_parent_and_preserves_renamed_paths`, `History_pages_and_resolved_refs_keep_full_commit_identity`, `Commit_blobs_are_bounded_and_binary_files_have_placeholders`.
- [x] Run them RED before implementation; test ordinary commits plus Unicode/space/newline paths and modified/deleted working files.
- [x] Implement metadata/NUL path parsing and first-parent/root comparisons. Validate exact ids and option boundaries, check blob size before reading it; reuse FileDiff/UnifiedDiffParser and size/binary rules.
- [x] Run GitCommitReaderTests and existing GitWorkingTreeReaderTests GREEN.

## Task 2: Select a displayed source without replacing live facts

**Files:** modify `src/Pact.Presentation/ViewModels/ProjectChangesViewModel.cs`; create `.Commits.cs` partial; extend `tests/Pact.Presentation.Tests/ViewModels/ProjectChangesViewModelTests.cs`.

**Consumes:** Task 1 reader/metadata. Constructor accepts an optional commit reader, with production composition using the same GitWorkingTreeReader instance.
**Produces:** `SelectedCommit`, `IsWorkingTree`, `SourceTitle`, `SourceDescription`, `IsSourceLoading`, `SourceError`, `CanChooseCommit`; `SelectCommitAsync(GitCommitInfo? commit, CancellationToken ct)` (null selects Working tree). `WorkingTreeHeadCommit` and live counters feed EntryTitle/EntrySubtitle independently of displayed Files/HeadCommit/totals.
`ShownDiff` gains optional trailing `TargetCommit` and `OriginalPath`; LoadedDiff carries the same identity.

- [x] Write RED tests `Commit_selection_reads_historical_text_and_selects_first_file`, `Working_tree_refresh_does_not_replace_commit_rows`, `Later_source_or_file_selection_wins_over_pending_commit_reads`, `Returning_to_working_tree_restores_live_changes`.
- [x] Add source/selection generation guards, invalidate on disposal, preserve existing display modes and root lifetime. Keep one working-tree observation/timer and cached live facts; no historical polling.
- [x] Include target SHA/original path in shown-diff equality, captured file loads and folding ownership. Failure reports status without mixed source/header/rows.
- [x] Run ProjectChangesViewModelTests and RenderedMarkdownTests GREEN.

## Task 3: Preserve remark provenance in storage, placement, send and MCP

**Files:** `src/Pact.Core/Remarks/RemarkThread.cs`, `RemarkPrompt.cs`; `src/Pact.Presentation/Services/Remarks/RemarkBook.cs`, `RemarkPlacer.cs`; `src/Pact.Presentation/ViewModels/ChangesRemarksViewModel.cs`, `RemarkThreadViewModel.cs`; `src/Pact.Presentation/Services/AgentControl/RemarkTools.cs`; corresponding Core/Presentation/Infrastructure remark tests.

**Consumes:** Task 2 source identity. `RemarkAnchor` gains optional trailing `TargetCommit`/`OriginalPath`. Existing constructors/deserialization remain valid; the same file stores all threads.
**Produces:** source labels/navigation metadata on thread VMs; source-aware Add/BeginSend flow; exact historical line placement in all callers; full target/base/side/path metadata in prompt/MCP.

- [x] Write RED tests `Historical_anchor_round_trips_beside_legacy_working_tree_threads`, `Historical_placement_ignores_head_and_changed_or_missing_disk_file`, `Historical_send_and_mcp_preserve_sha_line_and_not_outdated`, `Sending_one_source_keeps_other_source_drafts`, `Sibling_commit_editors_cannot_cross_target_identity`.
- [x] Persist optional fields atomically with thread creation; keep project-wide C ids and existing retry lifecycle. Filter Send and draft counts by selected target, including unsaved reply handling.
- [x] Keep Open/Resolved and OpenCount project-wide. Filter row/file markers by source. Off-source placement uses WorkingTreeHeadCommit for live anchors and stable historical lines for commit anchors.
- [x] Recompute on source changes without discarding thread VMs/drafts. Extend source identity in IsOfShownFile/MapRow and equality guards.
- [x] Run affected RemarkThread/Prompt/Locator, RemarkFileStore, RemarkBook/Placer/Tools and ChangesRemarksViewModel tests GREEN.

## Task 4: Picker, source actions and historical Markdown behavior

**Files:** create `src/Pact.Presentation/ViewModels/GitCommitPickerViewModel.cs`; create `src/Pact.App.Avalonia/Views/Dialogs/GitCommitPickerWindow.axaml` and `.axaml.cs`; modify ProjectScreenView, RenderedMarkdownView, MarkdownPreviewHook, RemarksPanel/RemarkThreadView and controller ProjectScreen wiring; add focused picker/headless/Markdown tests.

**Consumes:** Tasks 1–3. Picker VM owns only paged history/ref resolution and error/loading state; selected source remains in ProjectChangesViewModel.
**Produces:** Choose commit action and Working tree action, off-source thread navigation, source badges, source-aware local-resource policy. Explicit working-tree navigation is used by right-column/dashboard Changes actions.

- [x] Write RED tests `Commit_picker_loads_fifty_and_resolves_arbitrary_ref`, `Commit_picker_ignores_late_page_after_disposal`, `Historical_thread_remains_discoverable_from_working_tree_after_restart`, `Historical_markdown_blocks_local_assets_and_keeps_code_and_headings`.
- [x] Implement picker with list/author/date, Load more and SHA/ref input, using existing dialog/lifecycle/error conventions. Selection resolves before applying; cancellation changes no source.
- [x] Wire source/header controls and project-wide remark badges/open actions. Opening an off-source editor first navigates successfully; retain its typed text on failure. Do not lose unsent thread drafts when opening a commit.
- [x] Reuse code-aware Markdown preprocessing for historical local-image placeholders. Disable local file links with a visible explanation, preserve HTTP(S) and same-document headings; reject delayed old-source callbacks.
- [x] Verify actual controls at narrow/wide widths, all display modes, first-file behavior and working-tree right counters. Run relevant App/headless and picker tests GREEN.

## Task 5: Accumulated verification and delivery

- [x] Inspect the full feature diff including the dirty-checkout baseline boundary; trace affected callers for regressions. Preserve earlier dashboard/clipboard/review-message changes.
- [x] Run relevant Core, Infrastructure, Presentation and App tests once at the final source state; run the resource-bounded solution build and relevant native gate. Broaden only for failures/new risks.
- [x] Update public docs/backlog and record actual results, any unavailable live checks and implementation rulings in the ledger.
- [x] Obtain one fresh whole-feature code review, address required findings with focused regressions, then publish the framework-dependent win-x64 app to `artifacts/local-diag` and verify it. No commit/push without user request.

Self-review: the tasks cover source selection/history, immutable content, every remark placement consumer, source-specific Send versus global discovery/counts, legacy JSON forward reading, Markdown resource/link ownership and async publication. Shared DTO/signatures above are the cross-task contract; the spec wins any conflict.
