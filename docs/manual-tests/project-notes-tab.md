# Project notes protocol

## Purpose and prerequisites

Verify app-owned project notes and selection actions. Launch the exact candidate
with a fresh `--data-root C:\pact-test\notes`, add two disposable projects, and
start one visible terminal in each.

## Actions and expected results

1. Enable Notes, type text, wait for autosave, close normally, and reopen. The
   text and active tab restore from `Settings/Notes`; hiding the tab does not
   delete its file.
2. Hide and re-enable Notes, pause and restore the project, then remove and
   re-add the same root. The same normalized-root note reattaches.
3. Select Notes text with the mouse. The shared selection popover opens at the
   release point. Repeat with Shift+Arrow: the popover opens using fallback
   placement, without moving the caret or losing the selected range.
4. Send terminal selection to its own project's note and to the other project's
   note. Both Notes targets are offered, text is appended with the expected
   separator, and a hidden target opens.
5. Send selected note text to a visible terminal. Text is inserted without
   Enter, and the source note project is not offered as its own Notes append
   target. Appending to an already open note updates the editor immediately.
   This verifies Notes append in both directions without overwriting text.
6. With either selection source, confirm the right-panel `Actions` block and
   `Usage limits` remain visible while the center popover is open.
7. Select note text and press Shift+Delete. The selection is removed and the
   removed text is on the clipboard, so Shift+Insert pastes it back.
8. Start a scenario involving the project. Notes remain editable and persist
   independently of the scenario lock.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| Persistence, hide/show, pause, and reattach | NOT-RUN |  |  |
| Notes mouse and keyboard selection placement | NOT-RUN |  |  |
| Shift+Delete cut | NOT-RUN |  |  |
| Selection actions in both directions | NOT-RUN |  |  |
| Right-panel continuity | NOT-RUN |  |  |
| Scenario independence | NOT-RUN |  |  |

## Privacy cleanup

Close Pact and child terminals, verify the target is exactly
`C:\pact-test\notes`, and remove it. Leave `%APPDATA%\Pact` untouched.
