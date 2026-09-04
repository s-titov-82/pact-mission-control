# Git popup

This section configures the project's git panel ⎇ popup, split into two top-level tabs: Buttons (what the popup's own command buttons run) and External helpers.

Buttons tab. Simple built-in buttons (Pull, Stash, Pop stash) have an editable Command — the full git argument string, quotes group words and a leading "git " is tolerated. Dialog buttons (Commit, Push, Switch, Rebase, Merge) get their arguments from their dialogs, so only Extra flags is editable: fixed flags inserted right after the git subcommand (for example --no-ff for merge, --autostash for rebase); the read-only preview line shows where they land. Every built-in has an Enabled toggle — a disabled button is hidden in the popup — and no delete: built-ins can only be disabled, and even an entry deleted from the raw JSON silently falls back to its built-in default (the tab reappears with defaults on next load). "+" adds a custom entry: it becomes an extra button in the popup running its Command as-is, and unlike built-ins it can be deleted. The ◀/▶ buttons next to "+" move the selected tab one slot left or right — this reorders the popup's buttons too, since the tab order is the same order the buttons render in. The "Rebase onto base" scenario reuses the configured Pull command and the Rebase extra flags.

External helpers tab. Each one is an external git GUI tool integration; its buttons appear in the same popup, but only when the tool actually resolves on this machine — a helper that does not resolve contributes no buttons and is not shown at all.

Resolution order for Executable: an absolute path that exists on disk is used as-is; otherwise, on Windows, the Registry key/value probe is read (checking both the 64-bit and 32-bit registry views) and used if it points at a file that exists; otherwise the executable name is looked up on PATH. If none of these resolve to an existing file, the helper is hidden.

Each popup action has a Slot that decides where it appears:

- history — a history/log button in the popup's action list.
- custom — an extra command button, shown alongside history in the same list.
- resolve — the panel's conflict-resolution action; only the first action with this slot is used. When no helper defines a resolve action, "Resolve" falls back to running "git mergetool -y" directly.
- any other slot value is not surfaced anywhere and is effectively ignored.

Arguments (one per line) can use {root} and {branch} placeholders, substituted with the project's root path and current branch name when the action launches.

External tools worth wiring up this way: TortoiseGit, Fork, SourceTree, GitExtensions — each has its own command-line switches for opening a log view or a conflict resolver, which you translate into an action's Arguments.
