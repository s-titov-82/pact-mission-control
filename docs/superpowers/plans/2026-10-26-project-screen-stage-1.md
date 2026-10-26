# Project screen and Changes tab (stage 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A project screen (Overview, Changes, Review logs) in the center pane, a read-only diff of the working tree that refreshes itself, review logs moved out of Docs, and a reworked right column with a "Changes · N" entry.

**Architecture:** Core gets a unified-diff model and parser and a staged mark on status entries. Infrastructure gets a working-tree reader over `IGitCliRunner` (status, numstat, per-file diff, untracked files read directly) and a file-change signal. Presentation gets `ProjectChangesViewModel` (throttled reconciliation driven by a `TimeProvider`) and `ProjectScreenViewModel`. The App adds `PresentedSurface.Project`, a `ProjectScreenView`, routing (tree click, review logs, right-column entry) and the right-column rework.

**Tech Stack:** .NET 10, Avalonia 11 (headless tests), NUnit + Shouldly, Markdown.Avalonia, git CLI.

**Spec:** `docs/superpowers/specs/2026-10-26-project-screen-changes-design.md` — this plan covers stage 1 only (sections "Project screen and the Changes tab" and "Right column"; the Agent files slot in the right column is not built yet).

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`. One heavy build at a time. Read the build output: a failed build leaves old binaries and the tests then lie.
- Tests: `dotnet test tests/<Project> --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 5m --filter "<filter>" -- NUnit.NumberOfTestWorkers=2`.
- Analyzers fail the build: IDE0061 (local functions need block bodies), CA1826, CS1591 (public members need XML docs), IDE0005 (unused usings). Code style: tabs, file-scoped namespaces, `field` keyword properties as in the surrounding code.
- Every git call of the Changes feature: `--no-optional-locks` and `-c core.quotePath=false`; status with `--untracked-files=all`.
- Read-only: nothing in this stage stages, discards, or commits.
- Refresh runs only while the project screen or the right-column entry of that project is shown; at most one status run per project per `MinRefreshInterval` (2 s); periodic reconciliation every `ReconcileInterval` (5 s).
- Copy (UI text, English like the rest of the UI): tab headers `Overview`, `Changes N` (N omitted when 0), `Review logs`; entry `Changes · N`, sub-line `+A −R`; clean tree `No uncommitted changes`; binary `Binary file, not shown`; too large `File is too large to show`; cut diff link `Show all (N more lines)`.
- Commits on `main` are allowed; never push. Trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`.
- Logs never carry file contents.

## Review Focus

1. Paths with Cyrillic letters or spaces: git C-quotes them unless `core.quotePath=false`, which would make the diff call miss the file. Pinned in Task 3 (`Cyrillic_and_spaced_paths_come_back_verbatim`).
2. A new folder of untracked files: plain status shows only `dir/`. Pinned in Task 3 (`Files_inside_an_untracked_folder_are_listed_one_by_one`).
3. `git reset --soft HEAD~1` on a clean tree with no file event: must appear on the next reconciliation tick. Pinned in Task 3 (real git) and Task 4 (tick without signal).
4. Repository without commits (unborn HEAD): diff against the empty tree, not a git error. Pinned in Task 3.
5. The open file disappears from the list (committed meanwhile): selection moves to the neighbour, not to nothing or to a stale diff. Pinned in Task 4.

---

### Task 1: Staged mark on status entries

**Files:**
- Modify: `src/Pact.Core/Git/GitStatusSnapshot.cs` (record `GitFileEntry`)
- Modify: `src/Pact.Core/Git/GitStatusParser.cs` (`AddOrdinaryEntry`, `AddRenamedEntry`)
- Test: `tests/Pact.Core.Tests/Git/GitStatusParserTests.cs`

**Interfaces:**
- Produces: `public sealed record GitFileEntry(string Path, string? OriginalPath, GitChangeKind Kind, bool IsStaged = false)` — `IsStaged` is true when the porcelain v2 `X` (index) column of a `1`/`2` line is not `.`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public void Entries_changed_in_the_index_are_marked_staged()
{
	var snapshot = GitStatusParser.Parse(
		"1 M. N... 100644 100644 100644 a b src/a.cs\n" +
		"1 .M N... 100644 100644 100644 a b src/b.cs\n" +
		"1 MM N... 100644 100644 100644 a b src/c.cs\n" +
		"? new.txt\n");

	snapshot.Files.Single(f => f.Path == "src/a.cs").IsStaged.ShouldBeTrue();
	snapshot.Files.Single(f => f.Path == "src/b.cs").IsStaged.ShouldBeFalse();
	snapshot.Files.Single(f => f.Path == "src/c.cs").IsStaged.ShouldBeTrue();
	snapshot.Files.Single(f => f.Path == "new.txt").IsStaged.ShouldBeFalse();
}

