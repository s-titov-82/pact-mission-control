# Review profiles

Each tab is a reviewer-only launch profile used when an agent asks Pact to start a review. These profiles do not appear in the normal project launch menu.

Choose a model and reasoning effort for Claude or Codex, or keep the agent defaults. Pact reads the local Codex model cache and the Claude model aliases and cached options. A saved model stays available when it disappears from the cache. Use a custom launch command for wrappers and additional flags.

Connected agents are notified to refresh their review profile ids and scenario ids whenever a Settings reload changes the live catalog, including when malformed scenario JSON activates the built-in defaults. External file edits take effect only at the Settings reload boundary or after the application restarts.

Id and Command must be non-empty, and ids must be unique. Unknown JSON fields are preserved when the section is saved.
