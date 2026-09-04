# PACT MCP miniskill

Use the live `pact` MCP tool schema as the authority for available tools, required arguments, identifiers, and enum values. Do not rely on this file when it disagrees with the live catalog.

## Reviews

Use `pact_request_review` to ask another model to review work. Collect `scenarioId`, `reviewProfileId`, and `target` early, plus `maxIterations` when the user wants a non-default limit. Ask for missing choices before doing lengthy prerequisite work. If the user has already agreed to the review configuration, start it without asking again.

The orchestrator can inspect all active reviews with `pact_list_active_runs` and use `pact_get_review_run` for the current step, pause state, expected response file, and in-memory journal. `pact_pause_review` is cooperative: it requests a manual pause at a safe boundary rather than interrupting a step. `pact_resume_review` resumes an established pause and does not cancel a merely pending pause request.

## Notes

For a project session, use `pact_get_notes` to read the exact Notes text and revision, `pact_replace_notes` to replace or delete existing text, and `pact_append_note` for a simple append. Always read immediately before a revision-safe replacement and pass its `expectedRevision`; after a conflict, read again before retrying.

The orchestrator targets a running project explicitly: use `pact_get_project_notes`, `pact_replace_project_notes`, and `pact_append_project_note` with its `workspaceId`. Paused projects are deliberately not orchestrator targets. ROOT has no project Notes.

## Browser

Use `pact_open_web_tab` to open a visible web tab in Pact. Follow the live schema for URL and target arguments.

The orchestrator uses `pact_list_web_tabs` for tabs under running projects and ROOT. `pact_resume_web_tab` loads a paused tab in the background without selecting or focusing it. `pact_get_web_tab_html` requires an active host and returns a best-effort live `documentElement` HTML fragment paginated in UTF-16 code units. Continue with `nextOffset`; if the URL or `totalLength` changes between fragments, discard the collected fragments and restart at offset `0`.

The orchestrator has no arbitrary JavaScript, Git, project Markdown/document, or scenario-start tool. Do not imply those capabilities or route around the live catalog.

## Missing actions

When no suitable live Pact tool exists, read `PactCommonSkill.md` from the same directory before discussing Pact behavior or a manual fallback.
