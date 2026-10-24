# Review over MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the scenario engine with a review relay over the Pact MCP (visible reviewer tab, turns through `pact_get_review_turn`/`pact_submit_review_turn`, saved log opened in Docs), add `pact_open_document`, and describe every MCP tool in one place.

**Architecture:** A pure turn state machine (`ReviewConversation`, Core) is driven by a Presentation service (`ReviewRelay`) that renders messages, writes the log, and talks to the shell only through `IReviewRelayHost`. MCP tools become `AgentControlTool` objects (Presentation) from which `tools/list` and dispatch are derived; the App router is wiring only. The scenario engine, its UI and the file exchange are deleted.

**Tech Stack:** .NET 10, C#, Avalonia, NUnit + Shouldly, System.Text.Json.Nodes.

**Spec:** `docs/superpowers/specs/2026-10-23-review-over-mcp-design.md`

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`.
- Tests: `dotnet test tests/<proj> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`. One heavy build/test at a time.
- **Every "Run" step builds first**, then tests: the test command uses `--no-build`, so it would run stale binaries otherwise. A RED step for missing types is confirmed by the build failing with `CS0246`/`CS0117`/`CS1061` naming them; a RED step for behaviour is confirmed by the tests failing after a successful build. Build only the affected projects when that is enough, e.g. `dotnet build tests/Pact.Core.Tests --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`.
- No timing-sleep tests; use `ManualTimeProvider`/fakes and explicit signals.
- Terminal writes only through `SessionRuntimeCoordinator.DeliverPromptAsync`.
- The participant is `AgentControlToolCall.SessionId` (token), never an argument.
- Only a short fixed trigger is typed into a terminal; content travels through MCP.
- A trigger whose delivery wrote anything is never typed again.
- `.pact/.gitignore` contains `*`; the repository's own `.gitignore` is never edited.
- Analyzers are on (CA1034/CA1724 forbid public nested types; internal is fine).
- Commits on `main`, no push, trailer `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`. The spec, this plan and the backlog edit go in with Task 0.
- Existing user settings files (`scenarios.json`, `prompt-templates.json`) are never rewritten.

## Review Focus

1. The agent pulls and submits its turn before the trigger delivery finishes — the trigger must be cancelled and drained before the relay moves on, and never arrive afterwards (Task 5 test `Accepting_a_turn_cancels_and_drains_its_trigger`).
2. A retried `pact_submit_review_turn` after the review ended with `approved:true` returns the stored result, not an error (Task 3 test `Repeating_the_final_approval_returns_its_stored_result`).
3. The user's existing `scenarios.json` (file placeholders, completion-marker reviewer instructions) still produces a usable message: previous turn present, marker replaced by `approved` (Task 4 test `User_template_with_file_placeholders_still_carries_previous_turn`).
4. The user closes the reviewer tab while its trigger is pending — review Aborted, no delivery afterwards, author told (Task 5 test `Closing_the_reviewer_aborts_and_tells_the_author`).
5. `pact_open_document` with `..`, an absolute path outside the root, or a junction escaping the root is refused (Task 2 test `Escaping_paths_are_refused`).

---

### Task 0: Instrument today's review launch and reproduce the readiness failure

No behaviour change; diagnosis only, so that Task 6 fixes a known cause.

> **Done (see ledger).** Two causes were found and fixed right away, not in Task 6: readiness now
> accepts a settled empty composer (a fresh agent shows no Done marker), and a terminal created
> hidden starts at the size the host picked for its process. Task 6 keeps both rules.

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/ReviewLaunchService.cs` (trace lines through the shell's `AppendDiagnosticLog`, passed in as `Action<string>`), `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs` (`WaitForSessionReadyAsync`, through the existing `DeliveryTrace` sink: one trace line per outcome with elapsed time, whether initial output arrived, whether any screen state was read, its last verdict/busy/question), `AvaloniaMainShellController.cs` (pass the trace).
- Test: `tests/Pact.Presentation.Tests/Services/SessionRuntimeCoordinatorTests.cs` — `Readiness_wait_traces_its_outcome` (fake facts; never-ready → trace contains `ready=false` and `screenStates=0`).

- [ ] **Step 1: Write the failing test.** **Step 2: Build, run** → FAIL on the missing trace.
- [ ] **Step 3: Implement** the trace lines (no message or screen text, only facts: session id, phase, elapsed ms, counts, verdict names). **Step 4: Build, run** Presentation + App tests → PASS.
- [ ] **Step 5: Commit** with the spec, this plan and `docs/backlog/2026-10-09-development-directions.md`: `chore: trace agent-requested review launch`.
- [ ] **Step 6: Reproduce.** Publish `artifacts/local-diag` from this commit, ask the user to run it and have an agent call `pact_request_review`; read the trace lines in `%APPDATA%/Pact/Logs`. Use superpowers:systematic-debugging until the cause is named; ledger it as `Task 0: Ruling: readiness root cause — <cause> — <where Task 6 fixes it>`. Do not fix it here.

### Task 1: One description per MCP tool

**Files:**
- Create: `src/Pact.Presentation/Services/AgentControl/AgentControlTool.cs`, `src/Pact.Presentation/Services/AgentControl/AgentControlToolArguments.cs`, `src/Pact.Presentation/Services/AgentControl/SessionTools.cs`, `src/Pact.Presentation/Services/AgentControl/OrchestratorTools.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AgentControlToolRouter.cs` (becomes wiring), `src/Pact.Presentation/ViewModels/MainWindowViewModel.cs` (`ScenarioDefinitionsSnapshot`), `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs:243-252`
- Delete: `src/Pact.Infrastructure/AgentControl/AgentControlToolCatalog.cs`, `src/Pact.Infrastructure/AgentControl/OrchestratorToolCatalog.cs`
- Test: move `tests/Pact.Infrastructure.Tests/AgentControl/AgentControlToolCatalogTests.cs` and `OrchestratorToolCatalogTests.cs` to `tests/Pact.Presentation.Tests/Services/AgentControl/SessionToolsTests.cs` / `OrchestratorToolsTests.cs`; add `AgentControlToolSetTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record AgentControlTool(
      string Name, string Description, Func<JsonObject> BuildInputSchema,
      Func<AgentControlToolCall, CancellationToken, Task<AgentControlResultData>> InvokeAsync);
  public sealed class AgentControlToolSet(IReadOnlyList<AgentControlTool> tools) {
      public JsonNode BuildToolsList();            // {"tools":[{name,description,inputSchema}...]}
      public Task<AgentControlResultData> InvokeAsync(AgentControlToolCall call, CancellationToken ct); // unknown → "Unknown tool '<name>'." IsError
  }
  public static class AgentControlToolArguments {   // moved TryGetRequiredString/TryGetOptionalString/TryGetOptionalInt32, same error texts
      public static bool TryGetRequiredString(JsonNode args, string name, out string value, out AgentControlResultData error);
      public static bool TryGetOptionalString(JsonNode args, string name, out string? value, out AgentControlResultData error);
      public static bool TryGetOptionalInt32(JsonNode args, string name, out int? value, out AgentControlResultData error);
  }
  public static class SessionTools { public static AgentControlToolSet Create(AgentControlDispatcher dispatcher,
      Func<IReadOnlyList<ScenarioDefinition>> scenarios, Func<IReadOnlyList<ReviewProfile>> profiles,
      Func<string, Task> reportStatusAsync); }
  public static class OrchestratorTools { public static AgentControlToolSet Create(OrchestratorDispatcher dispatcher); }
  // MainWindowViewModel
  public IReadOnlyList<ScenarioDefinition> ScenarioDefinitionsSnapshot { get; } // immutable array replaced in ReplaceScenarioDefinitions, read from any thread (Volatile)
  ```
- `AgentControlToolRouter` keeps `BuildToolsList(bool orchestrator)` and `InvokeAsync(call, ct)` delegating to the two sets; it no longer takes `IUiTaskDispatcher` for listing.

- [ ] **Step 1: Port the catalog tests** to the new sets unchanged in substance: the five session names, the fifteen orchestrator names, the review enums/required arguments, notes schemas. Add:
  ```csharp
  [Test] public async Task Unknown_tool_is_an_error()          // InvokeAsync(name "pact_nope") → IsError, Text "Unknown tool 'pact_nope'."
  [Test] public async Task Missing_required_argument_names_it()  // pact_append_note {} → IsError, Text "Argument 'text' is required."
  [Test] public void Listing_reads_the_snapshot_without_the_ui() // SessionTools.Create with scenarios func returning [def]; enum contains def.Id
  ```
- [ ] **Step 2: Build** `tests/Pact.Presentation.Tests` → FAIL with `CS0246` for `AgentControlToolSet`/`SessionTools`/`OrchestratorTools`.
- [ ] **Step 3: Implement** the types; each tool's schema, parsing and dispatcher call move verbatim from the catalogs and router, including success/status texts of session tools ("Notes read." / "Agent action: notes read", etc.). Delete the two catalogs and their Infrastructure tests. Router + shell wiring use `ViewModel.ScenarioDefinitionsSnapshot` and `_reviewProfileProvider.Current`.
- [ ] **Step 4: Build the solution, then run** Presentation, Infrastructure and App tests → all PASS.
- [ ] **Step 5: Commit** `refactor: describe each MCP tool in one place`.

### Task 2: `pact_open_document`

**Files:**
- Create: `src/Pact.Core/AgentControl/ProjectDocumentPath.cs`
- Modify: `src/Pact.Core/AgentControl/AgentControlRequests.cs` (`OpenDocumentRequest`), `src/Pact.Presentation/Services/AgentControl/IAgentControlHost.cs`, `AgentControlDispatcher.cs`, `SessionTools.cs`, `src/Pact.Presentation/ViewModels/DocsAndNotesWorkspaceViewModel.cs`, `src/Pact.App.Avalonia/Controllers/AgentControlHost.cs`, `AvaloniaMainShellController.cs`
- Test: `tests/Pact.Core.Tests/AgentControl/ProjectDocumentPathTests.cs`, `tests/Pact.Presentation.Tests/ViewModels/DocsAndNotesWorkspaceViewModelTests.cs`, `AgentControlDispatcherTests.cs`, `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public sealed record OpenDocumentRequest(string Path);
  public static class ProjectDocumentPath {
      // relativePath uses '/', relative to projectRoot; failure codes: "invalid-argument" (blank/not .md), "outside-project", "not-found"
      public static bool TryResolve(string projectRoot, string path, out string relativePath, out AgentControlFailure? failure);
  }
  // IAgentControlHost
  Task<string> GetProjectRootAsync(string projectId, CancellationToken ct);
  Task OpenProjectDocumentAsync(string projectId, string relativePath, CancellationToken ct);
  // AgentControlDispatcher
  public Task<AgentControlResult> OpenDocumentAsync(string sessionId, OpenDocumentRequest request, CancellationToken ct);
  // DocsAndNotesWorkspaceViewModel — selects Docs for "docs/…", Common otherwise; expands ancestors; selects and activates the node; false if not in the tree after RefreshAsync
  public Task<bool> OpenDocumentAsync(string relativePath, CancellationToken ct);
  // AvaloniaMainShellController — ensures the project's Notes tab (ShowNotesTabAsync when closed), SelectNoteAsync, then OpenDocumentAsync
  public Task OpenProjectDocumentAsync(string projectId, string relativePath, CancellationToken ct = default);
  ```
- Tool: `pact_open_document`, description "Open a Markdown file of this session's project in the Docs pane and show it to the user.", argument `path` (required string, "Path relative to the project root, or an absolute path inside it."). Success text "Document opened.", status "Agent action: document opened". ROOT → `owner-not-a-project`.

- [ ] **Step 1: Write failing tests:**
  ```csharp
  [Test] public void Relative_markdown_inside_the_root_resolves()            // "docs/plan.md" → "docs/plan.md"
  [Test] public void Absolute_path_inside_the_root_resolves()
  [TestCase("../x.md")] [TestCase("docs/../../x.md")] public void Escaping_paths_are_refused(string p) // "outside-project"
  [Test] public void Junction_escaping_the_root_is_refused()                 // a reparse-point dir under root pointing outside → "outside-project" (skip with Assert.Ignore if junction creation is unavailable)
  [Test] public void File_link_escaping_the_root_is_refused()                // docs/plan.md is a file symbolic link to a file outside → "outside-project" (Assert.Ignore when symlink creation is not permitted)
  [Test] public void Non_markdown_is_refused()                               // "a.txt" → "invalid-argument"
  [Test] public void Missing_file_is_refused()                               // "not-found"
  // DocsAndNotesWorkspaceViewModelTests
  [Test] public async Task Opening_a_docs_file_selects_the_docs_section_and_the_node()   // SelectedSection==Docs, SelectedNode.RelativePath=="docs/superpowers/plans/p.md", ancestors IsExpanded, ActiveDocument set
  [Test] public async Task Opening_a_common_file_selects_common()
  // AgentControlDispatcherTests
  [Test] public async Task Open_document_from_root_is_refused()               // owner-not-a-project
  [Test] public async Task Open_document_resolves_against_the_project_root()  // host receives ("p1","docs/a.md")
  // AvaloniaMainShellControllerTests
  [Test] public async Task Opening_a_project_document_shows_it_in_notes()    // PresentedSurface is Notes; Workspace.SelectedNode path matches; works when Notes tab was closed
  ```
- [ ] **Step 2: Build** → FAIL with `CS0246`/`CS1061` for the new members.
- [ ] **Step 3: Implement.** `TryResolve`: `Path.GetFullPath(Path.Combine(root, path))`, must stay under root; reject when the file itself or any directory between root and the file is a reparse point (the root itself is trusted, as in `ProjectMarkdownCatalog`); extension `.md` case-insensitive. `ProjectMarkdownCatalog` also skips reparse-point files, so the Docs tree never lists an outside file (add `Catalog_skips_file_links` to `ProjectMarkdownCatalogTests`).
- [ ] **Step 4: Build, then run** Core, Infrastructure, Presentation, App tests → PASS.
- [ ] **Step 5: Commit** `feat: let an agent open a project document in Docs`.

### Task 3: `ReviewConversation` (turn rules)

**Files:**
- Create: `src/Pact.Core/Reviews/ReviewConversation.cs`, `src/Pact.Core/Reviews/ReviewState.cs`, `src/Pact.Core/Reviews/ReviewTurn.cs`
- Test: `tests/Pact.Core.Tests/Reviews/ReviewConversationTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  public enum ReviewState { Running, Completed, MaxIterationsReached, Failed, Aborted }
  public enum ReviewRole { Author, Reviewer }
  public sealed record ReviewTurn(string TurnId, ReviewRole Role, int Iteration);   // TurnId: "<reviewId>-<n>"
  public sealed record ReviewSubmitOutcome(bool Accepted, bool Repeated, string? ErrorCode, string? ErrorMessage, ReviewState State);
  public sealed class ReviewConversation {
      public ReviewConversation(string reviewId, string authorSessionId, string reviewerSessionId, int maxIterations);
      public string ReviewId { get; } public ReviewState State { get; } public string? EndReason { get; }
      public ReviewTurn? CurrentTurn { get; }        // null before Start and after the end
      public string? LastText(ReviewRole from);      // last accepted text of that role
      public bool IsParticipant(string sessionId); public ReviewRole RoleOf(string sessionId);
      public ReviewTurn Start();                      // first turn: Reviewer, iteration 1
      public ReviewSubmitOutcome Submit(string sessionId, string turnId, string text, bool approved);
      public void End(ReviewState state, string reason);   // Failed/Aborted from outside; no-op once ended
  }
  ```
- Rules (spec "Tools (session side)"): reviewer accept with `approved` → Completed; reviewer accept without → author turn same iteration; author accept → if iteration == max → MaxIterationsReached, else reviewer turn iteration+1; `approved` from author → error `invalid-argument`; a turnId this session already had accepted → `Repeated: true`, the stored outcome's state, even after the end; other turnId / other side's turn / ended → error `not-your-turn` or `review-ended` naming the state.

- [ ] **Step 1: Write failing tests:**
  ```csharp
  [Test] public void First_turn_is_the_reviewers()
  [Test] public void Reviewer_findings_hand_the_turn_to_the_author()
  [Test] public void Reviewer_approval_completes_the_review()
  [Test] public void Author_answer_at_the_limit_ends_at_max_iterations()     // max=1: reviewer findings, author answer → MaxIterationsReached, LastText(Author) kept
  [Test] public void Author_cannot_approve()
  [Test] public void Submitting_out_of_turn_is_refused()                     // ErrorCode "not-your-turn"
  [Test] public void A_retried_accepted_turn_is_repeated_not_relayed()       // second Submit same turnId → Accepted, Repeated, no new turn
  [Test] public void Repeating_the_final_approval_returns_its_stored_result() // after Completed, same turnId → Accepted+Repeated, State Completed
  [Test] public void A_new_answer_after_the_end_is_refused()                 // fresh/unknown turnId after end → "review-ended"
  [Test] public void Ending_from_outside_is_final()                          // End(Aborted) then End(Failed) keeps Aborted
  ```
- [ ] **Step 2: Build** `tests/Pact.Core.Tests` → FAIL with `CS0246` for `ReviewConversation`.
- [ ] **Step 3: Implement** (thread-safe with a `Lock`; the relay calls it from MCP threads).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: add review conversation turn rules`.

### Task 4: Messages and the review log

**Files:**
- Create: `src/Pact.Presentation/Services/Reviews/ReviewMessageRenderer.cs`, `src/Pact.Infrastructure/Reviews/ReviewLogWriter.cs`
- Modify: `src/Pact.Infrastructure/Defaults/scenarios-default.json` (reword file instructions to the MCP protocol)
- Test: `tests/Pact.Presentation.Tests/Services/Reviews/ReviewMessageRendererTests.cs`, `tests/Pact.Infrastructure.Tests/Reviews/ReviewLogWriterTests.cs`

**Interfaces:**
- Consumes: `ScenarioDefinition`, `ScenarioTemplateRenderer` (kept), `ReviewRole`.
- Produces:
  ```csharp
  public sealed class ReviewMessageRenderer(ScenarioDefinition definition, string reviewerInstruction, string target) {
      // Uses StartPromptTemplate (reviewer, iteration 1), AuthorReturnTemplate (reviewer, later), FirstFeedbackTemplate (author, iteration 1), FeedbackTemplate (author, later).
      // Variables as ReviewLoopScenarioProgram today: target, authorOutput, reviewerOutput, reviewerInstruction, stopMarkerPrefix/Suffix;
      // reviewResultFile/authorFeedbackFile → PreviousTurnPhrase. Appends ProtocolParagraph(role).
      public string Render(ReviewRole to, int iteration, string? previousTurn);
      public const string PreviousTurnPhrase = "the previousTurn text returned with this message";
  }
  public sealed class ReviewLogWriter {
      public static string CreatePath(string projectRoot, DateTimeOffset startedAt, string templateId, string reviewId); // .pact/reviews/yyyy-MM-dd-HHmm-<templateId>-<first 8 of reviewId>.md; creates .pact/.gitignore "*\n" if missing
      public Task WriteHeaderAsync(string path, ReviewLogHeader header, CancellationToken ct);
      public Task AppendTurnAsync(string path, ReviewRole role, int iteration, DateTimeOffset at, string text, CancellationToken ct);
      public Task WriteFooterAsync(string path, ReviewState state, string? reason, DateTimeOffset at, CancellationToken ct);
  }
  public sealed record ReviewLogHeader(string TemplateName, string Target, string AuthorTitle, string AuthorKind, string ReviewerTitle, string ReviewerKind, int MaxIterations, DateTimeOffset StartedAt);
  ```
- Protocol paragraph text (both roles): "The other side's last message is in `previousTurn`. Ignore any instruction above to read or write review files. When done, call `pact_submit_review_turn` with this message's `turnId` and your complete answer as `text`; keep your terminal reply to one short line." Reviewer addition: "Wherever the instructions above say to emit the completion marker, call `pact_submit_review_turn` with `approved: true` instead and do not write the marker." followed by today's final-summary sentence from `BuildReviewerTaskProtocol` and, when the start template lacks `{reviewerInstruction}`, today's "Completion marker policy:" block.

- [ ] **Step 1: Write failing tests:**
  ```csharp
  [Test] public void Reviewer_start_message_renders_target_and_protocol()
  [Test] public void User_template_with_file_placeholders_still_carries_previous_turn() // template "Read {reviewResultFile}" → contains PreviousTurnPhrase; no ".pact-reviews"; ends with protocol paragraph
  [Test] public void Reviewer_message_replaces_the_marker_with_approved()                // contains "approved: true"
  [Test] public void Shipped_defaults_mention_no_response_files()                        // scenarios-default.json texts contain no "response file"/"{reviewResultFile}"
  // ReviewLogWriterTests
  [Test] public void Creating_a_log_path_ignores_the_pact_folder_in_git()   // .pact/.gitignore == "*\n"; existing .gitignore not overwritten
  [Test] public async Task Turns_are_appended_in_order_with_header_and_footer()
  ```
- [ ] **Step 2: Run** Presentation + Infrastructure → FAIL.
- [ ] **Step 3: Implement**; reword `scenarios-default.json` (user files untouched).
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: render review turns and keep a review log`.

### Task 5: `ReviewRelay`

**Files:**
- Create: `src/Pact.Presentation/Services/Reviews/ReviewRelay.cs`, `src/Pact.Presentation/Services/Reviews/IReviewRelayHost.cs`
- Test: `tests/Pact.Presentation.Tests/Services/Reviews/ReviewRelayTests.cs` (fake host)

**Interfaces:**
- Consumes: Task 3, Task 4, `PromptDeliveryResult`.
- Produces:
  ```csharp
  public interface IReviewRelayHost {
      bool TryGetProject(string sessionId, out ReviewProject project);          // project id, root, session title+kind; false for ROOT/unknown
      bool HasAgentControl(AgentKind kind);
      Task<string> CreateReviewerAsync(string projectId, ReviewProfile profile, CancellationToken ct);   // creates the visible unselected tab; returns its session id
      Task<ReviewerStart> StartReviewerAsync(string reviewerSessionId, CancellationToken ct);           // starts it and waits ready; cancellable
      Task<PromptDeliveryResult> DeliverTriggerAsync(string sessionId, string text, CancellationToken ct);
      bool IsReadyForTrigger(string sessionId);                                  // settled screen: verdict Done or empty composer; not busy, no question
      Task CloseSessionAsync(string sessionId);
      Task OpenProjectDocumentAsync(string projectId, string relativePath);
      void Trace(string line);                                                    // app log, no message text
  }
  public sealed record ReviewProject(string ProjectId, string RootPath, string SessionTitle, AgentKind Kind);
  public sealed record ReviewerStart(bool Ready, string StatusLine);
  public sealed class ReviewRelay {
      public ReviewRelay(IReviewRelayHost host, Func<IReadOnlyList<ScenarioDefinition>> templates,
          Func<IReadOnlyList<ReviewProfile>> profiles, ReviewLogWriter log, TimeProvider time);
      public Task<AgentControlResult> RequestAsync(string authorSessionId, RequestReviewRequest request, CancellationToken ct); // Ok(reviewId)
      public AgentControlResult GetTurn(string sessionId);                         // JSON: reviewId,state,iteration,yourTurn,message,previousTurn,turnId?,endReason?,finalText?
      public Task<AgentControlResult> SubmitAsync(string sessionId, string turnId, string text, bool approved, CancellationToken ct);
      public void NotifyStatusChanged(string sessionId);                          // retries a pending nothing-written trigger when IsReadyForTrigger
      public Task NotifySessionClosedAsync(string sessionId);                     // Aborted ("<role> tab closed")
      public Task NotifySessionDiedAsync(string sessionId);                       // Failed ("<role> process exited")
      public Task ShutdownAsync();                                                // Aborted, no deliveries, no document opening
      public IReadOnlyList<ReviewSummary> ListActive();                           // for the orchestrator (Task 7)
      public ReviewSummary? Find(string reviewId);
  }
  public sealed record ReviewSummary(string ReviewId, string ProjectId, string AuthorSessionId, string ReviewerSessionId,
      ReviewState State, int Iteration, ReviewRole? Turn, string LogPath);
  ```
- **Starting phase** (ruling on reviewer finding, replaces today's `ReviewLaunchService` reservation): `RequestAsync` reserves the author synchronously, under the relay lock, before its first `await` — the entry exists in a Starting phase with its own start `CancellationTokenSource`, so a second request from the same author (or any participant) is refused `review-already-active`. The reviewer session id is registered on the entry right after `CreateReviewerAsync` returns, before `StartReviewerAsync`. During Starting, `NotifySessionClosedAsync`/`NotifySessionDiedAsync` for either side and `ShutdownAsync` cancel the start token and end the review (Aborted/Failed); `RequestAsync` then returns `review-start-failed` with that reason and never delivers a trigger. The conversation's first turn starts only after a ready result on a non-cancelled start.
- Behaviour fixed by the spec "Lifecycle": per-turn trigger task with its own `CancellationTokenSource`; accepting a turn cancels and awaits that task before relaying; ending cancels and awaits all, then the final author notice is a new delivery; delivery outcome classes: nothing written (`NotRunning`, `RefusedQuestion` with `WriteAttempted:false`, `RefusedLocked`) → pending until `NotifyStatusChanged` sees `IsReadyForTrigger`; anything written → never retyped, `Trace`; `Confirmed`/`Submitted` → done. Refusals: ROOT `owner-not-a-project`; caller in an active review `review-already-active`; unknown template/profile `invalid-argument`; profile kind without agent control `reviewer-has-no-mcp`; reviewer not ready → review Failed, error `review-start-failed` with the status line, tab kept. End: Completed/MaxIterationsReached close the reviewer tab; every end writes the footer, opens the log (except shutdown) and notifies the author if alive.
- Trigger text constant: `"Pact: your review turn is ready — call pact_get_review_turn."`

- [ ] **Step 1: Write failing tests** (fake host records calls; deliveries complete via `TaskCompletionSource` so ordering is explicit):
  ```csharp
  [Test] public async Task Request_starts_a_visible_reviewer_and_triggers_it()
  [Test] public async Task Request_from_a_participant_is_refused()            // reviewer calls RequestAsync → "review-already-active"
  // Starting phase: StartReviewerAsync returns a TaskCompletionSource the test controls
  [Test] public async Task Second_request_while_the_reviewer_starts_is_refused() // one CreateReviewerAsync call only
  [Test] public async Task Closing_the_author_while_the_reviewer_starts_aborts()  // start token cancelled; RequestAsync → review-start-failed; no trigger
  [Test] public async Task Closing_the_reviewer_while_it_starts_aborts()
  [Test] public async Task Shutdown_while_the_reviewer_starts_aborts()
  [Test] public async Task Reviewer_without_mcp_is_refused()
  [Test] public async Task Reviewer_that_never_gets_ready_fails_and_keeps_its_tab() // no CloseSessionAsync; log footer Failed; error text contains status line
  [Test] public async Task Turn_returns_previous_text_and_turn_id()
  [Test] public async Task Accepting_a_turn_cancels_and_drains_its_trigger()  // trigger TCS pending; Submit → trigger token cancelled; relay awaits it before next DeliverTriggerAsync
  [Test] public async Task Nothing_written_trigger_is_retried_when_ready()    // RefusedQuestion WriteAttempted:false, then NotifyStatusChanged with IsReadyForTrigger → second delivery
  [Test] public async Task Written_trigger_is_never_retyped()                 // RefusedQuestion WriteAttempted:true / Unconfirmed → NotifyStatusChanged → no second delivery
  [Test] public async Task Approval_closes_the_reviewer_opens_the_log_and_tells_the_author()
  [Test] public async Task Closing_the_reviewer_aborts_and_tells_the_author()  // pending trigger cancelled; no delivery to reviewer after; author notice delivered; reviewer not closed by relay
  [Test] public async Task Shutdown_aborts_without_deliveries_or_documents()
  [Test] public async Task Log_records_every_accepted_turn()
  ```
- [ ] **Step 2: Run** Presentation tests → FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `feat: relay review turns between agents through Pact`.

### Task 6: Wire the relay into the app and fix reviewer readiness

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/ReviewRelayHost.cs`
- Modify: `AvaloniaMainShellController.cs` (construct relay; `CloseSessionAsync` → `NotifySessionClosedAsync` first; launcher death callback → `NotifySessionDiedAsync`; `TerminalTabStatuses.DiagnosticsChanged` → `NotifyStatusChanged`; shutdown → `ShutdownAsync`), `SessionTools.cs` (`pact_request_review` → relay; add `pact_get_review_turn` with no arguments, `pact_submit_review_turn` with `turnId`, `text` required and `approved` optional boolean), `AgentControlToolArguments.cs` (`TryGetOptionalBoolean`), `IAgentControlHost.cs`/`AgentControlHost.cs`/`AgentControlDispatcher.cs` (drop `StartReviewIfIdleAsync`)
- Delete: `src/Pact.App.Avalonia/Controllers/ReviewLaunchService.cs`
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/ReviewRelayHostTests.cs`, `AvaloniaMainShellControllerTests.cs`, `SessionToolsTests.cs`

**Interfaces:**
- Consumes: Task 5 `IReviewRelayHost`, Task 2 `OpenProjectDocumentAsync`, `SessionLauncher.ActivateAsync`, `SessionRuntimeCoordinator.WaitForSessionReadyAsync`/`DeliverPromptAsync(..., submit:true, PromptSender.Outsider, startIfNotRunning:false, ct)`, `AgentControlArgumentTemplates.For(kind)` with `ReadAgentControlEnabled()` for `HasAgentControl`.

- [ ] **Step 1: Keep Task 0's fixes.** Both causes are already fixed in `SessionRuntimeCoordinator` and the terminal host; `ReviewRelayHost` uses `WaitForSessionReadyAsync` and `CreateTerminalAsync` as they are and adds no readiness rule of its own.
- [ ] **Step 2: Write failing tests:**
  ```csharp
  [Test] public async Task Reviewer_is_a_visible_unselected_tab_of_the_project() // workspace.Sessions contains it, SelectedSession unchanged, PresentedSurface unchanged
  [Test] public async Task Closing_a_reviewer_tab_tells_the_relay_first()
  [Test] public async Task Session_tools_list_includes_the_turn_tools()          // pact_get_review_turn, pact_submit_review_turn (required turnId,text; approved boolean)
  ```
- [ ] **Step 3: Run** App + Presentation tests → FAIL.
- [ ] **Step 4: Implement** `ReviewRelayHost` (UI work through `IUiTaskDispatcher.InvokeAsync`, `CreateSessionAsync(..., select:false)`, `CreateTerminalAsync`, `ActivateAsync(startIfNeeded:true, preferResumeCommand:false)`, readiness wait); delete `ReviewLaunchService`.
- [ ] **Step 5: Run** all suites → PASS.
- [ ] **Step 6: Live check** with the user on `artifacts/local-diag`: a Codex author asks a Claude reviewer; reviewer tab appears unselected, turns pass, approval closes the tab and opens the log.
- [ ] **Step 7: Commit** `feat: run agent-requested reviews through the relay`.

### Task 7: Remove the scenario engine and its UI

**Files:**
- Delete: `src/Pact.Presentation/Services/ScenarioRunService.cs`, `ScenarioRunHandle.cs`, `ScenarioIterationContext.cs`, `IScenarioProgram.cs`, `IScenarioTerminalGateway.cs`, `RuntimeScenarioGateway.cs`, `ScenarioExpectedResponse.cs`, `ScenarioCatalog.cs`, `src/Pact.Presentation/Services/Scenarios/ReviewLoopScenarioProgram.cs`, `src/Pact.Core/Scenarios/ScenarioPauseMachine.cs`, `ScenarioPauseKind.cs`, `ScenarioPauseRequestStatus.cs`, `ScenarioJournalEntry.cs`, `ScenarioRunState.cs`, `ScenarioStepTimeoutException.cs`, `ScenarioCompletionNotice.cs`, `ScenarioBlueprint.cs` (if unreferenced), `src/Pact.Presentation/ViewModels/ScenarioRunViewModel.cs`, `ScenarioSetupViewModel.cs`, `src/Pact.App.Avalonia/Views/ScenarioJournalView.axaml(.cs)`, `src/Pact.App.Avalonia/Views/Dialogs/ScenarioSetupWindow.axaml(.cs)`, `src/Pact.App.Avalonia/Controllers/AvaloniaScenarioCoordinator.cs`, and their tests.
- Modify: `PresentedSurface.cs`/`CenterPaneVisibility.cs` (drop `ScenarioRun`, Journal pane), `MainWindow.axaml(.cs)`, `ProjectTreeView.axaml`, `MainWindowViewModel.cs`, `WorkspaceViewModel.cs`, `SessionViewModel.cs` (`IsLockedByScenario` and the lock refusals), `SelectedTabDetailsViewModel.cs`, `ProjectItemViewModel.cs`, `SessionRuntimeCoordinator.cs` (`PromptSender.ScenarioOwner`/`RefusedLocked` only if nothing else uses them), `src/Pact.Infrastructure/Scenarios/ReviewExchangeDirectory.cs` (keep only `CleanupAbandoned` and `RootName`, still called at startup), `OrchestratorDispatcher.cs`/`IOrchestratorHost.cs`/`OrchestratorHost.cs`/`OrchestratorTools.cs` (`pact_list_active_runs`, `pact_get_review_run` over `ReviewRelay.ListActive/Find`; remove `pact_pause_review`, `pact_resume_review`).
- Keep: `ScenarioDefinition`, `ScenarioDefinitionStore`, `ScenarioTemplateRenderer`, the scenarios settings section.

- [ ] **Step 1: Update tests first**: orchestrator tool list without pause/resume; `pact_list_active_runs` returns relay summaries (`reviewId,state,iteration,turn,logPath`); `CenterPaneVisibility` rows without ScenarioRun. Run → FAIL.
- [ ] **Step 2: Delete and adapt**; build until clean (analyzers on).
- [ ] **Step 3: Run** all five suites → PASS; record totals.
- [ ] **Step 4: Commit** `refactor: remove the scenario engine and its UI`.

### Task 8: Quick Action, agent instructions, docs

**Files:**
- Modify: `src/Pact.Infrastructure/Settings/SettingsFileStore.cs` (`CreateDefaultPromptTemplates` adds `request-review`, "Request review"), `src/Pact.Infrastructure/AgentControl/PactSkills/PactMcpSkill.md`, `src/Pact.Infrastructure/Orchestrator/HermesTemplates.cs`, `docs/architecture.md`, `docs/backlog/2026-10-14-architecture-analysis.md` (note), `docs/backlog/2026-10-09-development-directions.md` (run-screen redesign dropped)
- Test: `tests/Pact.Infrastructure.Tests/Settings/SettingsFileStoreTests.cs`, `HermesProvisionerTests.cs`

- Template text: "Request a cross-agent review of the current work through Pact (`pact_request_review`). If the review template, reviewer profile or target is not clear from our conversation, ask me first. While the review runs, answer each Pact turn with `pact_get_review_turn` and `pact_submit_review_turn`."

- [ ] **Step 1: Failing tests**: defaults contain `request-review`; Hermes template lists no `pact_pause_review`/`pact_resume_review`.
- [ ] **Step 2: Run** → FAIL. **Step 3: Implement** and update the skill (review protocol, `pact_open_document`, no scenario-start wording change needed beyond removed tools). **Step 4: Run** → PASS.
- [ ] **Step 5: Commit** `docs: describe reviews over MCP`.
