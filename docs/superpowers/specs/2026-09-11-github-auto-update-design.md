# GitHub Automatic Updates and Soft Restart

## Summary

PACT:> Mission Control will check the repository's latest stable GitHub Release,
ask before downloading a newer Setup package, stage and verify that package, and
offer a soft restart when the running cockpit is safe to interrupt. A dedicated
updater process will install into the directory containing the running Pact
executable, delete the downloaded Setup package, and relaunch Pact without
rebooting Windows.

The same soft-restart path will be exposed under Settings for diagnostics. It
will restore the runtime surfaces that were active before restart, instead of
applying the ordinary cold-start behavior.

## Goals

- Check for a newer stable release automatically once per hour.
- Ask the user once before downloading a newly discovered version.
- Download only the expected Setup asset from the fixed public Pact repository.
- Verify the complete download against the release's `SHA256SUMS.txt`.
- Update the directory from which the current Pact executable is running,
  including a directory selected by the user during the original installation.
- Apply the update through a graceful Pact restart without rebooting Windows.
- Restore the terminal sessions and browser tabs that were live before the
  update, together with selection, orchestrator state, and unread markers.
- Keep projects and ROOT items that were already paused in their paused state.
- Provide a restart-only diagnostic action that exercises the same handoff and
  restoration path without downloading or installing anything.

## Non-goals

- Hot-patching the running Pact process.
- Preserving terminal scrollback, terminal screen snapshots, browser DOM, or
  operating-system process memory.
- Persisting terminal transcripts or scenario journals.
- Supporting prerelease, draft, or alternate update channels.
- Automatically resuming an active review scenario across an application
  restart.
- Silently restarting Pact without user confirmation.
- Maintaining parallel installed and portable update modes.
- Providing automatic rollback or side-by-side version directories.

## Existing contracts and baseline

The implementation assumes the existing Inno Setup packaging on `origin/main`
is part of the implementation base. The installer has a stable AppId, supports a
per-user destination chosen by the user, and publishes a Setup executable,
`SHA256SUMS.txt`, ZIP, and SPDX manifest in each release.

Pact's runtime data root remains unchanged. Update artifacts and one-time
restart state live below the existing `Temp/Retained` boundary; no fifth
top-level data-root directory is introduced. Durable project, ROOT, layout, and
web-monitor state continue to use their current stores.

## Delivery phases

The feature is delivered in four independently useful phases. A later phase may
depend on an earlier one, but no phase turns on an unverified downstream path.

1. **Discovery and notification.** Add the hourly latest-release check, version
   comparison, update notification, release-notes action, manual check, and
   rate-limit handling. This phase performs no artifact download or process
   handoff.
2. **Download and verification.** Add explicit download consent, atomic Setup
   staging, checksum verification, reuse of a verified staged package, and an
   action that opens its containing folder for manual installation. Pact does
   not launch Setup in this phase.
3. **Diagnostic soft restart.** Add updater handoff in `RestartOnly` mode,
   deterministic ticket routing, the restoration overlay, and the Settings
   diagnostic action. Ship and exercise this path independently before an
   application update can depend on it.
4. **Automatic application.** Add the updater's Setup mode, executable-file
   release gate, package deletion, failure relaunch, and connection from a
   verified staged package to **Restart and update**.

`RestartOnly` and the restoration overlay share phase 3 because a cold restart
without restoration has no useful standalone behavior to validate through the
diagnostic action. The risky restoration behavior is therefore released before,
not simultaneously with, automatic update application.

## Release discovery and version selection

`GitHubReleaseClient` in `Pact.Infrastructure` calls the unauthenticated public
GitHub endpoint:

```text
GET https://api.github.com/repos/s-titov-82/pact-mission-control/releases/latest
```

The owner and repository are code-owned constants, not user configuration. The
client sends the documented GitHub media type, API-version header, and a Pact
User-Agent. Redirects are accepted only for HTTPS downloads originating from
the GitHub release response.

The response is accepted only when all of these are true:

- the release is neither draft nor prerelease;
- `tag_name` matches exactly `^v[0-9]+\.[0-9]+\.[0-9]+$`, the same
  `vMAJOR.MINOR.PATCH` contract enforced by the release workflow;
- its SemVer precedence is greater than the running assembly version;
- exactly one asset has the expected name
  `pact-mission-control-<version>-win-x64-setup.exe`;
- exactly one asset is named `SHA256SUMS.txt`;
- both assets are in the uploaded state and have positive sizes.

