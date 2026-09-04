# Prompt/Shell templates

Templates are grouped as Prompts and Shell commands. Prompt templates target agent sessions: Codex, Claude, and Hermes. Shell commands target Pwsh and Custom sessions.

The exact {selectedText} token makes a template selection-aware regardless of its Type. Static templates appear in Quick actions. Selection-aware templates appear in Send selection to. Selected text is substituted verbatim; shell commands are not automatically quoted.

Auto-submit decides whether Enter is sent after inserting either type. Raw selection never submits. New Prompts default off; new Shell commands default on, and changing Type preserves the current checkbox. Its persisted JSON name is sendByDefault.

Available placeholders: {project}, {task}, {selectedText}, and {otherSessionSummary}. Unknown JSON fields are preserved; legacy selectionTemplate entries remain readable as Prompt templates.