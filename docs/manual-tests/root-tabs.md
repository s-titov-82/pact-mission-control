# ROOT tabs manual smoke

Use an isolated absolute `--data-root` and record each result as `PASS`, `FAIL`,
or `NOT-RUN`. Automated headless tests do not establish visual parity.

| Status | Check |
|---|---|
| PASS | Render the current Debug build with multiple ROOT terminals, a ROOT web page, and projects at a 300 px left-pane width. Confirm ROOT uses the same full-width native tree surface, disclosure glyph, child-row indentation, status cells, and right edge as project nodes; its `+` and `@` actions share the title row. Verified 2026-07-31 with an isolated data root. |
| NOT-RUN | Start with multiple ROOT terminals, ROOT web pages, and projects. Confirm `ROOT` and `PROJECTS` are uppercase, ROOT starts expanded, and collapsing ROOT keeps project navigation usable. |
| NOT-RUN | Use ROOT `+` to create Hermes and PowerShell terminals. Confirm each starts in the existing Windows user profile directory and Settings shows that directory for both agent and shell kinds. |
| NOT-RUN | Use ROOT `@` to open a general web-link template. Confirm the page loads in the native browser pane and survives an application restart. |
| NOT-RUN | Pause the selected ROOT terminal. Confirm its resume command is captured, the process stops, the row remains selected, and the center shows `Paused`. |
| NOT-RUN | Click the paused terminal row. Confirm it remains stopped. Click its explicit Resume button and confirm only that terminal restarts. |
| NOT-RUN | Pause the selected ROOT web page. Confirm its WebView unloads, retained unread monitoring state remains visible, row selection remains, and clicking the row does not reload it. |
| NOT-RUN | Click the web row's explicit Resume button. Confirm only that page reloads and monitoring resumes. |
| NOT-RUN | Confirm paused ROOT rows expose Resume, Edit, and Close, but not Restart, Reload, or another Pause action. |
| NOT-RUN | Select text in an unpaused ROOT terminal. Confirm the cursor popover's compact target tree shows only the ROOT source-owner group, excludes the source session, and keeps the right-panel Actions and Usage limits visible. |
| NOT-RUN | Click `More targets…`. Confirm eligible active project/session groups appear; paused ROOT terminals, all ROOT web pages, and scenario-locked sessions do not. |
| NOT-RUN | Open scenario setup from a project. Confirm no ROOT terminal appears as an author or reviewer candidate. |
| NOT-RUN | Edit a ROOT terminal and web page through each row's Edit button. Confirm Settings deep-links to the item and saved title/directory/commands/URL survive restart. |
| NOT-RUN | Close one ROOT item while several ROOT and project items exist. Confirm the nearest remaining ROOT item is selected and projects keep running. |
| NOT-RUN | Restart Pact after leaving one ROOT terminal and one ROOT web page paused. Confirm they remain paused and neither process nor WebView starts until explicit Resume. |
