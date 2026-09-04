# ADR 0003: Keep agents behind visible terminal sessions

- Status: Accepted
- Date: 2026-07-30

## Context

Codex, Claude, and Hermes are interactive subscription TUI tools. Their
terminal modes, mouse handling, selection, resume commands, and user-visible
state are part of the interaction contract. Replacing that boundary with a
hidden process or API proxy would change both product behavior and the user's
control over submitted prompts.

## Decision

Every live agent or shell session owns a visible xterm.js terminal backed by
one ConPTY process. Selecting another item changes presentation only; it does
not stop background sessions.

Manual prompt and selection actions insert into compatible live sessions and
submit only when the action explicitly requests Enter. Scenarios may submit
prompts, but they still target the same live sessions and lock only scenario
input. Terminal busy/idle remains UI evidence, not scenario completion.

## Consequences

- There is no headless agent API or hidden subscription-token integration.
- Session kind and current terminal mode stay available at the C# input
  boundary for compatibility rewrites.
- Background terminal output, pause/resume, and process exit must remain
  observable in the UI.
- Tests must prove routing, locking, start/stop ownership, and explicit submit
  behavior instead of mocking an agent API.

## Current code and evidence

- [Session runtime ownership](../../src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs)
- [Visible shell integration](../../src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs)
- [Prompt action policy](../../src/Pact.Core/Prompting/PromptActionPolicy.cs)
- [Runtime coordinator tests](../../tests/Pact.Presentation.Tests/Services/SessionRuntimeCoordinatorTests.cs)
- [Avalonia scenario runtime tests](../../tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaScenarioRuntimeTests.cs)
