# Windows installer smoke protocol

## Purpose

Verify the exact release Setup on clean Windows 11 x64 test machines. Record
the release version, source commit, Setup SHA-256, Windows
version, language, and observed result for every case. This protocol is manual;
CI compilation and artifact validation do not imply these cases passed.

## Core cases

1. With both .NET 10 Runtime x64 and WebView2 absent, confirm Setup names both
   prerequisites, asks before installing them, and installs PACT only after both
   are detected.
2. Repeat with only .NET missing, only WebView2 missing, and both already
   present. An already suitable runtime must not be reinstalled.
   Cover WebView2 installed per-user and per-machine separately.
3. Repeat .NET detection with an x86-only runtime, .NET 9 x64, a .NET 10
   preview, the .NET 10 SDK, the .NET 10 Desktop Runtime, and a newer stable
   .NET 10 patch. The first three must still require the stable .NET 10 x64
   runtime; the SDK, Desktop Runtime, and newer stable patch must satisfy it.
4. Decline prerequisite consent and decline the .NET elevation prompt. Setup
   must stop without installing PACT and remain safe to run again.
   Repeat elevation with alternate administrator credentials and verify the
   prerequisite is visible to the original user after elevation completes.
5. Block the .NET download or make a prerequisite installer fail. Setup must
   show an actionable error and must not leave a partial PACT installation.
   Separately replace the cached WebView2 bootstrapper with a corrupt or
   untrusted file before compiling a disposable candidate; the build must
   reject it, so such bytes never reach Setup.
6. Exercise a prerequisite result that requires reboot. PACT must not be
   installed until Windows is restarted and Setup is run again.
7. Install in English and Russian, including from a path containing Cyrillic
   characters. Verify the current-user destination, Start
   menu shortcut, optional desktop shortcut, launch action, Apps & features
   entry, and localized prerequisite/error text.
8. Install the same version twice. Verify one uninstall entry remains, the
   installed payload still matches the release ZIP, and user-owned files are
   unchanged.
9. Upgrade over the previous version while PACT is closed. Compare every
   installed payload hash with the new release ZIP; verify that obsolete
   release files are gone, one uninstall entry remains, shortcuts remain
   valid, and `%APPDATA%\Pact` data is unchanged.
10. Start PACT with live terminal sessions, then run the upgrade. Setup must
    use the Windows file-in-use flow and request that PACT be closed; it must
    not silently terminate active agent sessions. Close PACT only after saving
    work, continue Setup, and verify the replacement hashes.
11. Attempt to install an older version over a newer one. Record the current
   behavior and treat an accepted downgrade as a release blocker.
12. Launch the freshly installed application once. Verify ROOT renders, a
    terminal session can start, and a web tab can render before closing it.
13. Uninstall PACT. Installed application files and shortcuts must be removed;
    `%APPDATA%\Pact` and separately installed prerequisites must remain.

## Trust cases

- For an unsigned candidate, verify that Setup and Pact-owned binaries are
  reported as unsigned and that the published guidance matches SmartScreen.
- For a signed candidate, verify a valid expected Authenticode publisher on
  Setup, its uninstaller, and Pact-owned binaries. Verify the timestamp and
  compare Setup, ZIP, and SPDX hashes with `SHA256SUMS.txt`.

## Results

| Case | Status (`PASS`/`FAIL`/`NOT RUN`) | Environment | Evidence and notes |
| --- | --- | --- | --- |
| Missing both prerequisites |  |  |  |
| Missing .NET only |  |  |  |
| Missing WebView2 only |  |  |  |
| Both prerequisites present |  |  |  |
| Runtime architecture/version matrix |  |  |  |
| WebView2 per-user/per-machine |  |  |  |
| Consent/elevation cancelled |  |  |  |
| Alternate administrator credentials |  |  |  |
| Download/install failure |  |  |  |
| Corrupt/untrusted WebView2 bootstrapper |  |  |  |
| Reboot required |  |  |  |
| English, Russian, and Cyrillic path |  |  |  |
| Same-version reinstall |  |  |  |
| Upgrade while closed |  |  |  |
| Upgrade while running |  |  |  |
| Downgrade blocked |  |  |  |
| First installed launch |  |  |  |
| Uninstall preserves user data |  |  |  |
| Authenticode/SmartScreen state |  |  |  |
