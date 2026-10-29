# Agent files (stage 2) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** An agent adds a link to any existing file with a new MCP tool `pact_add_file_link`; the links of the shown session are listed in the right column under the Changes entry, and a click shows the file read-only in a panel lying over the right 60% of the terminal.

**Architecture:** Links live on the session record (`SessionRecord.FileLinks`), so they are saved in `projects.json` / the ROOT tabs file with the session, come back after a restart and go away with the session. The tool resolves and checks the path in `AgentControlDispatcher` and stores the link through `MainWindowViewModel.UpdateSessionAsync`, the same path every other session edit takes. The right column gets an `AgentFilesViewModel` that follows the shown session; the panel is a top-level `Popup` (like `SelectionActionsPopover`) placed over `CenterPane`, because the terminal is a native WebView that ordinary Avalonia overlays cannot cover. File reading reuses one text reader shared with the Changes tab, so missing, binary and too-large files get the same treatment.

**Tech Stack:** .NET 10, Avalonia 12.1, AvaloniaEdit 12 (`SourceEditor`), ClassIsland.Markdown.Avalonia 12 (`MarkdownScrollViewer` + `MarkdownPreviewHook`), NUnit 4 + Shouldly, Avalonia.Headless.NUnit.

**Spec:** `docs/superpowers/specs/2026-10-26-project-screen-changes-design.md`, section 2 (Agent files) and section 1 "Right column" item 5.

## Global Constraints