The running version comes from the entry assembly's
`AssemblyInformationalVersionAttribute`, because the SDK may append source
revision build metadata such as `+<sha>`. Pact parses the numeric major, minor,
and patch prefix and ignores build metadata for precedence. It does not use
`AssemblyVersion`. Prerelease identifiers are rejected even if GitHub were to
return such a release unexpectedly. Same-version and older releases are ignored.

## Check schedule and prompting

The update coordinator starts after shell initialization. It waits 30 seconds
before its first automatic check and checks every hour after that. It uses
`TimeProvider`, allows only one request at a time, and stops promptly during
application shutdown.

Automatic network or GitHub failures are written to the bounded application log
and do not interrupt the user. A manual check reports success, no-update, and
failure outcomes explicitly.

Unauthenticated GitHub requests share a 60-requests-per-hour limit by originating
IP. A `403` or `429` with `X-RateLimit-Remaining: 0` suppresses automatic checks
until the UTC epoch time in `X-RateLimit-Reset`, even when normal hourly ticks
occur first. `Retry-After` takes precedence when present. Other secondary-limit
responses use a bounded exponential backoff beginning at one minute. Manual
checks report the limit and earliest retry time but do not bypass it.

When a newer version is found, Pact presents one download prompt for that
version during the current application process:

- **Download** starts staging.
- **Later** suppresses repeated automatic prompts for that version until Pact
  is restarted.
- **Open release notes** opens the release's GitHub HTML page through the normal
  browser boundary.

The hourly check continues after **Later**, so a still newer release can be
discovered. A manual check may present the suppressed version again. There is no
durable ignored-version setting.

Once a package is verified and waiting for restart, later hourly or manual
checks do not issue another GitHub request. The prepared package and restart
action remain authoritative until handoff.

## Download and integrity

Release packages are staged under:

```text
Temp/Retained/Updates/Packages/<version>/
```

A restart-only diagnostic run uses its own
`Temp/Retained/Updates/Handoffs/<restart-id>/` directory, so it cannot overwrite
or consume a prepared release. Real update handoffs use the same Handoffs layout
and refer to the independently staged package.

Each asset is first written with a `.partial` suffix. Completion uses an atomic
rename within the same directory. Download code uses bounded HTTP timeouts that
cover response headers and the complete response body, streams to disk rather
than buffering the whole installer, enforces the asset size from GitHub
metadata, and supports cancellation during shutdown.

The checksum document must be BOM-free UTF-8 text whose non-empty lines each
have the exact GNU binary-checksum form
`<64 lowercase-or-uppercase hex> *<filename>`, with one LF-terminated line for
every published artifact. The parser requires
exactly one entry whose filename equals the Setup asset name using ordinal
comparison. It rejects NUL, path separators, duplicate filenames, malformed
hex, CR-only input, and a missing final LF. Pact hashes the downloaded Setup and
rejects any mismatch. Partial, ambiguous, oversized, or mismatched downloads
are deleted. A verified staged package can be reused after an ordinary Pact
restart without another download.

Current unsigned Pact releases remain eligible for automatic updates. The
single user authorization is permission to download the update; there is no
additional unsigned-package confirmation. If Authenticode is present, its
status is recorded for diagnostics, but signature validity is not yet an
eligibility requirement.

## Update state model

`UpdateCoordinator` in `Pact.Presentation` exposes a UI-neutral state:

```text
Idle
Checking
Available
Downloading
ReadyWaitingForSafeState
ReadyToRestart
Applying
Failed
```

Only the coordinator mutates this state. GitHub and filesystem work stays in
Infrastructure, while Avalonia owns dialogs and window-level restart actions.
Repeated checks cannot replace a downloaded newer version with an older one.
Cancellation returns transient work to a stable state and never leaves a
`.partial` file presented as ready.

## Safe restart policy

A prepared update may be offered for restart only when:

- no live project, ROOT, reviewer, or orchestrator terminal reports active work;
- no review scenario is active, including a scenario waiting while its terminal
  happens to appear idle;
- application shutdown has not already begun.

Browser loading or monitoring activity does not block the offer. The policy
uses the existing runtime/classifier facts rather than status text or glyphs.

Pact checks the policy again when the user selects **Restart and update**. If a
session became busy or a review started while the dialog was open, Pact cancels
the handoff and returns to `ReadyWaitingForSafeState`.

After the first automatic restart offer, choosing **Later** does not create
repeated modal dialogs. A persistent `Version <version> ready - restart` action
remains available until restart or a newer prepared version replaces it.

