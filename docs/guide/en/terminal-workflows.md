# Terminal workflows

[English](terminal-workflows.md) | [Русский](../ru/terminal-workflows.md)

PACT:> hosts real terminal applications. You can use an agent exactly as you
would in its own TUI while keeping several sessions, project context, and
actions visible around it.

## Launch profiles

The terminal menu is built from launch profiles. A profile chooses the agent
kind, shell route, command for a new session, and a resume-command template.
The starter set covers Codex, Claude, Hermes, and PowerShell; Custom profiles
can launch another terminal program.

The selected profile's command, model, account, and permission arguments remain
authoritative. Pact only appends its session guidance and, for supported agent
kinds, the optional agent-control connection. It does not rewrite global Codex
or Claude configuration.

Edit profiles in `Settings > Launch profiles`. See the
[Terminal templates reference](../../help/en/settings-launch-profiles.md) for
validation and resume behavior.

## Quick actions and prompts

The right panel shows actions for the selected live terminal. Agent sessions
offer prompt-template actions and paste text without pressing Enter unless the
template has `Auto-submit` enabled. PowerShell actions are terminal commands and
submit with Enter.

Templates can use project, task, selected-text, and other-session-summary
tokens. Treat inserted selected text as literal content: Pact does not add
shell quoting around it. Review a template before enabling `Auto-submit` when
its content can contain external or untrusted text.

## Selected text

Completing a non-empty selection in a terminal or Notes opens a contextual
popover. You can paste the raw selection, run a selection-aware prompt
template, send it to another eligible live terminal, or append it to project
Notes. Terminal insertion does not press Enter.

The compact target list stays within the current project or ROOT owner. Use
`More targets…` to choose another running project. Paused, web, and
scenario-locked targets are excluded.

Inside xterm, right-click copies an existing selection. With no selection it
requests clipboard paste from the application. Pasted text is bracketed and is
not submitted automatically.

## Status and links

Session indicators are derived from visible terminal evidence. `Busy` means the
agent appears to be working; `Input requested` means it is asking a question;
`Unread` means it completed while you were elsewhere. Selecting the terminal in
the active visible Pact window acknowledges its completion.

HTTP(S) links activated in a terminal open as saved browser tabs under the same
project or ROOT owner. Other URI schemes are rejected.

[Previous: Workspace and sessions](workspace-and-sessions.md) ·
[Next: Browser, Docs & Notes, and Git](browser-docs-and-git.md)
