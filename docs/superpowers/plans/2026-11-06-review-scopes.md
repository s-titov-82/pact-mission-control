# Review scopes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Review local edits, the whole feature, or work since the remote checkpoint, including local files in every aggregate.

**Architecture:** Reuse GitWorkingTreeReader and the existing observation loop. Separate review scope from the selected aggregate/commit filter. Keep immutable commit comparisons and live local comparisons distinct so remark provenance remains correct.

**Tech Stack:** .NET 10, C#, Avalonia 12, existing Git CLI runner and NUnit.

**Spec:** ../specs/2026-11-06-review-scopes-design.md

## Global Constraints

- Preserve the accumulated dirty checkout; execute inline, no unrelated cleanup.
- No automatic commit/push/pull or network updates. Explicit Pull (rebase) uses the existing Git operation log; no separate Fetch-only button.
- Publish only artifacts/local-diag, framework-dependent win-x64.
- One heavy command at a time. Build -m:2 -nr:false -v q -p:BuildInParallel=false; tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2.
- All remarks send/count/cleanup operations remain project-wide.
- Manual edits use apply_patch; document public APIs.

## Review Focus

- An added file later reverted locally disappears from net changes; counts are not additive.
- A path untracked relative to HEAD can still exist in the older comparison base.
- A staged version differs from disk: aggregate shows current disk, including undoing staged changes.
- A remote ref moves during Pull (rebase), external fetch or an open editor: old results cannot replace the current source or drop typed remarks.
- Opening a remark captured against a prior base must not silently substitute current HEAD or the current merge-base.

## Task 1: Read scope bases and net local comparisons

**Files:** GitWorkingTreeReader.Branch.cs, GitWorkingTreeReader.cs,
IGitComparisonReader.cs in src/Pact.Infrastructure/Git; new
GitReviewScope.cs beside those contracts; tests/Pact.Infrastructure.Tests/Git/GitCommitReaderTests.cs
and GitWorkingTreeReaderTests.cs.

**Interfaces:** Add GitReviewScope enum (Uncommitted, Unmerged, Unpushed),
GitReviewChanges(Scope, Branch, BaseRef, BaseCommit, HeadCommit, Files,
AddedLines, RemovedLines, AheadCount, BehindCount, IsFallback, UnavailableReason), and
IGitReviewReader with ReadReviewChangesAsync(root, scope, targetBranch, ct),
ReadLocalChangesAsync(root, baseCommit, ct), ReadBranchesAsync(root, ct),
ReadScopeCommitsAsync(root, baseCommit, headCommit, skip, count, ct).
ReadLocalChangesAsync returns GitLocalChanges(BaseCommit, HeadCommit, Files,
AddedLines, RemovedLines), with actual HEAD always distinct from comparison base.
WorkingTreeChanges.HeadCommit/WorkingTreeHeadCommit retain their actual HEAD
contract. Keep immutable comparison methods for historical remarks. Replace
ReadBranchChangesAsync/GitBranchChanges and migrate their tests to review scopes;
do not keep a second tracking-only reader or polling path.

- [x] Write real-repository failing tests for all three bases, default/explicit target, absent/gone tracking, divergence and no common ancestor.
- [x] Pin Unpushed to the exact tracking endpoint, including behind/diverged cases: remote-only edits appear as reversions in aggregate paths/patch/totals, while scope history excludes remote-only commits. Assert ahead/behind counts and keep absent-tracking fallback distinct from a configured missing ref.
- [x] Add cases for commit+dirty cancellation, staged/disk disagreement, ignored/untracked paths, an untracked path present in the base, git rm --cached with an identical/modified disk file, rename/delete/recreate, binary and bounded text. Assert unique net paths, exact patch content and totals including untracked text lines.
- [x] Build the scoped test project and observe failures using the resource-bounded commands.
- [x] Resolve full refs/SHAs, read net local differences and untracked content without temporary commits/index writes. Validate refs/HEAD before publishing. Reuse parser, blob limits and literal pathspecs; implement bounded range history.
- [x] Run GitCommitReaderTests and GitWorkingTreeReaderTests; require all passing.

## Task 2: Separate scope, filter and captured local base