## Soft-restart snapshot

Immediately before an approved restart, Pact generates a cryptographically
random restart id and atomically writes exactly one one-time ticket at:

```text
Temp/Retained/Updates/Handoffs/<restart-id>/update-resume.json
```

It contains only identifiers and restart metadata:

- schema version and opaque restart id;
- restart mode: `ApplyUpdate` or `RestartOnly`;
- expected target version;
- source Pact process id;
- normalized current executable path and installation directory;
- normalized data-root path and whether `--data-root` must be passed;
- Setup path and expected SHA-256 for `ApplyUpdate`;
- handoff outcome, initially `Pending` and atomically changed by the helper to
  `Restarted`, `UpdateApplied`, or `UpdateNotApplied` with at most one sanitized
  error category;
- optional absolute diagnostic probe-output path, valid only for
  `RestartOnly`;
- ids of terminal sessions with live controllers;
- ids of browser pages with loaded hosts;
- ids of terminal sessions with unread completions;
- the selected owner and selected item id;
- whether the orchestrator was running.

The snapshot does not contain terminal output, terminal screen text, prompts,
browser HTML, cookies, credentials, or scenario journals. Arbitrary original
command-line arguments are not replayed. Only the supported data-root argument,
the opaque restart id, and an originally present `RestartOnly` diagnostic
probe-output argument are passed to the new Pact process.

The application's complete accepted launch-option set remains explicit:
`--data-root` in separated or `=` form, `--engine-probe-output`,
`--soft-restart-id`, and the native diagnostic hook
`--soft-restart-probe-output`. The engine probe and diagnostic hook consume the
single parsed options object rather than reparsing raw arguments.

The relaunched process deterministically derives the ticket path from its
resolved data root and `--soft-restart-id <restart-id>`. It never scans
`Updates/**` for a candidate ticket. The ticket's embedded restart id must match
the command-line value and directory name. Housekeeping may age out other stale
handoff directories, but it never selects or consumes them on behalf of the
current launch.

After restart confirmation, the handoff calls a dedicated non-prompting entry
point rather than re-entering the normal close confirmation. The existing
graceful shutdown still captures the latest available agent
resume commands, flushes documents, stops session processes, disposes WebViews,
drains the agent-control endpoint, and releases the data-root mutex.

## Updater process

A new `Pact.Updater` project produces a framework-dependent single-file
`Pact.Updater.exe`. It depends only on the installed .NET runtime and base class
libraries. The release ZIP and Inno Setup payload both contain it.

Pact copies the helper into the handoff directory before launching it, so the
helper itself does not lock a file in the installation directory. The helper
accepts only the absolute path to the one-time ticket. It does not accept an
arbitrary command string.

The updater validates that:

- the ticket schema and mode are supported;
- the target executable is named `Pact.App.Avalonia.exe`;
- the installation directory is the executable's parent and is not a drive
  root or another broad target;
- every update-owned path stays below the expected staging directory;
- the source process id names the process that initiated the handoff;
- the Setup hash still matches the ticket before execution.

The updater first waits for the source Pact process to exit. That is necessary
but not sufficient: bundled ConPTY runs
`<installation>/conpty/OpenConsole.exe`, and the ConPTY process may outlive the
Pact parent or the agent process tracked by the terminal backend.

Before starting Setup, the updater repeatedly attempts to open both
`Pact.App.Avalonia.exe` and `conpty/OpenConsole.exe` with read/write access and
`FileShare.None`, immediately closing successful probes. Setup may start only
after both exclusive probes succeed in the same iteration. The retry has a
bounded timeout and reports which file remained locked. The updater never kills
Pact, OpenConsole, or agent processes. A file-release timeout occurs after Pact
has exited, so the helper marks the existing ticket `UpdateNotApplied` and
relaunches Pact without applying the update. If the source PID itself does not
exit within the 15-minute bounded wait, the helper removes the still-pending
ticket and exits without Setup or relaunch: the original process still owns the
data-root lease. This budget deliberately exceeds bounded resume capture and
sequential terminal teardown.

For `ApplyUpdate`, the updater starts Setup with structured arguments equivalent
to:

```text
/SP-
/SILENT
/NORESTART
/NORESTARTAPPLICATIONS
/NOCLOSEAPPLICATIONS
/DIR=<current installation directory>
/LOG=<bounded Pact update log path>
```

