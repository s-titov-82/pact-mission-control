# Review profiles

Each tab is a reviewer-only launch profile used when an agent asks Pact to start a review. These profiles do not appear in the normal project launch menu.

Choose a model and reasoning effort for Claude or Codex, or keep the agent defaults. Pact reads the local Codex model cache and the Claude model aliases and cached options. A saved model stays available when it disappears from the cache. Use a custom launch command for wrappers and additional flags.

Pact tells connected agents to refresh their review profile and review type IDs when reloading Settings changes the catalog. This includes switching to built-in defaults when `scenarios.json` is malformed. External file edits take effect when Settings reloads or Pact restarts.

Id and Command must be non-empty, and ids must be unique. Unknown JSON fields are preserved when the section is saved.
