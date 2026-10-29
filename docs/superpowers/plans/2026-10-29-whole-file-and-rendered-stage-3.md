# Whole-file and rendered modes, Markdown preview fixes (stage 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In the Changes tab, show a changed file whole (any text file) or rendered (Markdown) with its changes marked and remarks working in both; make links and images work in every Markdown preview; merge the Docs pane's Common and Docs tabs.

**Architecture:** Whole file is another row list for the same diff view: `ProjectChangesViewModel` builds `DiffRowViewModel` rows for every new line, with fold rows standing for removed runs, and publishes them through `DiffRows`/`ShownDiff` exactly as the hunk rows are published today, so stage 4's remark anchoring, placement and overlay work unchanged. Rendered mode sits on the whole-file rows too: a pure builder splits the new text into Markdown blocks, marks them from the rows and maps each block to the row of its first line, and a new view draws the blocks with Markdown.Avalonia's engine one block at a time. Links and images get one shared hook on Markdown.Avalonia's engine (`HyperlinkCommand`, `AssetPathRoot`) with a pure resolver in Presentation.

**Tech Stack:** .NET 10, Avalonia 12.1, AvaloniaEdit 12, ClassIsland.Markdown.Avalonia 12.0.0, NUnit 4 + Shouldly, Avalonia.Headless.NUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-project-screen-changes-design.md`, section 3 (widened 2026-10-29) and section 4 for remark rules.

## Global Constraints

- Builds: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`; tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 3m -- NUnit.NumberOfTestWorkers=2`. One heavy build or test run at a time. Kill a leftover `testhost` after App runs.
- Commits on `main`, never pushed; message trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Switch labels exactly: **Diff** (default), **Whole file**, **Rendered** (`.md` only). Binary and too-large files keep the placeholder and show no switch.
- The chosen mode stays while the user moves between files.
- Whole file: new version in full, new line numbers only; added lines tinted as in the diff; a removed run is one fold row `▸ N lines removed` (`▸ 1 line removed`), expanded `▾ N lines removed` followed by the old lines tinted as removed. A deleted file shows its old text as removed, without a fold.
- Rendered: blocks are a heading, a paragraph, a list item, a quote, a table, a code block. Added and changed blocks carry a coloured bar; removed blocks are the same thin fold, expanding to the old text rendered. Removed blocks take no remarks.
- Remarks in Whole file behave exactly as in Diff (section 4 anchors, ids, outdated rules). In Rendered a remark is a New-side remark on the block's first source line; a thread whose line lies inside a block shows under that block.
- Links: relative `.md` inside the project opens it (Docs pane: selects it in the tree; Rendered: opens it in the Docs pane); `/path.md` is project-root relative; `#heading` scrolls, also after `file.md#heading`; outside the project or missing → no navigation, a notice such as `Outside the project: ../x.md` / `Not found: x.md`; `http(s)` → system browser.
- Images: relative to the document's folder; `http(s)` loads; a missing image shows its alt text and never blocks.
- Docs pane tabs: **Notes** and **Docs**; one tree of every Markdown file the two tabs list today; in every folder subfolders first, then files, each by name; opening the tab selects root `README.md` when present, otherwise nothing.

## Review Focus