[Test]
public void A_staged_rename_is_marked_staged()
{
	var snapshot = GitStatusParser.Parse("2 R. N... 100644 100644 100644 a b R100 new.cs\told.cs\n");

	snapshot.Files.Single().IsStaged.ShouldBeTrue();
}
```

- [ ] **Step 2: Run, expect FAIL** (compile error: `IsStaged` missing)

Run: build, then `dotnet test tests/Pact.Core.Tests ... --filter "FullyQualifiedName~GitStatusParserTests"`

- [ ] **Step 3: Add `IsStaged` (doc comment on the param) and set it from `line[2] != '.'` in both entry parsers.**

- [ ] **Step 4: Run, expect PASS** (whole `GitStatusParserTests` class).

- [ ] **Step 5: Commit** — `feat: mark git status entries changed in the index`

---

### Task 2: Unified diff model and parser

**Files:**
- Create: `src/Pact.Core/Git/FileDiff.cs` (model)
- Create: `src/Pact.Core/Git/UnifiedDiffParser.cs`
- Test: `tests/Pact.Core.Tests/Git/UnifiedDiffParserTests.cs`

**Interfaces:**
- Produces:
  - `public enum DiffLineKind { Context, Added, Removed }`
  - `public sealed record DiffLine(DiffLineKind Kind, int? OldNumber, int? NewNumber, string Text)`
  - `public sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, string Header, IReadOnlyList<DiffLine> Lines)`
  - `public sealed record FileDiff(string? Placeholder, IReadOnlyList<DiffHunk> Hunks)` with `public static FileDiff Binary { get; }` (Placeholder `"Binary file, not shown"`), `public static FileDiff TooLarge { get; }` (`"File is too large to show"`), `public static FileDiff AllAdded(string text)` (one hunk, every line Added, new numbers from 1; empty text → no hunks), and `public int LineCount` (sum of hunk lines).
  - `public static class UnifiedDiffParser { public static FileDiff Parse(string gitDiffOutput); }` — skips the `diff --git`/`index`/`---`/`+++` header, returns `FileDiff.Binary` on a `Binary files … differ` line, ignores `\ No newline at end of file`, numbers lines from the `@@ -a,b +c,d @@` header (count defaults to 1 when omitted), keeps the header text after the second `@@` in `Header`. Empty input → no hunks, no placeholder. CRLF input is accepted and `\r` is not kept in `Text`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test]
public void Lines_are_numbered_from_the_hunk_header()
{
	var diff = UnifiedDiffParser.Parse(
		"diff --git a/x.cs b/x.cs\nindex 1..2 100644\n--- a/x.cs\n+++ b/x.cs\n" +
		"@@ -10,3 +10,3 @@ class X\n a\n-b\n+B\n c\n");

	var hunk = diff.Hunks.ShouldHaveSingleItem();
	hunk.Header.ShouldBe("class X");
	hunk.Lines.Select(l => (l.Kind, l.OldNumber, l.NewNumber, l.Text)).ShouldBe([
		(DiffLineKind.Context, 10, 10, "a"),
		(DiffLineKind.Removed, 11, null, "b"),
		(DiffLineKind.Added, null, 11, "B"),
		(DiffLineKind.Context, 12, 12, "c")]);
}

[Test]
public void A_hunk_header_without_counts_means_one_line()  // "@@ -1 +1 @@\n-a\n+b\n" → OldCount 1, NewCount 1

[Test]
public void Binary_output_becomes_the_binary_placeholder()  // "Binary files a/p.png and b/p.png differ\n" → Placeholder "Binary file, not shown", no hunks

[Test]
public void The_no_newline_marker_is_not_a_line()  // "@@ -1 +1 @@\n-a\n\\ No newline at end of file\n+a\n" → 2 lines

[Test]
public void Crlf_output_keeps_no_carriage_returns()  // "@@ -1 +1 @@\r\n-a\r\n+b\r\n" → texts "a","b"

[Test]
public void Several_hunks_are_kept_in_order()  // two @@ blocks → Hunks.Count 2, second starts at its own numbers

[Test]
public void All_added_numbers_every_line_from_one()  // FileDiff.AllAdded("x\ny\n") → one hunk, lines (Added,null,1,"x"),(Added,null,2,"y"); AllAdded("") → no hunks
```

