# Terminal compatibility protocol

## Purpose and prerequisites

Verify the visible ConPTY/xterm contract for PowerShell, Codex, Claude, and
Hermes against the exact release candidate. Use Windows x64 with the four
commands resolvable on `PATH`. Close the normal Pact instance first.

Create an empty absolute directory and launch:

```powershell
Pact.exe --data-root C:\pact-test\terminal
```

Record the candidate SHA-256 and source commit in the result table.

## Actions and expected results

1. Create PowerShell, Codex, Claude, and Hermes sessions in one sample project.
   Each starts interactively without an unsupported-terminal warning.
2. In every session, type input, paste multiple lines, interrupt a running
   command, resize repeatedly, switch away and back, and stop or exit. Input
   remains responsive, background processes survive switching, and shutdown
   leaves no child process.
3. In PowerShell, produce more than one screen of output, wheel through xterm
   scrollback, and finish a mouse selection near the center divider. The shared
   selection popover opens at the release point and its visible action/target
   divider aligns with that point; sending the selection pastes it elsewhere
   without Enter.
4. In Codex, use its wheel-owned history, xterm selection, and
   Shift/Ctrl+Enter multiline input. A mouse selection opens the popover at the
   release point. A modified newline must not submit early.
5. In Claude, use its wheel-owned history and copy through Claude's OSC 52
   path. A copy with coordinates anchors the popover there; a copy without
   coordinates uses fallback placement. Clicking or typing in that terminal
   afterwards closes the popover even though xterm never held the selection. `Send selected text` must use the
   resulting clipboard text when xterm has no selection. Verify Shift/Ctrl+Enter
   multiline input.
6. In Hermes, verify input, wheel, switching, and resize. Diagnostics must show
   that Pact delivered the final requested ConPTY dimensions and requested a
   redraw. Record Hermes' visible internal scaling separately; do not report
   visual parity merely because Pact delivered the correct size.
7. Start a long task, select another tab, and observe Busy then Unread. Selecting
   the completed tab in an active visible window clears Unread. When this is the
   final unread completion, taskbar attention clears too.
8. Select near the right edge and confirm the popover mirrors to the left.
   Resize Pact to its minimum supported size and confirm the action list,
   divider, target list, and popup chrome remain inside the center pane.
9. While any terminal selection popover is open, confirm the right-panel
   `Actions` block and `Usage limits` stay visible.
10. Confirm the isolated data root contains no transcript or durable session
   output directory.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| PowerShell and cursor/divider placement | NOT-RUN |  |  |
| Codex | NOT-RUN |  |  |
| Claude OSC 52 anchored and fallback placement | NOT-RUN |  |  |
| Right-edge mirroring and minimum window | NOT-RUN |  |  |
| Right-panel continuity | NOT-RUN |  |  |
| Hermes delivery and visible limitation | NOT-RUN |  |  |
| Switching, status, and clean exit | NOT-RUN |  |  |

## Privacy cleanup

Close Pact and all child agents. Verify the target is exactly
`C:\pact-test\terminal`, then remove that directory. Do not use or delete
`%APPDATA%\Pact`.