1. A file edited by the agent while Whole file or Rendered is open: the view refreshes in place, the scroll stays near, and a thread being written is not lost (same `ShownDiff` instance when nothing changed).
2. A file whose text could not be read consistently (`ShownDiff.NewLines` null, not deleted): Whole file falls back to the hunk rows rather than showing an empty or wrong file.
3. A Markdown file with a fenced code block containing blank lines, `#` lines or `- ` lines: it stays one block.
4. A link whose target has spaces or `%20`, or uses `\`: resolves to the same file; a link with `..` that climbs out of the project is refused, never opened.
5. A folded removed run that holds an old-side thread: the thread is not lost — it shows in the Open list (it has no row) and in place once the fold is expanded.

---

## File map

- Create `src/Pact.Core/Markdown/MarkdownBlocks.cs` — splits Markdown lines into blocks with 1-based line ranges.
- Create `src/Pact.Presentation/ViewModels/DiffViewMode.cs` — `Diff`, `WholeFile`, `Rendered`.
- Create `src/Pact.Presentation/ViewModels/WholeFileRows.cs` — builds whole-file rows and removed runs from a `FileDiff` and the new lines.
- Modify `src/Pact.Presentation/ViewModels/ProjectChangesViewModel.cs` — `Mode`, `CanShowWholeFile`, `CanRender`, `ToggleFold`, `DiffRowKind.Fold`, `ShownDiff.RemovedRuns`.
- Modify `src/Pact.Presentation/ViewModels/ChangesRemarksViewModel.cs` — skip fold rows where hunk headers are skipped.
- Create `src/Pact.Presentation/ViewModels/RenderedMarkdown.cs` — builds rendered blocks from a `ShownDiff` and places threads on them.
- Create `src/Pact.App.Avalonia/Views/Diff/RenderedMarkdownView.axaml(.cs)` — draws rendered blocks, bars, folds, "+" and threads.
- Modify `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml(.cs)` — mode switch, fold clicks, Rendered host.
- Create `src/Pact.Presentation/Services/MarkdownLinks.cs` — resolves an href against a document.
- Create `src/Pact.App.Avalonia/Views/MarkdownPreviewHook.cs` — sets the engine's link command and asset root on a viewer or engine.
- Modify `src/Pact.App.Avalonia/Views/NotesPaneView.axaml(.cs)` — link notice, hook, one Docs tab.
- Modify `src/Pact.Infrastructure/Documents/ProjectMarkdownCatalog.cs`, `src/Pact.Presentation/ViewModels/DocsAndNotesWorkspaceViewModel.cs` — merged tree.

---

### Task 1: Markdown blocks

**Files:**
- Create: `src/Pact.Core/Markdown/MarkdownBlocks.cs`
- Test: `tests/Pact.Core.Tests/Markdown/MarkdownBlocksTests.cs`

**Interfaces:**
- Produces: `public sealed record MarkdownBlock(int FirstLine, int LastLine);` and `public static class MarkdownBlocks { public static IReadOnlyList<MarkdownBlock> Split(IReadOnlyList<string> lines); }` in namespace `Pact.Core.Markdown`. Lines are 1-based and inclusive; blank lines between blocks belong to no block.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void Paragraph_lines_form_one_block_and_blank_lines_separate_blocks() =>
	MarkdownBlocks.Split(["a", "b", "", "c"]).ShouldBe([new(1, 2), new(4, 4)]);
[Test] public void A_heading_is_its_own_block_even_without_a_blank_line() =>
	MarkdownBlocks.Split(["# T", "text"]).ShouldBe([new(1, 1), new(2, 2)]);
[Test] public void Each_list_item_is_a_block_with_its_indented_continuation() =>
	MarkdownBlocks.Split(["- one", "  more", "- two", "1. three"]).ShouldBe([new(1, 2), new(3, 3), new(4, 4)]);
[Test] public void A_fenced_code_block_is_one_block_whatever_it_holds() =>
	MarkdownBlocks.Split(["```", "# x", "", "- y", "```", "after"]).ShouldBe([new(1, 5), new(6, 6)]);
[Test] public void Consecutive_quote_lines_and_table_lines_are_one_block_each() =>
	MarkdownBlocks.Split(["> a", "> b", "| x |", "|---|", "| 1 |"]).ShouldBe([new(1, 2), new(3, 5)]);
[Test] public void An_unclosed_fence_runs_to_the_end() =>
	MarkdownBlocks.Split(["```", "x"]).ShouldBe([new(1, 2)]);
[Test] public void A_longer_fence_is_not_closed_by_a_shorter_one_inside_it() =>
	MarkdownBlocks.Split(["````", "```", "x", "```", "````", "after"]).ShouldBe([new(1, 5), new(6, 6)]);
[Test] public void A_nested_list_stays_inside_its_parent_item() =>
	MarkdownBlocks.Split(["- one", "  - sub", "  - sub2", "- two"]).ShouldBe([new(1, 3), new(4, 4)]);
```

Why per-block source and not one rendering: Markdown.Avalonia renders a whole document into controls without source positions, so bars and remarks cannot be laid on its output. A block's source keeps its own parsing context — a nested list stays inside its parent item (test above), a fence holds its content — and Task 5 checks the result against the whole-document preview.

- [ ] **Step 2: Run them to see them fail**

Run: `dotnet test tests/Pact.Core.Tests --no-build ... --filter FullyQualifiedName~MarkdownBlocksTests` (after a build). Expected: build error, `MarkdownBlocks` missing.

- [ ] **Step 3: Implement `MarkdownBlocks.Split`**

Line scan with a current-block start. Fences follow CommonMark: an opening line of three or more backticks or tildes (indented at most three spaces) closes only on a line of the same character, at least as long, with nothing after it but spaces. Heading: trimmed start matches `#{1,6} `. List item start: trimmed start matches `[-*+] ` or `\d+[.)] `; a following non-blank line indented by at least one space (and not itself an item start) continues it. Quote: line starts with `>`; table: trimmed start begins with `|`; consecutive lines of the same kind join, a change of kind starts a new block.