The running Pact process is already gone, so Setup does not need permission to
close or force-close it. The installer declares `UsePreviousAppDir=no`, so an
explicit `/DIR` makes the current executable directory authoritative. To retain
ordinary interactive upgrade behavior, its code-backed `DefaultDirName` first
uses the current-user AppId registration's nonempty `InstallLocation` and falls
back to `{localappdata}\Programs\Pact Mission Control` only when no previous
location exists. Consequently a Setup launched without `/DIR` still presents
the user's existing directory, while the helper's `/DIR` overrides it. System
restart is prohibited.

For `RestartOnly`, the updater skips Setup and proceeds directly to relaunch.
This is the mode used by the diagnostic soft-restart action.

After a Setup attempt, the helper deletes the downloaded Setup package. It
atomically rewrites the same handoff ticket to `UpdateApplied` or
`UpdateNotApplied`, then starts the exact target executable with `--data-root`
when required and the opaque restart id. On an installer failure it attempts to
relaunch the existing Pact path so restoration still occurs and the bounded
summary can show the sanitized failure category. There is no second result
file. Installer output and command lines containing user paths are not copied
into ordinary status text.

The copied helper may still be executing when the relaunched application starts.
Retained-update housekeeping removes stale helper copies and completed staging
directories on a later safe cleanup pass. It never recursively deletes a path
that has not been resolved and verified below `Temp/Retained/Updates`.
At startup, a handoff directory is eligible only when its name is a canonical
restart id, it has no ticket, and it is not a reparse point. Cleanup deletes
only the known `Pact.Updater.exe` and `setup.log` files before removing an empty
directory.

## Restoration after relaunch

Pact accepts a soft-restart snapshot only when its restart id matches the
command-line value and the ticket carries a terminal outcome. `UpdateApplied`
also requires the running version to be at least the expected target version;
`UpdateNotApplied` deliberately accepts the old running version so restoration
survives the recoverable failure path. A malformed, stale, or still-pending
snapshot is quarantined and reported without preventing normal startup.

Normal durable state loads first. The restoration overlay then:

1. Restores terminal unread flags before selection can acknowledge them.
2. Starts exactly the terminal session ids that had live controllers before
   restart. A session resumes only when its current launch profile supplies a
   resume-command template and Pact captured a usable agent conversation id.
   Otherwise Pact performs the normal cold launch instead of skipping the
   session, and records that fallback in the restoration summary. This includes
   non-agent terminals and agent sessions whose id was not visible before
   shutdown.
3. Starts the orchestrator only when it was running in the snapshot and remains
   enabled and provisioned.
4. Reloads exactly the browser page ids whose hosts were loaded, using existing
   resume URLs and WebView profile data.
5. Leaves previously paused projects and explicitly paused ROOT items paused,
   even if a stale id appears in the snapshot.
6. Restores the prior selected owner and item after background surfaces exist.
   Selecting that item may acknowledge its unread state through the existing
   visibility rules; unread on other tabs remains set.
7. Reports individual restoration failures and any `UpdateNotApplied` category
   while continuing with remaining items.
8. Consumes the ticket once so later ordinary starts use the normal cold-start
   behavior.

Browser unread state continues to come from retained web-monitor snapshots.
Terminal unread state is restored only through the one-time soft-restart
snapshot and is not added to `projects.json` or `root-tabs.json`.

## User interface

Settings gains an **Updates** section containing:

- running Pact version;
- current update state and prepared version, if any;
- **Check for updates**;
- **Restart and update** when a package is ready;
- a collapsed **Diagnostics** group with
  **Soft restart and restore active tabs**.

The diagnostic action runs the same safe-restart check, snapshot writer,
graceful shutdown, temporary updater copy, and restoration overlay as a real
update. It differs only by selecting `RestartOnly` and omitting Setup metadata.
If restart is blocked, the UI names the blocking sessions or active review and
does not write a ticket.

The native diagnostic gate requests this same action through
`--soft-restart-probe-output <absolute-json-path>`. The hook writes bounded JSON
evidence after shell readiness; it does not introduce another restart path or
publish the product itself.

When an update is ready, the main window also exposes one compact persistent
action near the existing Settings entry. It opens the restart confirmation; it
does not install immediately.

Dialogs and labels distinguish three separate decisions:

- permission to download;
- readiness waiting for a safe moment;
- permission to restart and apply.

There is no separate warning solely because the current release is unsigned.

## Failure handling

- GitHub unavailable: log automatic failure and retry on the next hourly tick.
- Unsupported release metadata: log and ignore the release.
- Download cancelled or interrupted: remove partial files and remain usable.
- Checksum failure: delete the package, show an explicit failure, and require a
  later redownload.
