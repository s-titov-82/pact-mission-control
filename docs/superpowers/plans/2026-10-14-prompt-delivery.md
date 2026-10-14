# One Prompt Sender Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Route every programmatic prompt through one sender that refuses only on a question or a foreign scenario lock, confirms delivery from a fresh screen capture, repeats only Enter, and never resends text.

**Architecture:** `SessionRuntimeCoordinator.DeliverPromptAsync` runs the whole delivery under a per-session delivery gate, using the step-2 input queue for each write. Confirmation classifies an on-demand capture from the terminal web view (`ITerminalWebViewHost.CaptureScreenAsync`) with the session's stateless profile. The scenario layer stops resending; the watchdog only pauses.

**Tech Stack:** .NET 10, C#, NUnit + Shouldly, Avalonia 12, WebView2, xterm.js (node behavior tests).

**Spec:** `docs/superpowers/specs/2026-10-14-prompt-delivery-design.md`

## Global Constraints

- Build: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`
- Tests: `dotnet test <project> --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`
- One heavy build at a time; never run two builds or test runs in parallel.
- Builds must have 0 warnings (analyzers are errors in CI-like runs; IDE0005 unused usings included).
- Tabs for indentation in C#; XML docs on public API explaining the contract.
- No source-text, private-shape, or timing-sleep tests.
- Commit trailer: `Co-Authored-By: Claude Opus 5.5 (1M context) <noreply@anthropic.com>`
- Spec and this plan are committed only together with the first implementation commit (Task 1), never alone.
- Timings (spec): paste landing 5 s; pre-Enter capture 2 s; confirmation 10 s; poll 250 ms; watchdog 15 min (unchanged).
- Do not revert unrelated working-tree changes.

## Review Focus

1. A web view that never answers a capture: delivery must end within its budget, release the gate, and later answers must be ignored (Task 2 registry test, Task 4 coordinator test).
2. A question that appears between paste landing and Enter: Enter must not be written (Task 4 test `A_question_that_opens_while_the_paste_lands_stops_enter`).
3. User typing in the target terminal during confirmation: no repair Enter may submit the user's text (Task 4 test `Keyboard_input_during_confirmation_ends_the_delivery_without_repair`).
4. Two programmatic senders to one session (MCP + template): the second waits for the first one's result, and pastes never interleave (Task 4 test `A_second_delivery_waits_for_the_first_result`).
5. A session that exits while a delivery waits for the gate: the waiting delivery returns `NotRunning` instead of throwing (Task 4 test `A_delivery_queued_behind_an_exit_returns_not_running`).

---

## File Structure

| File | Responsibility |
|---|---|
| `src/Pact.Core/Sessions/PromptSender.cs` (new) | Who sends: `Outsider` or `ScenarioOwner`. |
| `src/Pact.Core/Sessions/PromptDeliveryResult.cs` | New outcome set (old values removed in Task 6). |
| `src/Pact.Core/ScreenVerdictProfiles/IAgentScreenProfile.cs`, `AgentScreenProfileBase.cs` | `ReadsComposer` capability. |
| `src/Pact.Core/Presentation/ITerminalWebViewHost.cs` | `CaptureScreenAsync`. |
| `src/Pact.App.Avalonia/Web/ScreenCaptureRequests.cs` (new) | Pending capture registry by request id. |
| `src/Pact.App.Avalonia/Web/AvaloniaTerminalWebViewHost.cs`, `TerminalWebMessageDecoder.cs`, `TerminalOutputBridgeBatcher.cs`, `WebAssets/terminalHost.js` | Capture round trip. |
| `src/Pact.Presentation/Services/SessionRuntime.cs` | Delivery gate, keyboard input counter. |
| `src/Pact.Presentation/Services/ISessionFacts.cs` (new) | What the coordinator reads about a session. |
| `src/Pact.Presentation/Services/DeliveryTrace.cs` (new) | One trace line per delivery. |
| `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs` | `DeliverPromptAsync`, `StartAndWaitReadyAsync`, `PasteAsKeyboardAsync`; old send methods removed. |
| `src/Pact.App.Avalonia/Controllers/ShellSessionFacts.cs` (new) | Shell implementation of `ISessionFacts`. |
| `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` | Callers migrated; SendTo reasons; `pact_start_session` routing. |
| `src/Pact.Presentation/Services/Orchestrator/*` , `src/Pact.App.Avalonia/Controllers/OrchestratorHost.cs`, `src/Pact.Infrastructure/AgentControl/OrchestratorToolCatalog.cs`, `src/Pact.Infrastructure/Orchestrator/HermesTemplates.cs` | MCP result mapping and the new tool. |
| `src/Pact.Presentation/Services/IScenarioTerminalGateway.cs`, `MainWindowScenarioGateway.cs`, `ScenarioRunService.cs`, `Scenarios/ReviewLoopScenarioProgram.cs` | Scenario policy: no resend, watchdog pauses only. |
| `AGENTS.md`, `docs/architecture.md`, `docs/agent-onboarding.md`, `docs/backlog/*.md` | Contract updates. |

---

### Task 1: Core vocabulary — sender, outcomes, composer capability

**Files:**
- Create: `src/Pact.Core/Sessions/PromptSender.cs`
- Modify: `src/Pact.Core/Sessions/PromptDeliveryResult.cs`
- Modify: `src/Pact.Core/ScreenVerdictProfiles/IAgentScreenProfile.cs`
- Modify: `src/Pact.Core/ScreenVerdictProfiles/AgentScreenProfileBase.cs`
- Test: `tests/Pact.Core.Tests/ScreenVerdictProfiles/ProfileComposerCapabilityTests.cs` (create; check the folder name under `tests/Pact.Core.Tests` first and match it)

**Interfaces:**
- Produces: `enum PromptSender { Outsider, ScenarioOwner }`; new `PromptDeliveryOutcome` members `Submitted`, `Unconfirmed`, `PasteNotShown`, `RefusedQuestion`, `RefusedLocked`, `NotRunning` (old members stay until Task 6); `bool IAgentScreenProfile.ReadsComposer`.

- [ ] **Step 1: Write the failing test**

```csharp
using Pact.Core.Agents;
using Pact.Core.ScreenVerdictProfiles;

namespace Pact.Core.Tests.ScreenVerdictProfiles;

public sealed class ProfileComposerCapabilityTests
{
	[TestCase(AgentKind.Codex, true)]
	[TestCase(AgentKind.Claude, true)]
	[TestCase(AgentKind.Pwsh, false)]
	[TestCase(AgentKind.Custom, false)]
	public void Only_agents_with_a_recognized_composer_read_it(AgentKind kind, bool expected) =>
		AgentScreenProfileSelector.ForKind(kind).ReadsComposer.ShouldBe(expected);
}
```

- [ ] **Step 2: Run it to see it fail**

Run: `dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false`
Expected: CS1061 `ReadsComposer` not found.

- [ ] **Step 3: Implement**

`IAgentScreenProfile.cs`, add inside the interface:

```csharp
	/// <summary>
	/// Whether this profile reports <see cref="TerminalScreenVerdict.PromptIsEmpty"/> and
	/// questions. Prompt delivery can be confirmed only for such profiles.
	/// </summary>
	bool ReadsComposer => false;
```

`AgentScreenProfileBase.cs`, add a public member:

```csharp
	/// <inheritdoc />
	public bool ReadsComposer => true;
```

`PromptSender.cs`:

```csharp
namespace Pact.Core.Sessions;

/// <summary>Who puts a prompt into a session, which decides what refuses it.</summary>
public enum PromptSender
{
	/// <summary>Anyone except the scenario run that owns the session; a scenario lock refuses it.</summary>
	Outsider,

	/// <summary>The scenario run that holds the session's lock; only a question refuses it.</summary>
	ScenarioOwner
}
```

`PromptDeliveryResult.cs`, add these members to `PromptDeliveryOutcome` after `BlockedByPendingInput` (keep the existing ones for now):

```csharp
	/// <summary>The session has no composer model; the paste showed and Enter was written.</summary>
	Submitted,

	/// <summary>
	/// The paste showed and Enter was written (and repeated once if the composer still held
	/// text), but an empty composer was never seen afterwards.
	/// </summary>
	Unconfirmed,

	/// <summary>The paste never appeared, so Enter was not written.</summary>
	PasteNotShown,

	/// <summary>A question is pending; see <see cref="PromptDeliveryResult.WriteAttempted"/>.</summary>
	RefusedQuestion,

	/// <summary>A scenario run owns the session; nothing was written.</summary>
	RefusedLocked,

	/// <summary>
	/// The session has no live process, and starting it was not allowed or did not reach
	/// readiness; <see cref="PromptDeliveryResult.StatusLine"/> gives the reason.
	/// </summary>
	NotRunning
```

Update the `StatusLine` param doc to: "The pending question for `RefusedQuestion`/`BlockedByInputRequest`, or the reason for `NotRunning`; empty otherwise."

- [ ] **Step 4: Build and run Core tests**

Run build, then `dotnet test tests/Pact.Core.Tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`
Expected: all pass.

- [ ] **Step 5: Commit (with spec and plan)**

```bash
git add src/Pact.Core tests/Pact.Core.Tests docs/superpowers/specs/2026-10-14-prompt-delivery-design.md docs/superpowers/plans/2026-10-14-prompt-delivery.md
git commit -m "feat: name prompt senders, delivery outcomes and composer-reading profiles"
```

---

### Task 2: On-demand screen capture

**Files:**
- Modify: `src/Pact.Core/Presentation/ITerminalWebViewHost.cs`
- Create: `src/Pact.App.Avalonia/Web/ScreenCaptureRequests.cs`
- Modify: `src/Pact.App.Avalonia/Web/TerminalWebMessageDecoder.cs`
- Modify: `src/Pact.App.Avalonia/Web/TerminalOutputBridgeBatcher.cs`
- Modify: `src/Pact.App.Avalonia/Web/AvaloniaTerminalWebViewHost.cs`
- Modify: `src/Pact.App.Avalonia/WebAssets/terminalHost.js`
- Modify fakes: `tests/Pact.App.Avalonia.Tests/Fakes/FakeTerminalWebViewHost.cs`, `RecordingHost` in `tests/Pact.Presentation.Tests/Services/SessionRuntimeCoordinatorTests.cs`, and any other `ITerminalWebViewHost` implementation (`grep -rn ": ITerminalWebViewHost" src tests`)
- Test: `tests/Pact.App.Avalonia.Tests/Web/ScreenCaptureRequestsTests.cs` (create), `TerminalWebMessageDecoderTests.cs`, `TerminalOutputBridgeBatcherTests.cs`, `terminalHost.behavior.test.js` + `TerminalHostAssetTests.cs`

**Interfaces:**
- Produces: `Task<TerminalScreenSnapshot?> ITerminalWebViewHost.CaptureScreenAsync(string sessionId, CancellationToken cancellationToken)` — returns the screen after all output the host received before the call was rendered; `null` when the session has no terminal in the web view; throws `OperationCanceledException` on cancellation.
- Produces (fakes): `FakeTerminalWebViewHost.CaptureScreen` (`Func<string, TerminalScreenSnapshot?>`, default returns `null`) and `RecordingHost.CaptureScreen` (same).

- [ ] **Step 1: Failing tests**

`ScreenCaptureRequestsTests.cs`:

```csharp
using Pact.App.Avalonia.Web;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Tests.Web;

public sealed class ScreenCaptureRequestsTests
{
	[Test]
	public async Task A_completed_request_returns_its_snapshot()
	{
		ScreenCaptureRequests requests = new();
		var (id, pending) = requests.Register();
		var snapshot = TerminalScreenSnapshot.FromText("screen");

		requests.Complete(id, snapshot).ShouldBeTrue();

		(await pending).ShouldBeSameAs(snapshot);
	}

	[Test]
	public void An_answer_for_a_removed_or_unknown_request_is_ignored()
	{
		ScreenCaptureRequests requests = new();
		var (id, _) = requests.Register();
		requests.Remove(id);

		requests.Complete(id, TerminalScreenSnapshot.FromText("late")).ShouldBeFalse();
		requests.Complete(id + 100, null).ShouldBeFalse();
	}
}
```

`TerminalWebMessageDecoderTests.cs`, add:

```csharp
	[Test]
	public void TryDecode_reads_a_screen_capture_and_a_missing_terminal()
	{
		var capture = TerminalWebMessageDecoder.TryDecode(
				"""{"type":"screenCapture","sessionId":"s1","requestId":7,"text":"screen","stable":true}""")
			.ShouldBeOfType<TerminalWebMessage.ScreenCapture>();
		capture.RequestId.ShouldBe(7);
		capture.Snapshot.ShouldNotBeNull().Text.ShouldBe("screen");

		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"screenCapture","sessionId":"s1","requestId":8,"missing":true}""")
			.ShouldBe(new TerminalWebMessage.ScreenCapture("s1", 8, null));
		TerminalWebMessageDecoder.TryDecode(
				"""{"type":"screenCapture","sessionId":"s1","text":"screen"}""")
			.ShouldBeNull();
	}
```

`TerminalOutputBridgeBatcherTests.cs`, add (adapt to the file's existing construction helper):

```csharp
	[Test]
	public async Task Flushing_one_session_sends_its_pending_output_without_activating_it()
	{
		List<(string SessionId, string Text)> sent = [];
		await using TerminalOutputBridgeBatcher batcher = new((id, text) => { sent.Add((id, text)); return Task.CompletedTask; });
		_ = batcher.EnqueueAsync("hidden", "pending output");

		await batcher.FlushSessionAsync("hidden");

		sent.ShouldBe([("hidden", "pending output")]);
	}
```

(If the existing constructor takes more parameters, copy the arrangement from the neighbouring tests. If you cannot assert "without activating" through public API, keep only the flush assertion.)

`terminalHost.behavior.test.js`, add a behavior and dispatch branch:

```js
function runScreenCaptureBehavior() {
  window.agentTerminal.showTerminal('session-1', { snapshotDebounceMs: 500 });
  const terminal = terminalInstances[0];
  window.agentTerminal.write('session-1', 'queued output');
  hostMessages.length = 0;

  window.agentTerminal.captureScreen('session-1', 5);

  assert.ok(terminal.writes.includes('queued output'), 'pending output is rendered before the capture');
  const capture = hostMessages.find(message => message.type === 'screenCapture');
  assert.equal(capture.requestId, 5);
  assert.equal(capture.sessionId, 'session-1');
  assert.equal(capture.stable, true);

  hostMessages.length = 0;
  window.agentTerminal.captureScreen('unknown', 6);
  assert.deepEqual(hostMessages, [{ type: 'screenCapture', sessionId: 'unknown', requestId: 6, missing: true }]);
}
```

```js
} else if (behavior === 'screen-capture') {
  runScreenCaptureBehavior();
```

`TerminalHostAssetTests.cs`: add `[TestCase("screen-capture")]` to `Terminal_host_snapshot_behaviors_execute_in_node`.

- [ ] **Step 2: Run to see them fail**

Build; expected compile errors (`ScreenCaptureRequests`, `ScreenCapture`, `FlushSessionAsync` missing).

- [ ] **Step 3: Implement**

`ITerminalWebViewHost.cs`:

```csharp
	/// <summary>
	/// Captures the session's visible screen after every output chunk the host received before
	/// this call has been rendered. Unlike <see cref="ScreenSnapshotReceived"/>, the capture is
	/// never deduplicated or delayed, so it is fresh evidence for a decision just made.
	/// </summary>
	/// <returns>The screen, or <see langword="null"/> when the session has no terminal.</returns>
	Task<TerminalScreenSnapshot?> CaptureScreenAsync(string sessionId, CancellationToken cancellationToken);
```

`ScreenCaptureRequests.cs`:

```csharp
using System.Collections.Concurrent;
using Pact.Core.Sessions;

namespace Pact.App.Avalonia.Web;

/// <summary>
/// Screen captures awaiting the web view's answer. A request that was cancelled or timed out
/// is removed, so its late answer is dropped instead of completing a newer request.
/// </summary>
internal sealed class ScreenCaptureRequests
{
	private readonly ConcurrentDictionary<long, TaskCompletionSource<TerminalScreenSnapshot?>> _pending = new();
	private long _nextId;

	public (long Id, Task<TerminalScreenSnapshot?> Answer) Register()
	{
		var id = Interlocked.Increment(ref _nextId);
		TaskCompletionSource<TerminalScreenSnapshot?> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = answer;
		return (id, answer.Task);
	}

	public bool Complete(long id, TerminalScreenSnapshot? snapshot) =>
		_pending.TryRemove(id, out var answer) && answer.TrySetResult(snapshot);

	public void Remove(long id) => _pending.TryRemove(id, out _);
}
```

`TerminalWebMessageDecoder.cs`:
- add record `internal sealed record ScreenCapture(string SessionId, long RequestId, TerminalScreenSnapshot? Snapshot) : TerminalWebMessage;`
- add `"screenCapture" => DecodeScreenCapture(root),` to the type switch;
- implement:

```csharp
	private static TerminalWebMessage.ScreenCapture? DecodeScreenCapture(JsonElement root)
	{
		if (!TryGetString(root, "sessionId", out var sessionId)
			|| !root.TryGetProperty("requestId", out var idElement)
			|| idElement.ValueKind != JsonValueKind.Number
			|| !idElement.TryGetInt64(out var requestId))
		{
			return null;
		}

		if (root.TryGetProperty("missing", out var missing) && missing.ValueKind == JsonValueKind.True)
		{
			return new TerminalWebMessage.ScreenCapture(sessionId, requestId, null);
		}

		return DecodeScreenSnapshot(root) is { } snapshot
			? new TerminalWebMessage.ScreenCapture(sessionId, requestId, snapshot.Snapshot)
			: null;
	}
```

`TerminalOutputBridgeBatcher.cs`, add next to `ActivateAndFlushAsync`:

```csharp
	/// <summary>Sends one session's pending output now, without making it the active session.</summary>
	public Task FlushSessionAsync(string sessionId)
	{
		ArgumentException.ThrowIfNullOrEmpty(sessionId);
		SessionState? state;
		lock (_sync)
		{
			ObjectDisposedException.ThrowIf(_disposed, this);
			if (!_sessions.TryGetValue(sessionId, out state))
			{
				return Task.CompletedTask;
			}

			state.ScheduleVersion++;
			state.FlushScheduled = false;
		}

		return FlushAsync(state);
	}
```

`AvaloniaTerminalWebViewHost.cs`:
- field `private readonly ScreenCaptureRequests _captures = new();`
- method:

```csharp
	/// <inheritdoc />
	public async Task<TerminalScreenSnapshot?> CaptureScreenAsync(
		string sessionId,
		CancellationToken cancellationToken)
	{
		await WaitUntilReadyAsync().WaitAsync(cancellationToken);
		await _outputBatcher.FlushSessionAsync(sessionId).WaitAsync(cancellationToken);
		var (id, answer) = _captures.Register();
		try
		{
			await ExecuteScriptAsync(
				$"window.agentTerminal.captureScreen({JsonSerializer.Serialize(sessionId)}, {id});");
			return await answer.WaitAsync(cancellationToken);
		}
		finally
		{
			_captures.Remove(id);
		}
	}
```

- in the message `type` switch add `TerminalWebMessage.ScreenCapture => "screenCapture",`
- in the handling switch add:

```csharp
			case TerminalWebMessage.ScreenCapture capture:
				_captures.Complete(capture.RequestId, capture.Snapshot);
				break;
```

`terminalHost.js`, add the function near `flushOutput`:

```js
// Renders everything queued for the terminal, then calls done. xterm processes writes in
// order, so a write issued now completes after any chunk already in flight.
function drainOutputThen(instance, done) {
  if (instance.outputTimer !== null) {
    clearTimeout(instance.outputTimer);
    instance.outputTimer = null;
  }
  const pending = instance.pendingOutput;
  instance.pendingOutput = '';
  instance.term.write(pending, done);
}
```

and in `window.agentTerminal`:

```js
  captureScreen: function (sessionId, requestId) {
    const instance = terminals.get(sessionId);
    if (!instance) {
      postHostMessage({ type: 'screenCapture', sessionId, requestId, missing: true });
      return;
    }
    drainOutputThen(instance, () => {
      postHostMessage({
        type: 'screenCapture',
        sessionId,
        requestId,
        ...captureScreenSnapshot(instance.term),
        stable: true
      });
    });
  },
```

Fakes: add to `FakeTerminalWebViewHost` and `RecordingHost`:

```csharp
	public Func<string, TerminalScreenSnapshot?> CaptureScreen { get; set; } = static _ => null;

	public Task<TerminalScreenSnapshot?> CaptureScreenAsync(string sessionId, CancellationToken cancellationToken) =>
		Task.FromResult(CaptureScreen(sessionId));
```

- [ ] **Step 4: Build and run App tests**

Run build, then `dotnet test tests/Pact.App.Avalonia.Tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`
Expected: all pass (node must be on PATH for the asset tests, as today).

- [ ] **Step 5: Commit**

```bash
git add src/Pact.Core/Presentation src/Pact.App.Avalonia/Web src/Pact.App.Avalonia/WebAssets tests
git commit -m "feat: capture a terminal screen on demand after pending output renders"
```

---

### Task 3: Delivery gate and keyboard input counter in `SessionRuntime`

**Files:**
- Modify: `src/Pact.Presentation/Services/SessionRuntime.cs`
- Test: `tests/Pact.Presentation.Tests/Services/SessionRuntimeTests.cs`

**Interfaces:**
- Produces: `internal Task<T> SessionRuntime.RunDeliveryExclusiveAsync<T>(Func<Task<T>> delivery, CancellationToken cancellationToken)` (same queue semantics as `RunInputExclusiveAsync`, a separate queue); `public long KeyboardInputCount { get; }`; `internal void NoteKeyboardInput()`.

- [ ] **Step 1: Failing tests** (add to `SessionRuntimeTests`)

```csharp
	[Test]
	public async Task Input_units_still_run_while_a_delivery_holds_its_gate()
	{
		SessionRuntime runtime = new("session-1");
		TaskCompletionSource releaseDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var delivery = runtime.RunDeliveryExclusiveAsync(
			async () => { await releaseDelivery.Task; return true; },
			CancellationToken.None);

		var typed = await runtime.RunInputExclusiveAsync(() => Task.FromResult(true), CancellationToken.None);

		typed.ShouldBeTrue();
		delivery.IsCompleted.ShouldBeFalse();
		releaseDelivery.SetResult();
		await delivery;
	}

	[Test]
	public async Task Deliveries_run_one_at_a_time()
	{
		SessionRuntime runtime = new("session-1");
		TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
		var secondStarted = false;
		var first = runtime.RunDeliveryExclusiveAsync(
			async () => { await releaseFirst.Task; return true; },
			CancellationToken.None);
		var second = runtime.RunDeliveryExclusiveAsync(
			() => { secondStarted = true; return Task.FromResult(true); },
			CancellationToken.None);

		secondStarted.ShouldBeFalse();
		releaseFirst.SetResult();
		await Task.WhenAll(first, second);
		secondStarted.ShouldBeTrue();
	}

	[Test]
	public void Keyboard_input_is_counted()
	{
		SessionRuntime runtime = new("session-1");
		runtime.NoteKeyboardInput();
		runtime.NoteKeyboardInput();
		runtime.KeyboardInputCount.ShouldBe(2);
	}
```

- [ ] **Step 2: Run to see them fail** — build: members missing.

- [ ] **Step 3: Implement**

Move the queue logic into a private nested class and use two instances:

```csharp
	private readonly ExclusiveQueue _inputQueue = new();
	private readonly ExclusiveQueue _deliveryQueue = new();
	private long _keyboardInputCount;

	/// <summary>Keyboard writes seen so far; a delivery compares it to notice the user typing.</summary>
	public long KeyboardInputCount => Interlocked.Read(ref _keyboardInputCount);

	internal void NoteKeyboardInput() => Interlocked.Increment(ref _keyboardInputCount);

	internal Task<T> RunInputExclusiveAsync<T>(Func<Task<T>> unit, CancellationToken cancellationToken) =>
		_inputQueue.RunAsync(unit, cancellationToken);

	/// <summary>
	/// Runs one whole prompt delivery after every delivery queued before it has finished, so
	/// two programmatic senders cannot interleave between a paste, its Enter, the confirmation
	/// and the repair. Keyboard input does not use this queue and is never held by it.
	/// </summary>
	internal Task<T> RunDeliveryExclusiveAsync<T>(Func<Task<T>> delivery, CancellationToken cancellationToken) =>
		_deliveryQueue.RunAsync(delivery, cancellationToken);

	private sealed class ExclusiveQueue
	{
		private readonly Lock _gate = new();
		private Task _tail = Task.CompletedTask;

		public async Task<T> RunAsync<T>(Func<Task<T>> unit, CancellationToken cancellationToken)
		{
			// body = the current RunInputExclusiveAsync body, using _gate and _tail
		}
	}
```

Copy the existing `RunInputExclusiveAsync` body verbatim into `ExclusiveQueue.RunAsync` (replace `_inputTail` with `_tail`), keep its XML doc on `RunInputExclusiveAsync`, and delete the `_inputTail` field.

- [ ] **Step 4: Build; run Presentation tests**

`dotnet test tests/Pact.Presentation.Tests --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2` — all pass.

- [ ] **Step 5: Commit**

```bash
git add src/Pact.Presentation/Services/SessionRuntime.cs tests/Pact.Presentation.Tests/Services/SessionRuntimeTests.cs
git commit -m "feat: hold a session for a whole prompt delivery and count keyboard input"
```

---

### Task 4: `DeliverPromptAsync` in the coordinator

**Files:**
- Create: `src/Pact.Presentation/Services/ISessionFacts.cs`
- Create: `src/Pact.Presentation/Services/DeliveryTrace.cs`
- Modify: `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs`
- Modify: `src/Pact.Presentation/Services/SessionRuntime.cs` (rename doc of `WaitForClientPasteAsync` only if needed)
- Test: `tests/Pact.Presentation.Tests/Services/SessionRuntimeCoordinatorTests.cs` (new region), `tests/Pact.Presentation.Tests/Services/DeliveryTraceTests.cs` (create)

**Interfaces:**
- Consumes: `PromptSender`, new outcomes, `ReadsComposer` (Task 1); `CaptureScreenAsync` (Task 2); `RunDeliveryExclusiveAsync`, `KeyboardInputCount`, `NoteKeyboardInput` (Task 3).
- Produces:

```csharp
public interface ISessionFacts
{
	SessionScreenState? ReadScreen(string sessionId);
	bool IsScenarioLocked(string sessionId);
	Task StartAsync(string sessionId, CancellationToken cancellationToken);
}

// SessionRuntimeCoordinator
public ISessionFacts? SessionFacts { get; set; }
public Action<string>? DeliveryTrace { get; set; }
public Task<PromptDeliveryResult> DeliverPromptAsync(string sessionId, string text, bool submit, PromptSender sender, bool startIfNotRunning, CancellationToken cancellationToken);
public Task<SessionReadiness> StartAndWaitReadyAsync(string sessionId, CancellationToken cancellationToken);
public Task<SessionReadiness> WaitForSessionReadyAsync(string sessionId, CancellationToken cancellationToken); // reads SessionFacts
public Task PasteAsKeyboardAsync(string sessionId, string text);
```

`WriteInputAsync` (keyboard) now also calls `runtime.NoteKeyboardInput()` before its write. The old `SendPromptAsync`, `WriteScenarioPromptAndSubmitAsync` and the delegate overload of `WaitForSessionReadyAsync` stay until Tasks 5–6 remove their callers.

Add an internal constructor parameter `TimeSpan? pasteLandingTimeout = null` (default 5 s, replacing the `ClientPasteLandingTimeout` constant at its use sites) and `TimeSpan? preEnterCaptureTimeout = null` (default 2 s); pass both through `CreateFastCoordinator` in tests (200 ms and 100 ms).

- [ ] **Step 1: Failing tests**

Add a test helper region to `SessionRuntimeCoordinatorTests`:

```csharp
	private const string Pasted = "\u001b[200~review this\u001b[201~";

	private sealed class FakeFacts : ISessionFacts
	{
		public SessionScreenState? Screen { get; set; } = Idle();
		public bool Locked { get; set; }
		public Func<string, Task>? Start { get; set; }
		public SessionScreenState? ReadScreen(string sessionId) => Screen;
		public bool IsScenarioLocked(string sessionId) => Locked;
		public Task StartAsync(string sessionId, CancellationToken cancellationToken) =>
			Start?.Invoke(sessionId) ?? Task.CompletedTask;
	}

	// A Codex session whose backend echoes pastes, with captures served from `capture`.
	private static async Task<(SessionRuntimeCoordinator Coordinator, RecordingBackend Backend, RecordingHost Host, FakeFacts Facts, TerminalController Controller)>
		CreateDeliverySessionAsync(AgentKind kind = AgentKind.Codex, bool echoPastes = true)
	{
		RecordingHost host = new();
		RecordingBackend backend = new();
		TerminalController controller = new(backend);
		await controller.StartAsync("fake", Environment.CurrentDirectory);
		var coordinator = CreateFastCoordinator(host);
		var runtime = coordinator.GetOrCreateRuntime("session-1");
		runtime.AttachController(controller);
		runtime.Kind = kind;
		FakeFacts facts = new();
		coordinator.SessionFacts = facts;
		backend.InputWritten = input =>
		{
			if (echoPastes && input.StartsWith("\u001b[200~", StringComparison.Ordinal))
			{
				runtime.AppendRecentOutput(input[6..^6]);
			}
		};
		return (coordinator, backend, host, facts, controller);
	}

	private static TerminalScreenSnapshot ComposerScreen(bool empty) => /* a Codex screen whose
		composer is empty or holds "review this"; build it with TerminalScreenSnapshot.FromRows
		exactly like Styled_composer_uses_existing_delivery_gate: an empty "› " composer row for
		empty, "› review this" (not dim) for holding text, then the footer row
		"GPT-6.1-Sol medium · main · Context 30% used". Verify with
		CodexScreenProfile.Instance.Classify(...).PromptIsEmpty in a one-off assertion. */;

	private static TerminalScreenSnapshot QuestionScreen() => /* a Codex screen that
		CodexScreenProfile classifies as InputRequested; copy one from
		tests/Pact.Core.Tests Codex profile tests. */;
