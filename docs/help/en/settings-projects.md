# Current projects

Each tab is one currently open (active) project. Editing name, root path, notes, GitLab repo id, or TeamCity project id here applies immediately to the running app — there is no separate publish step for other windows to see the change.

Root path applies to future session launches and to the git panel; a session that is already running keeps the working directory it started with.

The read-only summary line above the fields (id, status, created/active timestamps) reflects the project's current runtime state and cannot be edited here.

Every session that belongs to the project is listed under it. A session's title, working directory, launch command, and resume command are editable — except while the session is locked by a running scenario, when a "Locked by a running scenario" hint appears and its fields become read-only until the run finishes. The working directory field is hidden for agent sessions (Codex, Claude, Hermes), which always run in the project's own directory; it only applies to pwsh/custom shell sessions.

There is no delete button for a project in this section. Projects are created by opening a directory from the main window and removed by closing the project there — Settings only edits the projects that are currently open. A paused project is edited from the separate "Paused projects" section instead of here.
