# Terminal templates

Each tab is one launch profile; every profile becomes one launch button in the main window.

Command is the command that starts a brand-new session for this profile. Resume command is copied into a session as its initial resume command when that session is created, but it is not consulted again after that: if a session's own resume command text is not in a recognized shape (for example "codex resume <id>" or "claude --resume <id>"), restoring the session falls back to the session's own launch command instead.

Pact preserves the selected profile command and appends session-scoped guidance for Codex and Claude. Claude receives it through inline --append-system-prompt; Codex receives an invocation-level developer_instructions value, which overrides any value for the same key inherited from a selected Codex config profile.

Id, Command, and Shell must all be non-empty, and ids must be unique across profiles — Save section rejects the whole file and names the offending profile otherwise.