```

`CreateFastCoordinator` gains an optional `RecordingHost? host = null` parameter.

Tests (each `await using var _ = controller;`):

```csharp
	[Test]
	public async Task A_fresh_empty_composer_after_enter_confirms()
	{
		var (coordinator, backend, host, _, controller) = await CreateDeliverySessionAsync();
		await using var _ = controller;
		host.CaptureScreen = _ => ComposerScreen(empty: backend.InputWrites.Contains(PlainEnter));

		var result = await coordinator.DeliverPromptAsync("session-1", "review this", true, PromptSender.Outsider, false, CancellationToken.None);

		result.Outcome.ShouldBe(PromptDeliveryOutcome.Confirmed);
		backend.InputWrites.ShouldBe([Pasted, PlainEnter]);
	}

	[Test]
	public async Task An_unknown_composer_never_confirms()
	{
		// capture returns a screen with PromptIsEmpty == null (e.g. TerminalScreenSnapshot.FromText("x"))
		// expect Unconfirmed, writes [Pasted, PlainEnter]
	}

	[Test]
	public async Task Busy_agent_and_text_in_the_composer_do_not_refuse()
	{
		// facts.Screen = Busy(epoch: 1) with PromptIsEmpty false; capture: empty after Enter
		// expect Confirmed
	}

	[TestCase(PromptSender.Outsider)]
	[TestCase(PromptSender.ScenarioOwner)]
	public async Task A_pending_question_refuses_every_sender(PromptSender sender)
	{
		// facts.Screen = Question("Approve?"); expect RefusedQuestion, StatusLine "Approve?", no writes
	}

	[Test]
	public async Task A_scenario_lock_refuses_only_outsiders()
	{
		// facts.Locked = true; Outsider → RefusedLocked, no writes;
		// ScenarioOwner (capture empty after Enter) → Confirmed
	}

	[Test]
	public async Task A_paste_that_never_shows_is_not_entered()
	{
		// CreateDeliverySessionAsync(echoPastes: false); expect PasteNotShown, writes [Pasted]
	}

	[Test]
	public async Task A_question_that_opens_while_the_paste_lands_stops_enter()
	{
		// capture → QuestionScreen() (first capture is the pre-Enter check)
		// expect RefusedQuestion, WriteAttempted true, SubmitAttempted false, writes [Pasted]
	}

	[Test]
	public async Task A_capture_that_never_answers_writes_no_enter()
	{
		// host.CaptureScreenAsync never completes: make RecordingHost accept
		// Func<string, CancellationToken, Task<TerminalScreenSnapshot?>> CaptureScreenAsyncHandler;
		// return Task.Delay(Timeout.Infinite, token).ContinueWith(_ => (TerminalScreenSnapshot?)null, TaskContinuationOptions.OnlyOnRanToCompletion)
		// — simpler: `async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; }`
		// expect Unconfirmed, SubmitAttempted false, writes [Pasted], completes within 2 s
	}

	[Test]
	public async Task Text_left_in_the_composer_gets_exactly_one_more_enter()
	{
		// capture: holding text until 2 Enters written, then empty
		// expect Confirmed, writes [Pasted, PlainEnter, PlainEnter]
	}

	[Test]
	public async Task Text_that_stays_after_the_repair_ends_unconfirmed()
	{
		// capture: always holding text; expect Unconfirmed, writes [Pasted, PlainEnter, PlainEnter]
	}

	[Test]
	public async Task A_question_before_the_repair_stops_the_second_enter()
	{
		// capture: holding text while 1 Enter written and the confirmation runs; then
		// QuestionScreen() once the confirmation window has passed (count captures:
		// after N captures switch). expect RefusedQuestion, SubmitAttempted true, writes [Pasted, PlainEnter]
	}

	[Test]
	public async Task Keyboard_input_during_confirmation_ends_the_delivery_without_repair()
	{
		// capture: holding text; on first capture call coordinator.WriteInputAsync("session-1", "x", _ => false, (_, _) => Task.CompletedTask)
		// expect Unconfirmed; writes [Pasted, PlainEnter, "x"] (no second Enter)
	}

	[Test]
	public async Task A_session_without_a_composer_model_is_submitted()
	{
		// CreateDeliverySessionAsync(kind: AgentKind.Pwsh); capture handler throws if called
		// expect Submitted, writes [Pasted, PlainEnter]
	}

	[Test]
	public async Task A_second_delivery_waits_for_the_first_result()
	{
		// first delivery's capture blocks on a TaskCompletionSource after Enter;
		// start a second DeliverPromptAsync("second text"); assert backend.InputWrites has no
		// second paste while the first is unconfirmed; release; both complete;
		// writes order: [Pasted, PlainEnter, "\u001b[200~second text\u001b[201~", PlainEnter]
	}

	[Test]
	public async Task A_stopped_session_is_not_started_without_permission()
	{
		// coordinator with facts, no controller attached; startIfNotRunning false → NotRunning,
		// facts.Start never invoked
	}

	[Test]
	public async Task A_stopped_session_is_started_and_waits_for_readiness()
	{
		// facts.Start attaches a started controller to the runtime and calls runtime.NotifyOutputRendered();
		// facts.Screen = Question(TrustPromptDescription) until the first PlainEnter, then Idle();
		// capture empty after the 2nd Enter. Expect Confirmed and writes [PlainEnter (trust), Pasted, PlainEnter]
	}

	[Test]
	public async Task A_start_that_never_gets_ready_refuses_with_its_reason()
	{
		// facts.Screen = Question("Pick a model") → NotRunning, StatusLine "Pick a model", no paste
	}

	[Test]
	public async Task A_delivery_queued_behind_an_exit_returns_not_running()
	{
		// first delivery blocks in capture; meanwhile second delivery queued; the first one's
		// continuation detaches the controller (runtime.DetachController()) before releasing;
		// second → NotRunning, no writes of its own
	}

	[Test]
	public async Task Every_delivery_writes_one_trace_line()
	{
		// coordinator.DeliveryTrace = lines.Add; deliver confirmed; lines.Count 1;
		// line starts with "delivery session=session-1 sender=Outsider" and contains "outcome=Confirmed"
		// and contains no prompt text ("review this")
	}