- Builds: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`; tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 3m -- NUnit.NumberOfTestWorkers=2`. One heavy build or test run at a time. Kill a leftover `testhost` after App runs.
- Commits on `main`, never pushed; message trailer `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Tool name `pact_add_file_link(path, title?)`. `path` is absolute, or relative to the session's working directory. Any location is allowed. The file must exist when the link is added; the refusal says so.
- A link belongs to the calling session (project or ROOT), is stored with the session record, survives a restart, and is dropped with the session. Adding a path already in the list moves it to the top. At most 20 links per session; the oldest is dropped.
- The tool never opens the panel.
- Right column row: file name, and a short location: `scratchpad`, `Temp`, `project`, or the parent folder's name. A small ✕ removes the link.
- Panel: over the right part of the terminal, 60% of the center pane's width, full height; header with file name and full path and a ✕; Esc closes it; the terminal is not resized. Read-only monospaced text; Markdown (`.md`) rendered with the Docs preview. The file is read when the panel opens. Missing file → `File not found` in the panel, the link stays. Binary → `Binary file, not shown`; over 2 MB → `File is too large to show` (the Changes tab's texts).

## Review Focus

1. The same file added with different spellings (`C:/x/a.md`, `c:\x\A.md`, `.\a.md` from `C:\x`) is one link, moved to the top — not two rows. Pinned in Task 1 (`Add_treats_the_same_file_spelled_differently_as_one_link`) and Task 3 (`A_relative_path_is_resolved_against_the_working_directory`).
2. A link added while its session is not shown, or is paused, is still there when the session is shown and after a restart. Pinned in Task 3 (`A_link_survives_reloading_the_projects`) and Task 5 (`Agent_files_follow_the_shown_session`).
3. A file deleted, or replaced by a 50 MB log or a binary, between adding and clicking: the panel shows the placeholder and never reads a huge file into the editor or blocks the UI thread. Pinned in Task 2 and Task 4 (`Loading_reads_off_the_calling_thread_and_reports_placeholders`).
4. The center pane changes size (window resize, column splitter) or the window moves while the panel is open: the panel stays over the right 60% of the pane. Pinned in Task 6 (`The_panel_follows_the_center_pane_size`).
5. The user switches to another tab, closes the session, or pauses the project while the panel is open: the panel closes and does not stay floating over another surface. Pinned in Task 5 (`Changing_the_shown_surface_closes_the_agent_file`).

---

## File map

- Create `src/Pact.Core/Sessions/AgentFileLink.cs` — the link record and the list rules (dedupe, top, cap).
- Create `src/Pact.Core/Sessions/AgentFileLocation.cs` — the short location label.
- Modify `src/Pact.Core/Sessions/SessionRecord.cs` — `FileLinks`.
- Create `src/Pact.Infrastructure/Files/TextFileReader.cs` — reads a file as text or says why not; `GitWorkingTreeReader` uses it.
- Modify `src/Pact.Presentation/Services/AgentControl/{IAgentControlHost,AgentControlDispatcher,SessionTools}.cs`, `src/Pact.App.Avalonia/Controllers/AgentControlHost.cs`, `src/Pact.Infrastructure/AgentControl/PactSkills/PactMcpSkill.md`.
- Modify `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs` — add and remove a session's file link.
- Create `src/Pact.Presentation/ViewModels/AgentFilesViewModel.cs` — the right-column list of one session.
- Create `src/Pact.Presentation/ViewModels/AgentFileDocument.cs` — what the panel shows for one file.
- Modify `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController*.cs` — `CurrentAgentFiles`, `ShownAgentFile`, open/close/remove.
- Create `src/Pact.App.Avalonia/Views/AgentFilePanel.axaml(.cs)` — the popup panel.
- Modify `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml(.cs)`, `src/Pact.App.Avalonia/Views/MainWindow.axaml(.cs)`.

---

### Task 1: Link model and location label (Core)

**Files:**
- Create: `src/Pact.Core/Sessions/AgentFileLink.cs`, `src/Pact.Core/Sessions/AgentFileLocation.cs`
- Modify: `src/Pact.Core/Sessions/SessionRecord.cs`
- Test: `tests/Pact.Core.Tests/Sessions/AgentFileLinksTests.cs`, `tests/Pact.Core.Tests/Sessions/AgentFileLocationTests.cs`

**Interfaces:**
- Produces, namespace `Pact.Core.Sessions`:
  - `public sealed record AgentFileLink(string Path, string? Title, DateTimeOffset AddedAt);` — `Path` is absolute and already normalized by the caller (`System.IO.Path.GetFullPath`).
  - `public static class AgentFileLinks { public const int MaxPerSession = 20; public static IReadOnlyList<AgentFileLink> Add(IReadOnlyList<AgentFileLink> links, AgentFileLink link); public static IReadOnlyList<AgentFileLink> Remove(IReadOnlyList<AgentFileLink> links, string path); public static bool SamePath(string left, string right); }` — newest first; `SamePath` compares `GetFullPath` of both with trailing separators trimmed, `OrdinalIgnoreCase`.
  - `public static class AgentFileLocation { public static string Describe(string path, string? projectRoot, IReadOnlyList<string> tempRoots); }` — first match wins: a path segment equal to `scratchpad` (ignore case) → `"scratchpad"`; inside `projectRoot` → `"project"`; inside any of `tempRoots` → `"Temp"`; otherwise the parent folder's name (the drive root `C:\` for a file at a drive root).
  - `SessionRecord` gains `public IReadOnlyList<AgentFileLink> FileLinks { get; init; } = [];` with an XML doc in the style of `ProjectRecord`'s init properties.

- [ ] **Step 1: Write the failing tests**

`AgentFileLinksTests`:
- `Add_puts_the_new_link_first` — `[a]` + b → `[b, a]`.
- `Add_treats_the_same_file_spelled_differently_as_one_link` — `[C:\x\a.md, C:\x\b.md]` + `c:/X/A.md` with title "Plan" → two links; the first is the new link as given (`Path` `c:/X/A.md`, `Title` "Plan"), the second `C:\x\b.md`.
- `Add_drops_the_oldest_past_twenty` — 20 links + 1 → 21st call keeps 20, the new one first, the oldest (last) gone.
- `Remove_drops_the_link_with_that_path_in_any_spelling` and `Remove_of_an_unknown_path_returns_the_same_list` (`ShouldBeSameAs`).

`AgentFileLocationTests` (use `[TestCase]`, Windows paths):
- `C:\Users\u\AppData\Local\Temp\claude\p\s1\scratchpad\run.ps1`, root `D:\proj`, temp `[C:\Users\u\AppData\Local\Temp\]` → `scratchpad`.
- `D:\proj\docs\plan.md`, root `D:\proj` → `project`; `D:\project2\a.md`, root `D:\proj` → `project2` (a sibling with the same prefix is not inside).
- `C:\Users\u\AppData\Local\Temp\out.txt` → `Temp`; `C:\tools\bin\x.cs`, root null → `bin`; `C:\x.txt` → `C:\`.

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`
Expected: build FAIL, `AgentFileLink` / `AgentFileLocation` not found.