**Files:** ProjectChangesViewModel.cs, .Branch.cs, .Commits.cs and
GitCommitPickerViewModel.cs in src/Pact.Presentation/ViewModels;
ChangesRemarksViewModel.cs; RemarkPlacer.cs in Services/Remarks;
Services/AgentControl/RemarkTools.cs, Core/Remarks/RemarkLocator.cs and
RemarkPrompt.cs; src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs
composition of RemarkTools;
ProjectCommitChangesViewModelTests.cs, ChangesRemarksViewModelTests.cs and
RemarkPlacerTests.cs and Services/AgentControl/RemarkToolsTests.cs in
tests/Pact.Presentation.Tests; tests/Pact.Core.Tests/Remarks/RemarkPromptTests.cs and
RemarkLocatorTests.cs, plus affected controller composition tests.

**Interfaces:** ProjectChangesViewModel.SelectReviewScopeAsync(scope, ct),
SelectReviewFilterAsync(commitOrNull, uncommitted, ct),
SelectTargetBranchAsync(refOrNull, ct), independent Uncommitted/Unmerged/Unpushed
summary properties and paged scope history. Null commit plus false uncommitted
means All changes. Keep SelectCommitAsync for standalone history and captured
historical remarks. OpenRemarkSourceAsync with null target must honor the
captured local base instead of selecting HEAD automatically.
Introduce explicit ReviewSourceKind (Scope, FixedLocalBase, HistoricalCommit)
with FixedLocalBaseCommit (null means empty tree, distinguished by kind). A
commit selected within a scope retains kind Scope and its commit filter;
HistoricalCommit is for standalone history/captured historical navigation. A
captured local remark sets FixedLocalBase and leaves scope/filter selection.
Its local contents refresh against that fixed base, summary cards stay live,
and SourceTitle/Description explain the fixed base. Any scope/filter action
leaves it. Generalize the existing RetainCurrentComparison lease and replace
IsCurrentBranch/CurrentBranch*/SelectCurrentBranchAsync/ApplyBranchRefresh,
without leaving a parallel state or hold counter.

RemarkPlacer.PlaceOnDiskAsync(book, thread, IGitCommitReader, ct) validates a
local Old anchor through its captured base and original path, not _readHead.
Use ResolveCommitAsync/ReadCommitTextAsync to check the immutable old object
and line. New anchors retain snapshot relocation. For on-shown placement,
RemarkLocator.IsOldValid compares anchor.BaseCommit with the displayed base;
it is no longer a current-HEAD validity test. Apply the same captured-base rule
in Send, off-diff placement and pact_list_remarks. Retire obsolete read-HEAD
arguments where no longer used, adapting callers/fakes. Prompt labels say base.

- [x] Write gated failing tests for source/target/filter ordering, fixed commit display during live refresh, independent three-scope summaries, and editor retention.
- [x] Add remark tests for same path/different aggregate bases, old-side validity after HEAD moves in Send/MCP/off-diff, missing base/path, rename old path, prompt labels using base rather than HEAD, captured-base reopening after merge-base moves with live disk refresh, leaving FixedLocalBase by scope/filter action, snapshot-based new-side relocation and project-wide send/cleanup across all scopes.
- [x] Implement scope/filter state using the existing source-intent and file generations. Use one reconciliation loop, retaining stale/error status explicitly after failed reads. Keep target selections per live project branch, without persistence.
- [x] Adapt aggregate loading to base-versus-local content and historical loading to exact immutable objects. Base identity participates in all marker/editor matching; captured local navigation does not change aggregate summary facts.
- [x] Run affected Presentation classes; require all passing.

## Task 3: Three scopes, commit filter, target chooser and Pull (rebase)

**Files:** ProjectScreenView.axaml/.axaml.cs/.Commits.cs,
RightActionsPanel.axaml/.axaml.cs, MainWindow.axaml.cs,
Controllers/AvaloniaMainShellController.ProjectScreen.cs;
reuse GitCommitPickerWindow and GitPanelViewModel.RunCommandAsync;
tests/Pact.App.Avalonia.Tests/Views/RightActionsPanelHeadlessTests.cs,
ProjectScreenRemarksHeadlessTests.cs and Controllers/AvaloniaMainShellControllerTests.cs.