```

The comments above define each test's arrange/act/assert exactly; write them out in the style of `A_fresh_empty_composer_after_enter_confirms`. Capture handlers decide by `backend.InputWrites` contents or by a capture counter, never by sleeping.

`DeliveryTraceTests.cs`:

```csharp
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.Presentation.Tests.Services;

public sealed class DeliveryTraceTests
{
	[Test]
	public void A_trace_line_names_every_stage_without_prompt_text()
	{
		DeliveryTrace trace = new("s1", PromptSender.ScenarioOwner);
		trace.Admission = "ok";
		trace.Paste = "bracketed";
		trace.Shown = "12";
		trace.Enter = "260";
		trace.Confirmed = "900";
		trace.Repair = "none";

		trace.Format(new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed)).ShouldBe(
			"delivery session=s1 sender=ScenarioOwner start=none gate-wait=0 admission=ok "
			+ "paste=bracketed shown=12 enter=260 confirmed=900 repair=none keyboard=no outcome=Confirmed");
	}
}
```

- [ ] **Step 2: Run to see them fail** — build: `DeliverPromptAsync`, `ISessionFacts`, `DeliveryTrace` missing.

- [ ] **Step 3: Implement**

`ISessionFacts.cs`:

```csharp
using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// What prompt delivery needs to know about a session but does not own: its retained screen
/// state, whether a scenario run locks it, and how to start its process the way activating
/// its tab would.
/// </summary>
public interface ISessionFacts
{
	/// <summary>The retained classifier state, or <see langword="null"/> before classification.</summary>
	SessionScreenState? ReadScreen(string sessionId);

