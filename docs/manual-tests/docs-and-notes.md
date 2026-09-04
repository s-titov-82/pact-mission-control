# Docs & Notes protocol

## Purpose and prerequisites

Verify project Markdown discovery, editing, preview, autosave, and conflict
recovery. Launch the exact candidate with a fresh
`--data-root C:\pact-test\documents` and use a disposable sample repository.

## Actions and expected results

1. Open Docs & Notes. `README.md`, common Markdown, `docs/**`, and
   `docs/superpowers/**` appear in mutually exclusive groups; ignored/generated
   files do not appear.
2. Open an absent root README in Editor, type text, and wait for autosave. The
   file is created atomically. A transient write failure is visible and a later
   successful retry clears the failure without losing edits.
3. Switch between `Preview | Editor`. Preview renders headings, links, tables,
   and fenced code. The selected mode is retained per open document only.
4. Move caret and scroll position, switch documents, and return. Both positions
   are preserved; normal tab switching does not jump to the end.
5. Modify a clean active file externally and observe automatic reload. Make the
   editor dirty, modify disk again, and observe a conflict without overwrite.
6. Resolve separate conflicts with `Reload from disk` and `Save mine`; the
   selected content wins and autosave resumes.

## Results

| Area | Status (`PASS`/`FAIL`) | Date/version | Direct evidence and notes |
| --- | --- | --- | --- |
| Discovery, preview, and per-document mode |  |  |  |
| Autosave, retry, caret, and scroll |  |  |  |
| External reload and conflict choices |  |  |  |

## Privacy cleanup

Close Pact, remove only the disposable repository and the verified
`C:\pact-test\documents` data root, and leave `%APPDATA%\Pact` untouched.
