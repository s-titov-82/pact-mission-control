[CmdletBinding()]
param(
    [string]$CiWorkflow,
    [string]$ReleaseWorkflow,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-PactContains {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$Pattern,
        [Parameter(Mandatory)][string]$Description
    )

    if ($Text -notmatch $Pattern) {
        throw "Workflow contract is missing $Description."
    }
}

function Test-PactCommonWorkflow {
    param([Parameter(Mandatory)][string]$Text)

    $usesMatches = [regex]::Matches(
        $Text,
        '(?m)^\s*uses:\s*(?<reference>[^#\s]+)(?:\s+#\s*(?<comment>[^\r\n]+))?')
    foreach ($match in $usesMatches) {
        $reference = $match.Groups['reference'].Value
        if ($reference.StartsWith('./', [System.StringComparison]::Ordinal)) {
            continue
        }
        if ($reference -notmatch '^[^@\s]+@[0-9a-f]{40}$') {
            throw "Remote action is not pinned to a lowercase 40-character commit: $reference"
        }
    }

    foreach ($pin in @(
            @{
                Reference = 'actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803'
                Tag = 'v6'
            },
            @{
                Reference = 'actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1'
                Tag = 'v5'
            })) {
        Assert-PactContains `
            -Text $Text `
            -Pattern ('(?m)' + [regex]::Escape($pin.Reference) + '\s+#\s+' + $pin.Tag + '\s*$') `
            -Description "$($pin.Reference) with reviewed major-tag comment"
    }
}

function Assert-PactHostedNativeGateContract {
    param([Parameter(Mandatory)][string]$Text)

    $nativeGateInvocations = @(
        [regex]::Matches(
            $Text,
            '(?m)^\s*(?!#).*Invoke-NativeWebViewGate\.ps1(?<arguments>[^\r\n]*)$'))
    foreach ($invocation in $nativeGateInvocations) {
        if (-not $invocation.Groups['arguments'].Value.Contains(
                '-SelfTest',
                [System.StringComparison]::Ordinal)) {
            throw 'Hosted CI must not launch the interactive native WebView candidate.'
        }
    }
}

function Assert-PactDotnetTestsFailFast {
    param([Parameter(Mandatory)][string]$Text)

    $testCommands = @([regex]::Matches(
            $Text,
            '(?m)^\s*dotnet test\s+[^\r\n]+$'))
    $guardedCommands = @([regex]::Matches(
            $Text,
            '(?m)^\s*dotnet test\s+[^\r\n]+\r?\n\s*if \(\$LASTEXITCODE -ne 0\) \{ exit \$LASTEXITCODE \}\s*$'))
    if ($guardedCommands.Count -ne $testCommands.Count) {
        throw "Every dotnet test command must fail fast on a non-zero native exit; found $($guardedCommands.Count) guards for $($testCommands.Count) commands."
    }
}

function Assert-PactSixBoundedTestCommands {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][string]$WorkflowName
    )

    $testCommands = [regex]::Matches(
        $Text,
        '(?m)^\s*dotnet test\s+(?<command>[^\r\n]+)$')
    if ($testCommands.Count -ne 6) {
        throw "$WorkflowName must contain exactly six sequential dotnet test commands; found $($testCommands.Count)."
    }
    $updaterCommands = @($testCommands | Where-Object {
            $_.Groups['command'].Value.Contains(
                'tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj',
                [System.StringComparison]::Ordinal)
        })
    if ($updaterCommands.Count -ne 1) {
        throw "$WorkflowName must contain exactly one Pact.Updater.Tests command; found $($updaterCommands.Count)."
    }
    foreach ($testCommand in $testCommands) {
        $command = $testCommand.Groups['command'].Value
        foreach ($requiredArgument in @(
                '--no-build',
                '--no-restore',
                '-m:1',
                '-nr:false',
                'NUnit.NumberOfTestWorkers=2')) {
            if (-not $command.Contains(
                    $requiredArgument,
                    [System.StringComparison]::Ordinal)) {
                throw "Test command is missing $requiredArgument`: $command"
            }
        }
    }

    Assert-PactDotnetTestsFailFast -Text $Text
}

