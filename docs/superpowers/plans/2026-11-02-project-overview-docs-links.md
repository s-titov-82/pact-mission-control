# Project Overview, Docs and Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task.

**Goal:** Make the project screen the home for Notes, Docs, installed IDEs, active reviews and durable agent-added links.

**Architecture:** Preserve project document ownership and persistence boundaries. Build project links and installed-IDE services first, then consume both in the existing project screen; terminals continue running in the background.

**Tech Stack:** .NET 10, Avalonia 12, Windows, NUnit, existing loopback MCP.

**Spec:** `docs/superpowers/specs/2026-11-02-project-overview-docs-links-design.md`

## Global Constraints

- Tabs: **Overview · Docs · Changes · Review logs**. Notes occupy the lower two thirds of Overview; upper content scrolls when constrained.
- Remove the Docs & Notes tree row and project Notes visibility button. Preserve Notes storage identity, autosave, conflicts and legacy selection migration to Overview.
- No duplicate Changes summary, remarks count, event history, remote CI polling, new headless agent API or orchestrator tools.
- Project links belong to the project, survive session close/restart, accept HTTP(S) only and never fetch remote metadata. ROOT has no project ownership.
- Existing web templates and GitLab/TeamCity settings retain their behavior.
- Follow AGENTS.md, README.md, docs/architecture.md and docs/agent-onboarding.md. XML-document public APIs; use apply_patch for manual edits.
- Preserve unrelated changes. Work in the existing authorized checkout; **no commits** until requested. Do not commit this plan/spec alone.
- Prefix shell commands with rtk. One heavy command at a time. Build: `rtk dotnet build <project> --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`; Avalonia additionally `-p:UsedAvaloniaProducts=`. Restore only as needed with `--disable-parallel`. Tests: `rtk proxy dotnet test <project> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2` (headless UI workers 1). Read run-tests skill before running tests.
- Never replace/restart the installed Pact; native checks use isolated profiles. No subagents inside implementation agents.

## Review Focus

1. A project settings save racing an MCP link update must preserve both writes (Task 1 persistence test).
2. Duplicate normalized URLs at the 50-link cap must update rather than evict/refuse (Task 1 rule test).
3. Slow IDE discovery completing after project switch must launch only the current root (Tasks 2 launch-plan and 3 selection tests).
4. A Markdown load completing after pause/owner switch must not replace the current document/surface (Task 3 gated async test).
5. A legacy Notes selection or a new Docs selection must restore without starting stopped terminals or losing Notes text (Task 3 restoration tests).

### Task 1: Durable project links and ordinary-session MCP

**Files:** Create `src/Pact.Core/Projects/ProjectLink.cs`; modify `ProjectRecord.cs`, `WorkspaceViewModel.cs`, `MainWindowViewModel.cs`, `Services/AgentControl/{SessionTools,AgentControlDispatcher,IAgentControlHost}.cs`, App `Controllers/AgentControlHost.cs`, Infrastructure `AgentControl/PactSkillPublisher.cs` and its published guide source. Add focused Core/Presentation/Infrastructure tests alongside existing project, host, tool and storage fixtures.

**Interfaces:**
- Produces: `ProjectLink(string Title, string Url, string Kind, DateTimeOffset AddedAt)` and `ProjectRecord.Links` as an immutable-read collection, default empty. Kinds `ci`, `merge_request`, `issue`, `other`.
- Produces: observable current project links through `WorkspaceViewModel.Links` (record reconciliation, not a second persistence store).
- Produces: MainWindowViewModel `AddProjectLinkAsync(string projectId, string title, string url, string? kind, CancellationToken cancellationToken)` and `RemoveProjectLinkAsync(string projectId, string url, CancellationToken cancellationToken)`; return the saved link from add. These mutate latest persisted project state through existing serialized persistence and reconcile runtime only after success.
- Produces MCP `pact_add_project_link(title,url,kind?)` and `pact_list_project_links()` on ordinary session catalog only; derive owner from token, reject ROOT/unknown/stale owner. List returns title/url/kind/addedAt. Add does not open browser.

- [x] Write behavioral tests for HTTP(S) normalization, invalid URL/kind/title, newest-first upsert, same-kind distinct links, 50-link refusal without eviction, upsert at cap, backward JSON default, durable add/remove and concurrent project settings preservation.
- [x] Write MCP behavioral tests for own-project add/list and ROOT/unknown rejection; verify no orchestrator catalog extension and no browser side effect. Run focused tests RED before adding implementation.
- [x] Implement the above through existing host/dispatcher boundaries and ProjectPersistenceCoordinator. Keep normalization in a Core project-link rule helper; trim title, accept 1–200 characters, normalize absolute URI via Uri.AbsoluteUri, reject userinfo. Kind exact validated strings; omitted means other. Add a focused partial file for MainWindowViewModel only if needed to keep this new responsibility readable, without moving unrelated code.
- [x] Update published guidance: agents add created/received MR, issue and build URLs with title and kind; no status claims. Add guide publication contract coverage.
- [x] Run focused rule, persistence, MCP and guidance tests GREEN; run diff whitespace check on touched paths. Report commands/results/files and RED/GREEN evidence; no commit.

### Task 2: Installed IDE discovery and safe launch

