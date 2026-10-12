# Styled Screen and Composer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Carry the xterm Dim attribute of the bottom screen rows end-to-end so Claude/Codex report reliable nullable composer evidence without regressing terminal work state.

**Scope of styling (2026-10-12):** styling is needed only to tell a dim suggestion from a real draft in the composer, which both agents draw at the bottom of the screen. Capture only the Dim attribute, only for the bottom 15 physical rows. No colors, bold/italic/underline/inverse or full-screen styles until a concrete heuristic needs them. Work-state heuristics stay text-based.

**Architecture:** Extend the existing immutable screen/profile boundary and route it through the current status coordinator. Extract generic Dim ranges for the bottom rows in JavaScript; interpret provider chrome in Core. Keep existing delivery policy and work-state ownership.

**Tech Stack:** .NET 10, NUnit/VSTest, Avalonia 12, native WebView2, bundled xterm.js, Node.js 22+.

**Spec:** `docs/superpowers/specs/2026-10-09-harness-events-and-styled-classification-design.md`, revised 2026-10-12 according to its accompanying review.

## Global Constraints

- Hooks, hook credentials/trust, submit correlation, scenario refactoring and response transport are deferred.
- No new settings, endpoints, processes, dependencies or second VT emulator.
- Snapshot text and styles stay in memory; diagnostic/log output is content-free.
- Preserve the dirty tree's unrelated terminal input/paste and WebView changes.
- Core profiles own agent-specific recognition; JavaScript capture stays agent-neutral.
- Public production APIs require XML documentation.
- One heavy .NET command at a time. Build: `-m:2 -nr:false -v q`; tests: `-m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`.
- Do not commit plans/specifications independently; stage implementation and its documents together, preserving unrelated changes.
- Execution authorized by the user on 2026-10-12. Task commits are combined to exclude preexisting dirty changes.

## Review Focus

Each condition below has an explicit owning test in the tasks:

1. A dim suggestion becomes ordinary input with identical characters: publish and classify the changed styles.
2. Wide/combining characters wrap across physical rows: preserve logical text and style offsets without duplication.
3. A cropped/malformed screen resembles a composer: return Unknown when structure/style evidence is insufficient.
4. Transcript quotes or an old menu coexist with current chrome: classify the current frame, not historical words.
5. A Busy agent has an empty composer or waits for children: preserve Busy and independent question/lock gates.

## Validation Commands

Run only relevant tests per task after building their changed projects. The final verification build is:

```powershell
rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
```

If assets are missing, run the documented locked restore with `--disable-parallel` first.
The repository uses NUnit with Microsoft.NET.Test.Sdk/NUnit3TestAdapter and VSTest command mode.
Targeted commands after building:

```powershell
rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~TerminalScreenSnapshotTests|FullyQualifiedName~AgentScreenProfileTests|FullyQualifiedName~AgentComposerInspectorTests|FullyQualifiedName~TerminalTabStatusEngineTests" -- NUnit.NumberOfTestWorkers=2
rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~TerminalTabStatusCoordinatorTests|FullyQualifiedName~SessionRuntimeCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "FullyQualifiedName~TerminalWebMessageDecoderTests|FullyQualifiedName~TerminalHostAssetTests|FullyQualifiedName~AvaloniaScenarioRuntimeTests" -- NUnit.NumberOfTestWorkers=2
```

Commands run sequentially; expected result is zero failures and nonzero executed counts matching the filters.
The Node behavior cases are run by `TerminalHostAssetTests`; do not invoke the script without its required behavior argument.

## Task 1: Immutable snapshot and compatible profile contract

**Files:** Create `src/Pact.Core/Sessions/TerminalScreenSnapshot.cs` and `tests/Pact.Core.Tests/Sessions/TerminalScreenSnapshotTests.cs`. Modify `IAgentScreenProfile.cs`, `AgentScreenProfileBase.cs` in `src/Pact.Core/ScreenVerdictProfiles`.

**Interfaces:** Define `TerminalScreenSnapshot`, `TerminalScreenRow`, `TerminalTextRange` in the snapshot file. Snapshot exposes `Text`, `Rows`, `Columns`, `RowCount`, `Stable`; row exposes `Text`, `IsWrapped`, `DimRanges`; range exposes UTF-16 `Start`, `Length`. `DimRanges` is null when the row's styling was not captured (rows above the captured bottom area, legacy text) and empty when the row was captured and has no dim text. Use immutable collections.

