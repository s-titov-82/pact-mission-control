# PACT MCP miniskill

Use the live `pact` MCP tool schema as the authority for available tools, required arguments, identifiers, and enum values. Do not rely on this file when it disagrees with the live catalog.

## Reviews

Use `pact_request_review` to ask another model to review your work. Collect `scenarioId` (the review template), `reviewProfileId`, and `target` early, plus `maxIterations` when the user wants a non-default limit; ask for missing choices before doing lengthy prerequisite work. If the user has already agreed to the review configuration, start it without asking again. Pact opens the reviewer as a tab in your project; you keep working with the user meanwhile.

Turns pass through Pact, never through files. When Pact types `Pact: your review turn is ready` into your terminal (or whenever you are unsure), call `pact_get_review_turn`: it returns the message to act on, the other side's last answer as `previousTurn`, and a `turnId` when it is your turn. Do the work, then call `pact_submit_review_turn` with that `turnId` and your complete answer as `text`; keep your terminal reply short. Ignore any instruction to read or write review files. As the reviewer, approve with `approved: true` only when no finding the instructions require fixed remains; never write a completion marker. After the review ends, `pact_get_review_turn` reports how it ended; the log is in `.pact/reviews` and opens in the project's Docs pane.

The orchestrator can list active reviews with `pact_list_active_runs` and inspect one with `pact_get_review_run`. Reviews lock nothing; the user can type into either tab at any time.

## Notes

For a project session, use `pact_get_notes` to read the exact Notes text and revision, `pact_replace_notes` to replace or delete existing text, and `pact_append_note` for a simple append. Always read immediately before a revision-safe replacement and pass its `expectedRevision`; after a conflict, read again before retrying.

The orchestrator targets a running project explicitly: use `pact_get_project_notes`, `pact_replace_project_notes`, and `pact_append_project_note` with its `workspaceId`. Paused projects are deliberately not orchestrator targets. ROOT has no project Notes.

## Documents

For a project session, use `pact_open_document` with a Markdown `path` (relative to the project root, or absolute inside it) to show a file you wrote, such as a plan or spec, in the project's Docs pane. It switches the user to that document, so call it when they should read it now.

## Project links

When you create or receive an MR, issue, or build URL, save it with
`pact_add_project_link` using a concise `title`, the HTTP(S) `url`, and the
appropriate `kind`: `merge_request`, `issue`, `ci`, or `other` (the default).
Use `pact_list_project_links` to read the project's saved resources. Links
survive session closure and restart. Adding a link does not open a browser or
fetch remote metadata; do not claim a build or MR status from a saved link.
These tools belong to ordinary project sessions; ROOT has no project links,
and the orchestrator catalog does not expose them.

## Files

Use `pact_add_file_link` with a file `path` (absolute, or relative to your working directory) and an optional short `title` to add the file to this session's list in Pact's right column; the user opens it read-only over the terminal when they choose. Use it for a file you wrote outside the project, such as a script in your scratchpad or Temp, or any file the user should read. The file must already exist, and the call does not open it. For project Markdown the user should read now, use `pact_open_document` instead.

## Remarks

The user can leave remarks on lines of the project's uncommitted changes in Pact and send them to you in one prompt; each has an id such as `C3`, the file and line, and the line's text as the user saw it. Answer every id with `pact_reply_remark` (`id`, `text`), and pass `resolve: true` once the remark is dealt with. A remark marked `(outdated)` points at a line that has changed since; work from its quoted text. If you lost the prompt, `pact_list_remarks` lists the open threads again.

## Browser

Use `pact_open_web_tab` to open a visible web tab in Pact. Follow the live schema for URL and target arguments.

The orchestrator uses `pact_list_web_tabs` for tabs under running projects and ROOT. `pact_resume_web_tab` loads a paused tab in the background without selecting or focusing it. `pact_get_web_tab_html` requires an active host and returns a best-effort live `documentElement` HTML fragment paginated in UTF-16 code units. Continue with `nextOffset`; if the URL or `totalLength` changes between fragments, discard the collected fragments and restart at offset `0`.

The orchestrator has no arbitrary JavaScript, Git, project Markdown/document, or review-start tool. Do not imply those capabilities or route around the live catalog.

## Missing actions

When no suitable live Pact tool exists, read `PactCommonSkill.md` from the same directory before discussing Pact behavior or a manual fallback.