**Interfaces:** Three summary navigation events map to SelectReviewScopeAsync.
Remove superseded CurrentBranchEntryRequested/OnCurrentBranchClicked and their
MainWindow/controller wiring, replacing them rather than leaving dead actions.
Target picker consumes ReadBranchesAsync. Scope filter consumes the bounded
scope history and calls SelectReviewFilterAsync. Explicit Pull (rebase) routes
through the originating project's GitPanelViewModel.RunCommandAsync using
`pull --rebase --no-autostash`. Its label is `Pull (rebase)` and tooltip explains
the fixed strategy. Intentionally do not use GitButtonCommandSet.PullId
configured arguments; preserve the existing configurable Git panel Pull
default/overrides unchanged. Bind visibility to GitPanelViewModel.IsPullVisible
so the existing hide-Pull preference applies to this entry too.
Enable only for an attached branch with configured
tracking, clean index/worktree and no existing merge/rebase/conflict operation;
otherwise explain why. Recheck preconditions at execution, handle Git refusal
or conflict visibly, and retain existing Git status/resolve/abort controls and
log. Continuing a conflicted rebase is an explicit terminal action; no new
rebase-control subsystem is required.
Do not auto-stash, discard changes, retry or resolve conflicts automatically.
Refresh after success or failure because fetch can advance refs before a rebase
fails. All callbacks retain their originating owner and source intent.

- [x] Write failing actual-control/controller tests for three independent cards, source actions, scoped commit filtering and returning to All changes, manual target, fallback explanation, exact-server-difference description and behind/diverged warning.
- [x] Test Pull (rebase) command arguments and log routing; dirty/index-conflict/active-rebase/detached/no-tracking refusal without writes; success and fetch-success/rebase-failure refresh; conflict visibility and no automatic stash or repeat attempt. Scope/editor state is not reassigned to another owner if the project changes mid-operation.
- [x] With a custom PullId such as pull --ff-only, assert Git panel Pull retains that command while the distinct Pull (rebase) action sends the fixed rebase/no-autostash arguments and label/tooltip. Disabling PullId hides both entries; no global command configuration is rewritten.
- [x] Implement compact wrapping scope controls, target selection and All changes/Uncommitted/commit choices. Display unavailable/stale status and the actual base. Keep standalone Choose commit and common remark controls.
- [x] Verify stale target/commit dialog acceptance after project/source changes is discarded. Verify native editor drafts survive background refresh and right-panel navigation still respects newer intent.
- [x] Run affected App cases; inspect wide/narrow rendered layouts with valid, empty and unavailable scopes.

## Task 4: Review, documentation and local delivery

**Files:** README.md, README.ru.md, docs/architecture.md,
docs/agent-onboarding.md, superseded current-branch spec/plan; existing
.superpowers/sdd ledger and .tmp/root-dashboard-validation evidence paths.

- [x] Update documentation around the final three scopes and exact local/historical remark semantics; mark prior committed-only design superseded.
- [x] Request one independent whole-feature review as required by executing-plans, then fix substantiated findings with regression checks. No automatic commit.
- [x] Run a bounded solution build and final affected suites, then git diff --check HEAD for touched paths.
- [x] Publish only artifacts/local-diag and run tools/Invoke-NativeWebViewGate.ps1 against that exe with evidence under the existing validation folder.
- [x] Report implementation, verification and any remaining limits; do not claim a native UI workflow was manually exercised from headless evidence.

## Completion evidence

Implemented on feat/review-scopes after locally merging the eight accumulated
commits into main414466a. Integration and push require the user's explicit instruction.
Opus review da6fedef71344b3daf14cba65e8b8265 approved the accumulated diff after
four passes. Final bounded solution build:0 warnings/errors; Core591 PASS/1SKIP,
Presentation1151 PASS plus affected88 PASS, real-Git37 PASS, actual-control
suites20/13/38 PASS, affected controller4 PASS. Native gate0.1.12 and publication
into existing artifacts/local-diag PASS. Details and superseded failure evidence:
.tmp/root-dashboard-validation/review-scopes-results.md.

Modal target/commit acceptance guards were inspected statically; target resolution
and branch/source ordering were tested at reader/VM/controller levels. Rendered
wide/narrow/unavailable controls and native engine probes are automated evidence,
not a claim of manual end-to-end native review or Pull against a user's remote.
The Pull gate uses RunPullRebaseAsync with the existing busy gate and private
ExecuteGitCommandAsync rather than nesting RunCommandAsync (which would no-op
once the preflight holds that gate). User-configured generic Pull is unchanged.
