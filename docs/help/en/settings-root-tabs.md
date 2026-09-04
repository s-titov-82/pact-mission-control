# Root tabs

ROOT holds terminal and browser tabs that are not owned by any project. Their
definitions, last selected item, and individual pause states are stored in
`root-tabs.json`.

Terminal title, working directory, launch command, and resume command are
editable here. Unlike project agent sessions, every ROOT terminal has an
explicit working directory; newly created ROOT terminals start in the existing
Windows user profile directory.

Browser title and URL are editable here. Changing the URL affects the saved
address used the next time the page is loaded.

Pause and Resume remain per-row actions in the main window. Selecting a paused
row does not resume it.
