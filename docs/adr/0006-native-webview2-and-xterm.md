# ADR 0006: Use native WebView2 with xterm.js

- Status: Accepted
- Date: 2026-07-29

## Context

PACT:> needs a mature interactive terminal viewport and authenticated browser
pages inside one Windows desktop application. The terminal must preserve VT
parsing, selection, accessibility, clipboard, resize, and multiple live
sessions without shipping a second browser engine.

Loaded hidden browser pages must keep monitoring timers active, and hidden
terminals must keep processing output.

## Decision

The Avalonia head uses the installed Microsoft WebView2 runtime. The terminal
loads pinned repository-owned xterm.js assets through one native host with one
xterm instance per live session. Saved browser pages use separate native
WebView2 controllers.

The shared WebView2 environment deliberately enables
`--disable-background-timer-throttling`,
`--disable-renderer-backgrounding`, and
`--disable-backgrounding-occluded-windows`. This keeps terminal output and
loaded-page monitoring operational while hidden.

The interactive native gate launches the exact candidate executable and ties
sanitized engine-probe evidence to its hash. Its self-test validates only the
evidence protocol and is not native runtime evidence.

## Consequences

- Windows releases require WebView2 Runtime.
- Hidden loaded pages can consume more CPU, memory, and power than suspended
  Chromium content.
- C#/JavaScript bridge changes must preserve UI dispatch, lifecycle, and the
  native engine-probe contract.
- Pinned web assets require version, hash, license, notice, and SBOM review.
- A custom renderer or bundled CEF engine is outside the supported design.

## Current code and evidence

- [Shared WebView2 environment](../../src/Pact.App.Avalonia/Platform/AvaloniaWebViewEnvironment.cs)
- [Terminal WebView host](../../src/Pact.App.Avalonia/Web/AvaloniaTerminalWebViewHost.cs)
- [Browser page host](../../src/Pact.App.Avalonia/Web/AvaloniaWebPageHost.cs)
- [Background-switch tests](../../tests/Pact.App.Avalonia.Tests/Platform/AvaloniaWebViewEnvironmentLayoutTests.cs)
- [Browser host contract tests](../../tests/Pact.App.Avalonia.Tests/Web/AvaloniaWebPageHostContractTests.cs)
- [Interactive native gate](../../tools/Invoke-NativeWebViewGate.ps1)