Provide `TerminalScreenSnapshot.FromText(string text, bool stable = true)` for legacy input (all rows have unknown styling) and a validated `TerminalScreenSnapshot.FromRows(ImmutableArray<TerminalScreenRow> rows, int columns, bool stable)` factory. Add `TerminalScreenVerdict Classify(TerminalScreenSnapshot screen)` to the interface with a text delegation default; preserve `Classify(string screen)`. The base class implements the typed overload as virtual text delegation until Task 3.

- [x] Add failing projection cases: wrapped `"PS D:\\Personal\\"` + `"Pact>"` projects to the same logical string as the old host; unwrapped rows retain newlines; trailing empty logical rows are removed without trimming wrapped content.
- [x] Add range cases: a captured row without dim text (empty `DimRanges`) remains distinguishable from FromText's unknown styling (null); FromRows rejects out-of-bounds/overlapping ranges with ArgumentException; immutable construction cannot be changed by caller-owned collections. The decoder in Task 2 catches invalid style metadata and falls back to FromText.
- [x] Build the Core tests project with resource limits and run `TerminalScreenSnapshotTests`; establish expected behavioral failure before implementation.
- [x] Implement the types, factories and profile overload with XML contract documentation; retain existing string callers and their verdicts.
- [x] Run the snapshot and existing screen-profile tests; expect all passing with unchanged text-only work-state behavior.
- [x] Review/commit only this completed code and its relevant documents, never the plan alone.

## Task 2: Capture and route styled observations end-to-end

**Files:** Modify `src/Pact.App.Avalonia/WebAssets/terminalHost.js`, `Web/TerminalWebMessageDecoder.cs`, `Web/AvaloniaTerminalWebViewHost.cs`, `Controllers/AvaloniaMainShellController.cs`; `src/Pact.Core/Presentation/ITerminalWebViewHost.cs`; `src/Pact.Presentation/Services/TerminalTabStatusCoordinator.cs`; `src/Pact.Core/Sessions/TerminalTabStatusEngine.cs`. Extend existing decoder, host behavior, coordinator and engine tests, plus their affected terminal-host fakes.

**Interfaces:** Browser screenSnapshot messages carry existing `type/sessionId/text/stable` plus `columns/rows`. Each row carries `text/isWrapped` and, for the bottom 15 physical rows only, `dim` as `[start, length]` pairs; rows without `dim` decode to unknown styling. Change the host event to `EventHandler<(string SessionId, TerminalScreenSnapshot Snapshot)>`. Add coordinator `OnScreenSnapshot(string sessionId, TerminalScreenSnapshot snapshot, DateTimeOffset occurredAt)` and engine `OnScreenSnapshot(TerminalScreenSnapshot snapshot, DateTimeOffset occurredAt)`; retain string overloads delegating through FromText. Decode legacy text-only messages through that factory. Existing retained screen/message strings remain plain text.

- [x] Add host behavior cases `styled-snapshot`, `style-only-snapshot`, `styled-wrapped-snapshot`, `styled-wide-cells`, `styled-hidden-session`, `styled-bottom-rows-only` to `terminalHost.behavior.test.js` and its NUnit behavior list. Extend fake cells only to model the public APIs actually consumed; assert metadata and dedupe payloads, not source text.
- [x] Add decoder roundtrip and malformed metadata cases; assert missing/invalid styles yield unknown styling while text work-state classification remains usable. Add a coordinator-to-engine test proving the exact typed observation reaches the profile.
- [x] Run the new behavioral cases and record their expected failures.
- [x] For the bottom 15 physical rows only, walk xterm `getCell()` with one reused cell object, read width and `isDim()`, and coalesce dim cells into UTF-16 ranges. Other rows keep the existing `translateToString` path. Preserve physical wrapping and existing plain projection. Include the dim ranges in dedupe and preserve early unstable and final stable behavior.
- [x] Route the typed snapshot through decoder/event/controller/coordinator/engine and update affected fakes. Do not alter activity epoch or delivery policy. Unstable snapshots still provide only existing Busy evidence.
- [x] Run decoder/host/coordinator/engine suites and existing scenario-runtime smoke cases. Expect identical old text projections and preserved event/stability semantics.
- [x] Commit only the coherent completed transport change and documents.

## Task 3: Styled composer and structural provider heuristics

**Files:** Create `src/Pact.Core/ScreenVerdictProfiles/AgentComposerInspector.cs` and `tests/Pact.Core.Tests/Sessions/AgentComposerInspectorTests.cs`. Modify `AgentScreenProfileBase.cs`, `ClaudeScreenProfile.cs`, `CodexScreenProfile.cs`, `src/Pact.Core/Sessions/TerminalScreenVerdict.cs`, existing `AgentScreenProfileTests.cs`.

