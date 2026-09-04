# Settings, data, and backup

[English](settings-data-and-backup.md) | [Русский](../ru/settings-data-and-backup.md)

Open Settings with the gear button. The form-based window is the supported way
to edit configuration. Select a section, change its fields, and use
`Save section`; `Ctrl+S` saves the active section and `Esc` closes the window.
Switching sections or closing with unsaved changes asks before discarding them.

Every section has a `?` button for concise field-level help. The complete list
is available in the [Settings reference](../../help/en/README.md).

`Open raw JSON` is an escape hatch for advanced edits. Save or discard form
changes first, keep unknown properties intact, and return to the Settings save
boundary or restart Pact before expecting an external edit to affect live
behavior. Do not share `agent-control.json` or `orchestrator.json`.

## Data root

The default data root is `%APPDATA%\Pact`. It has four top-level directories:

- `Settings` contains durable JSON and project Notes;
- `WebView` contains browser profiles, cookies, and site data;
- `Logs` contains bounded disposable diagnostic logs;
- `Temp` contains disposable session staging and owner-managed caches.

PACT:> does not persist terminal transcripts or review journals. Resumed
history belongs to the external agent. The current terminal screen exists only
for the lifetime of its process.

Use `--data-root <absolute-path>` to evaluate a separate profile. One process
must release a data root before another process can open it.

## Browser data and privacy

Browser pages share a persistent WebView2 profile, so logins and site storage
survive restarts. Terminal xterm pages use a separate temporary profile that is
cleared at startup. Routine cleanup removes old browser disk cache but preserves
cookies, Local Storage, IndexedDB, service workers, passwords, autofill, and
settings.

Logs may include sanitized lifecycle and transport metadata, but scenario
prompts, responses, terminal screens, and status-line text are not written to
them. Before attaching a log to an issue, still review it for private hostnames,
paths, account details, or other personal information.

## Safe backup

Close Pact before copying its profile. From a source checkout, use the profile
tool with an empty absolute destination:

```powershell
dotnet run --project tools/Pact.ProfileTool/Pact.ProfileTool.csproj -- --source "$env:APPDATA\Pact" --destination "D:\Backups\Pact"
```

The tool copies durable Settings and Notes, not logs, browser state, or
temporary files. Add `--replace` only when you intentionally want to replace an
existing destination.

[Previous: Reviews and orchestrator](reviews-and-orchestrator.md) ·
[Next: Troubleshooting](troubleshooting.md)