	/// <summary>Whether a scenario run currently holds the session's input lock.</summary>
	bool IsScenarioLocked(string sessionId);

	/// <summary>Starts a stopped session in the background, without selecting its tab.</summary>
	Task StartAsync(string sessionId, CancellationToken cancellationToken);
}
```

`DeliveryTrace.cs`:

```csharp
using System.Diagnostics;
using Pact.Core.Sessions;

namespace Pact.Presentation.Services;

/// <summary>
/// One metadata-only line describing how a prompt delivery went, stage by stage. It never
/// holds prompt or screen text.
/// </summary>
public sealed class DeliveryTrace(string sessionId, PromptSender sender)
{
	private readonly long _startedAt = Stopwatch.GetTimestamp();

	public string Start { get; set; } = "none";
	public string GateWait { get; set; } = "0";
	public string Admission { get; set; } = "-";
	public string Paste { get; set; } = "-";
	public string Shown { get; set; } = "-";
	public string Enter { get; set; } = "-";
	public string Confirmed { get; set; } = "-";
	public string Repair { get; set; } = "none";
	public bool Keyboard { get; set; }

	/// <summary>Milliseconds since the delivery began, for the stage fields.</summary>
	public string Elapsed() =>
		((long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

	public string Format(PromptDeliveryResult result) =>
		$"delivery session={sessionId} sender={sender} start={Start} gate-wait={GateWait} "
		+ $"admission={Admission} paste={Paste} shown={Shown} enter={Enter} confirmed={Confirmed} "
		+ $"repair={Repair} keyboard={(Keyboard ? "yes" : "no")} outcome={result.Outcome}";
}
```

`SessionRuntimeCoordinator.cs` — add (the core of this task):

```csharp
	/// <summary>Session facts that prompt delivery reads; set once by the shell.</summary>
	public ISessionFacts? SessionFacts { get; set; }

	/// <summary>Receives one metadata-only line per prompt delivery.</summary>
	public Action<string>? DeliveryTrace { get; set; }

	/// <summary>
	/// Delivers <paramref name="text"/> to a session's visible TUI: admission, paste, a check
	/// that the paste showed, Enter, and confirmation from a fresh screen capture. The whole
	/// delivery holds the session against other programmatic senders. Text is never pasted
	/// twice; Enter is repeated once only when a fresh capture shows the text still waiting.
	/// </summary>
	public async Task<PromptDeliveryResult> DeliverPromptAsync(
		string sessionId,
		string text,
		bool submit,
		PromptSender sender,
		bool startIfNotRunning,
		CancellationToken cancellationToken)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
		ArgumentNullException.ThrowIfNull(text);
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		DeliveryTrace trace = new(sessionId, sender);
		var runtime = GetOrCreateRuntime(sessionId);
		var result = await runtime.RunDeliveryExclusiveAsync(
			() =>
			{
				trace.GateWait = trace.Elapsed();
				return DeliverUnderGateAsync(sessionId, text, submit, sender, startIfNotRunning, facts, trace, cancellationToken);
			},
			cancellationToken).ConfigureAwait(false);
		DeliveryTrace?.Invoke(trace.Format(result));
		return result;
	}

	private async Task<PromptDeliveryResult> DeliverUnderGateAsync(
		string sessionId,
		string text,
		bool submit,
		PromptSender sender,
		bool startIfNotRunning,
		ISessionFacts facts,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		if (!TryGetActiveController(sessionId, out _, out _))
		{
			if (!startIfNotRunning)
			{
				return new(PromptDeliveryOutcome.NotRunning, "the session is not running");
			}

			var readiness = await StartAndWaitReadyCoreAsync(sessionId, facts, cancellationToken).ConfigureAwait(false);
			trace.Start = readiness.IsReady ? "ok" : "not-ready";
			if (!readiness.IsReady)
			{
				return new(PromptDeliveryOutcome.NotRunning, readiness.StatusLine);
			}
		}

		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			return new(PromptDeliveryOutcome.NotRunning, "the session is not running");
		}

		var screen = facts.ReadScreen(sessionId);
		if (screen?.InputRequested == true)
		{
			trace.Admission = "question";
			return new(PromptDeliveryOutcome.RefusedQuestion, screen.StatusLine);
		}

		if (sender == PromptSender.Outsider && facts.IsScenarioLocked(sessionId))
		{
			trace.Admission = "locked";
			return new(PromptDeliveryOutcome.RefusedLocked);
		}