function Test-PactCiWorkflow {
    param([Parameter(Mandatory)][string]$Text)

    Test-PactCommonWorkflow -Text $Text
    foreach ($contract in @(
            @('runs-on:\s*windows-2025', 'windows-2025 runner'),
            @('(?ms)^permissions:\s*\r?\n\s+contents:\s*read\s*$', 'read-only contents permission'),
            @('persist-credentials:\s*false', 'disabled persisted checkout credentials'),
            @('cache-dependency-path:\s*["'']?\*\*/packages\.lock\.json', 'lockfile cache key'),
            @('VersionPrefix', 'package version source'),
            @('version=\$\(\$declared\[0\]\)', 'package version workflow output'),
            @('dotnet tool restore --disable-parallel', 'bounded local-tool restore'),
            @('dotnet restore Pact\.slnx --disable-parallel --locked-mode', 'locked solution restore'),
            @('dotnet build Pact\.slnx --no-restore -m:2 -nr:false -v q -p:BuildInParallel=false', 'bounded build'),
            @('--filter\s+["'']?TestCategory=NativeIntegration', 'real ConPTY integration category'),
            @('Invoke-NativeWebViewGate\.ps1 -SelfTest', 'native-gate contract self-test'),
            @('NOT RUN \(interactive gate\)', 'honest native-gate status'),
            @('Sync-XtermAssets\.ps1 -Verify', 'vendored xterm verification'),
            @('Test-MarkdownLinks\.ps1', 'public Markdown link validation'),
            @('Test-PublicTree\.ps1', 'public-tree privacy validation'),
            @('PactReleaseComposition\.Tests\.ps1', 'release composition self-test'),
            @('PactInstalledSmoke\.Tests\.ps1', 'installer smoke safety self-test'),
            @('Publish-Pact\.ps1 .* -AuthenticodeStatus Unsigned', 'unsigned CI packaging'),
            @('-CompilerPath ''\$\{\{ steps\.inno\.outputs\.compiler \}\}''', 'pinned compiler forwarded to CI release composition'),
            @('-DependencyCacheDirectory ./artifacts/installer-dependencies', 'installer dependency cache forwarded to CI release composition'),
            @('Install-InnoSetup\.ps1', 'pinned Inno Setup provisioning'),
            @('PactInstaller\.Tests\.ps1', 'installer builder self-test'),
            @('Test-PactInstaller\.ps1', 'disposable install/uninstall smoke'),
            @('Test-PublicationArtifacts\.ps1 .* -ExpectedAuthenticodeStatus Unsigned', 'artifact validation'),
            @('actions/upload-artifact@b7c566a772e6b6bfb58ed0dc250532a479d7789f\s+#\s+v6', 'immutable upload action'))) {
        Assert-PactContains -Text $Text -Pattern $contract[0] -Description $contract[1]
    }

    Assert-PactSixBoundedTestCommands -Text $Text -WorkflowName 'CI'

    Assert-PactHostedNativeGateContract -Text $Text

    if ($Text -match '0\.1\.0') {
        throw 'CI must derive the package version from Directory.Build.props instead of hard-coding 0.1.0.'
    }
}

