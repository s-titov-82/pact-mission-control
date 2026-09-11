# GitHub Automatic Updates and Soft Restart Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver stable-channel GitHub update discovery, verified Setup staging, a diagnostic soft restart that restores active runtime surfaces, and automatic installation into the directory containing the running Pact executable.

**Architecture:** Keep release/network/filesystem mechanics in `Pact.Infrastructure`, the update state machine and UI-neutral contracts in `Pact.Core`/`Pact.Presentation`, Avalonia dialogs and lifecycle wiring in `Pact.App.Avalonia`, and the post-exit install/relaunch boundary in a small `Pact.Updater` executable. A deterministic one-time handoff ticket under the resolved data root connects the old Pact, updater, and relaunched Pact; neither process scans for a ticket.

**Tech Stack:** .NET 10, C# 14, Avalonia 12, `HttpClient`, `System.Text.Json`, Inno Setup, NUnit, Shouldly, PowerShell release-contract tests.

**Spec:** [`docs/superpowers/specs/2026-09-11-github-auto-update-design.md`](../specs/2026-09-11-github-auto-update-design.md)

## Global Constraints

- Start from a branch that contains both the local `3cca66c` work and the installer/release baseline currently at `origin/main` (`4d7bcce`, tag `v0.1.1`). Preserve both histories; do not discard the local commit.
- Implement and ship the four phase gates in order. Do not connect automatic Setup execution until the diagnostic `RestartOnly` path has passed its native gate.
- Stable tags are exactly `^v[0-9]+\.[0-9]+\.[0-9]+$`; drafts and prereleases are never update candidates.
- The current install directory is always `Path.GetDirectoryName(Environment.ProcessPath!)`, never the Inno default and never a registry guess.
- No Windows reboot, forced process termination, transcript persistence, DOM persistence, or update path outside `Temp/Retained/Updates`.
- All public APIs receive XML contract documentation.
- Run one heavy .NET command at a time. Use the repository limits exactly:

  ```powershell
  rtk dotnet restore Pact.slnx --disable-parallel --locked-mode
  rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  ```

- Every task follows red-green-refactor: add a behavioral test, run it and observe the intended failure, implement the minimum contract, rerun the focused test, then commit the task with the plan/spec changes included no earlier than the first implementation commit.
- Before every phase commit, run `rtk git diff --check HEAD -- src tests tools installer .github README.md docs` and inspect `rtk git status --short` so unrelated user changes remain untouched.

---

## Preparation

### Task 0: Reconcile the installer baseline without losing local work

**Files:**

- Integrate from `origin/main`: `installer/**`, `tools/Build-PactInstaller.ps1`, `tools/Complete-PactRelease.ps1`, `tools/Publish-Pact.ps1`, `tools/Test-PactInstaller.ps1`, `tests/powershell/PactInstaller.Tests.ps1`, `tests/powershell/PactInstalledSmoke.Tests.ps1`, `.github/workflows/release.yml`
- Preserve local branch changes introduced by `3cca66c`

**Interfaces:** No product interface changes. This task establishes the release source of truth required by every later phase.

- [ ] Fetch and prove the divergence before mutation:

  ```powershell
  rtk git fetch origin
  rtk git log --oneline --decorate --graph --max-count=12 --all
  rtk git status --short
  ```

- [ ] Integrate `origin/main` using a normal merge so `3cca66c` remains reachable:

  ```powershell
  rtk git merge --no-edit origin/main
  ```

- [ ] Resolve any conflict from the public-readiness and installer work by retaining both behaviors. Confirm `installer/Pact.iss` still has the stable AppId and user-selectable `DefaultDirName`, and `.github/workflows/release.yml` still validates `vMAJOR.MINOR.PATCH`.
- [ ] Run the existing baseline gates before adding updater code:

  ```powershell
  rtk dotnet restore Pact.slnx --disable-parallel --locked-mode
  rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory!=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Do not create a separate plan-only commit. If the merge itself creates a merge commit, keep the spec and plan untracked until Task 1's implementation commit.

---

## Phase 1: Discovery, notification, release notes, and rate limiting

### Task 1: Add strict release-version and update contracts

**Files:**

- Create: `src/Pact.Core/Updates/StableReleaseVersion.cs`
- Create: `src/Pact.Core/Updates/UpdateRelease.cs`
- Create: `src/Pact.Core/Updates/PreparedUpdatePackage.cs`
- Create: `src/Pact.Core/Updates/UpdateState.cs`
- Create: `src/Pact.Core/Updates/UpdateStatus.cs`
- Create: `src/Pact.App.Avalonia/RunningPactVersion.cs`
- Create: `tests/Pact.Core.Tests/Updates/StableReleaseVersionTests.cs`
- Create: `tests/Pact.Core.Tests/Updates/UpdateStatusTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/RunningPactVersionTests.cs`

**Interfaces:**

```csharp
public readonly record struct StableReleaseVersion(int Major, int Minor, int Patch)
    : IComparable<StableReleaseVersion>
{
    public static bool TryParseTag(string? value, out StableReleaseVersion version);
    public static bool TryParseInformationalVersion(
        string? value,
        out StableReleaseVersion version);
    public override string ToString();
}

public sealed record UpdateAsset(string Name, Uri DownloadUri, long Size);

public sealed record UpdateRelease(
    StableReleaseVersion Version,
    string Tag,
    Uri ReleaseNotesUri,
    UpdateAsset Setup,
    UpdateAsset Checksums);

public enum UpdateState
{
    Idle,
    Checking,
    Available,
    Downloading,
    ReadyWaitingForSafeState,
    ReadyToRestart,
    Applying,
    Failed
}

public sealed record PreparedUpdatePackage(
    UpdateRelease Release,
    string SetupPath,
    string SetupSha256,
    string? AuthenticodeStatus);

public sealed record UpdateStatus(
    UpdateState State,
    StableReleaseVersion RunningVersion,
    UpdateRelease? AvailableRelease,
    PreparedUpdatePackage? PreparedPackage,
    string? Error);

internal static class RunningPactVersion
{
    public static StableReleaseVersion Read(Assembly entryAssembly);
}
```

- [ ] Write table-driven tests proving exact `v1.2.3` acceptance and rejection of missing `v`, prerelease/build syntax in a tag, leading/trailing text, missing components, overflow, same-version, and downgrade comparisons.
- [ ] Write informational-version tests for `1.2.3`, `1.2.3+sha`, and rejection of prerelease, missing, malformed, and overflow values. This parser deliberately ignores only `+build` metadata and is the only parser used for the running entry assembly version.
- [ ] Add a host test proving `RunningPactVersion.Read` reads `AssemblyInformationalVersionAttribute` from the supplied entry assembly, strips only `+build` metadata through `TryParseInformationalVersion`, and never reads `AssemblyVersion` or `FileVersion`.
- [ ] Run the focused tests and record the expected compile/test failure:

  ```powershell
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Updates" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~RunningPactVersionTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement immutable contracts and ordinal formatting with no dependency on a third-party SemVer package.
- [ ] Rerun the focused test, then commit together with the approved spec and this plan:

  ```powershell
  rtk git add src/Pact.Core/Updates src/Pact.App.Avalonia/RunningPactVersion.cs tests/Pact.Core.Tests/Updates tests/Pact.App.Avalonia.Tests/RunningPactVersionTests.cs docs/superpowers/specs/2026-09-11-github-auto-update-design.md docs/superpowers/plans/2026-09-11-github-auto-update.md
  rtk git commit -m "feat: define stable update contracts"
  ```

