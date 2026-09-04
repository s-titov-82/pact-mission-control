# ADR 0005: Use file-first scenario transport

- Status: Accepted
- Date: 2026-07-30

## Context

Review-loop prompts can be long, configurable, and sensitive to terminal
composer behavior. Typing a full prompt into an interactive TUI is slow and
can lose text or submit partially. Terminal busy/idle signals are presentation
heuristics and cannot prove that an agent produced a complete business
response.

## Decision

For every author or reviewer step, PACT:> atomically writes one complete,
immutable task file:

`.pact-reviews/<run>/pass-NNN-<role>-task.md`

The terminal receives only a short instruction naming that path, followed by a
separate Enter. The assigned agent writes the matching unique response file.
Its final non-empty line must equal the step's transport footer; only that
footer-complete response advances the run. The footer is removed before
content becomes the next prompt, final result, or marker input.

The journal remains in memory. Pausing retains the exchange directory and
resumes the same expected file. Every terminal run state removes only its own
Pact-owned directory; startup removes abandoned `.pact-reviews`, never generic
`.reviews`.

## Consequences

- Prompt transport is inspectable and independent of terminal typing speed.
- Partial files, wrong filenames, and missing footers cannot advance a step.
- Busy/idle evidence cannot block or complete a scenario.
- Exchange files are run-scoped transport, not durable audit history.

## Current code and evidence

- [Exchange directory](../../src/Pact.Infrastructure/Scenarios/ReviewExchangeDirectory.cs)
- [Review-loop program](../../src/Pact.Presentation/Services/Scenarios/ReviewLoopScenarioProgram.cs)
- [Run lifecycle service](../../src/Pact.Presentation/Services/ScenarioRunService.cs)
- [Transport tests](../../tests/Pact.Infrastructure.Tests/Scenarios/ReviewExchangeDirectoryTests.cs)
- [Review-loop behavior tests](../../tests/Pact.Presentation.Tests/Services/Scenarios/ReviewLoopScenarioProgramTests.cs)
- [Run lifecycle tests](../../tests/Pact.Presentation.Tests/Services/ScenarioRunServiceTests.cs)
