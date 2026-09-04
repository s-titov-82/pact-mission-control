# Development

## Prerequisites

- Windows 10 or 11 x64.
- .NET 10 SDK; `global.json` requests 10.0.302 and permits roll-forward to a
  newer .NET 10 feature band.
- Node.js 22 or newer on `PATH` for terminal-host behavior tests.
- Microsoft Edge WebView2 Runtime.
- PowerShell 7 for repository tools.

No global .NET tool installation or npm install is required. The repository
pins its .NET tools, NuGet graph, and vendored xterm npm graph.

## Restore, build, and test

Keep builds bounded on shared workstations:

```powershell
dotnet tool restore --disable-parallel
dotnet restore Pact.slnx --disable-parallel --locked-mode
dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory!=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
```

Run the bundled-ConPTY tests separately on Windows:

```powershell
dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
```

`TerminalHostAssetTests` runs the production terminal JavaScript with Node.js.
The native WebView2 gate needs an interactive desktop; its `-SelfTest` validates
only the evidence contract:

```powershell
pwsh -NoProfile -File tools/Invoke-NativeWebViewGate.ps1 -SelfTest
```

Do not report that self-test as a real WebView2 candidate run.

## Project layout

- `src/Pact.Core` — pure contracts and state machines.
- `src/Pact.Infrastructure` — Windows, storage, Git, and ConPTY adapters.
- `src/Pact.Presentation` — orchestration and view models.
- `src/Pact.App.Avalonia` — the sole desktop product head.
- `tests` — NUnit contract, integration, and application-head tests.
- `third_party` — pinned ConPTY and xterm inputs with provenance.
- `tools` — profile, verification, packaging, and publication helpers.

See [Architecture](architecture.md) for ownership and dependency boundaries.

## Agent CLI compatibility

The profile-driven MCP launch templates were verified on 2026-08-01 against:

- Claude Code 2.1.220, whose CLI accepts `--mcp-config <configs...>` for JSON
  MCP server configuration;
- Codex CLI 0.146.0, whose root CLI accepts repeatable
  `-c, --config <key=value>` dotted-path overrides. Its `codex mcp add` command
  confirms streamable HTTP `--url` and `--bearer-token-env-var` carriers.

Accordingly, shipped Claude profiles use `--mcp-config "{configPath}"`.
Shipped Codex profiles set `mcp_servers.pact.url={endpointUrl}` and
`mcp_servers.pact.bearer_token_env_var={tokenEnvVar}` through `-c`; Pact places
the bearer token in the named environment variable, never in command-line
arguments.

## Vendored terminal assets

Verify xterm assets without changing tracked files:

```powershell
pwsh -NoProfile -File tools/Sync-XtermAssets.ps1 -Verify
```

To update them, change the exact npm versions in
`third_party/xterm/package.json`, regenerate its lockfile, run the script
without `-Verify`, and review every generated hash and license.

## Repository validation

Run the repository-owned documentation, privacy, workflow, and SBOM contracts
before packaging:

```powershell
pwsh -NoProfile -File tools/Test-MarkdownLinks.ps1 -SelfTest
pwsh -NoProfile -File tools/Test-MarkdownLinks.ps1
pwsh -NoProfile -File tests/powershell/Test-PublicTree.Tests.ps1
pwsh -NoProfile -File tools/Test-PublicTree.ps1
pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1 -SelfTest
pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1 -CiWorkflow .github/workflows/ci.yml -ReleaseWorkflow .github/workflows/release.yml
pwsh -NoProfile -File tests/powershell/PactSbom.Tests.ps1 -ModulePath tools/PactSbom.psm1 -TemporaryRoot artifacts/sbom-selftest
```

## Packaging

Create and independently validate an unsigned local release:

```powershell
pwsh -NoProfile -File tools/Publish-Pact.ps1 -Version 0.1.0 -RepositoryUrl https://github.com/s-titov-82/pact-mission-control -AuthenticodeStatus Unsigned
pwsh -NoProfile -File tools/Test-PublicationArtifacts.ps1 -Version 0.1.0 -ReleaseDirectory artifacts/release/0.1.0 -ExpectedAuthenticodeStatus Unsigned
```

The supported target is framework-dependent `win-x64`.
Packaging derives the exact NuGet runtime set from the published `.deps.json`
and combines it with the reviewed mappings in
`third_party/runtime-components.json`. When those mappings or their license
evidence change, run the focused contract before packaging:

```powershell
pwsh -NoProfile -File tests/powershell/PactSbom.Tests.ps1 -ModulePath tools/PactSbom.psm1 -TemporaryRoot artifacts/sbom-selftest
```

## Publishing a release

Maintainers publish from an existing `vMAJOR.MINOR.PATCH` tag:

1. Update the single `VersionPrefix` in `Directory.Build.props`, update
   `CHANGELOG.md`, and run the full build, test, repository-validation, native,
   and packaging gates above.
2. Create and push a tag whose version exactly matches `VersionPrefix`. Pushing
   the tag starts `.github/workflows/release.yml`; a manual workflow dispatch
   accepts the same existing tag and does not create one.
3. Configure both `PACT_SIGNING_PFX_BASE64` and
   `PACT_SIGNING_PFX_PASSWORD` repository secrets to Authenticode-sign the Pact
   binaries. With neither secret the release is explicitly unsigned; providing
   only one is an error.
4. Verify that the workflow publishes the ZIP, standalone SPDX manifest, and
   `SHA256SUMS.txt`, creates provenance/SBOM attestations, and creates the GitHub
   release for the existing tag.

Hosted CI runs only the native WebView evidence-contract self-test. A real
interactive native WebView candidate run remains a separate release decision
and must not be inferred from the hosted result.

## Code and test policy

Warnings and analyzers remain errors. Fix diagnostics in code or use a narrow,
documented suppression; do not weaken global severities. Public classes,
members, and properties require XML documentation that explains their contract
or a non-obvious constraint.

Tests should exercise business and user-visible contracts, not private method
names, source text, or XAML shape. Use controlled signals and virtual time
instead of scheduler sleeps. Preserve unrelated dirty-worktree changes and run
focused tests plus the relevant native gate for ConPTY or WebView changes.