- [ ] **Step 3: Implement the three types and `SessionRecord.FileLinks`**

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/Pact.Core.Tests --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 3m --filter "FullyQualifiedName~AgentFile" -- NUnit.NumberOfTestWorkers=2`
Expected: PASS.

- [ ] **Step 5: Persistence round trip**

Add `Sessions_keep_their_file_links` to `tests/Pact.Infrastructure.Tests/Storage/JsonProjectStoreTests.cs` (a project whose session has two links saves and loads with both, in order, with titles and `AddedAt`) and `A_root_session_keeps_its_file_links` to `JsonRootTabsStoreTests.cs`; also `A_file_written_before_links_loads_with_none` (a `projects.json` text without `fileLinks` loads with `FileLinks` empty, not null). Build, run `dotnet test tests/Pact.Infrastructure.Tests ... --filter "FullyQualifiedName~FileLinks|FullyQualifiedName~before_links"`. Expected: PASS (System.Text.Json reads the init property; if the store uses a hand-written converter, extend it).

- [ ] **Step 6: Commit**

```bash
git add src/Pact.Core/Sessions tests/Pact.Core.Tests/Sessions tests/Pact.Infrastructure.Tests/Storage
git commit -m "feat: store agent file links with the session"
```

### Task 2: One text-file reader (Infrastructure)

**Files:**
- Create: `src/Pact.Infrastructure/Files/TextFileReader.cs`
- Modify: `src/Pact.Infrastructure/Git/GitWorkingTreeReader.cs:132-161`
- Test: `tests/Pact.Infrastructure.Tests/Files/TextFileReaderTests.cs`

**Interfaces:**
- Produces, namespace `Pact.Infrastructure.Files`:
  - `public enum TextFileState { Text, Missing, TooLarge, Binary }`
  - `public sealed record TextFileContent(TextFileState State, string? Text);`
  - `public static class TextFileReader { public const long MaxBytes = 2 * 1024 * 1024; public static Task<TextFileContent> ReadAsync(string path, CancellationToken cancellationToken); }` — the rules now in `GitWorkingTreeReader.ReadTextAsync`: missing → `Missing`; length over `MaxBytes` → `TooLarge` without reading; a zero byte in the first 8000 → `Binary`; else UTF-8 with BOM detection. `IOException` / `UnauthorizedAccessException` while reading → `Missing`. A directory path → `Missing`.
- `GitWorkingTreeReader.MaxFileBytes` stays and equals `TextFileReader.MaxBytes`; `ReadTextAsync` maps the states to its placeholders (`Missing` → `new FileDiff(null, [])`, `TooLarge` → `FileDiff.TooLarge`, `Binary` → `FileDiff.Binary`).

- [ ] **Step 1: Write the failing tests** — `Reads_text_and_drops_the_bom`, `A_missing_file_or_a_folder_is_missing`, `A_file_with_a_zero_byte_is_binary`, `A_file_over_two_megabytes_is_too_large` (write `MaxBytes + 1` bytes of `a`), in a `TemporaryDirectory`.
- [ ] **Step 2: Build** — Expected: FAIL, `TextFileReader` not found.
- [ ] **Step 3: Implement, and make `GitWorkingTreeReader` call it.**
- [ ] **Step 4: Run** `dotnet test tests/Pact.Infrastructure.Tests ... --filter "FullyQualifiedName~TextFileReader|FullyQualifiedName~GitWorkingTreeReader"` — Expected: PASS (the existing `GitWorkingTreeReader` tests prove the refactor).
- [ ] **Step 5: Commit** — `refactor: share the text file reader between the diff and agent files`.

### Task 3: `pact_add_file_link` and storing links

**Files:**
- Modify: `src/Pact.Presentation/Services/AgentControl/IAgentControlHost.cs`, `AgentControlDispatcher.cs`, `SessionTools.cs`; `src/Pact.Core/AgentControl/` (request record next to `OpenDocumentRequest`); `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs`; `src/Pact.Presentation/Services/ProjectPersistenceCoordinator.cs`; `src/Pact.App.Avalonia/Controllers/AgentControlHost.cs`; `src/Pact.Infrastructure/AgentControl/PactSkills/PactMcpSkill.md`
- Test: `tests/Pact.Presentation.Tests/Services/AgentControl/AgentControlDispatcherTests.cs`, `SessionToolsTests.cs`, `tests/Pact.Presentation.Tests/ViewModels/MainWindowViewModelTests.cs`, `tests/Pact.Infrastructure.Tests/AgentControl/PactSkillPublisherTests.cs`

**Interfaces:**
- Consumes: Task 1 `AgentFileLink`, `AgentFileLinks`, `SessionRecord.FileLinks`.
- Produces:
  - `public sealed record AddFileLinkRequest(string Path, string? Title);` beside `OpenDocumentRequest`.
  - `IAgentControlHost`: `Task<string?> GetSessionWorkingDirectoryAsync(string sessionId, CancellationToken cancellationToken);` (null for an unknown session) and `Task AddSessionFileLinkAsync(string sessionId, AgentFileLink link, CancellationToken cancellationToken);`.
  - `AgentControlDispatcher.AddFileLinkAsync(string sessionId, AddFileLinkRequest request, CancellationToken cancellationToken) -> Task<AgentControlResult>`; injected `TimeProvider` is not needed — `AddedAt` comes from `DateTimeOffset.UtcNow` like the other session edits.
  - `MainWindowViewModel.AddSessionFileLinkAsync(string sessionId, AgentFileLink link, CancellationToken cancellationToken) -> Task` and `RemoveSessionFileLinkAsync(string sessionId, string path, CancellationToken cancellationToken) -> Task`, both through the private `UpdateSessionAsync` (works for ROOT and project sessions; the session's `LastActiveAt` is not touched — a link is not user activity).
  - `ProjectPersistenceCoordinator.UpdateSessionAsync` today sets the project's `LastActiveAt` to the session's unconditionally, so a link edit on an older session would rewind the project. Change it to the later of the two (`project.LastActiveAt` vs `updatedSession.LastActiveAt`); every existing caller moves the session's time forward, so their result is unchanged.

Dispatcher rules: unknown session → `unknown-session`; blank path → `invalid-argument`; the path is trimmed of surrounding quotes, `~\`/`~/` expands to the user profile, a relative path combines with the working directory, then `GetFullPath`; an invalid path (`ArgumentException`, `NotSupportedException`, `PathTooLongException`) → `invalid-argument`; a directory → `not-a-file` "'<full>' is a folder; link a file."; missing → `file-not-found` "No file at '<full>'. Write the file before linking it."; success payload `{"path":"<full>"}` and status text `Agent action: file linked`.

- [ ] **Step 1: Write the failing tests**

`AgentControlDispatcherTests` (fake host gains `WorkingDirectory` and `AddedLinks`): `AddFileLink_stores_an_absolute_path`, `A_relative_path_is_resolved_against_the_working_directory` (`.\out\a.txt` and `out/a.txt` → same full path), `A_missing_file_is_refused_and_says_so` (code `file-not-found`, message contains the full path, nothing stored), `A_folder_is_refused`, `A_root_session_may_add_links`, `An_unknown_session_is_refused`.
`SessionToolsTests`: add `"pact_add_file_link"` to the tool list; `Add_file_link_requires_a_path` (required `["path"]`, `title` optional string).
`MainWindowViewModelTests`: `Adding_a_file_link_saves_it_on_the_session`, `Adding_the_same_file_again_moves_it_to_the_top`, `Removing_a_file_link_saves_the_list_without_it`, `A_link_survives_reloading_the_projects` (new view model over the same store sees it), `A_root_session_keeps_its_links`, `A_link_edit_on_an_older_session_keeps_the_projects_activity_time` (project `LastActiveAt` newer than the session's stays as it was after add and remove).
`PactSkillPublisherTests`: add `"pact_add_file_link"` to the documented names.

- [ ] **Step 2: Build and run** — Expected: FAIL (missing members, then failing asserts).
- [ ] **Step 3: Implement** the request, host members (App `AgentControlHost`: working directory read on the UI dispatcher from `RootTabs.Sessions` / `Workspaces` / `PausedWorkspaces`; add through `_viewModel.AddSessionFileLinkAsync`), dispatcher, view-model methods and the tool. Tool description: "Add a link to a file to this session's list in Pact's right column, so the user can open it read-only over the terminal. Use it for a file you wrote outside the project (scratchpad, Temp) or one the user should read. The file must exist. Does not open it." Schema: `path` "Absolute path, or relative to your working directory.", `title` "Optional short label." Skill guidance: a short `## Files` section after `## Documents` saying the same in one paragraph, and that `pact_open_document` is for project Markdown the user should read now.
- [ ] **Step 4: Run** Presentation tests (`--filter "FullyQualifiedName~AgentControl|FullyQualifiedName~FileLink"`) and Infrastructure `PactSkillPublisherTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `feat: pact_add_file_link stores a file link on the calling session`.

### Task 4: Right-column list and panel document (Presentation)

**Files:**
- Create: `src/Pact.Presentation/ViewModels/AgentFilesViewModel.cs`, `src/Pact.Presentation/ViewModels/AgentFileDocument.cs`
- Test: `tests/Pact.Presentation.Tests/ViewModels/AgentFilesViewModelTests.cs`, `AgentFileDocumentTests.cs`

**Interfaces:**
- Consumes: Task 1 `AgentFileLocation.Describe`, Task 2 `TextFileReader`, Task 3 `MainWindowViewModel.RemoveSessionFileLinkAsync`.
- Produces:
  - `public sealed record AgentFileItem(string Path, string FileName, string Location, string ToolTip);` — `ToolTip` is `Title + "\n" + Path` when a title exists, else `Path`.
  - `public sealed class AgentFilesViewModel : INotifyPropertyChanged, IDisposable` — ctor `(SessionViewModel session, string? projectRoot, IReadOnlyList<string> tempRoots, Func<string, string, CancellationToken, Task> removeAsync)` where `removeAsync(sessionId, path, ct)`; `SessionViewModel Session`; `IReadOnlyList<AgentFileItem> Items` rebuilt (raising `Items` and `HasItems`) when the session raises `Record` and its `FileLinks` changed by reference; `bool HasItems`; `Task RemoveAsync(AgentFileItem item, CancellationToken cancellationToken)`; `static IReadOnlyList<string> DefaultTempRoots` = `Path.GetTempPath()` and `%LOCALAPPDATA%\Temp`, distinct.
  - `public sealed record AgentFileDocument(string FullPath, string FileName, string? Text, string? Placeholder, bool IsMarkdown, string? ProjectRoot, string? ProjectRelativePath)` with `public static Task<AgentFileDocument> LoadAsync(string path, string? projectRoot, CancellationToken cancellationToken)` — reads with `TextFileReader` on the thread pool (`Task.Run`); placeholders exactly `File not found`, `Binary file, not shown`, `File is too large to show`; `IsMarkdown` for `.md` (ignore case) with text; `ProjectRoot`/`ProjectRelativePath` (forward slashes) set only when the file lies inside `projectRoot`, so the preview resolves links like Docs; outside the project both are null. Images never depend on them: the panel always takes the image base from the file's own folder (`Path.GetDirectoryName(FullPath)`).

- [ ] **Step 1: Write the failing tests** — `Items_show_name_and_location_newest_first`, `Items_follow_a_new_record_of_the_session`, `Remove_calls_back_with_the_session_and_path`, `Dispose_stops_following_the_session`; `Loading_reads_off_the_calling_thread_and_reports_placeholders` (missing / binary / over 2 MB give the three texts and `Text` null; a test `SynchronizationContext` that records posts shows the read did not run on it), `A_markdown_file_in_the_project_keeps_its_project_path`, `A_markdown_file_outside_the_project_has_no_project_path`.
- [ ] **Step 2: Build** — Expected: FAIL, types not found.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test tests/Pact.Presentation.Tests ... --filter "FullyQualifiedName~AgentFile"` — Expected: PASS.
- [ ] **Step 5: Commit** — `feat: view models for the agent file list and panel`.