- Restart becomes unsafe: cancel the handoff before shutdown and continue
  waiting.
- Graceful shutdown or ConPTY teardown fails: do not let the updater install
  while either executable probe remains locked; updater times out and relaunches
  without applying.
- Installer fails: delete Setup, retain sanitized result evidence, and relaunch
  Pact from the current path.
- Relaunch fails: updater leaves bounded diagnostics and exits; the user can
  start Pact normally.
- One session or browser page fails to restore: continue restoring others and
  show a summarized list after startup.
- Ticket version mismatch or reuse: quarantine/delete it and perform normal
  startup rather than entering a restart loop.

No failure triggers a Windows reboot, forced process termination, or deletion
outside the validated update staging directory.

## Testing strategy

### Core and presentation tests

- Stable SemVer parsing, precedence, build metadata, and rejection of
  prereleases, same versions, and downgrades.
- Update-state transitions and prevention of overlapping checks/downloads.
- Hourly scheduling and initial delay through virtual time.
- One automatic prompt per version per process and manual-check override.
- Safe-restart decisions for ordinary, ROOT, reviewer, and orchestrator activity
  and for active reviews.
- Revalidation when restart is confirmed.
- Snapshot round-trip, schema rejection, and one-time consumption.
- Exact restoration set for terminals, browser pages, selection, orchestrator,
  unread flags, and paused exclusions.
- Resume when both template and usable conversation id are available; cold-start
  fallback, rather than omission, when either prerequisite is missing, with the
  fallback included in the restoration summary.
- Partial restoration failures do not stop remaining restoration.

### Infrastructure and updater tests

- Latest-release parsing and exact asset selection through controlled HTTP
  responses.
- Exact `vMAJOR.MINOR.PATCH` tag validation and running
  `AssemblyInformationalVersion` parsing with `+<sha>` metadata.
- Redirect, size, timeout, cancellation, partial-file, and exact GNU
  checksum-line behavior.
- Primary and secondary GitHub rate limits, `Retry-After`,
  `X-RateLimit-Reset`, and suppression of intervening hourly/manual requests.
- Atomic staging and cleanup limited to the update root.
- Deterministic restart-id-to-ticket resolution without directory scanning,
  plus ticket and target-path validation.
- Restart-only handoff.
- Installer success, failure, timeout, package deletion, and relaunch arguments
  through controlled process boundaries.
- File-release gate tests that hold `Pact.App.Avalonia.exe` or
  `conpty/OpenConsole.exe` with `FileShare.None`, prove Setup is not launched,
  release the handle through a controlled signal, and prove installation then
  proceeds. A permanent lock proves bounded timeout without timing sleeps.
- Stale helper and completed-directory cleanup.

### Avalonia and packaging tests

- Headless interaction tests for download/restart dialogs and the Updates
  settings section.
- Diagnostic button success and blocked-state presentation.
- Release-composition tests proving `Pact.Updater.exe` is present in the ZIP and
  Setup payload.
- Existing installer smoke extended to a non-default target directory and the
  update command-line contract.

### Native acceptance gate

Use a disposable absolute `--data-root` and an installation directory different
from the Inno default. Prepare project and ROOT terminals, loaded browser pages,
terminal and browser unread markers, and paused items. Run the diagnostic soft
restart and verify that only previously live items return while paused items
stay paused. Then apply a locally built Setup through the same handoff and verify
the running version, unchanged installation directory, deleted package,
restored state, and absence of a Windows reboot.

All .NET restore, build, and test commands follow the repository's mandatory
resource limits and run one heavy command at a time.

## Acceptance criteria

- Pact checks the fixed repository's latest stable release every hour without
  overlapping requests or blocking startup.
- A newer version is never downloaded without the user's download approval.
- A package is never marked ready until its exact SHA-256 is verified.
- Update application is offered only at the agreed safe boundary and still
  requires restart approval.
- Setup receives the directory of the currently running Pact executable.
- Windows is never rebooted and Pact is never force-killed by the updater.
- Setup is deleted after the install attempt.
- Relaunched Pact uses the same data root and restores the previously live
  cockpit surfaces, selection, orchestrator state, and unread markers.
- Every previously live terminal is started; sessions without a viable resume
  command cold-start and are named in the restoration summary.
- Existing paused state and storage boundaries remain unchanged.
- The restart-only Settings action exercises the same production handoff and
  restoration path.
- Ordinary cold starts without a valid one-time ticket retain their existing
  behavior.