- [ ] **Step 2: Run, expect FAIL** (types missing).
- [ ] **Step 3: Implement the model and the parser.**
- [ ] **Step 4: Run `--filter "FullyQualifiedName~UnifiedDiffParserTests"`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: parse unified git diffs into hunks and numbered lines`

---

### Task 3: Working-tree reader over git

**Files:**
- Create: `src/Pact.Infrastructure/Git/GitWorkingTreeReader.cs`
- Test: `tests/Pact.Infrastructure.Tests/Git/GitWorkingTreeReaderTests.cs` (real git in a temp repo, `[NonParallelizable]`, same `RunGitAsync`/identity flags as `GitRepositoryWorkflowTests`)

**Interfaces:**
- Consumes: `IGitCliRunner`, `GitStatusParser`, `GitFileEntry.IsStaged` (Task 1), `UnifiedDiffParser`, `FileDiff` (Task 2).
- Produces:
  - `public sealed record WorkingTreeChanges(string? HeadCommit, string Branch, IReadOnlyList<GitFileEntry> Files, int AddedLines, int RemovedLines)` — `HeadCommit` null when HEAD is unborn; `AddedLines`/`RemovedLines` from `git diff --numstat <base>` over tracked files (binary `-` rows count 0; untracked files are not counted — my call, they are still in `Files`).
  - `public interface IGitWorkingTreeReader { Task<WorkingTreeChanges?> ReadChangesAsync(string root, CancellationToken ct); Task<FileDiff> ReadDiffAsync(string root, GitFileEntry entry, string? headCommit, CancellationToken ct); }` — `ReadChangesAsync` returns null when status fails (not a repository, git missing).
  - `public sealed class GitWorkingTreeReader(IGitCliRunner runner) : IGitWorkingTreeReader`
  - `public const string EmptyTreeId = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";` — the base when `headCommit` is null.
  - `public const long MaxFileBytes = 2 * 1024 * 1024;`
- Diff rules: Untracked → read the file directly: larger than `MaxFileBytes` → `FileDiff.TooLarge`; a NUL byte in the first 8000 bytes → `FileDiff.Binary`; otherwise `FileDiff.AllAdded(text)`. Everything else → `git -c core.quotePath=false --no-optional-locks diff --no-color --no-ext-diff -M <base> -- [OriginalPath] Path`, parsed with `UnifiedDiffParser`. Conflicted entries go through the same git diff.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] Modified_staged_and_untracked_files_are_read_with_line_totals()
// commit a.txt "1\n2\n"; change to "1\nX\n"; add+stage b.txt; create c.txt
// → Files: a.txt Modified !IsStaged, b.txt Added IsStaged, c.txt Untracked; AddedLines 2 (X + b's line), RemovedLines 1; HeadCommit = rev-parse HEAD; Branch "pact-test"

[Test] Files_inside_an_untracked_folder_are_listed_one_by_one
// new folder n/ with n/x.txt and n/y.txt → Files contains "n/x.txt" and "n/y.txt", not "n/"

[Test] Cyrillic_and_spaced_paths_come_back_verbatim
// untracked "док/план работ.md" → Files path "док/план работ.md"; ReadDiffAsync of it → AllAdded with its lines
// tracked modified "папка/файл.txt" → ReadDiffAsync returns hunks (not empty)

[Test] A_soft_reset_turns_a_clean_tree_into_staged_changes
// two commits; clean tree reads Files empty; git reset --soft HEAD~1 → next ReadChangesAsync lists the file as staged; HeadCommit is the first commit

[Test] A_repository_without_commits_is_compared_with_the_empty_tree
// init, write+stage a.txt, no commit → HeadCommit null, a.txt Added; ReadDiffAsync → AllAdded-like hunk with "+" lines (via git diff against EmptyTreeId)

[Test] A_modified_file_diff_is_parsed_into_hunks
// committed "1\n2\n3\n", now "1\nTWO\n3\n" → one hunk with Removed "2" and Added "TWO"

