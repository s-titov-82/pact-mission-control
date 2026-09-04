# Configuration

Open Settings with the gear button. The supported editing route is the
form-based settings window; raw JSON is available as an escape hatch, not the
normal workflow.

## Sections and files

The default data root is `%APPDATA%/Pact`. Durable files live under its
`Settings` directory:

| Settings section | Storage |
| --- | --- |
| Current projects and paused projects | `projects.json` |
| Root tabs | `root-tabs.json` |
| Launch profiles | `shell-profiles.json` |
| Review profiles | `review-profiles.json` |
| Orchestrator | `orchestrator.json` |
| Prompt and shell templates | `prompt-templates.json` |
| Web link templates | `web-link-templates.json` |
| Web monitoring rules | `web-monitor-rules.json` |
| Scenarios | `scenarios.json` |
| Git helpers | `git-helpers.json` |
| Recent directories | `recent-directories.json` |
| Appearance | `appearance.json` |

`agent-control.json` owns the fixed loopback MCP port and is not a normal
form-edited section. The orchestrator's durable credential is stored in
`orchestrator.json`; do not share either file.

Window geometry is stored in `window-layout.json`; project notes are stored
under `Settings/Notes`. Project and session edits apply to live runtime state.
File-backed sections preserve unknown JSON properties and entries when the
form saves a known field.

## Template tokens

- Prompt and shell bodies: `{project}`, `{task}`, `{selectedText}`, and
  `{otherSessionSummary}`.
- Web links: `%gitLabRepoId%` and `%teamCityProjectId%`.
- External Git helper arguments: `{root}` and `{branch}`.

`{selectedText}` makes a prompt or shell template selection-aware. Selected
text is inserted verbatim and shell quoting is not added automatically.
Auto-submit controls whether Pact sends Enter after insertion.

## Web monitoring rules

The first enabled rule whose URL pattern matches a loaded web tab owns that
tab. Activity and revision values come from declarative DOM extractors. Use
**Test on current tab** before enabling a rule.

Starter rules are disabled and contain a `CHANGE-ME-` host marker. Replace the
host and review the selectors for your authenticated site; a rule with the
marker still present cannot be enabled. Hidden monitoring continues only for a
WebView that has already been loaded. An unloaded or paused page has no polling
host.

## Data-root override and profiles

Pass an absolute isolated root after `--` when running from source:

```powershell
dotnet run --project src/Pact.App.Avalonia/Pact.App.Avalonia.csproj -- --data-root C:\Pact-Test
```

Only one process may own a data root. Application startup and profile copying
use the same named operating-system mutex; there is no `.lock` file in
`Settings`.

Browser pages use the default profile below `WebView`, so cookies and site
storage survive restarts. The terminal uses the separate `PactTerminal`
profile, which is cleared at startup. Browser cleanup removes only old disk
cache; it preserves cookies, Local Storage, IndexedDB, service workers,
passwords, autofill, and settings.

## Safe backup

Close Pact before copying its profile. Choose an empty absolute destination and
run the profile tool:

```powershell
dotnet run --project tools/Pact.ProfileTool/Pact.ProfileTool.csproj -- --source "$env:APPDATA\Pact" --destination "D:\Backups\Pact"
```

The tool leases both roots, stages the copy atomically, and copies durable
`Settings` and notes rather than logs, browser state, or temporary files. Add
`--replace` only when you intentionally want to replace an existing
destination.

The per-section help available from the Settings `?` button is mirrored in the
[English settings index](help/en/README.md) and
[Russian settings index](help/ru/README.md). For task-oriented instructions,
start with the [English user guide](guide/en/README.md) or
[Russian user guide](guide/ru/README.md).