- [ ] **Step 4: Run the tests** — Expected: 6 pass.
- [ ] **Step 5: Commit** — `feat: split Markdown text into blocks with line ranges`.

### Task 2: Whole-file rows in the changes model

**Files:**
- Create: `src/Pact.Presentation/ViewModels/DiffViewMode.cs`, `src/Pact.Presentation/ViewModels/WholeFileRows.cs`
- Modify: `src/Pact.Presentation/ViewModels/ProjectChangesViewModel.cs`, `src/Pact.Presentation/ViewModels/ChangesRemarksViewModel.cs:139,430`
- Test: `tests/Pact.Presentation.Tests/ViewModels/ProjectChangesViewModelTests.cs`, `tests/Pact.Presentation.Tests/ViewModels/ChangesRemarksViewModelTests.cs`

**Interfaces:**
- Produces:
  - `public enum DiffViewMode { Diff, WholeFile, Rendered }`.
  - `DiffRowKind.Fold` (new value, last). A fold row has empty numbers and text `▸ N lines removed` / `▾ N lines removed` (`line` when N is 1).
  - `public sealed record RemovedRun(int BeforeNewLine, int OldStart, IReadOnlyList<string> Lines);` — `BeforeNewLine` is the new line the run sits above (`NewLines.Count + 1` at the end).
  - `ShownDiff` gains a last parameter `IReadOnlyList<RemovedRun> RemovedRuns` (empty in Diff mode).
  - `ProjectChangesViewModel`: `DiffViewMode Mode { get; set; }` — the user's preferred mode (raises and rebuilds rows; kept across `SelectFileAsync`); `DiffViewMode EffectiveMode { get; }` — what is shown; `bool CanShowWholeFile` (a line diff with hunks, `DiffPlaceholder` null), `bool CanRender` (also path ends with `.md`, case-insensitive), `void ToggleFold(int row)`.
  - Effective mode: `Diff` when `Mode` is `Diff` or the file cannot show whole (placeholder) or its text was read inconsistently (`NewLines` null and not deleted); `WholeFile` when `Mode` is `WholeFile`, or `Mode` is `Rendered` but the file is not Markdown or is deleted (its old text shows as removed rows); `Rendered` only for a Markdown file with `NewLines`. Switch buttons show `EffectiveMode` as checked; `Mode` is untouched by a fallback, so the next Markdown file opens Rendered again.
  - `internal static class WholeFileRows { public static (IReadOnlyList<DiffRowViewModel> Rows, IReadOnlyList<RemovedRun> Runs) Build(FileDiff diff, IReadOnlyList<string>? newLines, bool deleted, IReadOnlySet<int> expandedOldStarts); }`.
- Rows for `WholeFile` and `Rendered` are the same and are never cut: the file is already bounded by `GitWorkingTreeReader.MaxFileBytes` and the editor draws only what is on screen, so `MaxShownLines` and "Show all" apply to hunk rows only (`HiddenLineCount` is 0 in the other modes). Rendered blocks therefore always have a row for every line.