[Test] A_renamed_file_is_diffed_against_its_old_path
// git mv old.txt new.txt and edit one line → entry Kind Modified (the parser maps R to Modified; keep that) with OriginalPath "old.txt"; diff has the edited line

[Test] Untracked_binary_and_oversized_files_get_placeholders
// file with a 0 byte → FileDiff.Binary; file of MaxFileBytes + 1 → FileDiff.TooLarge

[Test] A_folder_that_is_not_a_repository_reads_as_null
```

- [ ] **Step 2: Run, expect FAIL** (reader missing).
- [ ] **Step 3: Implement `GitWorkingTreeReader`.** Status: `-c core.quotePath=false --no-optional-locks status --porcelain=v2 --branch --untracked-files=all`. Head: `rev-parse --verify -q HEAD` (non-zero → null). Numstat: `-c core.quotePath=false --no-optional-locks diff --numstat <base>`.
- [ ] **Step 4: Run `--filter "FullyQualifiedName~GitWorkingTreeReaderTests"`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: read working-tree changes and per-file diffs through git`

---

### Task 4: Changes view model with throttled reconciliation

**Files:**
- Create: `src/Pact.Presentation/ViewModels/ProjectChangesViewModel.cs`
- Create: `src/Pact.Infrastructure/Git/ProjectFileChangeSignal.cs`
- Test: `tests/Pact.Presentation.Tests/ViewModels/ProjectChangesViewModelTests.cs` (fake `IGitWorkingTreeReader` with gated calls, `ManualTimeProvider` from `tests/TestSupport`)
- Test: `tests/Pact.Infrastructure.Tests/Git/ProjectFileChangeSignalTests.cs`

**Interfaces:**
- Consumes: `IGitWorkingTreeReader`, `WorkingTreeChanges`, `FileDiff` (Tasks 2–3).
- Produces:
  - `public sealed class ProjectChangesViewModel : INotifyPropertyChanged, IDisposable`, ctor `(string projectRoot, IGitWorkingTreeReader reader, TimeProvider time, Action<Action> postToUi)`. The shell passes `_uiTaskDispatcher.Post` (`IUiTaskDispatcher` is internal to the App, so the view model takes the delegate).
  - `public static readonly TimeSpan MinRefreshInterval = TimeSpan.FromSeconds(2)`, `ReconcileInterval = TimeSpan.FromSeconds(5)`, `public const int MaxShownLines = 3000`.
  - `IDisposable Observe()` — while at least one observation is open, a periodic timer on `time` calls refresh every `ReconcileInterval`; the first observation refreshes at once. Disposing the last stops the timer.
  - `void RequestRefresh()` — brings the next refresh forward; runs at once if `MinRefreshInterval` has passed since the last start, otherwise once when it has. Ignored with no observers. At most one refresh in flight; a request during a refresh schedules exactly one more.
  - State: `IReadOnlyList<ChangedFileViewModel> Files` (`Path`, `FileName`, `Folder`, `StatusLetter`: `R` when `OriginalPath` is set, else M (Modified), A (Added), D (Deleted), U (Untracked), C (Conflicted) — `GitChangeKind` itself is not extended, `IsStaged`, `Entry`), `ChangedFileViewModel? SelectedFile`, `FileDiff? SelectedDiff`, `IReadOnlyList<DiffRowViewModel> DiffRows` (hunk header rows and line rows, cut at `MaxShownLines`), `int HiddenLineCount`, `void ShowAllLines()`, `int FileCount`, `int AddedLines`, `int RemovedLines`, `bool IsAvailable` (false when the reader returned null), `string EntryTitle` (`Changes · N` or `No uncommitted changes`), `string EntrySubtitle` (`+A −R`, empty when clean), `string TabHeader` (`Changes N` / `Changes`).
  - `Task SelectFileAsync(ChangedFileViewModel? file, CancellationToken ct)` reads that file's diff.
  - Refresh semantics: new `Files` replace old ones; selection is kept by path; if the selected path is gone, select the file now at the old index (clamped), or none when empty; the selected file's diff is re-read on every refresh in which its entry is still present (cheap, keeps the diff live). The view keeps its scroll offset because rows are replaced, not the control (view concern, Task 7).
  - `public sealed class ProjectFileChangeSignal : IDisposable`, ctor `(string projectRoot, Action changed)` — a `FileSystemWatcher` over the root (subdirectories on), calling `changed` for paths where `IsRelevant(relativePath)` holds. `public static bool IsRelevant(string relativePath)`: false for anything under `.git/` except `.git/HEAD` and `.git/index`; true otherwise. Watcher errors are swallowed (reconciliation covers them).