### Task 5: Controller

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.AgentFiles.cs` (partial)
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` (call the new update from wherever `UpdateCurrentChanges()` is called after a surface change; dispose on shutdown)
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`

**Interfaces:**
- Consumes: Task 4 types; Task 3 `RemoveSessionFileLinkAsync`; existing `OpenProjectDocumentAsync(projectId, path, heading, ct)`.
- Produces on `AvaloniaMainShellController` (each raises `PropertyChanged`):
  - `AgentFilesViewModel? CurrentAgentFiles` — for `PresentedSurface.Terminal` and `PresentedSurface.PausedSession` of any session (ROOT or project; project root from its workspace), else null; one instance per shown session, disposed when replaced.
  - `AgentFileDocument? ShownAgentFile`; `string? ShownAgentFileProjectId` (the workspace id of the session the file was opened from, null for ROOT).
  - `Task OpenAgentFileAsync(AgentFileItem item, CancellationToken cancellationToken)` — loads with `AgentFileDocument.LoadAsync` and sets `ShownAgentFile` only if no later open, close or surface change happened meanwhile: a private `_agentFileGeneration` counter is incremented by every open, `CloseAgentFile` and surface change, and a load publishes only when the counter still holds the value it took at its start (the selection-version pattern of `ProjectChangesViewModel`). The loader is injectable for tests: an internal `Func<string, string?, CancellationToken, Task<AgentFileDocument>> AgentFileLoader` defaulting to `AgentFileDocument.LoadAsync`.
  - `void CloseAgentFile()`.
  - Any surface change closes the panel (`ShownAgentFile = null`), including a session closing or a project pausing.

- [ ] **Step 1: Write the failing tests** — `Agent_files_follow_the_shown_session` (a link added to session B while A is shown appears only after B is selected; selecting the project screen gives null), `Opening_an_agent_file_shows_its_document`, `Changing_the_shown_surface_closes_the_agent_file`, `A_slower_earlier_open_does_not_replace_a_later_one` (gated loader: open A, open B, finish B then A → B shown), `A_load_finishing_after_close_does_not_reopen_the_panel`, `A_load_finishing_after_switching_away_and_back_does_not_open_the_panel`, `Removing_a_link_updates_the_list_and_leaves_an_open_panel_open` (the file stays readable until the user closes it).
- [ ] **Step 2: Build/run** — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** `dotnet test tests/Pact.App.Avalonia.Tests ... --filter "FullyQualifiedName~AvaloniaMainShellControllerTests"` — Expected: PASS.
- [ ] **Step 5: Commit** — `feat: the shell follows the shown session's agent files`.

