# Review Delivery Attention Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Preserve a review whose notification failed and make manual recovery visible.

**Architecture:** The existing relay owns a per-turn delivery issue and retrieval
acknowledgement. Its existing Changed event refreshes session badges through the
UI dispatcher. No additional coordinator or persistence is needed.

**Tech Stack:** .NET 10, Avalonia 12, NUnit.

**Spec:** `docs/superpowers/specs/2026-10-31-review-delivery-attention-design.md`

## Global Constraints

- Never retry after a possible terminal write; preserve cancellation/draining.
- Keep attention in memory only and do not persist terminal text.
- One heavy command at a time; build with `-m:2 -nr:false -v q -p:BuildInParallel=false`.
- Tests use `--no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.
- Do not commit the design or plan separately from its implementation.

## Review Focus

- A late delivery failure must not recreate a warning after the turn was fetched.
- Polling by the waiting participant must not clear another participant's warning.
- A failed old trigger must not contaminate the next turn or an ended review.
- No-write refusals may retry, and success clears their warning.
- Programmatic attention changes must reach both session rows without new UI threading paths.

## Task 1: Relay attention and manual acknowledgement

**Files:** `src/Pact.Presentation/Services/Reviews/ReviewRelay.cs`,
`tests/Pact.Presentation.Tests/Services/Reviews/ReviewRelayTests.cs`.

**Interfaces:** Add an optional init-only `string? DeliveryIssue` to `ReviewSummary`;
preserve its existing constructor and all delivery host interfaces.

- [x] Write regressions for `PasteNotShown`, unconfirmed delivery, cancellation and
  exceptions, owning/waiting participant fetches, and late completion after fetch.
- [x] Build the Presentation test project and run `FullyQualifiedName~ReviewRelayTests`;
  observe the missing attention behavior before implementing it.
- [x] Publish issues only for the current, live, unread turn; acknowledge them in
  `GetTurn`, and suppress no-write retries once the owning agent fetched the turn.
- [x] Run the relay tests and verify unchanged no-repaste/drain behavior.

## Task 2: Visible warning and integration verification

**Files:** `src/Pact.Presentation/ViewModels/SessionReviewBadge.cs`,
`src/Pact.Presentation/ViewModels/WorkspaceViewModel.cs`,
`src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`,
`src/Pact.App.Avalonia/Views/ProjectTreeView.axaml`,
`tests/Pact.Presentation.Tests/ViewModels/SessionViewModelTests.cs` and the
existing project-tree headless tests if needed for rendered behavior.

- [x] Test warning projection for both participants and clearing after acknowledgement.
- [x] Reuse existing badge bindings for a danger-colored exclamation mark, warning
  text, and an actionable tooltip naming whose turn needs manual continuation.
- [x] Keep the warning visible on a collapsed project via the existing shell badge
  refresh; verify a real collapsed WorkspaceViewModel retains its expansion state.
- [x] Build the App test project with bounded resources; run the changed view tests
  and Presentation regressions. Run `git diff --check HEAD --` for touched files.
- [x] Publish `artifacts/local-diag` for the user's live check; report verification limits.

## Verification

- Full Presentation: 1020 passed, 0 failed; one installed-Codex explicit test not run.
  The healthy run was outside sandbox after a sandbox run failed on `File.Replace`.
- App `FullyQualifiedName~ProjectTree`: 32 passed, 0 failed, 0 skipped.
- Ending the review also hides its warning: a rendered regression failed before
  adding explicit false fallbacks for a null review and passed afterwards.
- Reports: `.tmp/review-validation/final/presentation.trx` and `project-tree.trx`;
  parsed and checked against the console results.
- Independent static review found one P2 (warning hidden by a collapsed project);
  the added collapsed-project regression failed before its fix and passed afterwards.
- Live clipboard delivery into installed Claude: NOT-RUN; its historical failure
  cause is not established and sender behavior was not changed.
