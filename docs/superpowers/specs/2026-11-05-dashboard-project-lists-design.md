# ROOT dashboard: paused and recent projects

User request, 2026-11-05: two bordered, linear lists beneath active cards,
paused projects on the left and recent folders on the right. Every row has a
play button. Use the application's existing English labels (Paused / Recent).

Reuse `MainWindowViewModel.PausedWorkspaces`, `RecentDirectoryStore`, the existing
resume operation and add-project-from-directory operation. Keep recent history's
newest-first order and exclude active/paused roots using existing root normalization
and Windows case-insensitive equality. Deduplicate equivalent recent paths.
No new persistent state, terminal launch policy or agent type is introduced.

Paused actions use the existing busy-overlay restore flow. Recent actions create
the project through the existing directory flow and open its Overview; they do
not create an agent session. Missing directories use the existing visible error
path. Empty lists remain visible with a short placeholder. Path text trims and
exposes its full value in a tooltip; both columns must fit a narrow center pane.

Recent history loads only while the dashboard is visible, on entry and project
membership changes. Late loads cannot publish after hiding/disposal or replace
a newer load. Project movement/root edits refresh exclusions, and disposal removes
subscriptions. Keep item sources stable when content is unchanged.

Verify exclusion/order, live collection movement, stale asynchronous loads, load
failure/retry, row identities/events, placement below cards and narrow layout.
Reuse existing controller tests for resume and folder creation. Publish only to
`artifacts/local-diag`; do not commit this specification separately.

## Verification

Implemented 2026-11-05. Solution build and Release publish passed (two existing
Avalonia Watermark deprecation warnings). Full Presentation suite: 1078 passed;
final targeted Presentation scope: 15 passed; App view/open/restore scope: 6 passed.

| Requirement | Evidence |
| --- | --- |
| History order, normalized exclusions, paused removal/root edit | `Recent_folders_exclude_active_and_paused_roots_and_keep_history_order` |
| Ignore stale history reads | `A_recent_folder_load_from_a_hidden_dashboard_cannot_replace_a_newer_load` |
| History failure and retry | `Recent_folder_read_failure_is_visible_and_retried_on_reopening` |
| Linear lists below cards, row identity and narrow layout | `Paused_and_recent_linear_lists_are_below_cards_and_route_their_play_buttons` |
| Open Overview, reuse existing root, no cold agent start | `Opening_a_recent_folder_creates_one_project_overview_without_starting_an_agent` |
| Duplicate click and newer navigation during creation | `Delayed_recent_project_creation_does_not_override_newer_navigation_or_create_twice` |

Existing folder-creation and eager-resume tests also passed. Actual Avalonia view
rendered and visually inspected at `.tmp/root-dashboard-project-lists.png`.
Payload: `artifacts/local-diag/Pact.App.Avalonia.exe`. No new runtime storage or
artifact directory. Whitespace check passed; no commit performed.
