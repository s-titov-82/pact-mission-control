# Durable attention and ordinary startup restoration

Double-clicking a ready agent row toggles a separate `Read later` marker.
Only another double-click clears it; selection and classifier updates do not.
The marker is durable in the owning session record (or orchestrator settings),
appears in the tree and attention feed, and never changes classifier facts.
Setting requires a live Codex, Claude, or Hermes session with a fresh Done or
stable empty prompt and no busy/input-request evidence. An existing mark can
always be cleared while its owner remains available.

Every ordinary startup restores the same live terminal/browser set, global
selection, and unread completion markers as soft restart. Paused items remain
paused, including a selected paused ROOT item. A content-free atomic checkpoint
under Settings survives an unexpected exit; graceful shutdown freezes it before
resume capture and process termination. Restoration suppresses checkpoint writes
until complete. A valid one-time update ticket takes precedence over the normal
checkpoint. Missing/malformed state falls back to the existing first-run startup.

Historical busy, input requests, composer, screen, readiness, and process state
are not replayed. The user explicitly chose to wait for new screen evidence.
Resume commands remain owned by existing session records; new processes receive
fresh MCP tokens. No transcripts, messages, review journals, or event history are
persisted. Manually stopped orchestrator state is preserved by the checkpoint.

Use existing snapshot/restorer behavior rather than synthesizing updater tickets.
Checkpoint updates are serialized and skip equal projections; shutdown awaits
the final write. Changes remain uncommitted until requested by the user.
