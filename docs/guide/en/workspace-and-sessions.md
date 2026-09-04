# Workspace and sessions

[English](workspace-and-sessions.md) | [Русский](../ru/workspace-and-sessions.md)

The left tree separates project-owned work from project-independent `ROOT`
tabs. Selecting an item changes the center pane; it does not stop other live
terminal processes.

## Projects

Each project points to one repository directory and owns its saved terminal
sessions, browser pages, Notes, project Markdown view, Git panel, and review
state. The project card provides actions for adding terminals and web pages,
opening Git, and editing the project in Settings.

Pausing a project stops its live sessions after attempting to capture their
resume commands. The project moves out of the active tree and can be restored
from the `PROJECTS` header menu. Restoring a project does not silently create a
new agent conversation: a saved session uses its own resume command when one
was captured, otherwise it falls back to its launch command or profile rule.

Closing the application also tries to capture resumable agent state before it
stops the terminal processes. A saved `Running` label from an earlier run is
not proof that a process is still alive.

## Sessions

A terminal session is a visible process with its own xterm.js screen and
bundled ConPTY backend. Sessions continue running while you inspect another
terminal, a document, or a browser page. Their tree indicators distinguish
active work, a question that needs input, unread completion, pause, and
failure.

Use the session edit action to change its title and launch or resume commands.
Agent kinds run in the owning project's directory. PowerShell and Custom
sessions can use an explicit working directory.

Project sessions have no per-row Pause/Resume action. If a saved project
session has no live process, selecting it starts the session and prefers its
resume command. Pause the project when you want to set aside all of its
sessions.

## ROOT

`ROOT` is for persistent terminals and browser pages that do not belong to a
repository. Use its `+` action for a terminal and its `@` action for a web page.
Each ROOT item has its own Pause/Resume action; there is no group-level pause or
close. Selecting a manually paused ROOT item does not resume it; use its
explicit `Resume` action.

ROOT terminals can receive manual prompt, paste, and selection actions. They
cannot participate in project review scenarios and do not own project Notes,
Markdown, or Git state.

Terminal and browser rows can be reordered by drag-and-drop within the same
owner and item type. Reordering does not move an item between projects or into
ROOT.

[Previous: Getting started](getting-started.md) ·
[Next: Terminal workflows](terminal-workflows.md)
