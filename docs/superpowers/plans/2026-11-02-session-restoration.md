# Session Restoration Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline; preserve the current authorized checkout and pending attention/Markdown changes.

**Goal:** Durable manual attention markers and normal startup restoration.
**Architecture:** Session records own manual markers. A content-free Settings checkpoint reuses the soft restart snapshot/restorer, leaving update transport validation untouched.
**Tech Stack:** .NET 10, Avalonia 12, NUnit.
**Spec:** `docs/superpowers/specs/2026-11-02-session-restoration-design.md`

## Global Constraints

- Never persist screens, messages, composer, readiness, or live classifier evidence.
- Manual marks clear only through the same double-click action.
- Preserve paused owners and issue fresh process credentials.
- One heavy command at a time; build `-m:2 -nr:false`, test `-m:1 -nr:false` and at most two NUnit workers.
- Preserve unrelated dirty files; do not commit, push, or restart the installed application.

## Review Focus

- Rapid manual toggles while a previous save is pending.
- Shutdown-generated selection/lifecycle changes overwriting the desired checkpoint.
- Partial restoration and stale/paused owner identities.
- Crash recovery before a graceful resume id can be captured.
- Startup unread acknowledgement and fresh classifier facts.

### Task 1: Durable manual marker

- [x] Add failing behavioral tests for marker/feed, persistence and toggle ordering.
- [x] Add SessionRecord/SessionViewModel marker and serialized project/ROOT toggle.
- [x] Wire ready-only double-click action, orchestrator persistence and separate UI marker.
- [x] Verify marker round trips and survives classifier updates/selection.

### Task 2: Ordinary restoration

- [x] Add failing checkpoint round-trip and startup/shutdown restoration tests.
- [x] Share content-free restore state between ticket and normal checkpoint.
- [x] Add atomic checkpoint store and event-driven, equality-suppressed serialized writes.
- [x] Restore on startup with ticket precedence; freeze/flush before shutdown.
- [x] Verify paused selection, stale ids, unread, malformed checkpoint and fresh busy/input facts.

### Task 3: Verify and document

- [x] Update public startup and attention contracts.
- [x] Run relevant suites and build with bounded resources; record limitations.
- [x] Obtain one fresh final review and address material findings.
- [x] Publish the diagnostic artifact without replacing the installed app.

Validation: Infrastructure 378 passed/1 skipped, Presentation1037 passed,
App789 behavioral cases passed followed by NUnit adapter OutOfMemoryException
while building its report (exit1, one synthetic assembly failure). Targeted
startup/gesture26 and fix/composition7 gates passed. Native soft-restart gate
passed against `artifacts/local-diag`, including updater, bundled ConPTY,
browser, unread and selection. Full evidence and limitations are recorded in
`.tmp/session-restoration-validation/verification.md`. No commit was made.