### Task 2: Implement strict GitHub latest-release discovery and backoff metadata

**Files:**

- Create: `src/Pact.Infrastructure/Updates/IGitHubReleaseClient.cs`
- Create: `src/Pact.Infrastructure/Updates/GitHubReleaseClient.cs`
- Create: `src/Pact.Infrastructure/Updates/GitHubReleaseResponse.cs`
- Create: `src/Pact.Infrastructure/Updates/GitHubRateLimit.cs`
- Create: `tests/Pact.Infrastructure.Tests/Updates/GitHubReleaseClientTests.cs`

**Interfaces:**

```csharp
public interface IGitHubReleaseClient
{
    Task<GitHubReleaseResponse> GetLatestStableAsync(
        StableReleaseVersion runningVersion,
        CancellationToken cancellationToken);
}

public abstract record GitHubReleaseResponse
{
    public sealed record NoUpdate : GitHubReleaseResponse;
    public sealed record Available(UpdateRelease Release) : GitHubReleaseResponse;
    public sealed record RateLimited(DateTimeOffset RetryAt, string Reason)
        : GitHubReleaseResponse;
    public sealed record Invalid(string Reason) : GitHubReleaseResponse;
}
```

- [ ] Build a scripted `HttpMessageHandler` test fixture. Assert the exact endpoint, `Accept: application/vnd.github+json`, GitHub API version, and nonempty Pact `User-Agent`.
- [ ] Add failing tests for draft/prerelease rejection, strict tag parsing, same/older version, duplicate/missing/non-uploaded/zero-size assets, exact Setup filename, exact `SHA256SUMS.txt`, and valid release-note URL.
- [ ] Add failing rate-limit tests: `Retry-After` wins; otherwise `403`/`429` plus remaining zero uses `X-RateLimit-Reset`; secondary-limit responses expose a bounded retry request rather than masquerading as `NoUpdate`.
- [ ] Run the focused test and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~GitHubReleaseClientTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement `GitHubReleaseClient` with code-owned constants for `s-titov-82/pact-mission-control`. Deserialize only fields used by the contract and never accept an arbitrary repository or asset name from settings.
- [ ] Configure the typed `HttpClient` in `src/Pact.App.Avalonia/CompositionRoot.cs` with a bounded request timeout. Do not add authentication or persist tokens.
- [ ] Rerun the focused infrastructure tests and commit:

  ```powershell
  rtk git add src/Pact.Infrastructure/Updates tests/Pact.Infrastructure.Tests/Updates src/Pact.App.Avalonia/CompositionRoot.cs
  rtk git commit -m "feat: discover stable GitHub releases"
  ```

### Task 3: Add the hourly update state machine

**Files:**

- Create: `src/Pact.Presentation/Updates/UpdateCheckOrigin.cs`
- Create: `src/Pact.Presentation/Updates/UpdateCoordinator.cs`
- Create: `src/Pact.Presentation/Updates/UpdateStatusChangedEventArgs.cs`
- Create: `tests/Pact.Presentation.Tests/Updates/UpdateCoordinatorTests.cs`
- Modify: `src/Pact.App.Avalonia/CompositionRoot.cs`

**Interfaces:**

```csharp
public enum UpdateCheckOrigin { Automatic, Manual }

public sealed class UpdateCoordinator : IAsyncDisposable
{
    public UpdateStatus Status { get; }
    public DateTimeOffset? AutomaticChecksSuppressedUntil { get; }
    public event EventHandler<UpdateStatusChangedEventArgs>? StatusChanged;

    public Task StartAsync(CancellationToken lifetimeToken);
    public Task<GitHubReleaseResponse> CheckNowAsync(
        UpdateCheckOrigin origin,
        CancellationToken cancellationToken);
    public void DeferAutomaticPrompt(StableReleaseVersion version);
}
```

- [ ] Add fake-time failing tests for first check after 30 seconds, subsequent checks one hour apart, one in-flight check, clean cancellation, and no overlapping manual/automatic request.
- [ ] Test that **Later** suppresses only automatic prompting for the same version in the current process, manual check can surface it again, and a still newer version is surfaced automatically.
- [ ] Test that availability is published only by the single `StatusChanged` transition into `UpdateState.Available`; one release transition produces one UI offer and cannot double-notify the controller.
- [ ] Test rate-limit scheduling separately from the normal hour: no automatic call before `RetryAt`; manual check returns the rate-limited result without bypass; secondary backoff progresses 1, 2, 4, 8 minutes and caps at 1 hour; a successful response clears it.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~UpdateCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement scheduling with `TimeProvider.Delay`, a single async gate, and a coordinator-owned immutable status transition method. Automatic failures return to `Idle` after logging by the host; manual failures become a caller-visible result. Do not use timers tied to an Avalonia control.
- [ ] Register one singleton coordinator and dispose it through the service provider. Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Presentation/Updates tests/Pact.Presentation.Tests/Updates src/Pact.App.Avalonia/CompositionRoot.cs
  rtk git commit -m "feat: schedule hourly update checks"
  ```

### Task 4: Surface discovery in Avalonia without downloading

**Files:**

- Create: `src/Pact.Presentation/Settings/ViewModels/UpdatesSectionViewModel.cs`
- Modify: `src/Pact.Presentation/Settings/SettingsSection.cs`
- Modify: `src/Pact.Presentation/Settings/SettingsHelpContent.cs`
- Modify: `src/Pact.Presentation/Settings/ViewModels/SettingsWindowViewModel.cs`
- Modify: `src/Pact.App.Avalonia/Views/Settings/SettingsTemplates.axaml`
- Modify: `src/Pact.App.Avalonia/Views/Settings/SettingsWindow.axaml.cs`
- Create: `src/Pact.App.Avalonia/Views/Dialogs/UpdateAvailableDialogWindow.axaml`
- Create: `src/Pact.App.Avalonia/Views/Dialogs/UpdateAvailableDialogWindow.axaml.cs`
- Create: `src/Pact.App.Avalonia/Controllers/AvaloniaUpdateController.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaShellControllerFactory.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `tests/Pact.Presentation.Tests/Settings/SettingsWindowViewModelTests.cs`
- Modify: `tests/Pact.Presentation.Tests/Settings/SettingsHelpContentTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Views/SettingsWindowInteractionTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaUpdateControllerTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Views/UpdateAvailableDialogWindowTests.cs`

**Interfaces:**

```csharp
public sealed class UpdatesSectionViewModel : SettingsSectionViewModelBase
{
    public string RunningVersion { get; }
    public string StateText { get; }
    public bool CanCheck { get; }
    public event EventHandler? CheckRequested;
    public event EventHandler? OpenReleaseNotesRequested;
}
```

