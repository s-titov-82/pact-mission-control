# Review types

Open **Settings → Reviews → Review types** to choose what the reviewer checks. Enter the criteria in **Review rules**. Pact includes them in the reviewer's first turn. For example, a database review might ask the reviewer to check migration order and compatibility with the previous schema.

A new review type allows five rounds. Each round gives the reviewer a turn to approve or report findings. If the reviewer reports findings, the author can fix or dispute them. The review ends when the reviewer approves or the author answers the final round.

With **Strict review by default** selected, the reviewer must verify that every finding has been resolved or successfully disputed before approving. With it cleared, the reviewer may approve with minor findings open and lists them in the final summary.

An agent starts a review with `pact_request_review`, supplying the scenario ID, reviewer profile ID, and target. Optional `strict` and `maxIterations` arguments override the defaults for that run.
