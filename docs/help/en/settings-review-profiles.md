# Review profiles

Each tab is a reviewer-only launch profile used when an agent asks Pact to start a review. These profiles do not appear in the normal project launch menu.

Command carries the model and effort flags for the reviewer. Pact tools are connected automatically for the Claude and Codex kinds, using the arguments each command-line interface accepts; no per-profile configuration is involved.

Connected agents are notified to refresh their review profile ids and scenario ids whenever a Settings reload changes the live catalog, including when malformed scenario JSON activates the built-in defaults. External file edits take effect only at the Settings reload boundary or after the application restarts.

Id and Command must be non-empty, and ids must be unique. Unknown JSON fields are preserved when the section is saved.