- [ ] Add failing presentation tests that `SettingsSection.Updates` exists, has help, is inserted after Appearance, is non-file-backed (`Open raw JSON`, Save, and Revert hidden/disabled), and reports running version/state.
- [ ] Add failing Avalonia tests for the Updates template and manual-check outcomes: update available, no update, rate-limited with retry time, and network failure. Implement a dedicated three-action dialog with **Download**, **Later**, and **Open release notes**; do not overload the existing fixed-label Yes/No dialog. Assert release notes go through `IExternalLauncher` and no browser is opened for a non-HTTPS URI.
- [ ] Run the two focused suites and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Settings" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Update" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement a non-persisting Updates section. `AvaloniaUpdateController` subscribes after shell initialization, starts the coordinator using the app lifetime token, shows one three-action availability dialog when `StatusChanged` enters `UpdateState.Available`, and logs automatic failures via `AppLog` without modal noise. Do not add a second discovery event.
- [ ] Keep phase 1 download-free: the **Download** action displays “Downloading is available in the next delivery phase” until Task 6 lands; no network asset stream is opened here.
- [ ] Rerun focused tests, manually verify Settings and release-note launch, then commit the independently shippable phase:

  ```powershell
  rtk git add src/Pact.Presentation/Settings src/Pact.App.Avalonia/Controllers src/Pact.App.Avalonia/Views tests/Pact.Presentation.Tests/Settings tests/Pact.App.Avalonia.Tests
  rtk git commit -m "feat: surface available Pact updates"
  ```

---

## Phase 2: Download, verify, stage, and open for manual installation

### Task 5: Add exact checksum parsing and update storage boundaries

**Files:**

- Modify: `src/Pact.Infrastructure/Storage/AppPaths.cs`
- Create: `src/Pact.Infrastructure/Updates/ReleaseChecksumManifest.cs`
- Create: `src/Pact.Infrastructure/Updates/UpdatePathPolicy.cs`
- Create: `tests/Pact.Infrastructure.Tests/Updates/ReleaseChecksumManifestTests.cs`
- Create: `tests/Pact.Infrastructure.Tests/Updates/UpdatePathPolicyTests.cs`
- Modify: `tests/Pact.Infrastructure.Tests/Storage/AppPathsTests.cs`

**Interfaces:**

```csharp
public sealed class ReleaseChecksumManifest
{
    public static ReleaseChecksumManifest Parse(ReadOnlySpan<byte> utf8);
    public string GetRequiredSha256(string fileName);
}

public sealed class UpdatePathPolicy
{
    public UpdatePathPolicy(AppPaths paths);
    public string GetPackageDirectory(StableReleaseVersion version);
    public string GetHandoffDirectory(string restartId);
    public string EnsureOwnedPath(string candidate);
}
```

- [ ] Add failing parser tests for the exact line `<64 hex> *<filename>\n`, upper/lower hex, multiple published artifacts, exact ordinal Setup lookup, BOM, CR/CRLF, missing final LF, duplicate names, NUL, `/` or `\` in names, malformed hex, whitespace variants, and missing Setup entry.
- [ ] Add failing `AppPathsTests` proving `UpdatesDirectory`, `UpdatePackagesDirectory`, and `UpdateHandoffsDirectory` are the one canonical directory map below `Temp/Retained/Updates` and never create a fifth top-level data-root directory. Add `UpdatePathPolicyTests` only for version/id derivation and containment rejection using those injected `AppPaths`; the policy must not expose duplicate package/handoff root properties.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~ReleaseChecksumManifestTests|FullyQualifiedName~UpdatePathPolicyTests|FullyQualifiedName~AppPathsTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement parsing directly over UTF-8 bytes so line-ending and BOM rules cannot be normalized away by `TextReader`. Add `UpdatesDirectory`, `UpdatePackagesDirectory`, and `UpdateHandoffsDirectory` to `AppPaths` below `RetainedTempDirectory`; inject that canonical map into `UpdatePathPolicy`, which owns only safe child derivation and containment validation.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Infrastructure/Storage/AppPaths.cs src/Pact.Infrastructure/Updates tests/Pact.Infrastructure.Tests
  rtk git commit -m "feat: define verified update storage"
  ```

### Task 6: Stream and verify Setup atomically

**Files:**

- Create: `src/Pact.Infrastructure/Updates/IUpdatePackageStore.cs`
- Create: `src/Pact.Infrastructure/Updates/UpdatePackageStore.cs`
- Create: `tests/Pact.Infrastructure.Tests/Updates/UpdatePackageStoreTests.cs`
- Modify: `src/Pact.Presentation/Updates/UpdateCoordinator.cs`
- Modify: `tests/Pact.Presentation.Tests/Updates/UpdateCoordinatorTests.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaUpdateController.cs`
- Modify: `src/Pact.Presentation/Settings/ViewModels/UpdatesSectionViewModel.cs`
- Modify: `src/Pact.App.Avalonia/Views/Settings/SettingsTemplates.axaml`

**Interfaces:**

```csharp
public interface IUpdatePackageStore
{
    Task<PreparedUpdatePackage?> TryGetVerifiedAsync(
        UpdateRelease release,
        CancellationToken cancellationToken);
    Task<PreparedUpdatePackage> DownloadAndVerifyAsync(
        UpdateRelease release,
        IProgress<long>? progress,
        CancellationToken cancellationToken);
}
```

- [ ] Add failing tests that both assets stream to `.partial`, enforce GitHub sizes, reject truncation/oversize/hash mismatch/redirect to non-HTTPS, atomically rename only after verification, delete failed partial/package files, and reuse a fully rehashed staged Setup after ordinary restart.
- [ ] Test that redirects are accepted only when the original asset URI came from the accepted GitHub response and every effective URI remains HTTPS. Do not trust a filename from `Content-Disposition`.
- [ ] Add coordinator transition tests: `Available -> Downloading -> ReadyWaitingForSafeState`; cancellation returns to `Available`; failure becomes `Failed`; an older later response cannot replace a newer prepared package. `UpdateStatus.PreparedPackage` is the coordinator's only prepared-package representation, and ready states always carry that exact object.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~UpdatePackageStoreTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~UpdateCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement bounded streaming and SHA-256 verification. Query Authenticode only for diagnostic status; do not reject `NotSigned`.
- [ ] Replace phase 1's temporary Download response with explicit consent followed by `DownloadAndVerifyAsync`. Add **Open containing folder** through `IExternalLauncher`; phase 2 still never invokes Setup.
- [ ] Rerun focused suites, manually download a disposable release asset into an isolated `--data-root`, verify the folder action, and commit the phase:

  ```powershell
  rtk git add src/Pact.Infrastructure/Updates src/Pact.Presentation/Updates src/Pact.Presentation/Settings src/Pact.App.Avalonia tests
  rtk git commit -m "feat: stage verified Pact updates"
  ```

---

## Phase 3: Diagnostic soft restart and restoration overlay