		trace.Admission = "ok";
		var profile = AgentScreenProfileSelector.ForKind(runtime.Kind);
		var keyboardAtEnter = 0L;
		var first = await runtime.RunInputExclusiveAsync<PromptDeliveryResult?>(
			async () =>
			{
				if (!await WritePasteAsync(runtime, controller, text, trace, cancellationToken).ConfigureAwait(false))
				{
					return new PromptDeliveryResult(PromptDeliveryOutcome.PasteNotShown, WriteAttempted: true);
				}

				if (!submit)
				{
					return new PromptDeliveryResult(PromptDeliveryOutcome.Confirmed, WriteAttempted: true);
				}

				if (_scenarioSubmitSettleDelay > TimeSpan.Zero)
				{
					await Task.Delay(_scenarioSubmitSettleDelay, cancellationToken).ConfigureAwait(false);
				}

				if (profile.ReadsComposer)
				{
					var check = await CaptureAsync(sessionId, profile, _preEnterCaptureTimeout, cancellationToken).ConfigureAwait(false);
					if (check is not { } beforeEnter)
					{
						return new PromptDeliveryResult(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true);
					}

					if (beforeEnter.Question is { } question)
					{
						return new PromptDeliveryResult(PromptDeliveryOutcome.RefusedQuestion, question, WriteAttempted: true);
					}
				}

				EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
				keyboardAtEnter = runtime.KeyboardInputCount;
				await WriteRequiredInputAsync(controller, runtime.EncodeEnter()).ConfigureAwait(false);
				trace.Enter = trace.Elapsed();
				return (PromptDeliveryResult?)null;
			},
			cancellationToken).ConfigureAwait(false);
		if (first is { } stopped)
		{
			return stopped;
		}

		if (!profile.ReadsComposer)
		{
			trace.Confirmed = "no-model";
			return Sent(PromptDeliveryOutcome.Submitted);
		}

		var observed = await ConfirmAsync(sessionId, runtime, profile, keyboardAtEnter, trace, cancellationToken).ConfigureAwait(false);
		if (observed.Result is { } settled)
		{
			return settled;
		}

		// Timed out. Repeat Enter only on fresh evidence that the text is still waiting.
		var repaired = await runtime.RunInputExclusiveAsync<PromptDeliveryResult?>(
			async () =>
			{
				if (runtime.KeyboardInputCount != keyboardAtEnter)
				{
					trace.Keyboard = true;
					return Sent(PromptDeliveryOutcome.Unconfirmed);
				}

				var fresh = await CaptureAsync(sessionId, profile, _preEnterCaptureTimeout, cancellationToken).ConfigureAwait(false);
				if (fresh is { Question: { } question })
				{
					return Sent(PromptDeliveryOutcome.RefusedQuestion, question);
				}

				if (fresh is { PromptIsEmpty: true })
				{
					trace.Confirmed = trace.Elapsed();
					return Sent(PromptDeliveryOutcome.Confirmed);
				}

				if (fresh is not { PromptIsEmpty: false })
				{
					return Sent(PromptDeliveryOutcome.Unconfirmed);
				}

				EnsureScenarioControllerIsCurrent(sessionId, runtime, controller);
				await WriteRequiredInputAsync(controller, runtime.EncodeEnter()).ConfigureAwait(false);
				trace.Repair = "enter";
				return (PromptDeliveryResult?)null;
			},
			cancellationToken).ConfigureAwait(false);
		if (repaired is { } repairStopped)
		{
			return repairStopped;
		}

		var second = await ConfirmAsync(sessionId, runtime, profile, keyboardAtEnter, trace, cancellationToken).ConfigureAwait(false);
		return second.Result ?? Sent(PromptDeliveryOutcome.Unconfirmed);

		static PromptDeliveryResult Sent(PromptDeliveryOutcome outcome, string statusLine = "") =>
			new(outcome, statusLine, WriteAttempted: true, SubmitAttempted: true);
	}

	// Polls fresh captures until the composer is seen empty, a question opens, the user types,
	// a capture goes unanswered (all settled), or the confirmation timeout passes (Result null).
	private async Task<(PromptDeliveryResult? Result, bool TimedOut)> ConfirmAsync(
		string sessionId,
		SessionRuntime runtime,
		IAgentScreenProfile profile,
		long keyboardAtEnter,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		var startedAt = Stopwatch.GetTimestamp();
		while (true)
		{
			var remaining = _confirmationTimeout - Stopwatch.GetElapsedTime(startedAt);
			if (remaining <= TimeSpan.Zero)
			{
				trace.Confirmed = "timeout";
				return (null, true);
			}

			if (runtime.KeyboardInputCount != keyboardAtEnter)
			{
				trace.Keyboard = true;
				return (new(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true, SubmitAttempted: true), false);
			}

			var reading = await CaptureAsync(sessionId, profile, remaining, cancellationToken).ConfigureAwait(false);
			if (reading is not { } seen)
			{
				trace.Confirmed = "no-capture";
				return (new(PromptDeliveryOutcome.Unconfirmed, WriteAttempted: true, SubmitAttempted: true), false);
			}

			if (seen.Question is { } question)
			{
				return (new(PromptDeliveryOutcome.RefusedQuestion, question, WriteAttempted: true, SubmitAttempted: true), false);
			}

			if (seen.PromptIsEmpty == true)
			{
				trace.Confirmed = trace.Elapsed();
				return (new(PromptDeliveryOutcome.Confirmed, WriteAttempted: true, SubmitAttempted: true), false);
			}

			var wait = _confirmationTimeout - Stopwatch.GetElapsedTime(startedAt);
			if (wait > TimeSpan.Zero)
			{
				await Task.Delay(wait < _statePollInterval ? wait : _statePollInterval, cancellationToken).ConfigureAwait(false);
			}
		}
	}

	private readonly record struct ScreenReading(string? Question, bool? PromptIsEmpty);

	// One capture, bounded by its own budget; null when the web view did not answer in time
	// or the session has no terminal.
	private async Task<ScreenReading?> CaptureAsync(
		string sessionId,
		IAgentScreenProfile profile,
		TimeSpan budget,
		CancellationToken cancellationToken)
	{
		using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		limit.CancelAfter(budget);
		try
		{
			var snapshot = await _terminalHost.CaptureScreenAsync(sessionId, limit.Token).ConfigureAwait(false);
			if (snapshot is null)
			{
				return null;
			}

			var verdict = profile.Classify(snapshot);
			return new ScreenReading(
				verdict.State == TerminalScreenVerdictState.InputRequested ? verdict.Description : null,
				verdict.PromptIsEmpty);
		}
		catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
		{
			return null;
		}
	}

	/// <summary>
	/// Starts a stopped session through <see cref="SessionFacts"/> and waits until it can
	/// receive a prompt (see <see cref="WaitForSessionReadyAsync(string, CancellationToken)"/>).
	/// A running session is reported ready without waiting.
	/// </summary>
	public Task<SessionReadiness> StartAndWaitReadyAsync(string sessionId, CancellationToken cancellationToken)
	{
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		return TryGetActiveController(sessionId, out _, out _)
			? Task.FromResult(new SessionReadiness(true, string.Empty))
			: StartAndWaitReadyCoreAsync(sessionId, facts, cancellationToken);
	}

	private async Task<SessionReadiness> StartAndWaitReadyCoreAsync(
		string sessionId,
		ISessionFacts facts,
		CancellationToken cancellationToken)
	{
		GetOrCreateRuntime(sessionId).PrepareForControllerStart();
		await facts.StartAsync(sessionId, cancellationToken).ConfigureAwait(false);
		return await WaitForSessionReadyAsync(sessionId, () => facts.ReadScreen(sessionId), cancellationToken)
			.ConfigureAwait(false);
	}

	/// <summary>Waits for readiness reading the screen through <see cref="SessionFacts"/>.</summary>
	public Task<SessionReadiness> WaitForSessionReadyAsync(string sessionId, CancellationToken cancellationToken)
	{
		var facts = SessionFacts ?? throw new InvalidOperationException("Session facts are not configured.");
		return WaitForSessionReadyAsync(sessionId, () => facts.ReadScreen(sessionId), cancellationToken);
	}

	/// <summary>
	/// Pastes text the way a keyboard paste would: no admission, no Enter, no confirmation.
	/// Counts as keyboard input for a delivery in progress.
	/// </summary>
	public async Task PasteAsKeyboardAsync(string sessionId, string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		if (!TryGetActiveController(sessionId, out var runtime, out var controller))
		{
			throw new InvalidOperationException("Target session is not running.");
		}

		runtime.NoteKeyboardInput();
		await runtime.RunInputExclusiveAsync(
			async () =>
			{
				await WriteRequiredInputAsync(controller, BuildPastedInput(text)).ConfigureAwait(false);
				return true;
			},
			CancellationToken.None).ConfigureAwait(false);
	}
```

`WritePasteAsync` — new overload used by deliveries (keep the old one for the old methods until Task 6 deletes them):

```csharp
	// Returns false when the paste did not show within the landing timeout.
	private async Task<bool> WritePasteAsync(
		SessionRuntime runtime,
		TerminalController controller,
		string text,
		DeliveryTrace trace,
		CancellationToken cancellationToken)
	{
		var viaClipboard = ClientPasteClipboard is { } clipboard
			&& runtime.InputModes.IsSgrMouseActive
			&& await OfferOnClipboardAsync(clipboard, text).ConfigureAwait(false);
		trace.Paste = viaClipboard ? "clipboard" : "bracketed";
		using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		// The wait starts before the write, so output from a fast paste cannot be missed.
		var landed = runtime.WaitForClientPasteAsync(text, _pasteLandingTimeout, stopWaiting.Token);
		try
		{
			await WriteRequiredInputAsync(controller, viaClipboard ? ClientPasteClick : BuildPastedInput(text))
				.ConfigureAwait(false);
		}
		catch
		{
			await stopWaiting.CancelAsync().ConfigureAwait(false);
			await landed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
			throw;
		}

		try
		{
			await landed.ConfigureAwait(false);
			trace.Shown = trace.Elapsed();
			return true;
		}
		catch (TimeoutException)
		{
			trace.Shown = "timeout";
			return false;
		}
	}
