# Web-tab monitoring protocol

## Purpose and prerequisites

Verify loaded-page monitoring, retained unread state, timeout recovery, and
background execution. Launch the exact candidate with a fresh
`--data-root C:\pact-test\web-monitoring`. Serve a deterministic mutable HTML
fixture from `127.0.0.1`; do not depend on a private authenticated service.

## Actions and expected results

1. Add a disabled rule for the fixture, use `Test on current tab`, then enable
   it. First observation establishes a baseline without Unread.
2. Change the activity node while selected and while backgrounded. Activity has
   priority; completion in the background becomes Unread, while a selected
   visible active page acknowledges it.
3. Hide the loaded page behind a terminal or note without unloading it. Continue
   mutating the fixture and verify polling and state transitions remain active.
4. Minimize or deactivate Pact during a change. Unread is retained and clears
   only after the page is selected in an active visible window.
5. Force one evaluation past its timeout, then restore the fixture. The error is
   visible, no overlapping polls accumulate, and the normal cadence recovers.
6. Navigate through unmatched, redirect, fragment-only, and matching URLs.
   Rebaseline and snapshot cleanup follow the stable-URL contract.
7. Pause and restore the project, restart Pact, and close the page. An unloaded
   page does not poll; retained unread restores, and closing deletes its snapshot.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| Baseline, Activity, Unread, and acknowledgement |  |  |  |
| Hidden loaded page and timeout recovery |  |  |  |
| Navigation, persistence, pause, and cleanup |  |  |  |

## Privacy cleanup

Stop the local fixture server and Pact. Verify the target is exactly
`C:\pact-test\web-monitoring`, then remove it. Leave `%APPDATA%\Pact`
untouched.
