# Current Attention Feed Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Show current attention across tabs and navigate to the selected card.
**Architecture:** Presentation observes the existing workspace/ROOT/orchestrator
view models. A separate Avalonia view renders cards and emits activation;
the shell reuses selection and the tree reveals the target without selecting twice.
**Tech Stack:** .NET 10, Avalonia 12, NUnit.
**Spec:** `docs/superpowers/specs/2026-11-02-attention-feed-design.md` (approved).

## Global Constraints

- No history, persistence, toast, polling or new agent-state channel.
- Include InputRequested, Unread, Failed, review delivery warnings and web unread;
  exclude Busy/Paused, paused projects and manually paused ROOT tabs.
- One card per tab; questions, failures, unread, then tree order.
- Preserve existing acknowledgement and normal activation semantics.
- Preserve pending official Markdown package migration.
- One heavy command at a time; build -m:2 -nr:false -v q -p:BuildInParallel=false;
  test --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2.

## Review Focus

- Same-title/replaced tabs must retain owner identity, not capture another target.
- Removing/pausing an owner must reject an already-rendered stale card.
- Switching main surfaces must not hide the global feed.
- ROOT/orchestrator activation must route to the correct existing selector.
- Tree reveal must scroll and expand without issuing another activation.

## Task 1: Presentation projection

Files: new `src/Pact.Presentation/ViewModels/AttentionFeedViewModel.cs`,
`tests/Pact.Presentation.Tests/ViewModels/AttentionFeedViewModelTests.cs`;
modify `MainWindowViewModel.cs` and shell disposal.
Interfaces: `AttentionFeedViewModel(workspaces, rootTabs, orchestratorSlot)`,
`Items`, `HasItems`, `Contains(AttentionItem item)`, `Dispose()`.
`AttentionItem` captures the original target/owner, label, title, indicator and tooltip.

- [x] Add behavioral tests for inclusion/priority/deduplication, web unread,
  rename/identity replacement, ROOT/orchestrator, pause/removal, and disposal.
- [x] Observe RED with an empty projection; implement bounded event subscriptions
  and immutable equal-value suppression; verify GREEN.

## Task 2: Reusable view and navigation

Files: new `Views/AttentionFeedView.axaml[.cs]`; modify `RightActionsPanel`,
`ProjectTreeView.axaml.cs`, `MainWindow.axaml.cs`; new rendered feed tests.
Interfaces: view DataContext is AttentionFeedViewModel; `ItemRequested` carries
AttentionItem; tree `RevealAttentionItem(AttentionItem)` only expands/scrolls.

- [x] Rendered RED tests for cards/empty state and click target; implement
  separate bounded-height block above Changes, outside surface-specific content.
- [x] Wire through existing observed event-task lifecycle; reject stale cards,
  reuse ordinary tab activation, reveal matching tree/orchestrator row once.
- [x] Test collapsed/offscreen navigation and stale cards, then run focused
  Presentation and headless UI tests with resource limits.
- [x] One fresh final review; fix required findings with regressions, update
  docs, publish diagnostic build and check whitespace. No automatic commit.

## Verification

- Bounded solution build passed (13 projects).
- Full Presentation after final fix:1033 passed; one explicit installed-Codex
  test not executed.
- Full App:776 passed,1 File.Replace IO failure in unchanged
  `An_agent_reply_reaches_the_open_changes_tab`; its isolated retry passed.
  All new UI cases passed in the full run. No fully-green App claim.
- Independent review inspected21/21 files; its combined-reason tooltip finding
  was fixed with `Unread_and_review_delivery_failure_keep_both_reasons` RED→GREEN.
- Published `artifacts/local-diag`; native manual verification is still NOT-RUN.
- Detailed evidence: `.tmp/attention-validation/verification.md` and TRX files.
