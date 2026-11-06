# Remark cleanup implementation

Spec: ../specs/2026-11-06-remark-cleanup-design.md.

2026-11-06 user correction: sending, draft count and cleanup are project-wide
across all sources. The earlier per-source boundary below is superseded.
Update APIs and completion to one project state; verify mixed-source sends,
last resolve across sources, source-independent No/manual cleanup, late thread
protection, id continuity and persistence. Preserve source-aware line provenance.

1. Add a captured review-list cleanup operation to RemarkBook. Under its existing
   gate compare the complete source snapshot, require resolved/nonpending threads,
   atomically remove only those records and retain NextNumber. Tests first:
   success, independent sources, changed snapshot, unresolved/pending messages,
   failed persistence and id continuity.
2. Add completion tracking and pending inline question to ChangesRemarksViewModel.
   Observe book snapshots so queued UI callbacks do not erase transitions. No
   dismisses; explicit action offers again. Source-specific readiness includes
   unsaved replies. Block relevant editors while saving. Tests first: final
   resolution/agent reply, startup/no/restart, switching and concurrent changes.
3. Add inline question, source/count, Yes/No and a standalone cleanup button to
   RemarksPanel. Reuse observed UI tasks. Add actual-control tests for No, later
   manual cleanup and safe persistence.
4. Run bounded relevant tests/build and a fresh independent review, fix required
   findings with regressions, update public contracts, publish framework-dependent
   win-x64 to artifacts/local-diag and run native gate. Do not commit.

One heavy command at a time: build -m:2 -nr:false -v q -p:BuildInParallel=false;
tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2.

Completed 2026-11-06: solution build PASS; full Presentation 1119 PASS; App
Diff/Rendered/remark UI scope 53 PASS; fresh independent review finding reproduced
and fixed with a regression; whitespace PASS. Inline question and No state
visually checked. Published artifacts/local-diag; native WebView gate PASS.
No commit or push.

Project-wide correction verified 2026-11-06: four contract regressions RED then
GREEN, full Presentation 1120 PASS, mixed-source App cleanup interaction PASS,
independent incremental review without required code findings, updated docs and
visual count, local-diag republished and native gate PASS. No commit or push.
