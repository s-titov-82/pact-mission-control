# Troubleshooting

[English](troubleshooting.md) | [Русский](../ru/troubleshooting.md)

## The application does not start

- Confirm that Windows is x64 and the .NET 10 Desktop Runtime x64 is installed.
- Install or repair the Microsoft Edge WebView2 Runtime.
- If SmartScreen warns about an early unsigned release, verify its checksum and
  GitHub attestation before deciding whether to run it.
- Make sure another Pact process is not already using the same data root.

See [Release verification](../../release-verification.md) before running a
downloaded archive.

## A terminal profile does not start

- Run the configured command in PowerShell and confirm it is available on
  `PATH`.
- Check the profile's Command, Shell, and Id in Settings.
- For PowerShell starter sessions, confirm that PowerShell 7 provides `pwsh`.
- For a resumed session, inspect the session's own resume command as well as its
  launch command.

Use the `?` button in `Settings > Launch profiles` or read
[Terminal templates](../../help/en/settings-launch-profiles.md).

## A session does not receive a prompt

PACT:> deliberately refuses automatic input while a terminal is busy, asking a
question, known to contain unsent input, or locked by a review scenario. Answer
the question or wait for the session to become idle. Manual paste and prompt
actions normally insert text without Enter unless `Auto-submit` is enabled.

If an agent-control action is unavailable after changing scenarios or review
profiles, save Settings to refresh the live catalogs. Direct JSON edits require
that reload boundary or an application restart.

## A browser page is not monitored

- Load or resume the page; an unloaded WebView is never polled.
- Confirm that the first matching rule is enabled and all starter placeholders
  have been replaced.
- Run `Test on current tab` against the loaded page.
- Remember that Pause unloads the page and stops polling until Resume.

See [Web monitoring rules](../../help/en/settings-web-monitoring-rules.md).

## A review is waiting or paused

Terminal activity alone does not complete a review step. The assigned agent
must write the requested non-empty response file with the exact footer. Check
the active review state and journal, answer any visible agent question, and use
Resume only after a manual pause. A valid response can advance while paused.

Closing the project or application aborts the active review. Finished and
aborted runs clean their own `.pact-reviews` directory.

## Documents report a conflict

Pact detected that both the editor buffer and the file on disk changed. Choose
`Reload from disk` to keep the external version or `Save mine` to intentionally
replace it. The local edit remains available after a failed save so you can
retry.

## Get support

Search the [user guide](README.md) and [Settings reference](../../help/en/README.md)
first. For a reproducible defect, open a
[bug report](https://github.com/s-titov-82/pact-mission-control/issues/new?template=bug_report.yml)
and include the Pact version, Windows build, terminal profile, minimal steps,
and sanitized evidence.

Do not put suspected vulnerabilities in a public issue. Follow the
[security policy](../../../SECURITY.md) and use GitHub's private reporting form.

[Previous: Settings, data, and backup](settings-data-and-backup.md) ·
[Back to the guide index](README.md)