### Task 6: Right column list and the panel (views)

**Files:**
- Create: `src/Pact.App.Avalonia/Views/AgentFilePanel.axaml`, `AgentFilePanel.axaml.cs`
- Modify: `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml(.cs)`, `src/Pact.App.Avalonia/Views/MainWindow.axaml(.cs)`
- Test: `tests/Pact.App.Avalonia.Tests/Views/RightActionsPanelHeadlessTests.cs`, `tests/Pact.App.Avalonia.Tests/Views/AgentFilePanelHeadlessTests.cs`, `MainWindowHeadlessTests.cs`

**Interfaces:**
- Consumes: Task 4/5 types and controller members; `SourceEditor` (`IsReadOnly = true`, `SetSyntaxFor(path)`); `MarkdownPreviewHook.Attach/SetDocument/Prepare/OpenWeb/ScrollToHeading`.
- Produces:
  - `RightActionsPanel.AgentFiles` (`AgentFilesViewModel?`), events `AgentFileRequested` and `AgentFileRemoveRequested` (`EventHandler<AgentFileItem>`). Layout: a new `AgentFilesSection` right under `ChangesSection` in grid row 0 (wrap both in one `StackPanel`), title `Agent files`, an `ItemsControl` of rows (file name; location muted, 11px; ✕ button `compact-icon` with tooltip `Remove link`), inside a `ScrollViewer` with `MaxHeight="240"`; hidden when `HasItems` is false.
  - `AgentFilePanel : UserControl` with `Popup x:Name="FilePopup"` (`IsLightDismissEnabled="False"`, `Placement="AnchorAndGravity"`, `PlacementAnchor="TopLeft"`, `PlacementGravity="BottomRight"`, `PlacementConstraintAdjustment="None"`, like `SelectionActionsPopover`); `void Show(AgentFileDocument document, Control pane)`, `void Hide()`, `bool IsOpen`, events `CloseRequested` and `LinkFollowed` (`EventHandler<MarkdownLinkTarget>`). Placement: `PlacementTarget = pane`, `HorizontalOffset = pane.Bounds.Width * 0.4`, `VerticalOffset = 0`, size `0.6 × width` by full height; recomputed on `pane.BoundsProperty` changes and on the window's `PositionChanged`. Content: header (`FileName` semibold, `FullPath` muted and selectable, ✕ with tooltip `Close`), then one of: `SourceEditor` read-only (text), `MarkdownScrollViewer` (Markdown; link hook attached once, its document callback returning the current document's `ProjectRoot`/`ProjectRelativePath` or null; on every `Show`, `MarkdownPreviewHook.SetDocument(viewer, folder, fileName)` with the file's own folder so relative images draw inside and outside the project; text through `MarkdownPreviewHook.Prepare` with that folder), a centered muted `TextBlock` with the placeholder; a muted link-notice line under the header for `RefusedLink`. Esc on the panel raises `CloseRequested`.
  - `MainWindow`: hosts `AgentFilePanel x:Name="AgentFile"` next to `SelectionActions`; binds `RightActions.AgentFiles` to `CurrentAgentFiles`; `AgentFileRequested` → `OpenAgentFileAsync` through the window's observed task group; `AgentFileRemoveRequested` → `CurrentAgentFiles.RemoveAsync`; `ShownAgentFile` → `AgentFile.Show(doc, CenterPane)` / `Hide()`; `CloseRequested` → `CloseAgentFile()`; `LinkFollowed`: `WebLink` → `OpenWeb`, `HeadingLink` → scroll in the panel, `ProjectDocumentLink` → `CloseAgentFile()` then `OpenProjectDocumentAsync(ShownAgentFileProjectId, path, heading, ct)` (only when the project id is known), `RefusedLink` → the notice.

