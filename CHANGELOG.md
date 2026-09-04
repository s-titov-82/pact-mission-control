# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - Unreleased

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
- Checksummed release ZIP, SPDX 2.2 SBOM, and GitHub build attestations.

### Security

- Single-owner data-root lease and bounded cleanup of disposable runtime data.

[0.1.0]: https://github.com/s-titov-82/pact-mission-control/releases/tag/v0.1.0
