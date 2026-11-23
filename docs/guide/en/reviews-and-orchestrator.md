# Reviews and orchestrator

[English](reviews-and-orchestrator.md) | [Русский](../ru/reviews-and-orchestrator.md)

Pact opens a reviewer in the author's project when an agent requests a review.
The current desktop app keeps Hermes settings on disk and hides its sessions.

## Review types

Under **Settings → Reviews → Review types**, enter criteria for the reviewer's first turn
and choose the default approval mode. New types start in strict mode.
Each review allows five rounds by default. A saved `maxIterations` sets the
limit for its review type. Under **Reviewers**, choose the agent that will
perform the review.

The author starts a run with `pact_request_review`. Each participant reads its turn
with `pact_get_review_turn` and submits its answer with `pact_submit_review_turn`.
The reviewer can approve when every finding has been resolved or successfully
disputed. In non-strict mode, minor and nit findings may remain open.
The request can override the defaults with `strict` and `maxIterations`.

See [Review types](../../help/en/settings-scenarios.md) and
[Review profiles](../../help/en/settings-review-profiles.md) for configuration.

## Agent control

When agent control is enabled, supported Codex and Claude sessions receive a
temporary authenticated connection to Pact's loopback MCP endpoint when they
start or resume. Their tools can request actions already available in the UI:
work with the owning project's Notes, create a browser page, or request a
review. A ROOT session has no project Notes.

Tokens are session-specific and are revoked when the session stops, exits, or
restarts. Pact does not write the connection into the agent's global
configuration.

If a review notification cannot be delivered or confirmed, both session rows
show a red `!` and `Review needs attention`. The project row also shows the warning,
even while collapsed. Hover over a warning for the
reason and whose turn needs attention. Open that session and ask its agent to
continue the review by calling `pact_get_review_turn`. The review keeps its current
turn; Pact does not paste a possibly written notification again. The warning
clears when the owning agent retrieves the turn or the review ends.

## Hermes orchestrator

The orchestrator is unavailable in the current desktop app. Its retained
implementation uses a single pinned Hermes session above ROOT that can
inspect live projects, sessions, screens, usage, and active reviews; submit
prompts to eligible live terminals; work with Notes of running projects; and
inspect or resume their saved web pages.

It has no project ownership, Git access, project Markdown access, arbitrary
JavaScript, or command for starting a new scenario. Paused projects are not
targets. Stop, disabling the feature, and application shutdown are intentional
and do not restart the orchestrator; an unexpected exit uses bounded restart
backoff.

Its credential is durable and stored in the selected data root and the
provisioned Hermes profile. Do not copy it into ordinary profiles or logs. See
the [Orchestrator reference](../../help/en/settings-orchestrator.md).

[Previous: Browser, Docs & Notes, and Git](browser-docs-and-git.md) ·
[Next: Settings, data, and backup](settings-data-and-backup.md)
