# ADR 0001: Use a Windows-first Avalonia product head

- Status: Accepted
- Date: 2026-07-30

## Context

PACT:> Mission Control combines native Windows terminal integration, WebView2
browser surfaces, desktop window lifecycle, and a shared UI-neutral
orchestration layer. Multiple product heads would duplicate lifecycle and
native-integration behavior and make runtime evidence ambiguous.

## Decision

`Pact.App.Avalonia` is the only product head. It targets Windows, composes the
Core, Infrastructure, and Presentation projects, and owns all Avalonia and
native WebView2 adapters.

Dependencies point inward: Core has no application dependencies;
Infrastructure depends on Core; Presentation depends on Core and
Infrastructure; the Avalonia head composes all three. Cross-platform UI and a
second desktop head are not current product goals.

## Consequences

- Windows behavior and packaging are first-class release requirements.
- UI-neutral business and orchestration logic stays outside the application
  head.
- Native integration evidence must exercise the shipping Avalonia executable.
- A future platform head would require a new decision and equivalent runtime
  contracts rather than conditional branches throughout the current head.

## Current code and evidence

- [Pact.App.Avalonia project](../../src/Pact.App.Avalonia/Pact.App.Avalonia.csproj)
- [Application entry point](../../src/Pact.App.Avalonia/Program.cs)
- [Application lifetime owner](../../src/Pact.App.Avalonia/AppBootstrap.cs)
- [Dependency-direction tests](../../tests/Pact.Presentation.Tests/Architecture/PresentationDependencyTests.cs)