### Task 7: Define deterministic launch options and one-time handoff tickets

**Files:**

- Create: `src/Pact.Core/Updates/SoftRestartMode.cs`
- Create: `src/Pact.Core/Updates/SoftRestartOutcome.cs`
- Create: `src/Pact.Core/Updates/SoftRestartTicket.cs`
- Create: `src/Pact.Core/Updates/SoftRestartSelection.cs`
- Create: `src/Pact.Core/Updates/RestorationSummary.cs`
- Create: `src/Pact.Infrastructure/Updates/SoftRestartTicketStore.cs`
- Create: `src/Pact.Infrastructure/Updates/ISoftRestartTicketStore.cs`
- Create: `src/Pact.App.Avalonia/AppLaunchOptions.cs`
- Modify: `src/Pact.App.Avalonia/AppProfileDefaults.cs`
- Modify: `src/Pact.App.Avalonia/Program.cs`
- Create: `tests/Pact.Infrastructure.Tests/Updates/SoftRestartTicketStoreTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/AppLaunchOptionsTests.cs`

**Interfaces:**

```csharp
public enum SoftRestartMode { ApplyUpdate, RestartOnly }

public enum SoftRestartOutcomeKind
{
    Pending,
    Restarted,
    UpdateApplied,
    UpdateNotApplied
}

public sealed record SoftRestartOutcome(
    SoftRestartOutcomeKind Kind,
    string? ErrorCategory);

public sealed record SoftRestartTicket(
    int SchemaVersion,
    string RestartId,
    SoftRestartMode Mode,
    StableReleaseVersion? ExpectedTargetVersion,
    int SourceProcessId,
    string ExecutablePath,
    string InstallationDirectory,
    string DataRoot,
    bool PassDataRoot,
    string? SetupPath,
    string? SetupSha256,
    SoftRestartOutcome Outcome,
    string? SoftRestartProbeOutputPath,
    IReadOnlyList<string> LiveTerminalIds,
    IReadOnlyList<string> LoadedWebPageIds,
    IReadOnlyList<string> UnreadTerminalIds,
    SoftRestartSelection? Selection,
    bool OrchestratorWasRunning);

internal sealed record AppLaunchOptions(
    AppDataProfile Profile,
    string? EngineProbeOutputPath,
    string? SoftRestartId,
    string? SoftRestartProbeOutputPath);

public interface ISoftRestartTicketStore
{
    Task<string> WriteNewAsync(SoftRestartTicket ticket, CancellationToken cancellationToken);
    Task<SoftRestartTicket?> ConsumeExactAsync(
        string restartId,
        StableReleaseVersion runningVersion,
        CancellationToken cancellationToken);
}
```

- [ ] Add failing argument tests for the complete accepted set: absolute optional `--data-root <path>` and `--data-root=<path>`, absolute `--engine-probe-output <json-path>`, one opaque `--soft-restart-id <id>`, and absolute `--soft-restart-probe-output <json-path>`. Reject duplicate, missing, empty, invalid, or unknown arguments and preserve deterministically whether `--data-root` must be replayed.
- [ ] Add failing ticket tests for atomic write, schema/mode/outcome validation, embedded id matching both CLI id and directory, exact path derivation, single consumption, quarantine of malformed/stale/pending tickets, and no directory scan when another valid ticket exists. Enforce the ApplyUpdate running-version floor only for `UpdateApplied`; accept `UpdateNotApplied` with the old running version so the captured state can still be restored and the sanitized failure category reported. The optional absolute probe-output path is valid only for `RestartOnly` and is the only diagnostic launch argument the helper may replay.
- [ ] Add traversal tests for restart id, Setup, executable, data-root, and diagnostic probe-output paths. Use a 32-byte cryptographically random id encoded as 64 lowercase hex characters.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~SoftRestartTicketStoreTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AppLaunchOptionsTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Parse all launch options once before acquiring `AppDataProcessLease`. Pass the typed options through `AppBootstrap`/DI, change `EngineProbeRunner` to consume `EngineProbeOutputPath` instead of reparsing raw `args`, and do not call `StartWithClassicDesktopLifetime(args)` with arbitrary original arguments.
- [ ] Implement strict JSON options, atomic same-directory replacement, and exact deterministic consumption at `Handoffs/<restart-id>/update-resume.json`. New tickets start with `Pending`; the helper atomically rewrites that same ticket to one terminal outcome before relaunch. Housekeeping may remove only validated, aged directories and must never select a ticket.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Core/Updates src/Pact.Infrastructure/Updates src/Pact.App.Avalonia tests/Pact.Infrastructure.Tests/Updates tests/Pact.App.Avalonia.Tests/AppLaunchOptionsTests.cs
  rtk git commit -m "feat: add deterministic soft restart tickets"
  ```

### Task 8: Make runtime-state capture and safe-restart policy explicit

**Files:**

- Modify: `src/Pact.Presentation/Services/SessionRuntimeCoordinator.cs`
- Modify: `src/Pact.Presentation/Services/TerminalTabStatusCoordinator.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaWebPageCoordinator.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaScenarioCoordinator.cs`
- Create: `src/Pact.App.Avalonia/Controllers/SoftRestartSafetyPolicy.cs`
- Create: `src/Pact.App.Avalonia/Controllers/SoftRestartSnapshotBuilder.cs`
- Modify: `tests/Pact.Presentation.Tests/Services/SessionRuntimeCoordinatorTests.cs`
- Modify: `tests/Pact.Presentation.Tests/Services/TerminalTabStatusCoordinatorTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaWebPageCoordinatorTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartSafetyPolicyTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartSnapshotBuilderTests.cs`

**Interfaces:**

```csharp
public IReadOnlyList<string> GetActiveSessionIds();
public IReadOnlyDictionary<string, TerminalClassifierDiagnostics> GetDiagnosticsSnapshot();
internal IReadOnlyList<string> GetLoadedPageIds();
internal bool HasActiveRun { get; }

internal sealed record SoftRestartBlocker(string Id, string Description);
internal sealed record SoftRestartSafetyResult(
    bool CanRestart,
    IReadOnlyList<SoftRestartBlocker> Blockers);
```

- [ ] Add failing snapshot tests for live project, ROOT, reviewer, and orchestrator terminals; loaded but hidden web pages; unread terminal diagnostics; current owner/item selection; and orchestrator running state.
- [ ] Add failing safety-policy tests for every terminal category busy, any nonterminal scenario including paused/waiting, shutdown begun, and a fully safe cockpit. Browser navigation/monitoring must not block.
- [ ] Assert paused projects and paused ROOT items are omitted from live restore sets even if a stale runtime id is presented to the builder.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~SessionRuntimeCoordinatorTests|FullyQualifiedName~TerminalTabStatusCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~SoftRestart|FullyQualifiedName~AvaloniaWebPageCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Add read-only snapshot APIs that copy collections under their existing locks. Determine busy from `TerminalClassifierDiagnostics`, not label text or glyph state. Determine active scenarios from `ScenarioRunViewModel.IsTerminal`, including manually paused runs.
- [ ] Implement `SoftRestartSnapshotBuilder` over current runtime facts; never serialize screen text, last agent messages, prompts, terminal output, browser HTML, or credentials.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Presentation/Services src/Pact.App.Avalonia/Controllers tests/Pact.Presentation.Tests/Services tests/Pact.App.Avalonia.Tests/Controllers
  rtk git commit -m "feat: capture safe soft restart state"
  ```