- [ ] **Step 1: Write the failing tests** (view-model tests drive time with `ManualTimeProvider.Advance` and complete reader calls through gates; no `Task.Delay`)

```csharp
[Test] Observing_reads_at_once_and_then_every_reconcile_interval()
// Observe() → 1 read; Advance(ReconcileInterval) → 2 reads; dispose observation; Advance(ReconcileInterval*3) → still 2

[Test] A_baseline_change_without_any_signal_shows_on_the_next_tick()
// reader returns empty, then (no RequestRefresh) a staged file; Advance(ReconcileInterval) → FileCount 1

[Test] Requests_inside_the_minimum_interval_collapse_into_one_read()
// after a read at t0, three RequestRefresh at t0+0.5s → no read yet; Advance to t0+2s → exactly one more read

[Test] A_request_during_a_read_schedules_exactly_one_more()
// gate first read; RequestRefresh twice; release → after MinRefreshInterval exactly one more read

[Test] Requests_without_observers_read_nothing()

[Test] Selection_follows_the_path_and_falls_to_the_neighbour_when_the_file_leaves()
// files [a,b,c], select b; next read [a,c] → SelectedFile c; next read [] → SelectedFile null, DiffRows empty

[Test] The_selected_diff_is_reread_when_the_list_refreshes()

[Test] Long_diffs_are_cut_and_can_be_shown_whole()
// diff of MaxShownLines + 10 lines → HiddenLineCount 10; ShowAllLines → HiddenLineCount 0

[Test] Entry_texts_follow_the_counts()
// 5 files +84 −12 → EntryTitle "Changes · 5", EntrySubtitle "+84 −12", TabHeader "Changes 5"; empty → "No uncommitted changes", "", "Changes"

[Test] A_folder_that_is_not_a_repository_is_unavailable()  // reader null → IsAvailable false

[Test] A_timer_refresh_publishes_only_through_the_ui_poster()
// postToUi queues actions instead of running them; a timer tick completes a read with one file
// → FileCount still 0 and no PropertyChanged raised; run the queued actions → FileCount 1 and PropertyChanged raised
```

```csharp
// ProjectFileChangeSignalTests
[TestCase(".git/HEAD", true)]
[TestCase(".git/index", true)]
[TestCase(".git/objects/ab/cdef", false)]
[TestCase(".git/refs/heads/main", false)]
[TestCase("src/a.cs", true)]
[TestCase(".pact/reviews/x.md", true)]
public void Relevance_ignores_git_internals_except_head_and_index(string path, bool relevant)
```

- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement both classes.** The timer comes from `time.CreateTimer`. Git reads run off the UI thread; every change to UI-facing state (`Files`, selection, `DiffRows`, counts, entry texts) and every `PropertyChanged` happens inside the `postToUi` callback — never on the timer or read continuation. Nothing marshals a child view model's events for it: `RightActionsPanel` and the screen view handle `PropertyChanged` synchronously.
- [ ] **Step 4: Run both test classes, expect PASS.**
- [ ] **Step 5: Commit** — `feat: keep a project's change list fresh with throttled reconciliation`

---

### Task 5: Project screen view model with review logs

**Files:**
- Create: `src/Pact.Presentation/ViewModels/ProjectScreenViewModel.cs`
- Create: `src/Pact.Infrastructure/Documents/ReviewLogFiles.cs`
- Test: `tests/Pact.Presentation.Tests/ViewModels/ProjectScreenViewModelTests.cs`
- Test: `tests/Pact.Infrastructure.Tests/Documents/ReviewLogFilesTests.cs`