**Interfaces:** Shared stateless helper exposes `TerminalPromptEvidence? Inspect(TerminalScreenSnapshot screen, char promptCharacter, bool bordered, IReadOnlyList<string> chromeLabels)`; profiles own provider boundaries and the composer subframe passed to it. Extend `TerminalPromptEvidence` with trailing optional `bool ContentIsReliable = true` and `int? DraftCharacterCount = null`. `IsEmpty` returns null when BoundaryFound or ContentIsReliable is false; otherwise it compares `(DraftCharacterCount ?? NonWhitespaceCharacterCount)` to zero. Existing constructor callers keep their behavior. Keep the non-whitespace count's meaning; typed inspection explicitly marks unreadable non-empty content unreliable. The typed profile overload combines work verdict with composer evidence; string-only non-empty Claude/Codex composers remain Unknown.

- [x] Add table cases with explicit expected `(State, PromptIsEmpty)`: ordinary blank composer `(Done or existing work verdict, true)`, dim suggestion `true`, fully styled ordinary draft `false`, wrapped draft `false`, non-empty unstyled text `null`, incomplete composer `null`, current selected menu `(InputRequested, null)`.
- [x] Add style tests: composer rows with unknown styling give `null`; identical text changing Dim changes only composer evidence where work chrome is unchanged. Fixtures are built by hand with `FromRows` and dim ranges matching the known Claude/Codex suggestion layout; no captured real-screen corpus is required before implementation.
- [x] Add current-frame fixtures: old permission menu above a new composer does not request input; quoted `esc to interrupt` in transcript/draft is not Busy chrome; a structurally anchored footer hint is recognized. Keep the full existing completion/error/structured-question corpus.
- [x] Establish failing cases, then implement the shared inspection and provider-specific framing. Inspect all draft continuation rows; do not infer work completion from Empty or separators alone.
- [x] Run helper/profile/engine regressions. Assert `Busy + true` and parent waiting for background agents remain Busy; composer metadata is independent of activity and unread completion.
- [x] Commit the completed profile change and relevant documentation.

## Task 4: Consumer regressions, native evidence and docs

**Files:** Extend existing `TerminalTabStatusEngineTests.cs`, `TerminalTabStatusCoordinatorTests.cs`, `SessionRuntimeCoordinatorTests.cs`, `AvaloniaScenarioRuntimeTests.cs`. Update `docs/architecture.md`, `docs/agent-onboarding.md`. Extend `src/Pact.App.Avalonia/Diagnostics/EngineProbeRunner.cs` and native probe evidence only as needed to assert real xterm metadata through the existing gate.

**Interfaces:** Existing `PromptIsEmpty` delivery gates consume the typed profiles' evidence; task introduces no new delivery API. Existing diagnostics expose content-free evidence only.

- [x] Add consumer cases asserting known draft blocks the existing programmatic gate; Empty cannot bypass Busy, question or scenario-lock checks; Unknown retains existing behavior; existing dropped-submit/dropped-paste choices use corresponding composer evidence without changed retry policy.
- [x] Run affected cases and all three targeted commands after one bounded solution build. Preserve existing local Enter, unread and resume behavior.
- [x] Use the bundled real xterm in a native engine probe to write SGR dim/ordinary text in the bottom rows, wide/combining characters and a style-only replacement, then assert the received typed snapshot. Mock-cell tests alone do not prove attribute capture. Log the capture time once for a 200x50 screen to confirm it is negligible; no separate budget gate.
- [x] Run the native WebView gate against that built executable using `tools/Invoke-NativeWebViewGate.ps1` and the documented evidence contract. Confirm the probe tested the current executable; a script self-test is not the native gate.
- [ ] In separate visible Claude/Codex test sessions, check the composer evidence in tab details for a freshly started agent (suggestion shown) and after typing a draft; only if a result is wrong, save that screen and turn it into a fixture. Record content-free composer outcomes; do not restart user sessions or claim synthetic fixtures prove actual provider layout. Report any unperformed manual check as unverified.
- [x] Update public docs to explain styled snapshots, nullable composer facts and preserved independent work status. Remove statements that Claude/Codex always return null due to losing styling; describe text-only/partial fallback accurately.
- [x] Run `git diff --check HEAD --` on touched paths; review the final scoped diff and commit implementation with spec/plan. No preliminary documentation-only commit.

## Execution Recommendation

Implementation and automated/native checks completed on 2026-10-12. Separate
visible installed Claude/Codex suggestion/draft checks remain unverified; native
xterm metadata capture does not prove every installed provider layout.

Implement natively in this session: the four tasks share one snapshot contract and the native probe depends on that entire path. Obtain plan review/execution selection before implementation. Independent review at completion follows the chosen execution skill; do not spawn agents merely to write or review this plan.