- [ ] **Step 1: Write the failing tests** (in `ProjectChangesViewModelTests`, using the file's existing fake reader)

```csharp
// new text a,B,c,d ; old a,b,x,c,d  → "b","x" removed before line 2, B added at 2
[Test] public async Task Whole_file_shows_every_new_line_with_added_lines_marked_and_removed_runs_folded()
{ /* Mode = WholeFile, select the file */
	rows.Select(r => (r.Kind, r.NewNumber, r.Text)).ShouldBe([
		(DiffRowKind.Context, "1", "a"), (DiffRowKind.Fold, "", "▸ 2 lines removed"),
		(DiffRowKind.Added, "2", "B"), (DiffRowKind.Context, "3", "c"), (DiffRowKind.Context, "4", "d")]);
	changes.ShownDiff!.RemovedRuns.ShouldBe([new RemovedRun(2, 2, ["b", "x"])], ...); }
[Test] public async Task Toggling_a_fold_shows_the_old_lines_as_removed_rows_with_old_numbers_and_folds_them_back()
	// rows[1].Text "▾ 2 lines removed", then Removed rows "b"(old 2) "x"(old 3); toggling again restores the fold row only
[Test] public async Task The_mode_stays_when_another_file_is_selected()
[Test] public async Task A_deleted_file_shows_its_old_text_as_removed_rows_without_a_fold()
[Test] public async Task A_placeholder_file_cannot_show_whole_and_keeps_the_placeholder()   // CanShowWholeFile false, rows empty
[Test] public async Task Rendered_is_offered_for_markdown_only()                            // x.md true, x.cs false
[Test] public async Task Text_read_inconsistently_falls_back_to_hunk_rows_in_whole_file_mode() // NewLines null → hunk rows (Review Focus 2)
[Test] public async Task A_refresh_that_changed_nothing_keeps_the_whole_file_shown_diff_instance() // Review Focus 1
[Test] public async Task An_expanded_fold_stays_expanded_after_a_refresh()                   // keyed by OldStart
[Test] public async Task Whole_file_rows_are_not_cut_at_the_shown_line_limit()                // 3500-line file: every line, HiddenLineCount 0
[Test] public async Task Rendered_falls_back_to_whole_file_for_a_deleted_markdown_file_and_for_a_non_markdown_file() // Mode stays Rendered
[Test] public async Task Rendered_falls_back_to_hunk_rows_when_a_refresh_reads_the_text_inconsistently()          // EffectiveMode Diff, then Rendered again on a consistent read
```

In `ChangesRemarksViewModelTests`:

```csharp
[Test] public async Task A_remark_on_a_whole_file_line_far_from_any_hunk_anchors_to_that_new_line()
	// Mode WholeFile; AddRemarkAsync(shown, rowOfNewLine(40)) → Anchor.Side New, Line 40, LineText as in file
[Test] public async Task A_fold_row_takes_no_remark()          // CanRemarkOn(shown, foldRow) false
[Test] public async Task A_thread_on_a_folded_removed_line_has_no_row_and_shows_in_place_once_expanded() // Review Focus 5
[Test] public async Task A_thread_made_in_diff_mode_sits_on_its_line_in_whole_file_mode()
```

- [ ] **Step 2: Build and run; see them fail** — Expected: compile errors for `Mode`, `DiffRowKind.Fold`, `RemovedRun`.
- [ ] **Step 3: Implement**

Rows follow `EffectiveMode`. `WholeFileRows.Build`: walk new lines 1..N; added new numbers come from the hunks' `Added` lines; each hunk's consecutive `Removed` lines form a run placed before the next new line of that hunk (or at `BeforeNewLine = N + 1`). A run whose `OldStart` is in `expandedOldStarts` adds the fold row (`▾`) and then one `Removed` row per line with its old number; otherwise only the `▸` fold row. `deleted` → every hunk line as a `Removed` row, no fold. In the model, rows come from `WholeFileRows` when `EffectiveMode != Diff`, otherwise from the hunk builder with its cut. `ToggleFold(row)` flips the run's `OldStart` in a set kept per selected path (cleared when another path is selected) and re-applies. In `ChangesRemarksViewModel`, treat `Fold` like `HunkHeader` in `AddRemarkAsync` and in the context walk.

A remark being typed survives a real edit of its file (finding 6). `ChangesRemarksViewModel` gains `public int? MapRow(ShownDiff from, int row, ShownDiff to)`: same path required; a New-side row maps through `RemarkLocator.LocateNew` from `from.NewLines` to `to.NewLines` (null when the line did not survive unchanged); an Old-side row maps to the row with the same `OldNumber` while `HeadCommit` is unchanged. `AddRemarkAsync(shown, ...)` accepts a `shown` that is no longer current when its `Path` equals the current one: the remark is anchored to the text the user saw when they started typing (its snapshot is `shown.NewLines`), and placement then shows it in place or outdated by the section 4 rules — never silently rebound to another line. Tests:

```csharp
[Test] public async Task A_row_maps_to_the_same_line_after_lines_were_inserted_above_it()
[Test] public async Task A_row_whose_line_changed_maps_to_nothing()
[Test] public async Task A_remark_started_before_an_edit_anchors_to_the_text_seen_when_typing_began()
```

- [ ] **Step 4: Run Presentation tests** — Expected: all of `ProjectChangesViewModelTests` and `ChangesRemarksViewModelTests` pass, and the full Presentation project stays green.
- [ ] **Step 5: Commit** — `feat: whole-file rows with folded removed runs for the changes view`.

### Task 3: Mode switch and folds in the diff view

**Files:**
- Modify: `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml`, `.axaml.cs`, `src/Pact.App.Avalonia/Views/Diff/DiffLineBackgroundRenderer.cs`
- Test: `tests/Pact.App.Avalonia.Tests/Views/ProjectScreenViewHeadlessTests.cs`, `tests/Pact.App.Avalonia.Tests/Views/ProjectScreenRemarksHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 2's `Mode`, `CanShowWholeFile`, `CanRender`, `ToggleFold`, `DiffRowKind.Fold`.
- Produces: named controls `DiffModeButton`, `WholeFileModeButton`, `RenderedModeButton` (ToggleButtons in a header row above the diff, row 0 of the changes grid, next to the placeholder text); `RenderedHost` (a `Panel` in row 1 that Task 5 fills; hidden unless `Mode == Rendered`).

- [ ] **Step 1: Write the failing headless tests**

```csharp
[AvaloniaTest] public async Task The_mode_switch_shows_whole_file_and_back()
	// click WholeFileModeButton → DiffEditor.Document.Text contains every line of the file; DiffModeButton → hunk text again
[AvaloniaTest] public async Task Rendered_is_offered_only_for_markdown_and_no_switch_for_a_binary_file()
[AvaloniaTest] public async Task Clicking_a_fold_line_expands_it_and_clicking_again_folds_it()
	// press on the fold row's text → Document has the removed lines; again → fold only
[AvaloniaTest] public async Task A_remark_added_in_whole_file_mode_on_an_unchanged_line_shows_its_mark()   // in ProjectScreenRemarksHeadlessTests
[AvaloniaTest] public async Task An_edit_of_the_open_file_keeps_the_scroll_position_in_diff_and_whole_file()
[AvaloniaTest] public async Task A_first_remark_being_typed_survives_an_edit_of_its_file_and_follows_its_line()
	// open "+" on line 30, type "draft", the agent inserts 2 lines at the top → overlay still open with "draft", over the row of old line 30 (now 32); Ctrl+Enter → anchor Line 30 of the captured text, placed at 32
[AvaloniaTest] public async Task Selecting_another_file_still_starts_at_the_top_and_closes_the_overlay()
```

- [ ] **Step 2: Run them; see them fail** — Expected: `FindControl("WholeFileModeButton")` null.
- [ ] **Step 3: Implement** — The buttons set `Changes.Mode`; their `IsChecked` and visibility follow `Mode`, `CanShowWholeFile`, `CanRender`. A pointer press on a fold row's line in the text view (row from `RowAt`) calls `ToggleFold` and is handled (it does not also close the overlay or select). Fold rows get a muted brush in `DiffLineBackgroundRenderer` (same as `HeaderBrush`). The margins read rows, so whole-file rows work through `DiffRows`. `RefreshDiffText` changes for a refresh of the same path (spec section 1, which today's `ScrollToHome`/`CloseOverlay` on every row change does not meet): keep the vertical offset instead of scrolling home, and keep an open overlay — move it to `MapRow(_overlayDiff, _overlayRow, newShown)` when that is not null, otherwise leave it on its old row index clamped to the document — with its typed text untouched; submitting uses the overlay's captured `_overlayDiff`. A different path keeps today's behaviour (top, overlay closed with drafts saved).
- [ ] **Step 4: Run** `ProjectScreenViewHeadlessTests` and `ProjectScreenRemarksHeadlessTests` — Expected: pass.
- [ ] **Step 5: Commit** — `feat: switch the changes view between diff and whole file`.

### Task 4: Rendered blocks model

**Files:**
- Create: `src/Pact.Presentation/ViewModels/RenderedMarkdown.cs`
- Test: `tests/Pact.Presentation.Tests/ViewModels/RenderedMarkdownTests.cs`

**Interfaces:**
- Consumes: `MarkdownBlocks.Split` (Task 1); `ShownDiff` with whole-file rows and `RemovedRuns` (Task 2).
- Produces (namespace `Pact.Presentation.ViewModels`):
  - `public enum RenderedChange { None, Added, Changed }`
  - `public abstract record RenderedItem;`
  - `public sealed record RenderedBlock(int FirstLine, int LastLine, string Markdown, RenderedChange Change, int Row) : RenderedItem;` — `Row` is the index in `ShownDiff.Rows` of `FirstLine`.
  - `public sealed record RenderedRemoved(int OldStart, string Markdown, int LineCount) : RenderedItem;`
  - `public static class RenderedMarkdown { public static IReadOnlyList<RenderedItem> Build(ShownDiff shown); public static IReadOnlyDictionary<int, IReadOnlyList<RemarkThreadViewModel>> ThreadsByBlock(IReadOnlyList<RenderedItem> items, ShownDiff shown, RowThreads? placed); }` — `ThreadsByBlock` keys by the block's `FirstLine`; a thread on any new line of a block lands on that block.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public void A_block_whose_lines_are_all_added_is_added_and_one_with_some_is_changed()
[Test] public void A_run_removed_inside_a_block_marks_the_block_changed()
[Test] public void A_run_removed_between_blocks_becomes_a_removed_item_in_place()   // Markdown = run lines joined with '\n'
[Test] public void Each_block_points_at_the_row_of_its_first_line()
[Test] public void A_thread_on_the_third_line_of_a_paragraph_lands_on_that_paragraph()
[Test] public void A_fenced_block_with_blank_lines_stays_one_block()                 // Review Focus 3
[Test] public void A_run_removed_before_a_blank_separator_sits_between_the_paragraphs() // new ["a","","b"], run before line 2 → [a, removed, b]
```

- [ ] **Step 2: Run; see them fail** — Expected: compile errors.
- [ ] **Step 3: Implement** — Blocks from `Split(shown.NewLines!)`; added lines from rows of kind `Added`; a run with `BeforeNewLine` strictly inside `(FirstLine, LastLine]` marks that block `Changed`; other runs become `RenderedRemoved` before the first block whose `FirstLine >= BeforeNewLine`, in source order; only runs after the last block go at the end. Row of a new line: index of the row with that `NewNumber`.
- [ ] **Step 4: Run** — Expected: pass.
- [ ] **Step 5: Commit** — `feat: build rendered Markdown blocks with change marks and threads`.

### Task 5: Rendered view with block remarks

**Files:**
- Create: `src/Pact.App.Avalonia/Views/Diff/RenderedMarkdownView.axaml`, `.axaml.cs`
- Modify: `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml(.cs)` (host it in `RenderedHost`)
- Test: `tests/Pact.App.Avalonia.Tests/Views/RenderedMarkdownViewHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 4's `RenderedMarkdown.Build`/`ThreadsByBlock`; stage 4's `ChangesRemarksViewModel.AddRemarkAsync(ShownDiff, int row, string, CancellationToken)`, `CanRemarkOn`, `RemarkOverlay` and `RemarkThreadView`; Task 7's `MarkdownPreviewHook` (wire it once Task 7 exists; until then links use the engine default).
- Produces: `internal sealed partial class RenderedMarkdownView : UserControl` with `void Show(ShownDiff shown, ChangesRemarksViewModel remarks)`; named parts for tests: each block row is a `Border` with class `rendered-block` and `Tag` = `FirstLine`; its "+" is a `Button` with class `block-add`; a removed item is a `Button` with class `rendered-removed` whose content reads `▸ N lines removed` and toggles the old text rendered below it.

- [ ] **Step 1: Write the failing headless tests**

```csharp
[AvaloniaTest] public async Task Changed_and_added_blocks_carry_a_bar_and_unchanged_ones_do_not()
[AvaloniaTest] public async Task A_removed_block_expands_to_its_old_text_rendered()
[AvaloniaTest] public async Task Adding_a_remark_on_a_block_anchors_it_to_the_blocks_first_line()
	// click "+" of block FirstLine 3, type, Ctrl+Enter → book thread Anchor.Side New, Line 3; switching to Diff shows its mark on the row of line 3
[AvaloniaTest] public async Task A_thread_inside_a_paragraph_shows_under_that_paragraph()
[AvaloniaTest] public async Task A_block_with_a_thread_carries_a_mark_and_a_removed_block_has_no_add_button()
[AvaloniaTest] public async Task An_agent_edit_redraws_the_blocks_and_keeps_an_open_thread()   // Review Focus 1
[AvaloniaTest] public async Task Adding_and_resolving_a_thread_updates_its_block_without_redrawing_the_text() // same block control instances
[AvaloniaTest] public async Task Threads_placed_after_the_first_draw_appear_under_their_blocks()
[AvaloniaTest] public async Task A_first_remark_being_typed_on_a_block_survives_an_edit_of_the_file()
[AvaloniaTest] public async Task A_nested_list_and_an_ordered_item_render_as_in_the_docs_preview() // nested bullets kept; "3. three" shows 3
```

- [ ] **Step 2: Run; see them fail.**
- [ ] **Step 3: Implement** — One `ScrollViewer` over a `StackPanel`; per `RenderedBlock` a grid of bar (4 px, green for `Added`, blue for `Changed`, none otherwise), the block's control from `new Markdown.Avalonia.Markdown().Transform(block.Markdown)` (engine created once per view, asset root and link command from Task 7's hook), the "+" visible on pointer over and while `CanRemarkOn(shown, block.Row)`, and under it the block's threads as `RemarkThreadView`s plus a `RemarkOverlay` in inline mode for a new remark. Two update paths: block controls are rebuilt only when `ShownDiff` changes instance (keeping the vertical offset; an inline new-remark editor with typed text is moved to the block that now holds `MapRow` of its row, or kept on its old block position when the line is gone, and submits against its captured `ShownDiff`); thread lists and marks are refreshed in place on every `ThreadsByRow` publication whose `ForDiff` is the shown diff, without recreating block controls or an open editor. An ordered item whose engine output restarts numbering at 1 is rendered with its own number kept (builder's choice of how, checked by the test above).
- [ ] **Step 4: Run** the new tests and `ProjectScreenRemarksHeadlessTests` — Expected: pass.
- [ ] **Step 5: Commit** — `feat: rendered Markdown mode with block remarks`.

### Task 6: Link resolution

**Files:**
- Create: `src/Pact.Presentation/Services/MarkdownLinks.cs`
- Test: `tests/Pact.Presentation.Tests/Services/MarkdownLinksTests.cs`

**Interfaces:**
- Produces (namespace `Pact.Presentation.Services`):
  - `public abstract record MarkdownLinkTarget;` with `ProjectDocument(string RelativePath, string? Heading)`, `SameDocumentHeading(string Heading)`, `Web(Uri Uri)`, `Refused(string Notice)`.
  - `public static class MarkdownLinks { public static MarkdownLinkTarget Resolve(string projectRoot, string documentRelativePath, string href); }` — `Refused.Notice` is `Outside the project: <href>` or `Not found: <href>`.

- [ ] **Step 1: Write the failing tests**

```csharp
[TestCase("other.md", "docs/a/other.md")]  [TestCase("../b.md", "docs/b.md")]
[TestCase("/README.md", "README.md")]       [TestCase("my%20file.md", "docs/a/my file.md")]
[TestCase("sub\\c.md", "docs/a/sub/c.md")]
public void A_relative_link_to_a_project_markdown_file_resolves_to_its_path(string href, string expected) // doc docs/a/x.md; files exist in a temp root
[Test] public void A_heading_after_a_file_is_kept()                 // "other.md#Some-Part" → Heading "Some-Part"
[Test] public void A_bare_heading_stays_in_the_document()
[Test] public void A_link_climbing_out_of_the_project_is_refused()  // "../../../x.md" → Refused("Outside the project: ../../../x.md")
[Test] public void A_missing_file_is_refused()                      // Refused("Not found: nope.md")
[Test] public void An_http_link_is_web()
[Test] public void A_link_to_a_non_markdown_project_file_is_refused() // Refused("Not a Markdown file: x.cs")  (my call: only .md opens in the pane)
```

- [ ] **Step 2: Run; see them fail.**
- [ ] **Step 3: Implement** — Unescape `%xx`, normalise `\` to `/`, split off `#`, combine with the document's folder (or the root for a leading `/`), `Path.GetFullPath`, check it stays under the root (`Path.GetRelativePath` not starting with `..`) and exists.
- [ ] **Step 4: Run** — Expected: pass.
- [ ] **Step 5: Commit** — `feat: resolve Markdown links against the project`.

### Task 7: Links and images in every Markdown preview (B1, B2)

**Files:**
- Create: `src/Pact.App.Avalonia/Views/MarkdownPreviewHook.cs`
- Modify: `src/Pact.App.Avalonia/Views/NotesPaneView.axaml(.cs)` (notice `LinkNoticeText` above `Preview`), `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml.cs` (review logs), `RenderedMarkdownView` (Task 5)
- Test: `tests/Pact.App.Avalonia.Tests/Views/MarkdownPreviewHeadlessTests.cs`

**Interfaces:**
- Consumes: `MarkdownLinks.Resolve` (Task 6); `DocsAndNotesWorkspaceViewModel.OpenDocumentAsync(string, CancellationToken)`.
- Produces: `internal static class MarkdownPreviewHook { public static void Attach(MarkdownScrollViewer viewer, Func<(string ProjectRoot, string DocumentRelativePath)?> document, Action<MarkdownLinkTarget> navigate); public static void Apply(Markdown.Avalonia.Markdown engine, string projectRoot, string documentRelativePath, Action<MarkdownLinkTarget> navigate); }` — sets `Engine.HyperlinkCommand` to a command that resolves and calls `navigate` (never `Process.Start` for anything but `Web`), and `AssetPathRoot` to the document's folder whenever the shown document changes.

- [ ] **Step 1: Reproduce B1 before fixing** — Under superpowers:systematic-debugging, find why a click on `other.md` freezes today (the engine's default command is `DefaultHyperlinkCommand`, which runs `Process.Start(url, UseShellExecute)` on the UI thread with a relative path). Record the cause in the commit message. Write the headless test that clicks a relative link in the Docs preview and asserts `Workspace.SelectedDocument` becomes `other.md` within 2 s.
- [ ] **Step 2: Write the other failing tests**

```csharp
[AvaloniaTest] public async Task A_link_to_a_file_outside_the_project_shows_a_notice_and_stays()
[AvaloniaTest] public async Task A_heading_link_scrolls_the_preview_to_the_heading()
[AvaloniaTest] public async Task A_relative_image_is_drawn_from_the_documents_folder()   // a tiny png next to the doc → an Image with a non-null Source
[AvaloniaTest] public async Task A_missing_image_leaves_its_alt_text_and_the_rest_renders()
[AvaloniaTest] public async Task A_review_log_link_opens_the_document_in_the_docs_pane()
```

- [ ] **Step 3: Run; see them fail.**
- [ ] **Step 4: Implement** — Attach the hook to `NotesPaneView.Preview`, `ProjectScreenView.ReviewLogPreview` and the Rendered engine. Navigation: `ProjectDocument` → `OpenDocumentAsync` then scroll to the heading if any; `SameDocumentHeading` → scroll; `Web` → `Process.Start` with `UseShellExecute` off the UI thread; `Refused` → show the notice for 5 s (`LinkNoticeText`; in Rendered, a notice in the diff header). Heading scroll: find the heading control whose text slug (lowercase, spaces → `-`, punctuation dropped) matches and bring it into view. For `http(s)` images, verify the engine's bitmap loader fetches them; if it does not, set a loader that downloads off the UI thread.
- [ ] **Step 5: Run** the new tests and `NotesAndActionsHeadlessTests` — Expected: pass.
- [ ] **Step 6: Commit** — `fix: open Markdown links inside Pact and draw images in previews`.

### Task 8: One Docs tab

**Files:**
- Modify: `src/Pact.Infrastructure/Documents/ProjectMarkdownCatalog.cs` (one list `All`, review logs still excluded), `src/Pact.Presentation/ViewModels/DocsAndNotesWorkspaceViewModel.cs` (drop `DocsAndNotesSection.Common`, `CommonTree`; `DocsTree` = `MarkdownTreeNode.Build(catalog.All)`; activating Docs selects `README.md`), `src/Pact.App.Avalonia/Views/NotesPaneView.axaml(.cs)` (remove `CommonTab`)
- Test: `tests/Pact.Infrastructure.Tests/Documents/ProjectMarkdownCatalogTests.cs`, `tests/Pact.Presentation.Tests/ViewModels/DocsAndNotesWorkspaceViewModelTests.cs`, `tests/Pact.App.Avalonia.Tests/Views/NotesAndActionsHeadlessTests.cs`

**Interfaces:**
- Produces: `ProjectMarkdownCatalog(IReadOnlyList<ProjectMarkdownFileEntry> All)`; `DocsAndNotesSection { Notes, Docs }`.

- [ ] **Step 1: Update and add failing tests**

```csharp
[Test] public void The_catalog_lists_root_and_docs_files_together_without_review_logs()
[Test] public async Task The_docs_tree_shows_folders_before_files_and_selects_the_root_readme()
	// tree top level: ["docs" (folder), "src" (folder), "AGENTS.md", "README.md"]; SelectedDocument README.md
[Test] public async Task Without_a_root_readme_nothing_is_selected()
[Test] public async Task Opening_a_root_document_selects_it_in_the_docs_tree()   // OpenDocumentAsync("AGENTS.md")
[AvaloniaTest] public async Task The_docs_pane_has_two_tabs()                   // replaces DocsAndNotesPaneShowsThreePrimaryTabsAndNoNestedStrip
```

- [ ] **Step 2: Run; see them fail.**
- [ ] **Step 3: Implement** as in Files.
- [ ] **Step 4: Run** Infrastructure, Presentation and `NotesAndActionsHeadlessTests` — Expected: pass.
- [ ] **Step 5: Commit** — `feat: merge the Common and Docs tabs into one tree`.

### Finish

- [ ] Full suites: Core, Infrastructure, Presentation, Updater, App (the App project's runtimeconfig caps the heap; run it alone). Expected: all green.
- [ ] Publish `artifacts/local-diag` for a manual look; send the branch range to codex review.
