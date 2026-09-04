# Reviews and orchestrator

[English](reviews-and-orchestrator.md) | [Русский](../ru/reviews-and-orchestrator.md)

PACT:> can coordinate a bounded author/reviewer loop between two already-live
agent terminals. A separate optional Hermes orchestrator can observe all live
sessions and relay prompts and status.

## Review scenarios

Review definitions live in `Settings > Scenarios`; reviewer launch commands
and instruction presets live in `Settings > Review profiles`. The supported
review loop alternates between an author and a reviewer until the configured
completion marker appears or the iteration limit is reached.

Start a review only with sessions that are ready for new input. While the
scenario owns them, their input is locked, but their terminals remain visible
and scrollable. Pact waits rather than writing over a busy agent, an unanswered
question, or known unsent input.

Each step sends a short instruction that points to a task file under the
project's temporary `.pact-reviews` directory. A response advances the review
only when it is non-empty and ends with the exact requested footer. Terminal
`Busy`, `Done`, or unread indicators are status hints, not review completion.

Use Pause when a person needs to intervene. Pause releases the involved
terminals and blocks new automatic writes. Resume continues the same waiting
step. If a valid response file appears while paused, Pact can continue without
requiring Resume. Closing the project or application aborts its active review.

See [Scenarios](../../help/en/settings-scenarios.md) and
[Review profiles](../../help/en/settings-review-profiles.md) for configuration.

## Agent control

When agent control is enabled, supported Codex and Claude sessions receive a
temporary authenticated connection to Pact's loopback MCP endpoint when they
start or resume. Their tools can request actions already available in the UI:
work with the owning project's Notes, create a browser page, or request a
file-first review. A ROOT session has no project Notes.

Tokens are session-specific and are revoked when the session stops, exits, or
restarts. Pact does not write the connection into the agent's global
configuration.

## Hermes orchestrator

The orchestrator is a single pinned Hermes session above ROOT. It is disabled
by default and must be provisioned explicitly in Settings. When enabled, it can
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