```

`WriteInputAsync` (keyboard): add `runtime.NoteKeyboardInput();` immediately before its `RunInputExclusiveAsync` call. Add `using Pact.Core.ScreenVerdictProfiles;` if not present.

- [ ] **Step 4: Build; run Presentation tests** — all pass (old tests untouched).

- [ ] **Step 5: Commit**

```bash
git add src/Pact.Presentation tests/Pact.Presentation.Tests
git commit -m "feat: deliver prompts under one gate with fresh-capture confirmation"
```

---

### Task 5: Migrate manual, MCP and workstation-lock callers

**Files:**
- Create: `src/Pact.App.Avalonia/Controllers/ShellSessionFacts.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` (`SendTextToSessionAsync` ~3399, `PasteAsync` ~3008, `DeliverWorkstationLockPromptAsync` ~2091, `SendOrchestratorMessageAsync` ~2137, review readiness call ~2366, constructor ~140)
- Modify: `src/Pact.Presentation/Services/Orchestrator/IOrchestratorHost.cs`, `OrchestratorDispatcher.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/OrchestratorHost.cs`
- Modify: `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs` (delete `SendPromptAsync`)
- Modify: `tests/Pact.App.Avalonia.Tests/Fakes/FakeTerminalBackend.cs` (echo pastes)
- Test: `tests/Pact.Presentation.Tests/Services/Orchestrator/OrchestratorDispatcherTests.cs` (find the existing file with `grep -rln "OrchestratorDispatcher" tests`), `AvaloniaMainShellControllerTests.cs`, coordinator tests using `SendPromptAsync`

**Interfaces:**
- Consumes: `DeliverPromptAsync`, `PasteAsKeyboardAsync`, `SessionFacts`, `DeliveryTrace`, `WaitForSessionReadyAsync(string, CancellationToken)` (Task 4).
- Produces: `Task<PromptDeliveryResult> IOrchestratorHost.SendMessageAsync(string sessionId, string text, CancellationToken cancellationToken)` (return type changes from `Task`).

- [ ] **Step 1: Failing tests**

Orchestrator dispatcher (fake host returns a configurable `PromptDeliveryResult`):

```csharp
	[TestCase(PromptDeliveryOutcome.Confirmed, null)]
	[TestCase(PromptDeliveryOutcome.Submitted, null)]
	[TestCase(PromptDeliveryOutcome.RefusedQuestion, "input-requested")]
	[TestCase(PromptDeliveryOutcome.RefusedLocked, "session-scenario-locked")]
	[TestCase(PromptDeliveryOutcome.NotRunning, "session-not-alive")]
	[TestCase(PromptDeliveryOutcome.Unconfirmed, "not-confirmed")]
	[TestCase(PromptDeliveryOutcome.PasteNotShown, "paste-not-shown")]
	public async Task Send_message_maps_the_delivery_outcome(PromptDeliveryOutcome outcome, string? code)
	{
		// arrange the dispatcher's fake host so SendMessageAsync returns new(outcome, "Approve?")
		// act: SendMessageAsync("target", "hello", ct)
		// assert: code null → result is Ok; else result is Refused with that code
	}

	[Test]
	public async Task Send_message_to_a_busy_session_is_sent()
	{
		// fake host TryGetScreen returns a busy screen; SendMessageAsync is called and Ok returned
	}
```

Remove the dispatcher tests asserting that `IsScenarioLocked`/`TryGetScreen` refuse before sending (the sender now decides); keep the `session-not-alive` early check and self-target tests.

Shell (`AvaloniaMainShellControllerTests`):

```csharp
	[Test]
	public async Task A_template_send_to_a_session_with_a_question_writes_nothing_and_reports_it()
	{
		// arrange a running Codex session whose TerminalTabStatuses screen is a question
		// (use the existing helper that feeds a screen snapshot to the status engine);
		// act: send a selection/template to it
		// assert: backend.Inputs has no paste; StatusText mentions "waiting for an answer"
	}
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement**

`ShellSessionFacts.cs`:

```csharp
using Pact.Core.Sessions;
using Pact.Presentation.Services;

namespace Pact.App.Avalonia.Controllers;

/// <summary>The shell's answers to what prompt delivery needs to know about a session.</summary>
internal sealed class ShellSessionFacts(
	Func<string, SessionScreenState?> readScreen,
	Func<string, bool> isScenarioLocked,
	Func<string, CancellationToken, Task> startAsync) : ISessionFacts
{
	public SessionScreenState? ReadScreen(string sessionId) => readScreen(sessionId);

	public bool IsScenarioLocked(string sessionId) => isScenarioLocked(sessionId);

	public Task StartAsync(string sessionId, CancellationToken cancellationToken) => startAsync(sessionId, cancellationToken);
}
```

Shell constructor, after `_runtimeCoordinator` and `ViewModel` exist:

```csharp
		_runtimeCoordinator.SessionFacts = new ShellSessionFacts(
			ReadScreenState,
			sessionId =>
			{
				var locked = false;
				_uiTaskDispatcher.Post(() => locked = ViewModel.FindSession(sessionId)?.IsLockedByScenario == true);
				return locked;
			},
			StartSessionByIdAsync);
		_runtimeCoordinator.DeliveryTrace = AppendDiagnosticLog;
```

```csharp
	private async Task StartSessionByIdAsync(string sessionId, CancellationToken cancellationToken)
	{
		SessionViewModel? session = null;
		_uiTaskDispatcher.Post(() => session = ViewModel.FindSession(sessionId));
		if (session is null)
		{
			throw new InvalidOperationException($"Session '{sessionId}' is not registered.");
		}

		await StartPromptRuntimeAsync(session, cancellationToken);
	}
```

`SendTextToSessionAsync`:

```csharp
	private async Task SendTextToSessionAsync(SessionViewModel target, string text, bool submit, CancellationToken cancellationToken)
	{
		var result = await _runtimeCoordinator.DeliverPromptAsync(
			target.Record.Id, text, submit, PromptSender.Outsider, startIfNotRunning: true, cancellationToken);
		if (DescribeUndelivered(result) is { } reason)
		{
			await ReportStatusAsync($"{target.Title}: {reason}");
		}
	}

	private static string? DescribeUndelivered(PromptDeliveryResult result) => result.Outcome switch
	{
		PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Submitted => null,
		PromptDeliveryOutcome.RefusedQuestion when result.WriteAttempted =>
			$"a question opened after the text was pasted ({result.StatusLine}); it was not submitted",
		PromptDeliveryOutcome.RefusedQuestion => $"waiting for an answer ({result.StatusLine}); nothing was sent",
		PromptDeliveryOutcome.RefusedLocked => "locked by a running scenario; nothing was sent",
		PromptDeliveryOutcome.NotRunning => $"not running ({result.StatusLine}); nothing was sent",
		PromptDeliveryOutcome.PasteNotShown => "the pasted text never showed, so it was not submitted",
		PromptDeliveryOutcome.Unconfirmed => "sent, but the agent was not seen taking it",
		_ => $"delivery ended as {result.Outcome}"
	};
```

`PasteAsync` (Ctrl+V): keep the lock check it has via `IsLockedByScenario`, then `await _runtimeCoordinator.PasteAsKeyboardAsync(selectedSession.Record.Id, text);` (if the session is locked, call `ReportStatusAsync("Terminal is locked by a running scenario")` and return).

`SendOrchestratorMessageAsync` → returns the result:

```csharp
	private Task<PromptDeliveryResult> SendOrchestratorMessageAsync(SessionViewModel target, string text, CancellationToken cancellationToken) =>
		_runtimeCoordinator.DeliverPromptAsync(
			target.Record.Id, text, submit: true, PromptSender.Outsider, startIfNotRunning: false, cancellationToken);
```

Update the delegate type passed to `OrchestratorHost` and `OrchestratorHost.SendMessageAsync` to return `Task<PromptDeliveryResult>` (`return await _uiTaskDispatcher.InvokeAsync(() => _sendMessageAsync(session, text, cancellationToken));` — check `IUiTaskDispatcher.InvokeAsync` has a `Func<Task<T>>` overload; if not, capture the result in a local inside the lambda).

`OrchestratorDispatcher.SendMessageAsync`: delete the `IsScenarioLocked` and `TryGetScreen` refusal blocks; replace the tail with:

```csharp
		var delivery = await _host.SendMessageAsync(sessionId, text, cancellationToken).ConfigureAwait(false);
		return delivery.Outcome switch
		{
			PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Submitted => AgentControlResult.Ok(),
			PromptDeliveryOutcome.RefusedQuestion => AgentControlResult.Refused(
				"input-requested",
				delivery.WriteAttempted
					? $"Session '{sessionId}' opened a question ({delivery.StatusLine}) after the text was pasted; it was not submitted."
					: $"Session '{sessionId}' is waiting for a human answer ({delivery.StatusLine}); nothing was sent."),
			PromptDeliveryOutcome.RefusedLocked => AgentControlResult.Refused(
				"session-scenario-locked",
				_host.IsScenarioLocked(sessionId, out var runId)
					? $"Session '{sessionId}' is controlled by review run '{runId}'."
					: $"Session '{sessionId}' is controlled by a review run."),
			PromptDeliveryOutcome.NotRunning => AgentControlResult.Refused(
				"session-not-alive",
				$"Session '{sessionId}' has no live terminal; start it with pact_start_session."),
			PromptDeliveryOutcome.PasteNotShown => AgentControlResult.Refused(
				"paste-not-shown",
				$"The text never showed in session '{sessionId}', so it was not submitted."),
			_ => AgentControlResult.Refused(
				"not-confirmed",
				$"The prompt was submitted to session '{sessionId}' but the agent was not seen taking it; "
					+ "check the session with pact_get_session before sending again.")
		};
```

`DeliverWorkstationLockPromptAsync`: remove the explicit `screen?.InputRequested` block; replace the send with:

```csharp
		var result = await _runtimeCoordinator.DeliverPromptAsync(
			_orchestratorSession.Record.Id, prompt, submit: true, PromptSender.Outsider, startIfNotRunning: false, CancellationToken.None);
		if (DescribeUndelivered(result) is { } reason)
		{
			ViewModel.OrchestratorSlot.Apply(
				isProvisioned: _orchestratorRecord.IsProvisioned,
				isEnabled: _orchestratorRecord.Enabled,
				isRunning: true,
				stateText: $"{(locked ? "Lock" : "Unlock")} prompt: {reason}");
		}
```

Review readiness (~2366): replace the delegate overload call with `_runtimeCoordinator.WaitForSessionReadyAsync(sessionId, cancellationToken)`.

`SessionRuntimeCoordinator.SendPromptAsync`: delete it and the old bool `WritePasteAsync(..., awaitLanding, ...)` only if `WriteScenarioPromptAndSubmitAsync` no longer uses it (it still does until Task 6 — keep that overload). Delete coordinator tests that call `SendPromptAsync` whose behavior is covered by Task 4 tests; port the remaining ones (`A_programmatic_prompt_is_one_bulk_bracketed_paste`, `A_submitted_prompt_to_codex_in_win32_mode_uses_the_encoded_enter`, clipboard tests, `Typed_input_waits_until_a_paste_and_its_enter_are_written`) to `DeliverPromptAsync` using `CreateDeliverySessionAsync`, with the clipboard tests arranging `clickOutput` echo as today.

`FakeTerminalBackend`: add `public bool EchoPastes { get; set; } = true;` and in `WriteAsync`, after `Inputs.Add(text)`:

```csharp
		if (EchoPastes && text.StartsWith("\u001b[200~", StringComparison.Ordinal) && text.EndsWith("\u001b[201~", StringComparison.Ordinal))
		{
			EmitOutput(text[6..^6]);
		}
```