function Test-PactReleaseWorkflow {
    param([Parameter(Mandatory)][string]$Text)

    Test-PactCommonWorkflow -Text $Text
    foreach ($permission in @(
            'contents:\s*write',
            'id-token:\s*write',
            'attestations:\s*write',
            'artifact-metadata:\s*write')) {
        Assert-PactContains `
            -Text $Text `
            -Pattern $permission `
            -Description "release permission $permission"
    }

    foreach ($contract in @(
            @('runs-on:\s*windows-2025', 'windows-2025 release runner'),
            @('tags:\s*\r?\n\s+-\s+["'']v\[0-9\]\+\.\[0-9\]\+\.\[0-9\]\+["'']', 'version-tag trigger'),
            @('workflow_dispatch:', 'manual release dispatch'),
            @('Invoke-NativeWebViewGate\.ps1 -SelfTest', 'native-gate contract self-test'),
            @('NOT RUN \(interactive gate\)', 'honest native-gate status'),
            @('Test-MarkdownLinks\.ps1', 'public Markdown link validation'),
            @('Test-PublicTree\.ps1', 'public-tree privacy validation'),
            @('PactReleaseComposition\.Tests\.ps1', 'release composition self-test'),
            @('PactInstalledSmoke\.Tests\.ps1', 'installer smoke safety self-test'),
            @('Publish-Pact\.ps1 .* -Phase Prepare', 'prepare-only packaging phase'),
            @('Install-InnoSetup\.ps1', 'pinned Inno Setup provisioning'),
            @('PactInstaller\.Tests\.ps1', 'installer builder self-test'),
            @('PACT_SIGNING_PFX_BASE64:\s*\$\{\{\s*secrets\.PACT_SIGNING_PFX_BASE64\s*\}\}', 'optional PFX secret mapping'),
            @('PACT_SIGNING_PFX_PASSWORD:\s*\$\{\{\s*secrets\.PACT_SIGNING_PFX_PASSWORD\s*\}\}', 'optional PFX password mapping'),
            @('signtool\.exe', 'Windows SDK signing tool discovery'),
            @('/fd SHA256', 'SHA-256 Authenticode file digest'),
            @('/td SHA256', 'SHA-256 RFC 3161 digest'),
            @('/tr https://', 'RFC 3161 timestamp service'),
            @('/sha1', 'CurrentUser certificate thumbprint selection'),
            @('/s My', 'CurrentUser personal certificate store'),
            @("Phase = 'Finalize'", 'finalize-only packaging phase'),
            @('CompilerPath = ''\$\{\{ steps\.inno\.outputs\.compiler \}\}''', 'pinned compiler forwarded to release composition'),
            @("DependencyCacheDirectory = './artifacts/installer-dependencies'", 'installer dependency cache forwarded to release composition'),
            @('Publish-Pact\.ps1 @publishArguments', 'unified release composition'),
            @('\$env:PACT_SIGNING_PFX_BASE64 = \$null', 'PFX secret cleared before compiler invocation'),
            @('\$env:PACT_SIGNING_PFX_PASSWORD = \$null', 'PFX password cleared before compiler invocation'),
            @('Remove-Item -LiteralPath \$pfxPath -Force', 'temporary PFX removed before compiler invocation'),
            @('Test-PactInstaller\.ps1', 'disposable install/uninstall smoke'),
            @('Test-PublicationArtifacts\.ps1', 'signed-or-unsigned artifact validation'),
            @('subject-checksums:\s*artifacts/release/.*/SHA256SUMS\.txt', 'checksum provenance attestation'),
            @('subject-path:\s*artifacts/release/.+\.zip', 'ZIP SBOM attestation subject'),
            @('sbom-path:\s*artifacts/release/.*/manifest\.spdx\.json', 'standalone SPDX attestation'),
            @('gh release create', 'GitHub CLI release creation'),
            @('--verify-tag', 'existing-tag verification'),
            @('--generate-notes', 'generated release notes'))) {
        Assert-PactContains -Text $Text -Pattern $contract[0] -Description $contract[1]
    }

    $attestPins = [regex]::Matches(
        $Text,
        '(?m)^\s*uses:\s*actions/attest@f7c74d28b9d84cb8768d0b8ca14a4bac6ef463e6\s+#\s+v4\s*$')
    if ($attestPins.Count -ne 2) {
        throw "Release workflow must use the reviewed actions/attest v4 commit exactly twice; found $($attestPins.Count)."
    }

    Assert-PactSixBoundedTestCommands -Text $Text -WorkflowName 'Release'
    Assert-PactHostedNativeGateContract -Text $Text
    if ($Text -match '(?m)/p\s+\$env:PACT_SIGNING_PFX_PASSWORD') {
        throw 'The PFX password must not be forwarded to Inno Setup or signtool arguments.'
    }
}

function Assert-PactFixtureFails {
    param(
        [Parameter(Mandatory)][scriptblock]$Action,
        [Parameter(Mandatory)][string]$ExpectedMessage
    )

    try {
        & $Action
    }
    catch {
        if ($_.Exception.Message -notmatch $ExpectedMessage) {
            throw
        }
        return
    }
    throw "Invalid self-test fixture was accepted: $ExpectedMessage"
}

function Invoke-PactSelfTest {
    $validCommon = @'
uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6
uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5
'@
    Assert-PactFixtureFails `
        -Action { Test-PactCommonWorkflow -Text ($validCommon -replace 'checkout@[0-9a-f]{40}', 'checkout@v6') } `
        -ExpectedMessage 'not pinned'

    $invalidHostedGate = @'
uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6
uses: actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1 # v5
    ./tools/Invoke-NativeWebViewGate.ps1 -CandidatePath app.exe
'@
    Assert-PactFixtureFails `
        -Action { Assert-PactHostedNativeGateContract -Text $invalidHostedGate } `
        -ExpectedMessage 'must not launch'

    $invalidNativeTestBlock = @'
    dotnet test first.csproj --no-build
    dotnet test second.csproj --no-build
'@
    Assert-PactFixtureFails `
        -Action { Assert-PactDotnetTestsFailFast -Text $invalidNativeTestBlock } `
        -ExpectedMessage 'must fail fast'
    Assert-PactDotnetTestsFailFast -Text @'
    dotnet test first.csproj --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
'@

    $boundedCommands = @(
        'tests/Pact.Core.Tests/Pact.Core.Tests.csproj',
        'tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj',
        'tests/Pact.Presentation.Tests/Pact.Presentation.Tests.csproj',
        'tests/Pact.App.Avalonia.Tests/Pact.App.Avalonia.Tests.csproj',
        'tests/Pact.Updater.Tests/Pact.Updater.Tests.csproj',
        'tests/Pact.Infrastructure.Tests/Pact.Infrastructure.Tests.csproj') |
        ForEach-Object {
            "dotnet test $_ --no-build --no-restore -m:1 -nr:false -- NUnit.NumberOfTestWorkers=2`nif (`$LASTEXITCODE -ne 0) { exit `$LASTEXITCODE }"
        }
    $boundedFixture = [string]::Join("`n", $boundedCommands)
    Assert-PactSixBoundedTestCommands -Text $boundedFixture -WorkflowName 'Fixture'
    Assert-PactFixtureFails `
        -Action {
            Assert-PactSixBoundedTestCommands `
                -Text ($boundedFixture -replace ' -m:1', '') `
                -WorkflowName 'Fixture'
        } `
        -ExpectedMessage 'missing -m:1'

    $invalidRelease = $validCommon + "`npermissions:`n  contents: write`n  id-token: write`n  attestations: write"
    Assert-PactFixtureFails `
        -Action { Test-PactReleaseWorkflow -Text $invalidRelease } `
        -ExpectedMessage 'artifact-metadata'

    Write-Output 'PASS: workflow contract self-tests rejected mutable actions, unguarded native tests, hosted GUI launch, and incomplete release permissions.'
}