**Interfaces:**
- Consumes: `WorkspaceViewModel` (`Id`, `Name`, `RootPath`, `IsGitRepository`), `ProjectChangesViewModel` (Task 4), `IProjectMarkdownFileStore.LoadAsync`.
- Produces:
  - `public enum ProjectScreenTab { Overview, Changes, ReviewLogs }`
  - `public static class ReviewLogFiles { public const string Folder = ".pact/reviews"; public static IReadOnlyList<string> List(string projectRoot); }` — project-relative paths (`.pact/reviews/<name>.md`, forward slashes) of the `.md` files directly in the folder, newest first by file name descending (log names start with a sortable `yyyy-MM-dd-HHmm` timestamp, e.g. `2026-10-24-1400-code-review-4ad5d08e.md`), empty when the folder is missing.
  - `public sealed class ProjectScreenViewModel : INotifyPropertyChanged`, ctor `(WorkspaceViewModel workspace, ProjectChangesViewModel? changes, IProjectMarkdownFileStore fileStore)`; `changes` is null for a non-git project.
  - `ProjectScreenTab SelectedTab`, `void SelectTab(ProjectScreenTab tab)` (Changes is ignored when `changes` is null), `bool HasChanges`.
  - Overview: `string Name`, `string RootPath`, `string Branch` (from the last `WorkingTreeChanges`, empty for non-git).
  - Review logs: `IReadOnlyList<string> ReviewLogs` (re-listed on `SelectTab(ReviewLogs)` and on `OpenReviewLogAsync`), `string? SelectedReviewLog`, `string ReviewLogText`, `Task SelectReviewLogAsync(string? relativePath, CancellationToken ct)`, `Task<bool> OpenReviewLogAsync(string relativePath, CancellationToken ct)` — selects the tab and the log; false when the path is not in the list. Selecting the tab with nothing selected selects the newest log.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] Review_logs_are_listed_newest_first_and_ignore_subfolders_and_other_files()  // ReviewLogFilesTests
[Test] A_missing_reviews_folder_lists_nothing()