**Files:** Create `src/Pact.Core/Platform/InstalledIde.cs`, `IInstalledIdeService.cs`; create `src/Pact.Infrastructure/Platform/WindowsInstalledIdeService.cs` and focused discovery/launch-plan tests in `tests/Pact.Infrastructure.Tests/Platform/`. Use small internal helper files only where native icon/discovery ownership requires separation. No project-screen edits yet.

**Interfaces:**
- Produces: `InstalledIde(string Id, string Name, string ExecutablePath, byte[]? IconPng)` and `IInstalledIdeService.DiscoverAsync(bool refresh, CancellationToken cancellationToken)` returning `Task<IReadOnlyList<InstalledIde>>`.
- Produces: `IInstalledIdeService.OpenAsync(InstalledIde ide, string projectRoot, CancellationToken cancellationToken)` returning Task. Structured process arguments only; launching failure is surfaced to caller.
- Service instance app-wide, discovery async and cached, refresh explicit. Native discovery never starts an IDE. Icon bytes are PNG, in-memory only; missing icon keeps name.

- [x] Write RED tests using bounded discovery inputs/seams for per-user and machine installs, PATH and JetBrains Toolbox, missing/inaccessible entries, path deduplication and distinguishable installations; test root paths containing spaces and multiple solutions.
- [x] Implement catalog VS Code, Visual Studio, Rider, Cursor, Windsurf, Zed, IntelliJ IDEA, WebStorm, PyCharm. Probe known registry locations (HKCU/HKLM and architecture views), PATH and known directories, including Toolbox; no whole-disk scan. Reuse existing executable/process launch conventions, avoid adding a general command framework. Check official primary documentation for CLI behavior where uncertain.
- [x] Extract installed EXE icon to PNG with a narrowly owned/disposed native Windows boundary; no remote assets or new runtime dependency merely for logos. Test icon failure remains a usable entry.
- [x] Launch folders for supporting IDEs; Visual Studio: use the only root-level .sln/.slnx if exactly one, otherwise use its documented folder-open argument (never arbitrary first solution). Validate current root exists. Serialize cache refresh so late earlier discovery cannot replace a newer refresh result.
- [x] Run focused tests GREEN, then a real read-only discovery smoke check on this Windows machine (do not launch IDEs). Report detected app names and icon availability, API differences if any, commands/results and RED/GREEN evidence; no commit.

### Task 3: Unified project screen, migration and integration

**Files:** Modify Presentation `ProjectScreenViewModel.cs`, `DocsAndNotesWorkspaceViewModel.cs`, `WorkspaceViewModel.cs`, `MainWindowViewModel.cs`; App `Views/ProjectScreenView*`, `NotesPaneView*`, `CenterPaneVisibility.cs`, project tree templates/buttons and `Controllers/AvaloniaMainShellController*`, `PresentedSurface.cs`, factory/DI; Core restoration selection record and corresponding restoration builder/restorer if necessary. Update relevant headless/controller/restoration/Markdown tests, README.md/README.ru.md and onboarding/product docs to reflect actual behavior.

**Interfaces:**
- Consumes Task 1 project links and add/remove methods, Task 2 app-wide IInstalledIdeService.
- Produces `ProjectScreenTab.Docs`; ProjectScreen owns its existing DocsAndNotesWorkspace and Notes document, reusing editor/preview controls and stores. No separate Notes presented surface or visible Notes tree item.
- Extend existing startup selection with optional project screen tab and Markdown relative path metadata, backward compatible defaults. Capture actual presented project surface; legacy Notes item selects Overview. New selection metadata must participate in checkpoint equality/invalidation.

- [x] Read csharp-refactoring skill for ownership/move work. Write RED tests: tab order, Notes always present with retained body despite hidden legacy flag; project tree no pseudo row; MCP/open-document and relative links open Docs; legacy Notes selection maps Overview; selected Docs file/tab persists across restore; Changes auto-first behavior stays intact.
- [x] Integrate Notes in Overview lower 2/3 using existing editor/autosave/conflict model. Expose Docs-only mode in reused workspace/view, preserving document identity and tree/heading navigation. Update all selection/close/pause/disposal and generation guards; flush correct document before leaving owner. Add gated async load-after-switch/pause regression tests.
- [x] Build upper 1/3 with name/path and Git address/branch (origin, else first fetch remote; unavailable data cleared), active reviews from current relay (participants/current turn, participant/log navigation), IDE icon buttons + refresh, folder-open and project-link open/copy/remove. No duplicate Changes/remarks summary. Use existing Pact browser navigation and reuse same normalized URL under the project; keep failure visible and changes durable.
- [x] Wire app-wide IDE service and icon adapters with deterministic disposal. Discovery starts on first Overview without blocking UI; late results cannot target old root. Add UI tests for correct project launch after switch and link open/remove routing.
- [x] Preserve live terminals and restoration checkpoint ordering/unread semantics. Test owner close/pause, ROOT rejection, Notes conflict/autosave, legacy selection and Docs restoration. Update public docs and approved spec only when implementation choices clarify existing scope.
- [x] Build relevant test projects with resource limits and run focused suites GREEN, then Presentation and relevant App fixtures in bounded partitions (known full-App NUnit report OOM). Run diff check. Parent coordinates final accumulated-diff review, diagnostic publish and isolated native gate where relevant. Report exact validation limits; no commit.