App tests that assert an Enter after a send to a Codex/Claude session must set `FakeTerminalWebViewHost.CaptureScreen` to return an empty-composer screen after the Enter (add a shared helper in the test file); sends to `Pwsh` sessions need nothing.

- [ ] **Step 4: Build; run all three test projects** (one at a time). Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "refactor: send manual, MCP and lock prompts through the one sender"
```

---

### Task 6: Scenario delivery without resends

**Files:**
- Modify: `src/Pact.Presentation/Services/IScenarioTerminalGateway.cs`, `MainWindowScenarioGateway.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` (`SendScenarioPromptAndSubmitAsync` ~2909) and `AvaloniaScenarioCoordinator.cs` (delegate type)
- Modify: `src/Pact.Presentation/Services/ScenarioRunService.cs` (`ScenarioIterationContext.SendAsync` ~331, `WaitForResponseAsync` ~443, final notice sender — `grep -n "confirmDelivery: false" src`)
- Modify: `src/Pact.Presentation/Services/Scenarios/ReviewLoopScenarioProgram.cs` (~250–310)
- Modify: `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs` (delete `WriteScenarioPromptAndSubmitAsync`, `Observation`, the bool-`awaitLanding` `WritePasteAsync`, `WriteSubmitAfterPasteAsync` if unused, the `ClientPasteLandingTimeout` constant, `_quietComposerPeriod` and its constructor parameter)
- Modify: `src/Pact.Core/Sessions/PromptDeliveryResult.cs` (delete `Written`, `BlockedByBusy`, `BlockedByInputRequest`, `BlockedByPendingInput`; `IsSent` becomes `SubmitAttempted && Outcome is Confirmed or Submitted or Unconfirmed`)
- Test: `ScenarioRunServiceTests.cs`, `Scenarios/FakeScenarioTerminalGateway.cs`, `MainWindowScenarioGatewayTests.cs`, `SessionRuntimeCoordinatorTests.cs`, `ScenarioJournalHeadlessTests.cs`, `MainWindowViewModelTests.cs`, `ScenarioRunViewModelTests.cs`, `WorkspaceViewModelTests.cs`, `AvaloniaScenarioRuntimeTests.cs`

**Interfaces:**
- Consumes: `DeliverPromptAsync` (Task 4).
- Produces: `IScenarioTerminalGateway.SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)` (no `confirmDelivery`); `ScenarioIterationContext.WaitForResponseAsync(string stepId, string role, Func<TimeSpan, CancellationToken, Task<string>> waitForResponseAsync, CancellationToken cancellationToken)` (no recovery parameter).

- [ ] **Step 1: Failing tests** (`ScenarioRunServiceTests`)

Replace `SendAsync_WrittenDeliveryRetriesAutomatically` with:

```csharp
	[Test]
	public async Task SendAsync_unconfirmed_delivery_pauses_for_attention_without_resending()
	{
		// gateway.DeliveryResult = new(PromptDeliveryOutcome.Unconfirmed, "", true, true)
		// run a program that calls SendAsync once, then waits for a response delivered by the test
		// assert: gateway.Sent.Count == 1; run state Paused with StuckSessionId == the session;
		// after the test supplies the response, state returns to Running and the run completes;
		// gateway.Sent.Count still 1
	}

	[TestCase(PromptDeliveryOutcome.PasteNotShown)]
	[TestCase(PromptDeliveryOutcome.RefusedQuestion)] // with WriteAttempted: true
	public async Task SendAsync_written_but_unsubmitted_delivery_is_never_retried(PromptDeliveryOutcome outcome)
	{
		// same shape as above; DeliveryResult = new(outcome, "Approve?", WriteAttempted: true)
		// assert Sent.Count == 1 after 1 s of fake progress (use the existing TestClock/time helpers
		// in this file, not sleeps)
	}

	[Test]
	public async Task SendAsync_question_before_writing_retries_after_it_clears()
	{
		// DeliveryResults: RefusedQuestion (WriteAttempted false), then Confirmed
		// assert Sent.Count == 2 and the run is Running after the second
	}

	[Test]
	public async Task SendAsync_not_running_retries_after_the_session_returns()
	{
		// DeliveryResults: NotRunning, then Confirmed → Sent.Count == 2
	}
```

Replace the watchdog recovery tests (`ReviewLoop_watchdog_retries_delivery_while_response_observa…`, `ReviewLoop_watchdog_never_overlaps_recovery_with_initial_de…`, `Manual_pause_suppresses_watchdog_delivery_recovery_until_re…`, `Manual_pause_escalated_during_recovery_send_survives_delive…`) with:

```csharp
	[Test]
	public async Task ReviewLoop_watchdog_pauses_without_resending_and_a_late_response_resumes()
	{
		// step watchdog = short (constructor option used by the existing watchdog tests);
		// gateway confirms the first send; no response until after the watchdog fires;
		// assert: state Paused (attention) after the watchdog, Sent.Count == 1;
		// then write the response file; assert: state Running/advanced, locks re-applied,
		// Sent.Count still 1
	}
```

`ReviewLoop_watchdog_keeps_observing_response_and_clears_pause_without_resume` stays and must pass unchanged in meaning.

Remove all `BlockedByBusy` polling tests.

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement**

Gateway: drop `confirmDelivery` from `IScenarioTerminalGateway.SendPromptAsync`, `MainWindowScenarioGateway` (constructor delegate becomes `Func<string, string, CancellationToken, Task<PromptDeliveryResult>>`), `FakeScenarioTerminalGateway`, the `FakeGateway` in `ScenarioRunServiceTests`, and the final-notice call site.

Shell:

```csharp
	private Task<PromptDeliveryResult> SendScenarioPromptAndSubmitAsync(
		string sessionId,
		string prompt,
		CancellationToken cancellationToken) =>
		_runtimeCoordinator.DeliverPromptAsync(
			sessionId, prompt, submit: true, PromptSender.ScenarioOwner, startIfNotRunning: false, cancellationToken);
```

`ScenarioIterationContext.SendAsync` — replace from `if (delivery.IsConfirmed)` to the end of the loop body with:

```csharp
			if (delivery.Outcome is PromptDeliveryOutcome.Confirmed or PromptDeliveryOutcome.Submitted)
			{
				_handle.ResumeAfterDelivery();
				return;
			}

			var retry = delivery.Outcome == PromptDeliveryOutcome.NotRunning
				|| (delivery.Outcome == PromptDeliveryOutcome.RefusedQuestion && !delivery.WriteAttempted);
			var reason = delivery.Outcome switch
			{
				PromptDeliveryOutcome.RefusedQuestion when !delivery.WriteAttempted =>
					$"the agent is waiting for an answer in its terminal ({delivery.StatusLine}) - nothing "
						+ "was sent; Pact sends the trigger once the question is answered",
				PromptDeliveryOutcome.NotRunning =>
					$"the session is not running ({delivery.StatusLine}) - nothing was sent; Pact sends the "
						+ "trigger once it is running again",
				PromptDeliveryOutcome.RefusedQuestion =>
					$"a question opened after the trigger was typed ({delivery.StatusLine}) - it may still "
						+ "be in the input; answer the question and submit it yourself. Pact never resends "
						+ "it and keeps watching the response file",
				PromptDeliveryOutcome.PasteNotShown =>
					"the trigger never showed in the terminal and was not submitted - type the task path "
						+ "yourself; Pact never resends it and keeps watching the response file",
				_ =>
					"the trigger was submitted but the agent was not seen taking it - check the terminal; "
						+ "Pact never resends it and keeps watching the response file"
			};
			if (!retry)
			{
				await _handle.PauseForBlockedDeliveryAsync(stepId, sessionId, reason, TimeSpan.Zero, cancellationToken)
					.ConfigureAwait(false);
				return;
			}

			await _handle.PauseForBlockedDeliveryAsync(
				stepId,
				sessionId,
				reason,
				DeliveryRetryDelay,
				cancellationToken).ConfigureAwait(false);
```

Check `PauseForBlockedDeliveryAsync` with `TimeSpan.Zero`: it pauses for attention and, unless manually paused, returns after `Task.Delay(0)`. When a manual pause is active it waits for resume — acceptable: the step then waits for the response file. If that wait is undesirable, call `PauseForTimeoutAsync`-style `PauseForAttentionAsync(..., waitForResume: false, ...)` through a new `internal Task PauseForUndeliveredAsync(string stepId, string sessionId, string reason, CancellationToken ct)` that does only the attention pause; prefer this new method.

`WaitForResponseAsync`: delete the `recoverAfterTimeoutAsync` parameter, `recoveryTask`, `recoveryCancellation`, `ObserveCompletedRecoveryAsync`, `StopRecoveryAsync` and their calls; the `ScenarioStepTimeoutException` catch becomes:

```csharp
				catch (ScenarioStepTimeoutException)
				{
					await _handle.PauseForTimeoutAsync(stepId, sessionId, cancellationToken).ConfigureAwait(false);
					continue;
				}
```

Update its XML doc: the watchdog pauses for attention and keeps waiting; it never resends.

`ReviewLoopScenarioProgram`: delete `initialDeliveryReady` and the recovery lambda argument; keep `Task.WhenAny(responseTask, deliveryTask)` and the cancellation of `deliveryTask` when the response wins.

Coordinator and Core: delete the members listed under Files. Fix every compile error in tests by mapping old outcomes: `Written` → `Unconfirmed`; `BlockedByInputRequest` → `RefusedQuestion`; delete tests whose subject was busy/pending-input refusal or repaste (`A_busy_session_refuses_delivery_without_writing`, `A_dropped_paste_is_repaired_after_the_composer_stays_empty_and_quiet`, `A_composer_holding_text_is_refused_without_writing`, `Styled_composer_uses_existing_delivery_gate`, `An_unreadable_state_is_written_once_without_repair`, `A_new_busy_cycle_confirms_delivery`, `Codex_win32_mode_uses_encoded_enter_and_confirms_delivery`, `A_dropped_submit_is_repaired_with_enter_alone`, `An_agent_that_never_reacts_ends_the_step_written`, `A_question_appearing_after_the_paste_blocks_the_repair`, `A_pending_question_is_refused_without_writing`, `An_unconfirmed_send_writes_exactly_once`, `A_scenario_trigger_to_a_mouse_tracking_client_is_entered_after_its_paste_shows`, `WriteScenarioPromptAndSubmitAsync_ControllerReplacedDuringSettle_DoesNotSubmitToEitherController`). Port the last one to `DeliverPromptAsync` (controller replaced during settle → throws `InvalidOperationException`, replacement gets nothing) since the guard still exists. Readiness tests keep using the delegate overload of `WaitForSessionReadyAsync`, which stays (internal callers use it).

- [ ] **Step 4: Build; run all three test projects** (one at a time). Expected: all pass. Run `ScenarioRunServiceTests` 3 times to check for flakes.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "refactor: never resend a scenario trigger; the watchdog only pauses"
```