[Test] Opening_a_review_log_selects_the_tab_and_loads_its_text()
[Test] Selecting_the_review_logs_tab_opens_the_newest_log()
[Test] Opening_an_unknown_log_reports_false_and_changes_nothing()
[Test] A_project_without_git_has_no_changes_tab()  // HasChanges false; SelectTab(Changes) leaves Overview
[Test] Overview_shows_name_root_and_branch()
```

- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run both classes, expect PASS.**
- [ ] **Step 5: Commit** — `feat: add the project screen view model with review logs`

---

### Task 6: Review logs leave the Docs pane

**Files:**
- Modify: `src/Pact.Presentation/ViewModels/DocsAndNotesWorkspaceViewModel.cs` (remove `DocsAndNotesSection.Reviews`, `ReviewsTree`, `ReviewsPrefix` routing in `OpenDocumentAsync`, the Reviews branch of section activation)
- Modify: `src/Pact.App.Avalonia/Views/NotesPaneView.axaml(.cs)` (remove `ReviewsTab` and its handler/check)
- Modify: `src/Pact.Infrastructure/Documents/ProjectMarkdownCatalog.cs` (drop the `Reviews` group from the record; keep `.pact/reviews` files out of Common)
- Test: `tests/Pact.Presentation.Tests/ViewModels/DocsAndNotesWorkspaceViewModelTests.cs` (delete the three Reviews tests), `tests/Pact.Infrastructure.Tests/Documents/ProjectMarkdownCatalogTests.cs` (replace `Scan_puts_review_logs_in_their_own_group_newest_first`), `tests/Pact.App.Avalonia.Tests/Views/NotesAndActionsHeadlessTests.cs` if it names `ReviewsTab`.

**Interfaces:**
- Produces: `ProjectMarkdownCatalog(Common, Docs)`; Docs pane sections Notes, Common, Docs.

- [ ] **Step 1: Write the failing test**

```csharp
[Test]
public void Scan_leaves_review_logs_out_of_common_and_docs()
// .pact/reviews/2026-10-26-x.md and README.md → Common contains only README.md, Docs empty
```
Plus in `DocsAndNotesWorkspaceViewModelTests`: `Opening_a_review_log_path_is_not_a_docs_document()` — `OpenDocumentAsync(".pact/reviews/x.md")` returns false (it is no longer listed anywhere in Docs).

- [ ] **Step 2: Run, expect FAIL** (the record still has three groups / the log still opens).
- [ ] **Step 3: Remove the Reviews section and group** (the deleted tests go with it).
- [ ] **Step 4: Build; run `ProjectMarkdownCatalogTests`, `DocsAndNotesWorkspaceViewModelTests`, `NotesAndActionsHeadlessTests`, expect PASS.**
- [ ] **Step 5: Commit** — `refactor: take review logs out of the Docs pane`

Tasks 6 and 7 leave review-log opening briefly unrouted between their commits; Task 7 restores it. Do not publish in between.

---

### Task 7: Project surface, routing and the screen view

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/PresentedSurface.cs` (add `Project`)
- Modify: `src/Pact.App.Avalonia/Views/CenterPaneVisibility.cs` (add `Project` flag)
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
- Create: `src/Pact.App.Avalonia/Views/ProjectScreenView.axaml(.cs)`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml(.cs)` (host the view in `CenterPane`, visibility, wiring)
- Modify: composition where `GitCliRunner` is created for the shell, to pass a `GitWorkingTreeReader` (follow how `_gitCliRunner` reaches the controller; `ShellControllerTestBuilder` gets the same knob)
- Test: `tests/Pact.App.Avalonia.Tests/Views/CenterPaneVisibilityTests.cs`, `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`, new `tests/Pact.App.Avalonia.Tests/Views/ProjectScreenViewHeadlessTests.cs`

**Interfaces:**
- Consumes: Tasks 4–6.
- Produces:
  - `public sealed record Project(ProjectScreenViewModel Screen) : PresentedSurface;`
  - `CenterPaneVisibility` gains `bool Project` (exactly one pane visible, as today).
  - Controller: `Task OpenProjectScreenAsync(WorkspaceViewModel workspace, ProjectScreenTab tab, CancellationToken ct = default)`; `ProjectScreenViewModel? CurrentProjectScreen` (from the surface); `ProjectChangesViewModel? CurrentChanges` — the changes of the project owning what is shown (session → workspace whose `Sessions` contains it; web page → `WebPages`; notes → the note's project; project screen → its workspace), raised with `OnPropertyChanged` when it changes. Screens and changes view models are cached per project id. When a project is paused (`PauseWorkspaceAsync`) or closed (`CloseWorkspaceAsync`): first, if the presented surface is that project's screen, present the existing fallback (`RestoreSelectedHostAsync` or `Present(new PresentedSurface.None())` — whatever those methods use today when the shown item goes away; neither currently replaces the surface for a project screen, so this must be added explicitly); then end the observation, dispose the signal and the view models, and raise `CurrentChanges` so the right column lets go of it.
  - Routing:
    - `SelectItemAsync` with a `WorkspaceViewModel` (a user click on the project row) → `OpenProjectScreenAsync(workspace, Overview)` after the existing `SelectWorkspaceAsync`. The git flyout keeps calling `SelectWorkspaceAsync` only.
    - `OpenProjectDocumentAsync(projectId, relativePath)` with a path under `ReviewLogFiles.Folder + "/"` → `OpenProjectScreenAsync(workspace, ReviewLogs)` + `screen.OpenReviewLogAsync(path)`; other paths keep the Docs route. This covers the relay's end-of-review log, the badge click and `pact_open_document`.
  - The current changes view model is observed (`Observe()`) while `CurrentChanges` is non-null or the project screen is on the Changes tab, and `RequestRefresh()` is wired from a `ProjectFileChangeSignal` for that project and from window activation (`MainWindow` `Activated`).
  - View: tab strip of `ToggleButton`s `OverviewTab`, `ChangesTab` (content `TabHeader`, hidden when `!HasChanges`), `ReviewLogsTab`; Overview panel (name, root path, branch); Changes panel (`ChangedFilesList` with status letter, file name, muted folder, a small `staged` mark; `DiffRows` in an `ItemsControl` inside a `ScrollViewer` named `DiffScroller`, monospaced, gutter with old/new numbers, added/removed tinted with the existing success/danger brushes; placeholder text when `SelectedDiff.Placeholder` is set; `ShowAllButton` with `Show all (N more lines)`); Review logs panel (`ReviewLogList` + `md:MarkdownScrollViewer` named `ReviewLogPreview`, `SelectionEnabled="True"`).

- [ ] **Step 1: Write the failing tests**

```csharp
// CenterPaneVisibilityTests
[Test] The_project_screen_is_the_only_visible_pane()