### Task 9: Report actual resume versus cold-start fallback and restore unread state

**Files:**

- Create: `src/Pact.Presentation/Services/SessionStartPlan.cs`
- Modify: `src/Pact.Presentation/Services/ShellProfileCommandPlanner.cs`
- Modify: `src/Pact.Core/Sessions/TerminalTabStatusEngine.cs`
- Modify: `src/Pact.Presentation/Services/TerminalTabStatusCoordinator.cs`
- Modify: `tests/Pact.Presentation.Tests/Services/ShellProfileCommandPlannerTests.cs`
- Modify: `tests/Pact.Core.Tests/Sessions/TerminalTabStatusEngineTests.cs`
- Modify: `tests/Pact.Presentation.Tests/Services/TerminalTabStatusCoordinatorTests.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`

**Interfaces:**

```csharp
public sealed record SessionStartPlan(
    string CommandLine,
    TerminalStartMode Mode,
    bool FellBackToColdStart,
    string? FallbackReason);

public static SessionStartPlan GetStartPlan(
    SessionRecord session,
    bool preferResumeCommand);

public void RestoreUnreadCompletion(DateTimeOffset occurredAt);
public bool RestoreUnreadCompletion(string sessionId, DateTimeOffset occurredAt);
```

- [ ] Extend planner tests to assert actual mode, not just command text: valid template plus captured concrete id is `Resume`; missing template, not-yet-captured id, malformed agent resume command, and non-agent terminal are `Cold` with a stable summary reason and still launch `LaunchCommand`.
- [ ] Add failing engine/coordinator tests proving unread can be seeded before selection, remains on nonselected tabs, and follows the existing acknowledgement rule when the restored selected tab becomes visible and active.
- [ ] Add controller tests proving startup diagnostics receive `SessionStartPlan.Mode`; remove the current inference based only on `ResumeCommand` being nonempty.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~TerminalTabStatusEngineTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~ShellProfileCommandPlannerTests|FullyQualifiedName~TerminalTabStatusCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~AvaloniaMainShellControllerTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement the explicit plan. A restore request never skips a terminal: if resume is unavailable, perform a normal cold launch and append one item to `RestorationSummary.ColdStartFallbacks`.
- [ ] Seed unread state after sessions are registered but before restoring selection. Keep terminal unread one-time and out of `projects.json`/`root-tabs.json`.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Core/Sessions src/Pact.Presentation/Services src/Pact.App.Avalonia/Controllers tests
  rtk git commit -m "feat: preserve restart resume diagnostics"
  ```

### Task 10: Implement and validate `Pact.Updater` in `RestartOnly` mode

**Files:**

- Create: `src/Pact.Updater/Pact.Updater.csproj`
- Create: `src/Pact.Updater/Program.cs`
- Create: `src/Pact.Updater/UpdaterCommandLine.cs`
- Create: `src/Pact.Updater/UpdaterRunner.cs`
- Create: `src/Pact.Updater/InstalledFileReleaseGate.cs`
- Create: `src/Pact.Updater/ProcessLauncher.cs`
- Create: `src/Pact.Updater/packages.lock.json` (generated by the first unlocked restore)
- Create: `tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj`
- Create: `tests/Pact.Updater.Tests/packages.lock.json` (generated by the first unlocked restore)
- Create: `tests/Pact.Updater.Tests/UpdaterCommandLineTests.cs`
- Create: `tests/Pact.Updater.Tests/InstalledFileReleaseGateTests.cs`
- Create: `tests/Pact.Updater.Tests/UpdaterRunnerTests.cs`
- Modify: `Pact.slnx`
- Modify: `tools/Publish-Pact.ps1`

**Interfaces:**

```csharp
internal sealed record UpdaterOptions(string TicketPath);

internal interface IProcessLauncher
{
    Task<int?> WaitForExitAsync(int processId, TimeSpan timeout, CancellationToken token);
    Task<int> RunSetupAsync(string setupPath, IReadOnlyList<string> arguments, CancellationToken token);
    void StartPact(string executablePath, IReadOnlyList<string> arguments);
}