if (-not $SelfTest -and
    [string]::IsNullOrWhiteSpace($CiWorkflow) -and
    [string]::IsNullOrWhiteSpace($ReleaseWorkflow)) {
    $repositoryRoot = [System.IO.Path]::GetFullPath(
        (Join-Path (Split-Path -Parent $PSCommandPath) '..'))
    $CiWorkflow = Join-Path $repositoryRoot '.github/workflows/ci.yml'
    $ReleaseWorkflow = Join-Path $repositoryRoot '.github/workflows/release.yml'
}

if ($SelfTest) {
    Invoke-PactSelfTest
}

if (-not [string]::IsNullOrWhiteSpace($CiWorkflow)) {
    $ciPath = [System.IO.Path]::GetFullPath($CiWorkflow)
    Test-PactCiWorkflow -Text (Get-Content -LiteralPath $ciPath -Raw)
    Write-Output "PASS: CI workflow contract is valid: $ciPath"
}

if (-not [string]::IsNullOrWhiteSpace($ReleaseWorkflow)) {
    $releasePath = [System.IO.Path]::GetFullPath($ReleaseWorkflow)
    Test-PactReleaseWorkflow -Text (Get-Content -LiteralPath $releasePath -Raw)
    Write-Output "PASS: release workflow permissions and action pins are valid: $releasePath"
}
