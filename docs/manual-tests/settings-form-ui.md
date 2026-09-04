# Settings form protocol

## Purpose and prerequisites

Verify form-based settings editing, node-preserving saves, and live deep links
against the exact candidate. Launch it with a fresh
`--data-root C:\pact-test\settings` and add a sample project.

## Actions and expected results

1. Open Settings and visit every section: current and paused projects, terminal
   templates, web links, monitoring rules, prompt/shell templates, Git popup,
   scenarios, recent directories, and appearance. Each section loads and its
   help button shows section-specific text.
2. Add, edit, reorder where supported, save, close, and reopen representative
   terminal, prompt, Git, scenario, recent-directory, and appearance values.
   Saved values round-trip and `Revert` discards only unsaved changes.
3. Before saving, add an unknown property and an unknown array entry to the
   active JSON file while Pact is closed. Reopen, edit through the form, save,
   and verify both unknown nodes remain byte-for-byte equivalent JSON values.
4. Verify `Open raw JSON`, Ctrl+S, unsaved-change confirmation, multiline field
   wrapping, `Auto-submit`, folder picker, and disabled starter monitor markers.
5. Use project and session pencil buttons. The intended item opens, the session
   title is visible and focused, and a saved edit updates the running app.
6. During a scenario, involved session fields are read-only; unrelated settings
   remain editable. Pause a project and verify its settings remain editable and
   persistent in the paused-project section.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| Navigation, help, and form behavior |  |  |  |
| Round-trip and node preservation |  |  |  |
| Project/session deep links and locks |  |  |  |

## Privacy cleanup

Close Pact, verify the target is exactly `C:\pact-test\settings`, and remove it.
Do not inspect, modify, or delete `%APPDATA%\Pact`.