internal sealed class InstalledFileReleaseGate
{
    public Task<FileReleaseResult> WaitAsync(
        IReadOnlyList<string> paths,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
```

- [ ] Configure the updater as framework-dependent `win-x64`, `PublishSingleFile=true`, no Avalonia or third-party package dependency, and no arbitrary command-string input. Reference `Pact.Core` only for the strongly typed ticket contract; it is bundled into the single file.
- [ ] Add failing command-line/ticket validation tests: exactly one absolute ticket path; supported schema/mode; executable basename `Pact.App.Avalonia.exe`; installation directory equals executable parent; neither is a drive root; source PID is positive; while the source process is live its executable path matches the normalized ticket path; every update-owned path stays below the Updates root derived from the ticket's exact `Handoffs/<restart-id>` location.
- [ ] Add the required Windows file-lock tests. Create disposable files named `Pact.App.Avalonia.exe` and `conpty/OpenConsole.exe`; hold each independently with `FileAccess.ReadWrite, FileShare.None`; release it through a `TaskCompletionSource`-controlled action and prove the gate completes. Hold either permanently and prove the injected timeout returns its exact locked path. Do not use timing sleeps.
- [ ] Add runner tests proving `RestartOnly` waits for source PID exit, skips Setup and the file gate, atomically changes the existing ticket outcome from `Pending` to `Restarted`, and relaunches exact Pact with optional `--data-root`, required `--soft-restart-id`, and optional `--soft-restart-probe-output` as structured argument-list entries. No other original argument is replayed. A source-PID timeout must delete the exact ticket, exit without relaunching, and leave the still-running Pact process alone.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet restore Pact.slnx --disable-parallel
  rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
  rtk dotnet test tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Confirm the unlocked restore generated both new `packages.lock.json` files, then return to `rtk dotnet restore Pact.slnx --disable-parallel --locked-mode` for every later restore. Implement `RestartOnly`. Keep retry/time behavior injectable so tests use controlled virtual completion. Never kill Pact, OpenConsole, or agent processes.
- [ ] Extend the prepare phase of `tools/Publish-Pact.ps1` to publish `Pact.Updater` as a framework-dependent `win-x64` single file and place exactly `Pact.Updater.exe` in the app publish root. This makes phase 3's diagnostic button executable from a prepared publish before installer integration is enabled.
- [ ] Rerun updater tests and commit:

  ```powershell
  rtk git add src/Pact.Updater tests/Pact.Updater.Tests Pact.slnx tools/Publish-Pact.ps1
  rtk git commit -m "feat: add soft restart updater helper"
  ```

### Task 11: Wire diagnostic handoff, graceful shutdown, and restoration overlay

**Files:**

- Create: `src/Pact.App.Avalonia/Controllers/SoftRestartCoordinator.cs`
- Create: `src/Pact.App.Avalonia/Controllers/SoftRestartRestorer.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaMainShellController.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaShellControllerFactory.cs`
- Modify: `src/Pact.App.Avalonia/CompositionRoot.cs`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.Shutdown.cs`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs`
- Create: `src/Pact.App.Avalonia/Diagnostics/SoftRestartProbeRunner.cs`
- Modify: `src/Pact.Presentation/Settings/ViewModels/UpdatesSectionViewModel.cs`
- Modify: `src/Pact.App.Avalonia/Views/Settings/SettingsTemplates.axaml`
- Modify: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaMainShellControllerTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartCoordinatorTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Controllers/SoftRestartRestorerTests.cs`
- Create: `tests/Pact.App.Avalonia.Tests/Diagnostics/SoftRestartProbeArgumentTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Views/SettingsWindowInteractionTests.cs`

**Interfaces:**

```csharp
internal sealed record SoftRestartRequestResult(
    bool Started,
    IReadOnlyList<SoftRestartBlocker> Blockers,
    string? Error);

internal sealed class SoftRestartCoordinator
{
    public Task<SoftRestartRequestResult> RequestRestartOnlyAsync(
        CancellationToken cancellationToken);
}

internal sealed class SoftRestartRestorer
{
    public Task<RestorationSummary> RestoreAsync(
        SoftRestartTicket ticket,
        CancellationToken cancellationToken);
}
```

- [ ] Add failing coordinator tests for: safety checked before any write; cryptographic id; atomic pending ticket; updater copied from install directory to exact handoff directory; helper started with exact absolute ticket; safety rechecked immediately before helper start; ticket removed if the second check fails; then the non-prompting graceful-shutdown entry point is requested.
- [ ] Add failing restoration tests for exact terminal and browser sets, cold fallback report, orchestrator only if snapshot said running and current config is enabled/provisioned, paused project/ROOT exclusion, failures isolated per item, selection restored last, and ticket consumed once.
- [ ] Add failing UI test for collapsed **Diagnostics** and **Soft restart and restore active tabs**. A blocked action lists blockers and writes no ticket; confirmation defaults to No.
- [ ] Add failing shutdown tests proving declining the diagnostic confirmation creates neither ticket nor helper, while accepting it calls a dedicated confirmed-restart entry point that invokes `StartGracefulShutdown` directly and never re-enters `OnClosing`'s active-session confirmation. Add probe tests for the exact `--soft-restart-probe-output` option and bounded JSON evidence.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~SoftRestart|FullyQualifiedName~SettingsWindowInteractionTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement handoff so the helper is already running before the dedicated non-prompting confirmed-restart entry point calls `StartGracefulShutdown`, but cannot proceed past source-PID wait until Pact exits. Reuse `AppBootstrap.ShutdownAsync`; do not bypass resume-command capture, Notes/layout save, WebView disposal, endpoint shutdown, scenario abort, or data-root mutex release. If the source PID does not exit within the updater's bounded wait, the helper removes the exact ticket and exits without launching another Pact into the still-leased data root.
- [ ] During startup, load durable stores first, then consume the exact terminal-outcome ticket and apply the overlay after terminal/WebView hosts exist. Show one bounded restoration summary listing failed items, cold-start fallbacks, and an `UpdateNotApplied` error category when present; do not block normal startup on a bad ticket. `SoftRestartProbeRunner` invokes the same confirmed diagnostic action after shell readiness and writes only bounded evidence to its parsed absolute output path.
- [ ] Rerun focused tests and the complete nonnative test set, then commit:

  ```powershell
  rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory!=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk git add src tests Pact.slnx
  rtk git commit -m "feat: restore active tabs after soft restart"
  ```

### Task 12: Run the diagnostic native gate before enabling installation

**Files:**

- Create: `tools/Test-PactSoftRestart.ps1`
- Create: `tests/powershell/PactSoftRestart.Tests.ps1`
- Modify: `docs/agent-onboarding.md`

**Interfaces:** The script consumes an existing published app directory, accepts an isolated absolute data root and timeout, and returns nonzero unless the same restart id is consumed and all expected restoration facts are observed. It launches Pact with the typed `--soft-restart-probe-output <absolute-json-path>` hook; it never builds or publishes the product itself.

- [ ] Write script self-tests for path validation, child-process cleanup, timeout, and evidence parsing. Use controlled process/event evidence; do not assert via source text or fixed sleeps.
- [ ] Implement the gate to validate and consume the supplied publish directory, copy it into one disposable test installation, launch with an isolated `--data-root`, establish one live resumable agent session, one live non-agent session, one loaded background browser tab, selection, and an unread marker, invoke the diagnostic action through `--soft-restart-probe-output`, and validate the one-time restoration summary.
- [ ] Include a bundled-ConPTY case and prove both Pact and `OpenConsole.exe` from that publish directory are gone before cleanup. Never kill unrelated processes by image name; retain exact PIDs created by the gate.
- [ ] Run self-tests, then the interactive/native gate on Windows:

  ```powershell
  rtk proxy pwsh -NoProfile -File tests/powershell/PactSoftRestart.Tests.ps1 -ScriptPath ./tools/Test-PactSoftRestart.ps1
  rtk proxy pwsh -NoProfile -File tools/Publish-Pact.ps1 -Version 0.1.1 -RepositoryUrl https://github.com/s-titov-82/pact-mission-control -AuthenticodeStatus Unsigned -Phase Prepare
  rtk proxy pwsh -NoProfile -File tools/Test-PactSoftRestart.ps1 -PublishDirectory ./artifacts/publish/win-x64 -DataRoot ./artifacts/soft-restart-data
  ```

- [ ] Record PASS evidence in the implementation handoff. If the native gate is not run or fails, stop before Task 13; phase 4 remains disconnected.
- [ ] Commit the independently useful diagnostic phase:

  ```powershell
  rtk git add tools/Test-PactSoftRestart.ps1 tests/powershell/PactSoftRestart.Tests.ps1 docs/agent-onboarding.md
  rtk git commit -m "test: gate diagnostic soft restart"
  ```

---

## Phase 4: Automatic Setup application

### Task 13: Add `ApplyUpdate` behavior with the executable-release gate

**Files:**

- Modify: `src/Pact.Updater/UpdaterRunner.cs`
- Modify: `src/Pact.Updater/InstalledFileReleaseGate.cs`
- Modify: `src/Pact.Updater/ProcessLauncher.cs`
- Modify: `tests/Pact.Updater.Tests/InstalledFileReleaseGateTests.cs`
- Modify: `tests/Pact.Updater.Tests/UpdaterRunnerTests.cs`

**Interfaces:**

```csharp
internal sealed record FileReleaseResult(
    bool Released,
    IReadOnlyList<string> LockedPaths);

internal static IReadOnlyList<string> BuildSetupArguments(
    string installationDirectory,
    string logPath);
```

- [ ] Add failing ApplyUpdate tests: rehash Setup immediately before launch; wait for source Pact exit; then require same-iteration exclusive read/write `FileShare.None` probes for both `Pact.App.Avalonia.exe` and `conpty/OpenConsole.exe`; never start Setup on timeout.
- [ ] Assert exact structured Inno arguments `/SP-`, `/SILENT`, `/NORESTART`, `/NORESTARTAPPLICATIONS`, `/NOCLOSEAPPLICATIONS`, `/DIR=<current executable parent>`, `/LOG=<owned bounded log>` with no shell concatenation.
- [ ] Add failure-path tests: hash mismatch, lock timeout, nonzero Setup exit, Setup launch exception, and relaunch exception. Every Setup attempt deletes the downloaded installer; pre-launch validation failure also removes an untrusted package. Before relaunch, atomically rewrite the existing ticket to `UpdateNotApplied` with a sanitized error category but no command line or unredacted user paths. The old running version must consume and restore this outcome without applying the target-version floor.
- [ ] Add success tests: Setup exit zero, package deletion, atomic ticket rewrite to `UpdateApplied`, and relaunch of the exact installed Pact with supported args. Only `UpdateApplied` enforces `runningVersion >= ExpectedTargetVersion` during consumption. No path uses `%LOCALAPPDATA%\Programs\Pact Mission Control` unless that is actually the executable parent.
- [ ] Run updater tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement bounded PID wait and file-release retry. The release gate must close every successful probe immediately and require both probes to succeed in one iteration. It never force-closes applications and never requests a reboot.
- [ ] On every failure after the source process has exited, atomically mark the same handoff ticket `UpdateNotApplied` and attempt to relaunch the existing Pact so restoration and the failure category appear in the summary. If relaunch itself fails, leave the terminal-outcome ticket for explicit recovery and exit nonzero; do not introduce a second result artifact or scan for it on an ordinary launch. A source-PID timeout is the exception: the still-running source owns the data root, so remove the pending ticket and exit without relaunch or Setup.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Updater tests/Pact.Updater.Tests
  rtk git commit -m "feat: apply updates after installed files unlock"
  ```

### Task 14: Connect prepared updates to safe restart UI

**Files:**

- Modify: `src/Pact.App.Avalonia/Controllers/AvaloniaUpdateController.cs`
- Modify: `src/Pact.App.Avalonia/Controllers/SoftRestartCoordinator.cs`
- Modify: `src/Pact.Presentation/Updates/UpdateCoordinator.cs`
- Modify: `src/Pact.Presentation/Settings/ViewModels/UpdatesSectionViewModel.cs`
- Modify: `src/Pact.App.Avalonia/Views/Settings/SettingsTemplates.axaml`
- Modify: `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml`
- Modify: `src/Pact.App.Avalonia/Views/RightActionsPanel.axaml.cs`
- Modify: `src/Pact.App.Avalonia/Views/MainWindow.axaml.cs`
- Modify: `tests/Pact.Presentation.Tests/Updates/UpdateCoordinatorTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Controllers/AvaloniaUpdateControllerTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Views/NotesAndActionsHeadlessTests.cs`
- Modify: `tests/Pact.App.Avalonia.Tests/Views/SettingsWindowInteractionTests.cs`

**Interfaces:**

```csharp
internal Task<SoftRestartRequestResult> RequestApplyUpdateAsync(
    PreparedUpdatePackage package,
    CancellationToken cancellationToken);

public bool IsRestartActionVisible { get; }
public string RestartActionText { get; }
public event EventHandler? RestartAndUpdateRequested;
```

- [ ] Add failing state tests for `ReadyWaitingForSafeState -> ReadyToRestart` only when the policy is safe, back to waiting when activity starts, and `Applying` only after a second safe check at confirmation.
- [ ] Add failing UI tests for a compact persistent `Version <version> ready - restart` action adjacent to Settings, Updates-section **Restart and update**, confirmation default No, and **Later** suppressing repeated modal offers while leaving the persistent action.
- [ ] Add race tests: a terminal becomes busy or a review begins while confirmation is open; no ticket/helper/shutdown occurs and state returns to waiting.
- [ ] Run focused tests and confirm red:

  ```powershell
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~UpdateCoordinatorTests" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-restore -m:1 -nr:false --filter "FullyQualifiedName~Update|FullyQualifiedName~NotesAndActionsHeadlessTests" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Implement ApplyUpdate ticket creation with normalized current `Environment.ProcessPath`, its parent directory, staged Setup path/hash, and current PID. Copy updater before launch exactly as in RestartOnly.
- [ ] Trigger the first restart offer when a ready package first becomes safe; after **Later**, retain only the persistent action. Browser activity never changes readiness.
- [ ] Rerun tests and commit:

  ```powershell
  rtk git add src/Pact.Presentation src/Pact.App.Avalonia tests/Pact.Presentation.Tests tests/Pact.App.Avalonia.Tests
  rtk git commit -m "feat: offer safe restart for prepared updates"
  ```

### Task 15: Include updater in publish, installer, ZIP, and release validation

**Files:**

- Modify: `src/Pact.App.Avalonia/Pact.App.Avalonia.csproj`
- Modify: `src/Pact.Updater/Pact.Updater.csproj`
- Modify: `tools/Publish-Pact.ps1`
- Modify: `tools/Build-PactInstaller.ps1`
- Modify: `tools/Test-PactInstaller.ps1`
- Modify: `tools/Test-PublicationArtifacts.ps1`
- Modify: `tests/powershell/PactInstaller.Tests.ps1`
- Modify: `tests/powershell/PactInstalledSmoke.Tests.ps1`
- Modify: `tests/powershell/PactReleaseComposition.Tests.ps1`
- Modify: `installer/Pact.iss`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `tools/Test-WorkflowContracts.ps1`
- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/agent-onboarding.md`

**Interfaces:** Release payload contains one `Pact.Updater.exe` at the installation root. Its version matches Pact's `VersionPrefix`; Setup and ZIP contain identical helper bytes. Inno declares `UsePreviousAppDir=no`, allowing the updater's explicit `/DIR=<running executable parent>` to remain authoritative. `DefaultDirName` uses a code-owned resolver that returns the existing current-user AppId registration's nonempty `InstallLocation` when present and otherwise returns `{localappdata}\Programs\Pact Mission Control`; therefore an ordinary Setup launched without `/DIR` still defaults to the user's previous installation directory.

- [ ] Add failing release-contract tests that publish the updater framework-dependent single file, reject missing/multiple/wrong-version updater helpers, include it in SPDX output, include it in ZIP and Setup payload, and preserve the exact checksum line `<64 lowercase hex> *<filename>\n` with final LF.
- [ ] Add installer contract coverage for `UsePreviousAppDir=no` together with the code-backed `DefaultDirName` resolver and exact current-user uninstall key `Software\Microsoft\Windows\CurrentVersion\Uninstall\PactMissionControl_is1`. Add two isolated installed-smoke cases: (1) install the old version into A, run the upgrade without `/DIR`, and assert A is upgraded while the hard-coded default directory is not created; (2) leave AppId remembering A, invoke the upgrade with `/DIR=B` where B is the simulated running executable parent, and assert B is upgraded rather than A. Keep the ordinary custom-directory assertion and verify uninstall leaves no updater-owned package outside the data root.
- [ ] Add `Pact.Updater.Tests` as a sixth sequential bounded `dotnet test` command in both `.github/workflows/ci.yml` and `.github/workflows/release.yml`. Update `Test-WorkflowContracts.ps1` and its self-tests from exactly five to exactly six commands while preserving fail-fast and resource-limit assertions for every command.
- [ ] Run PowerShell self-tests and confirm red:

  ```powershell
  rtk proxy pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1 -SelfTest
  rtk proxy pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1 -CiWorkflow .github/workflows/ci.yml -ReleaseWorkflow .github/workflows/release.yml
  rtk proxy pwsh -NoProfile -File tests/powershell/PactReleaseComposition.Tests.ps1 -ScriptPath ./tools/Complete-PactRelease.ps1 -TemporaryRoot ./artifacts/release-composition-selftest
  $pactInnoCompiler = rtk proxy pwsh -NoProfile -File tools/Install-InnoSetup.ps1 -DestinationDirectory ./artifacts/inno-7.1.0 | Select-Object -Last 1
  rtk proxy pwsh -NoProfile -File tests/powershell/PactInstaller.Tests.ps1 -BuildScriptPath ./tools/Build-PactInstaller.ps1 -CompilerPath $pactInnoCompiler -DependencyCacheDirectory ./artifacts/installer-dependencies -TemporaryRoot ./artifacts/installer-selftest
  rtk proxy pwsh -NoProfile -File tests/powershell/PactInstalledSmoke.Tests.ps1 -ScriptPath ./tools/Test-PactInstaller.ps1
  ```

- [ ] Preserve Task 10's updater publish step. In `installer/Pact.iss`, set `UsePreviousAppDir=no` and make `DefaultDirName` call the previous-install resolver described above; do not hard-code the default alone. Explicit `/DIR` then wins for the helper, while a run without `/DIR` presents the recorded location. Make release finalization verify that the same `Pact.Updater.exe` in the common publish tree is consumed by both ZIP and Inno. Keep the app framework-dependent `win-x64` contract.
- [ ] Document stable-only checks, hourly cadence, explicit download consent, current-directory update behavior, safe restart gating, diagnostic soft restart, exact restoration limits, and cold-start fallback reporting.
- [ ] Rerun workflow-contract, release-composition, and installer tests with the pinned toolchain, then commit:

  ```powershell
  rtk git add src tools installer tests .github/workflows/ci.yml .github/workflows/release.yml README.md docs/architecture.md docs/agent-onboarding.md
  rtk git commit -m "build: package Pact automatic updater"
  ```

### Task 16: Final verification and end-to-end update drill

**Files:** Verification only; fix failures in their owning files and amend the corresponding task commit rather than adding workaround code.

**Interfaces:** The end-to-end evidence must identify old/new versions, custom install path, handoff id, Setup exit, deletion result, restored/cold-fallback items, and absence of a Windows reboot request.

- [ ] Run formatting/whitespace checks on every touched path:

  ```powershell
  rtk git diff --check origin/main -- src tests tools installer .github README.md docs
  ```

- [ ] Restore/build once, then run test projects sequentially:

  ```powershell
  rtk dotnet restore Pact.slnx --disable-parallel --locked-mode
  rtk dotnet build Pact.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false
  rtk dotnet test tests/Pact.Core.Tests/Pact.Core.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory!=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2
  rtk dotnet test tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj --no-build --no-restore -m:1 -nr:false --filter "TestCategory=NativeIntegration" -- NUnit.NumberOfTestWorkers=2
  ```

- [ ] Run existing public/release gates plus the new restart gate:

  ```powershell
  rtk proxy pwsh -NoProfile -File tools/Sync-XtermAssets.ps1 -Verify
  rtk proxy pwsh -NoProfile -File tools/Test-MarkdownLinks.ps1
  rtk proxy pwsh -NoProfile -File tools/Test-PublicTree.ps1
  rtk proxy pwsh -NoProfile -File tools/Test-WorkflowContracts.ps1 -CiWorkflow .github/workflows/ci.yml -ReleaseWorkflow .github/workflows/release.yml
  rtk proxy pwsh -NoProfile -File tools/Publish-Pact.ps1 -Version 0.1.1 -RepositoryUrl https://github.com/s-titov-82/pact-mission-control -AuthenticodeStatus Unsigned -Phase Prepare
  rtk proxy pwsh -NoProfile -File tools/Test-PactSoftRestart.ps1 -PublishDirectory ./artifacts/publish/win-x64 -DataRoot ./artifacts/soft-restart-data
  ```

- [ ] Build unsigned `0.1.1` and `0.1.2` installers from disposable release worktrees whose `VersionPrefix` and tags match those versions. First install `0.1.1` into disposable nondefault directory A, launch the `0.1.2` Setup interactively without `/DIR`, and prove A is the preselected upgrade directory and is updated after acceptance. In a clean repeat, leave AppId remembering A but run/copy the test installation from distinct nondefault directory B containing spaces. Serve a fixture `v0.1.2` release matching the real GitHub JSON/assets/checksum contract, consent to download, wait until a busy terminal becomes idle, and approve restart; prove the helper's `/DIR=B` upgrades B rather than remembered A.
- [ ] During the drill, keep the bundled `conpty/OpenConsole.exe` alive briefly after Pact exits and prove Setup is not launched until exclusive opens of both installed executables succeed. Also run the permanent-lock variant and prove bounded abort plus Pact relaunch without Setup.
- [ ] Verify the new executable launches from the same custom directory, the Setup package is deleted, the exact terminal-outcome ticket is consumed once, live terminal/browser sets return, missing resume ids cold-start and appear in the summary, unread survives on nonselected tabs, paused items remain paused, and ordinary next launch is cold. Repeat with a controlled Setup failure and prove the old executable restores the same snapshot and reports `UpdateNotApplied` instead of quarantining it for being below the target version.
- [ ] Inspect `rtk git status --short`, `rtk git diff --stat origin/main`, and commit only any evidence-driven fixes. Do not commit disposable installers, test data roots, logs, or handoff artifacts.

## Completion Criteria

- [ ] Phase 1 can ship without downloading assets.
- [ ] Phase 2 can ship with verified manual installation but no Setup launch.
- [ ] Phase 3's Settings diagnostic button passes the native soft-restart gate before phase 4 is enabled.
- [ ] Phase 4 updates the executable's actual parent directory, waits for both Pact and bundled OpenConsole files to be exclusively openable, deletes Setup, and relaunches without reboot.
- [ ] Stable-only discovery, hourly/rate-limit scheduling, deterministic terminal-outcome ticket routing, exact checksum grammar, explicit download consent, safe-state recheck, failed-update restoration, cold-start fallback reporting, unread restoration, and one-time consumption all have behavioral tests.
- [ ] Full bounded test/build/release gates pass, and the final worktree contains no unrelated or generated changes.
