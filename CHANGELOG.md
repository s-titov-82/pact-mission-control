# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.11] - 2026-10-01

### Added

- Selecting text with the mouse in Codex opens the selection actions popover
  again. Codex now copies its selection straight to the Windows clipboard, so
  Pact picks up a clipboard change right after the mouse release and opens the
  popover at that point.

### Fixed

- Typing and session switches no longer stall in bursts while a Codex session
  shows code: classifying its screen took up to half a second on the UI
  thread and now takes under a millisecond.
- Subscription usage is read off the UI thread, and scanning Codex session
  files no longer queries every file separately.
- Quick actions and other prompts set to submit now actually submit in Codex
  instead of leaving the text in the composer.
- Ctrl+U, Ctrl+K and other Ctrl+letter editing shortcuts work in Codex.
- Abandoned agent notification streams are released instead of accumulating
  for the life of the application, and the in-memory WebView diagnostic trace
  no longer grows without bound.

## [0.1.10] - 2026-09-30

### Added

- `Custom URL...` offers a copied address: when the clipboard holds exactly one
  absolute HTTP(S) address, the dialog opens with it filled in and the cursor at
  the end. Any other clipboard content leaves the field empty as before.

### Fixed

- Hidden terminals no longer keep repainting background agent output, which
  delayed typing echo and session switches in the selected terminal.
- Codex tabs launch in embedded mode when Pact passes configuration overrides,
  so they no longer print "Running without the shared background server" on
  every start.
- A terminal restored after an update restart is no longer marked unread just
  because its agent redrew the conversation. A completion the user actually
  waited for is still marked unread.

## [0.1.8] - 2026-09-17

### Fixed

- A web page that becomes the actively viewed tab now starts its faster polling
  immediately. A monitoring loop that was entering its wait at that exact moment
  could miss the wake-up and keep the rule's ordinary interval instead.
- `Restart current` on an agent session resumes the conversation again instead
  of silently starting a new one. When the conversation id is not known — after
  an unexpected exit, or while the session is still running — the agent opens
  its own conversation picker, so the previous conversation stays reachable
  rather than being lost.

### Changed

- A published release now shows the changelog entry for that version, together
  with what to download and how to verify it, in place of a generated list of
  commits.

## [0.1.6] - 2026-09-16

### Added

- Codex subscription limits are read from Codex itself when its session files
  age past ten minutes, so the panel keeps reporting without a recent Codex
  session. A failed live read keeps the last known figures and explains why.

### Fixed

- An agent that animates its screen is reported as busy instead of staying on
  "Input needed" for the whole turn after its question was answered, and
  terminal details name the verdict the indicator was derived from.

## [0.1.1] - 2026-09-07

### Added

- Current-user Windows Setup with prerequisite detection, in-place upgrades,
  Start menu integration, and uninstall support.
- Checksummed Setup alongside the portable ZIP and SPDX 2.2 SBOM.

### Changed

- Public installation and release-verification guidance is available in
  English and Russian and now treats Setup as the primary distribution.
- The supported desktop baseline is Windows 11 x64, with .NET 10 Runtime and
  WebView2 Runtime 150 or newer checked by Setup.

## [0.1.0] - 2026-09-04

### Added

- Initial public Windows x64 release of PACT:> Mission Control.
- Visible ConPTY-backed coding-agent and PowerShell terminal sessions.
- Project sessions, WebView2 pages, Docs & Notes, prompt actions, settings, Git
  helpers, and file-first author/reviewer scenarios.
- Documentation pane now has three tabs (Notes, Common MD's, Docs) and selects
  documents from a project tree in the right panel.
- A loopback, per-session agent control channel for requesting reviews,
  appending project notes, and opening browser tabs.
- A pinned, opt-in Hermes orchestrator for cross-session status and messaging,
  including optional workstation lock and unlock prompts.
- Scenario prompts are written only into a session that is idle with an empty
  composer and confirmed by the activity that follows, a detected question
  blocks a programmatic send from a scenario step or the orchestrator, and the
  folder-trust dialog is answered once before a review starts.
- Checksummed portable ZIP, SPDX 2.2 SBOM, and GitHub build attestations.

### Security

- Single-owner data-root lease and bounded cleanup of disposable runtime data.

[0.1.1]: https://github.com/s-titov-82/pact-mission-control/releases/tag/v0.1.1
[0.1.0]: https://github.com/s-titov-82/pact-mission-control/releases/tag/v0.1.0
