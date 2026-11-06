# Current branch implementation

Spec: ../specs/2026-11-06-current-branch-design.md.

1. Add comparison/branch reader DTOs beside the existing commit reader. Extract
   shared arbitrary-base comparison reads; preserve first-parent commit behavior.
   Add real Git tests first for upstream states, dirty exclusion and immutable
   endpoint diffs.
2. Extend Changes source state with live Current branch, separate summary and
   fixed captured-comparison navigation. Reuse the observation timer, reject old
   source/pair/file results, and bind historical remarks to target plus base.
   Add gated VM/remark tests first.
3. Add source button, two right-panel entries and explicit navigation events.
   Preserve project-wide remark controls and counts. Test actual controls and
   controller navigation, and inspect a narrow/wide rendered screen.
4. Independent whole-feature review, bounded solution/relevant tests, docs and
   local-diag publish/native gate. No commit/push or unrelated cleanup.

One heavy command at a time; builds -m:2 -nr:false -v q -p:BuildInParallel=false;
tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2.

Completed 2026-11-06. Independent review's two source-selection findings fixed
with observed failing regression cases. Final build, 28 Git tests, 1129
Presentation tests and 74 affected UI cases passed (one obsolete UI assertion
corrected and rerun separately). Wide/narrow layout inspected; local-diag publish,
native WebView gate and whitespace check passed. No commit or push.