---

### Task 7: `pact_start_session`

**Files:**
- Modify: `src/Pact.Infrastructure/AgentControl/OrchestratorToolCatalog.cs`, `src/Pact.Infrastructure/Orchestrator/HermesTemplates.cs`
- Modify: `src/Pact.Presentation/Services/Orchestrator/IOrchestratorHost.cs`, `OrchestratorDispatcher.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/OrchestratorHost.cs`, `AvaloniaMainShellController.cs` (tool switch ~632)
- Test: dispatcher tests; catalog test (find with `grep -rln "OrchestratorToolCatalog" tests`)

**Interfaces:**
- Consumes: `StartAndWaitReadyAsync` (Task 4).
- Produces: `Task<SessionStartOutcome> IOrchestratorHost.StartSessionAsync(string sessionId, CancellationToken cancellationToken)` with

```csharp
public enum SessionStartStatus { Ready, AlreadyRunning, NotFound, NotReady }
public readonly record struct SessionStartOutcome(SessionStartStatus Status, string Reason = "");
```

(put both in `IOrchestratorHost.cs` next to the other host records); `Task<AgentControlResult> OrchestratorDispatcher.StartSessionAsync(string sessionId, CancellationToken cancellationToken)`.

- [ ] **Step 1: Failing tests**

```csharp
	[TestCase(SessionStartStatus.Ready, null)]
	[TestCase(SessionStartStatus.AlreadyRunning, "already-running")]
	[TestCase(SessionStartStatus.NotFound, "session-not-found")]
	[TestCase(SessionStartStatus.NotReady, "not-ready")]
	public async Task Start_session_maps_the_host_outcome(SessionStartStatus status, string? code)
	{
		// fake host StartSessionAsync returns new(status, "Pick a model")
		// code null → Ok; else Refused(code), and for not-ready the message contains "Pick a model"
	}

	[Test]
	public void The_orchestrator_catalog_offers_start_session()
	{
		// OrchestratorToolCatalog tools contain "pact_start_session" with required "sessionId"
	}
```

The self-target rule applies: `StartSessionAsync` on the orchestrator's own id → `Refused("self-target", ...)`.

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement**

Catalog, after `pact_send_message`:

```csharp
			Tool(
				"pact_start_session",
				"Start a session that is not running, the same way opening its tab would, and wait "
					+ "until it can take a prompt. pact_send_message refuses sessions that are not running.",
				new JsonObject
				{
					["sessionId"] = StringProperty("Session to start.")
				},
				"sessionId"),
```

HermesTemplates, after the `pact_send_message` line:

```text
		- `pact_start_session`: start a stopped session and wait until it can take a prompt.
```

Dispatcher:

```csharp
	/// <summary>Starts a stopped session and waits until it can receive a prompt.</summary>
	public async Task<AgentControlResult> StartSessionAsync(string sessionId, CancellationToken cancellationToken)
	{
		if (string.Equals(sessionId, _host.OrchestratorSessionId, StringComparison.Ordinal))
		{
			return AgentControlResult.Refused("self-target", "The orchestrator cannot start its own session.");
		}

		var outcome = await _host.StartSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
		return outcome.Status switch
		{
			SessionStartStatus.Ready => AgentControlResult.Ok(),
			SessionStartStatus.AlreadyRunning => AgentControlResult.Refused("already-running", $"Session '{sessionId}' is already running."),
			SessionStartStatus.NotFound => AgentControlResult.Refused("session-not-found", $"Session '{sessionId}' does not exist."),
			_ => AgentControlResult.Refused("not-ready", $"Session '{sessionId}' started but is not ready: {outcome.Reason}.")
		};
	}
```

`OrchestratorHost.StartSessionAsync`: find the session via `_uiTaskDispatcher.Post(() => session = FindSession(sessionId))` → `NotFound`; `_isSessionAlive(sessionId)` → `AlreadyRunning`; else `var readiness = await _startAndWaitReadyAsync(sessionId, cancellationToken)` (new constructor delegate wired from the shell to `_runtimeCoordinator.StartAndWaitReadyAsync`) → `Ready` or `NotReady` with `readiness.StatusLine`.

Shell tool switch: add `case "pact_start_session":` reading `sessionId` with `TryGetRequiredString` exactly like `pact_send_message`, then `result = await _orchestratorDispatcher.StartSessionAsync(targetSessionId, cancellationToken);`.

- [ ] **Step 4: Build; run Presentation and App tests.**

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: let the orchestrator start a session and wait until it is ready"
```

---

### Task 8: SendTo explains when it does nothing

**Files:**
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs` (`SendSelectionToSessionAsync` ~1255)
- Test: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`

**Interfaces:** none new.

- [ ] **Step 1: Failing tests**

```csharp
	[Test]
	public async Task SendTo_with_nothing_selected_says_so()
	{
		// no selection snapshot, host GetSelectedTextAsync returns "", clipboard empty
		// act: SendSelectionToSessionAsync(target) → false
		// assert: StatusText == "Nothing to send: no selected text."
	}

	[Test]
	public async Task SendTo_from_a_selection_whose_tab_changed_says_so()
	{
		// capture a selection in session A, then select session B, then SendSelectionToSessionAsync(C)
		// assert: false; StatusText == "Nothing sent: the selection's terminal is no longer the active one."
	}
```

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement** — in `SendSelectionToSessionAsync`:

```csharp
		if (snapshot is not null && !IsSelectionSourceCurrent(snapshot.Source))
		{
			ClearSelectionActions(restoreTerminalFocus: false);
			AppendDiagnosticLog($"selection-send target={target.Record.Id} reason=stale-source");
			await ReportStatusAsync("Nothing sent: the selection's terminal is no longer the active one.");
			return false;
		}
```

```csharp
		if (string.IsNullOrEmpty(selectedText))
		{
			AppendDiagnosticLog($"selection-send target={target.Record.Id} reason=empty-selection");
			await ReportStatusAsync("Nothing to send: no selected text.");
			return false;
		}
```

and wrap the router call:

```csharp
		try
		{
			await router.SendToSessionAsync(target, selectedText, cancellationToken);
		}
		catch (InvalidOperationException exception)
		{
			AppendDiagnosticLog($"selection-send target={target.Record.Id} reason=refused");
			await ReportStatusAsync($"Nothing sent: {exception.Message}");
			return false;
		}
```

(Delivery outcomes are already reported by `SendTextToSessionAsync` from Task 5 and traced by `DeliveryTrace`.)

- [ ] **Step 4: Build; run App tests.**

- [ ] **Step 5: Commit**

```bash
git add src/Pact.App.Avalonia tests/Pact.App.Avalonia.Tests
git commit -m "fix: say why sending a selection did nothing"
```

---

### Task 9: Contract documents

**Files:**
- Modify: `AGENTS.md` (scenario rules ~97–125)
- Modify: `docs/architecture.md` (scenario delivery ~225–245; runtime coordinator paragraph added in step 2)
- Modify: `docs/agent-onboarding.md` (~119–140 delivery; ~175 Logs entry; orchestrator tool list if present)
- Modify: `docs/backlog/2026-10-09-development-directions.md` (add Ctrl+V item)
- Modify: `docs/backlog/2026-10-14-architecture-analysis.md` (step 3 done)

- [ ] **Step 1: AGENTS.md** — replace the sentences from "A run writes only into a session that is idle…" through "…whether anything was typed." with:

```text
  A run writes into its session whenever no question is pending: a busy agent
  queues the trigger, and text already in the composer does not block it. A
  freshly launched session first reaches readiness through one bounded budget
  covering its first output, a settle delay, and the folder-trust dialog —
  answered with a single Enter, once per launched session. Every prompt Pact
  sends, from any channel, goes through one sender that holds the session for
  the whole delivery: the paste must show before Enter, a question seen just
  before Enter stops it, and delivery is confirmed only by a fresh screen
  capture showing an empty composer after Enter. Pact's own Enter is never
  evidence. If the composer still holds the text, Enter alone is repeated
  once; text is never pasted twice. An unconfirmed delivery pauses the run for
  attention and is never resent; the response file still advances the run.
```

and replace the watchdog/retry wording in "The response-file observer starts before prompt delivery and remains active through delivery retries, watchdog attention, and manual pause. Automatic…" so that it says the watchdog pauses for attention after 15 minutes, keeps observing, never resends, and a footer-complete response re-locks and resumes. Read the whole bullet first and keep every other rule in it.

- [ ] **Step 2: architecture.md** — rewrite the paragraph ending "…repeats the paste only after the composer remains empty while the agent is idle." with the same rules as Step 1 in this document's voice, and extend the step-2 runtime coordinator sentence: "A prompt delivery additionally holds a per-session delivery gate for its whole cycle and confirms itself from `ITerminalWebViewHost.CaptureScreenAsync`, a capture taken after pending output renders, classified with the session's profile."

- [ ] **Step 3: agent-onboarding.md** — update the delivery paragraph (~119–140) the same way; in the `Logs` entry (~175) add: "Every prompt delivery appends one metadata-only `delivery …` line naming the session, sender, start, admission, paste kind, stage timings, repair and outcome." If the onboarding lists orchestrator tools, add `pact_start_session`.

- [ ] **Step 4: Backlogs** — in `2026-10-09-development-directions.md` add an item: "**Ctrl+V into a terminal.** The paste command path (`PasteAsync` → `PasteAsKeyboardAsync`) does not work reliably in any agent; investigate per agent (Codex win32 input, Claude mouse capture)." In the analysis doc, after step 3 in «Порядок рефакторинга», add: "*Сделано 2026-10-14: `DeliverPromptAsync`, подтверждение по свежему снимку, повторов текста нет; спека `docs/superpowers/specs/2026-10-14-prompt-delivery-design.md`.*"

- [ ] **Step 5: Check and commit**

Run: `git diff --check HEAD -- AGENTS.md docs`
Expected: no output.

```bash
git add AGENTS.md docs
git commit -m "docs: describe the one prompt sender and the no-resend scenario rules"
```

---

## Final verification

- [ ] Full build, 0 warnings.
- [ ] Core, Presentation, App tests, one project at a time; all pass. Rerun `ScenarioRunServiceTests` 3× for flakes.
- [ ] `grep -rn "WriteScenarioPromptAndSubmitAsync\|SendPromptAsync(\|BlockedByBusy\|BlockedByPendingInput\|recoverAfterTimeoutAsync" src tests` → only `IScenarioTerminalGateway.SendPromptAsync` and its implementations remain.
- [ ] Hand the user the live-check list from the spec (busy Codex/Claude, placeholder redraw, Codex Enter repair, fast answer, PowerShell `Submitted`, SendTo Claude→Codex) and where the `delivery …` lines land (`%APPDATA%\Pact\Logs`).