- [ ] **Step 1: Write the failing tests**
  - `RightActionsPanelHeadlessTests`: `Agent_files_show_under_the_changes_entry` (two items: names, locations, section visible; null or empty → hidden), `Clicking_an_agent_file_raises_the_request`, `The_cross_raises_removal_and_not_the_open_request`.
  - `AgentFilePanelHeadlessTests`: `A_text_file_shows_read_only_with_its_path`, `A_markdown_file_is_rendered`, `Relative_images_draw_from_the_files_folder_outside_the_project` (a scratchpad-like folder with `pic.png` next to `notes.md` → the viewer's `AssetPathRoot` is that folder and the image stays as written; then showing a second file from another folder moves `AssetPathRoot` there), `A_placeholder_shows_instead_of_text`, `The_panel_follows_the_center_pane_size` (pane 1000×600 → popup offset 400, size 600×600; pane resized to 800×500 → 320, 480×500), `Escape_and_the_cross_request_closing`, `A_refused_link_shows_its_notice`.
  - `MainWindowHeadlessTests`: `Shown_agent_file_opens_the_panel_over_the_center_pane_and_closing_hides_it`.
- [ ] **Step 2: Build/run** — Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** the three test classes, then the full App suite: `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false --blame-hang-timeout 3m -- NUnit.NumberOfTestWorkers=2` — Expected: PASS (a lone headless-initialisation "different thread" flake reruns green alone; note it).
- [ ] **Step 5: Docs and commit** — mark stage 2 done in `docs/backlog/2026-10-25-next-steps.md` with the date and plan path; `git commit -m "feat: agent file list in the right column and a read-only panel over the terminal"`.

### Finish

- Full suites: Core, Infrastructure, Presentation, App (the commands from Global Constraints).
- Publish the local diagnostic build as for stage 3 and tell the user what to check by hand: the panel over the live WebView terminal (does it draw above it, follow a window move, stay above after Alt+Tab back), Esc after clicking into the panel, and a link added by a real agent.
