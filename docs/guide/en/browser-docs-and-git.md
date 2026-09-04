# Browser, Docs & Notes, and Git

[English](browser-docs-and-git.md) | [Русский](../ru/browser-docs-and-git.md)

Project context in PACT:> is more than a terminal. Saved WebView2 pages,
Markdown, Notes, and Git status remain available beside each project's live
sessions.

## Browser pages

Use the `@` action on a project or ROOT card to create a page from a configured
web-link template. `Custom URL...` accepts an exact absolute HTTP(S) address.
Pages preserve their current URL and browser profile state across application
restarts. Cookies and site storage are retained in the selected Pact data root.

A project page that has no loaded WebView loads when selected. ROOT pages also
have per-row Pause/Resume actions: Pause unloads the WebView without deleting
the saved tab, while selecting a manually paused ROOT page does not load it.
Use `Resume` for that page. Closing a page removes the saved tab.

Web monitoring applies only to pages whose WebView has been loaded. The first
enabled rule whose URL pattern matches owns the page. Configure rules in
Settings, replace every starter placeholder, and use `Test on current tab`
before enabling a rule. A hidden loaded page can continue polling; an unloaded
page, including a manually paused ROOT page, cannot.

When a monitored page changes in the background, its tree indicator becomes
unread. Viewing it in the active visible Pact window acknowledges the change.
See [Web monitoring rules](../../help/en/settings-web-monitoring-rules.md) for
the rule fields and scheduling behavior.

## Docs & Notes

Open a project's `Docs & Notes` pseudo-tab to work with three groups:

- `Notes` contains the app-owned project notebook;
- `Common MD's` contains Markdown outside the repository's `docs` directory;
- `Docs` contains the complete `docs` tree.

Notes opens in Editor mode; project files open in Preview. Use the explicit
`Preview | Editor` control to switch modes. Project Markdown is edited in place
and autosaved atomically.

If the file changes on disk while your local buffer is dirty, Pact does not
overwrite either version. Choose `Reload from disk` to discard the local edit
or `Save mine` to intentionally replace the disk version. A clean open document
reloads automatically after an external change.

## Git popup

The `⎇` action on a Git-backed project opens the Git popup. It reads repository
state with separate Git processes and shows the remote, branch, ahead/behind
state, working-tree counters, operation log, and common actions. It never sends
Git commands through an agent terminal.

Stash, pop, resolve, and abort actions appear only when applicable. Optional
external helpers such as TortoiseGit are configured in
`Settings > Git helpers`; unavailable helpers remain hidden.

[Previous: Terminal workflows](terminal-workflows.md) ·
[Next: Reviews and orchestrator](reviews-and-orchestrator.md)