// AvaloniaMainShellControllerTests (use ShellControllerTestBuilder with a fake IGitWorkingTreeReader)
[Test] Clicking_a_project_row_opens_its_screen_on_overview()
[Test] Opening_the_git_popup_does_not_open_the_project_screen()
[Test] A_review_log_path_opens_the_review_logs_tab_with_that_log()
[Test] Another_document_path_still_opens_docs()
[Test] Current_changes_follow_the_project_of_the_shown_session()
[Test] Pausing_a_project_without_sessions_while_its_screen_is_shown_leaves_the_screen()
// project with no sessions; OpenProjectScreenAsync; PauseWorkspaceAsync → PresentedSurface is not Project, CurrentChanges null, the reader is no longer called on later ticks
[Test] Closing_a_project_while_its_screen_is_shown_leaves_the_screen()

// ProjectScreenViewHeadlessTests
[Test] Selecting_a_changed_file_shows_its_numbered_lines()
[Test] A_binary_file_shows_the_placeholder()
[Test] The_review_logs_tab_renders_the_selected_log()
```

- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement surface, controller routing, view and window wiring.** Keep `HiddenFocus` release logic: the project screen has no WebView, so showing it hides the terminal pane exactly like Notes does.
- [ ] **Step 4: Build; run the three classes plus `MainWindowPaneVisibilityHeadlessTests`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: open a project screen with overview, changes and review logs`

---

### Task 8: Right column rework with the Changes entry

**Files:**
- Modify: `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml(.cs)`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml(.cs)` (pass `CurrentChanges`, open the Changes tab on the entry click)
- Test: `tests/Pact.App.Avalonia.Tests/Views/NotesAndActionsHeadlessTests.cs` (or a new `RightActionsPanelHeadlessTests.cs`)

**Interfaces:**
- Consumes: `ProjectChangesViewModel.EntryTitle/EntrySubtitle/IsAvailable`, `OpenProjectScreenAsync(..., ProjectScreenTab.Changes)`.
- Produces: `RightActionsPanel.Changes { get; set; }` (`ProjectChangesViewModel?`), `event EventHandler? ChangesEntryRequested`.
- Layout, top to bottom: header row with only the Settings and Restart-and-update buttons (the `Actions` title removed); Documents tree (unchanged, only while Docs is shown); Quick actions; a separator; `ChangesEntry` (a button with `EntryTitle` and `EntrySubtitle`; hidden when `Changes` is null or `!IsAvailable`; when clean it shows `No uncommitted changes` as plain muted text, not a button); free space (the star row); the selected-tab details block (terminal diagnostics); status text. The window keeps the separator and Usage limits below the panel as today.

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] The_actions_title_is_gone_and_the_settings_button_stays()
[Test] The_changes_entry_sits_between_quick_actions_and_the_tab_details()  // compare Bounds.Y of QuickActionsList, ChangesEntry, SelectedTabDetailsSection
[Test] The_changes_entry_shows_counts_and_raises_its_request_on_click()
[Test] A_clean_tree_shows_the_no_changes_line_without_a_button()
[Test] A_project_without_git_shows_no_changes_entry()
```

- [ ] **Step 2: Run, expect FAIL.**
- [ ] **Step 3: Implement the layout and wiring** (`MainWindow`: set `RightActions.Changes` on `CurrentChanges` change; `ChangesEntryRequested` → `OpenProjectScreenAsync(workspace of CurrentChanges, Changes)` through `ObserveEvent`).
- [ ] **Step 4: Build; run the panel tests and `MainWindowLayoutHeadlessTests`, expect PASS.**
- [ ] **Step 5: Commit** — `feat: rework the right column around a changes entry`

---

### Task 9: Full verification and publish

- [ ] **Step 1:** Build, then run every test project (Core, Infrastructure, Presentation, App, Updater) with the global test command, no filter. Expected: all pass; a known flaky test (`StayingHiddenAfterNavigationRequestsNoNativeRepaint`) is rerun alone before being called flaky.
- [ ] **Step 2:** Update `docs/backlog/2026-10-25-next-steps.md` section 2: stage 1 done, stages 2–4 next in the order 4 (agent files), 3 (Markdown mode), 2 (remarks). Commit `docs: record stage 1 of change review as done`.
- [ ] **Step 3:** Publish to `artifacts/local-diag` with the command from the project notes and tell the user what to check live: project row click → Overview; right-column entry → Changes; a soft reset shows within ~5 s; review badge → Review logs.
