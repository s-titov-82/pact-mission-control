# ADR 0002: Ship a bundled ConPTY

- Status: Accepted
- Date: 2026-07-29

## Context

Codex enables mouse input through the Win32 console API. The inbox ConPTY on
the supported Windows 11 22H2 baseline does not translate that console mode
into the VT mouse sequences required by xterm.js. Codex therefore cannot
receive wheel input even though the same TUI works in products that ship a
modern OpenConsole.

Environment variables, terminal capability replies, and display transcript
replay do not repair the missing live input translation.

## Decision

PACT:> ships pinned `conpty.dll` and `OpenConsole.exe` binaries sourced from
the version and hashes recorded in the third-party inventory.
`ConptyLibrary` requires the complete vendored pair and loads its `Conpty*`
exports. There is no silent fallback to inbox ConPTY.

The supported runtime remains framework-dependent `win-x64`. A different
architecture cannot be published until a matching ConPTY payload has been
reviewed and tested.

## Consequences

- The two binaries are one versioned runtime unit and must remain together.
- Missing, incomplete, or incompatible payloads fail explicitly.
- Updates require source-version, signature, checksum, license, notice, and
  SBOM review.
- Native tests must cover process-tree shutdown, bidirectional I/O, and an
  observed resize through the real child console.

## Current code and evidence

- [ConPTY loader](../../src/Pact.Infrastructure/Terminal/ConptyLibrary.cs)
- [ConPTY backend](../../src/Pact.Infrastructure/Terminal/ConPtyTerminalBackend.cs)
- [Vendored payload provenance](../../third_party/conpty/README.md)
- [Native ConPTY contract tests](../../tests/Pact.Infrastructure.Tests/Terminal/ConPtyTerminalBackendTests.cs)
