# Troubleshooting

[English](troubleshooting.md) | [Русский](../ru/troubleshooting.md)

## The application does not start

- Confirm that Windows is x64 and the .NET 10 Runtime x64 is installed.
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

Pact stops automatic prompt delivery if the terminal is waiting for an
answer. Answer the question and retry. Manual paste and prompt actions insert
text without Enter by default. Enable
`Auto-submit` on a template to submit its text.

If an agent-control action is unavailable after changing review types or
reviewer profiles, save Settings to refresh the catalogs. After editing JSON
directly, reload Settings or restart Pact.

## A browser page is not monitored

- Select an unloaded project page to load it. For a manually paused ROOT page,
  use its explicit `Resume` action. An unloaded WebView is never polled.
- Confirm that the first matching rule is enabled and all starter placeholders
  have been replaced.
- Run `Test on current tab` against the loaded page.
- Remember that the per-row ROOT Pause action unloads the page and stops
  polling until Resume.

See [Web monitoring rules](../../help/en/settings-web-monitoring-rules.md).

## A review is waiting for a turn

If Pact shows **Review needs attention**, hover over the warning to see which
session owns the turn. Open that session, answer any pending question, and
ask the agent to call `pact_get_review_turn`. The agent sends its answer with
`pact_submit_review_turn`.

Pausing or closing the project, or closing Pact, aborts the review. Look for
the review log in the project's Docs.

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
