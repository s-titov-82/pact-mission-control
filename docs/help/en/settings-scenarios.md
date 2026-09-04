# Scenarios

A scenario automates a review loop between two live terminal sessions: an author and a reviewer. You start one from the Scenarios list in the main window by clicking its button; a setup dialog then binds the author and reviewer roles to actual running sessions and lets you set the review target (a scope pointer or pasted text) before the run starts.

Every scenario definition here is a "review-loop": the reviewer checks the author's work against a stop marker across up to Max iterations passes, using four prompt templates in a fixed order:

- Start prompt template — sent to the reviewer for pass 1; carries the full review brief (target, criteria, marker rules).
- First feedback template — sent to the author after pass 1, carrying the reviewer's findings.
- Author return template — sent to the reviewer for pass 2 through N, carrying the author's reply for re-verification.
- Feedback template — sent to the author for pass 2 through N, carrying the reviewer's follow-up findings.

Completion is decided from the footer-complete reviewer response file: whenever
that response contains the exact Stop marker text, the run ends successfully —
there is no other automatic success condition. Terminal screen state or
captured output alone never completes a pass. If Max iterations passes elapse
without the marker appearing, the run stops incomplete.

Reviewer instructions are free-form text presets, not a fixed list of disciplines; add or remove them with the +/− buttons next to the list, and edit each preset's Id, Name, and Text. Default reviewer instruction picks which preset is pre-selected when you set up a run of this scenario.

Default target seeds the setup dialog's review-target field; it can still be overridden for any individual run.

While a run is active, both involved sessions are input-locked — you cannot type
into them, though their output stays visible and scrollable. Manual Pause
unlocks both sessions and blocks new automatic terminal writes until Resume; a
valid response file that appears during the pause advances the run and restores
the locks without requiring Resume. If watchdog attention pauses a run because
a session looks stuck, only that affected session unlocks so you can answer it
manually and let the run resume.

Every run is journaled in memory while it can still be shown in the journal panel. Closing the run discards that journal; no scenario journal is written to the data root.
