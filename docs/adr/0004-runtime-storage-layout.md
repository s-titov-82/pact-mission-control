# ADR 0004: Use one four-directory runtime data root

- Status: Accepted
- Date: 2026-07-30

## Context

PACT:> persists durable settings and browser identity while also producing
disposable logs, WebView cache, atomic-write staging, and monitoring
snapshots. Mixing those lifetimes makes backup unsafe and encourages broad
cleanup that can destroy authentication or project state.

The same root must not be opened concurrently by the application or profile
copy tool.

## Decision

The default root is `%APPDATA%\Pact`; `--data-root` accepts an absolute
isolated alternative. It has exactly four top-level directories:

- `Settings` — durable JSON and `Settings/Notes`;
- `WebView` — shared WebView2 user data and named terminal profile;
- `Logs` — bounded disposable application logs;
- `Temp` — session staging and owner-managed disposable snapshots.

`Settings/projects.json` owns projects and nested saved terminal and browser
sessions. Startup and profile copying acquire the same named operating-system
mutex derived from the normalized root. No lock file is stored in the data
root.

## Consequences

- Backup copies durable settings with the application closed; it does not
  infer durability from arbitrary files.
- Cleanup is owner-specific. It preserves browser cookies and site storage,
  bounds logs and cache, and never blanket-deletes durable settings.
- Terminal transcripts and scenario journals are not persisted.
- Adding a fifth top-level directory requires revisiting this decision and the
  profile-copy contract.

## Current code and evidence

- [Runtime paths](../../src/Pact.Infrastructure/Storage/AppPaths.cs)
- [Data-root housekeeping](../../src/Pact.Infrastructure/Storage/DataRootHousekeeping.cs)
- [Cross-process root lease](../../src/Pact.Infrastructure/Storage/AppDataProcessLease.cs)
- [Profile copy tool](../../tools/Pact.ProfileTool/Program.cs)
- [Storage layout tests](../../tests/Pact.Infrastructure.Tests/Storage/DataRootHousekeepingTests.cs)
- [Lease tests](../../tests/Pact.Infrastructure.Tests/Storage/AppDataProcessLeaseTests.cs)
