# Getting started

[English](getting-started.md) | [Русский](../ru/getting-started.md)

PACT:> Mission Control runs on Windows 11 x64. Setup checks for
the [.NET 10 Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0)
and the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)
and offers to install a missing runtime before PACT.
Install the command-line agents you plan to use separately and make their
commands available on `PATH`. The starter profiles expect `codex`, `claude`,
`hermes`, and `pwsh`; PowerShell sessions therefore need PowerShell 7.

## Install

1. Download the Setup executable and `SHA256SUMS.txt` from the
   [latest release](https://github.com/s-titov-82/pact-mission-control/releases/latest).
2. Compare the Setup digest with its entry in `SHA256SUMS.txt`:

   ```powershell
   Get-FileHash .\pact-mission-control-0.1.0-win-x64-setup.exe -Algorithm SHA256
   ```

3. Run Setup and follow its prompts. It installs for the current user and does
   not require elevation for PACT itself; Windows may request elevation only
   when the .NET runtime is missing. Keep an Internet connection available if
   Setup reports a missing prerequisite: it downloads .NET or the current
   Evergreen WebView2 Runtime only after you consent.
4. Start PACT from the Start menu.

The release ZIP is the portable/manual alternative. Install .NET 10 Runtime
x64 and WebView2 first, extract the ZIP to a user-writable directory, and run
`Pact.App.Avalonia.exe`.

Early releases may be unsigned by Authenticode, so Windows SmartScreen can
show a warning. See [Release verification](../../release-verification.md) for
checksum, GitHub attestation, and SPDX SBOM checks. Do not run an archive whose
checksum differs.

## Update or uninstall

Close PACT normally so live agent sessions can finish, then run Setup for the
newer version. Setup upgrades the current-user installation in place and
blocks downgrades. Remove PACT through Windows Installed apps when needed.
Updating or uninstalling does not remove `%APPDATA%\Pact` or separately
installed .NET and WebView2 runtimes.

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
