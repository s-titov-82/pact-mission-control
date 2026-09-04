# Getting started

[English](getting-started.md) | [Русский](../ru/getting-started.md)

PACT:> Mission Control runs on Windows 10 or Windows 11 x64. Before starting a
release, install the [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)
and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
Install the command-line agents you plan to use separately and make their
commands available on `PATH`. The starter profiles expect `codex`, `claude`,
`hermes`, and `pwsh`; PowerShell sessions therefore need PowerShell 7.

## Install

1. Download the ZIP and `SHA256SUMS.txt` from the
   [latest release](https://github.com/s-titov-82/pact-mission-control/releases/latest).
2. Compare the ZIP digest with its entry in `SHA256SUMS.txt`:

   ```powershell
   Get-FileHash .\pact-mission-control-0.1.0-win-x64.zip -Algorithm SHA256
   ```

3. Extract the ZIP to a user-writable directory.
4. Start `Pact.App.Avalonia.exe`.

Early releases may be unsigned by Authenticode, so Windows SmartScreen can
show a warning. See [Release verification](../../release-verification.md) for
checksum, GitHub attestation, and SPDX SBOM checks. Do not run an archive whose
checksum differs.

## Add your first project

1. Click `+` beside `PROJECTS`.
2. Select the root directory of a repository.
3. Use the project card's terminal action and choose a launch profile.
4. Start the new session.

Agent sessions start in the project directory. PACT:> embeds the real visible
TUI; it does not replace the agent with a background API. Switching to another
tab leaves the process running.

Use `ROOT` for a terminal or browser page that should not belong to a project.
ROOT terminals start in the current Windows user's profile directory. ROOT has
no project Notes, project documents, Git panel, or review scenarios.

## Use an isolated data root

By default, durable data lives under `%APPDATA%\Pact`. For evaluation, pass a
different absolute path:

```powershell
.\Pact.App.Avalonia.exe --data-root C:\Pact-Test
```

Only one PACT:> process can own a particular data root at a time.

[Next: Workspace and sessions](workspace-and-sessions.md)
