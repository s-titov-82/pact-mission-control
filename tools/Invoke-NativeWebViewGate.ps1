[CmdletBinding()]
param(
    [string]$ApplicationPath,
    [string]$Version,
    [string]$EvidencePath,
    [string]$ExpectedSourceCommit,
    [switch]$VerifyEvidence,
    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pactExpectedProbes = @(
    'navigation-completed'
    'javascript-ready'
    'webmessage-thread-sequence'
    'runtime-started'
    'first-clean-terminal-output'
    'browser-first-render'
    'terminal-browser-terminal-switch'
    'adapter-lifecycle'
    'shutdown-ui-thread'
    'dom-text'
    'dom-attribute'
    'dom-regex'
    'dom-missing'
    'background-timer'
    'web-process-attribution'
)

function Get-PactJsonProperty {
    param(
        [Parameter(Mandatory)][object]$InputObject,
        [Parameter(Mandatory)][string]$Name
    )

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-PactRepositoryCommit {
    $repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $commit = & git -C $repositoryRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        throw 'Unable to resolve the current Git commit.'
    }

    return $commit.Trim()
}

function Get-PactFreeLoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        0)
    try {
        $listener.Start()
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Get-PactRawProbeFailures {
    param(
        [object]$RawResult,
        [int]$ProcessExitCode,
        [bool]$TimedOut
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    if ($TimedOut) {
        $failures.Add('process-timeout')
    }

    if ($ProcessExitCode -ne 0) {
        $failures.Add("process-exit:$ProcessExitCode")
    }

    if ($null -eq $RawResult) {
        $failures.Add('raw-result-missing')
        return $failures.ToArray()
    }

    if (-not [string]::Equals(
            [string](Get-PactJsonProperty $RawResult 'decision'),
            'PASS',
            [System.StringComparison]::Ordinal)) {
        $failures.Add('decision')
    }

    $reportedError = Get-PactJsonProperty $RawResult 'error'
    if ($null -ne $reportedError -and -not [string]::IsNullOrWhiteSpace([string]$reportedError)) {
        $firstErrorLine = ([string]$reportedError -split '\r?\n', 2)[0]
        $failures.Add("reported-error:$firstErrorLine")
    }

    foreach ($arrayName in @('required', 'passed')) {
        $values = @((Get-PactJsonProperty $RawResult $arrayName))
        $uniqueValues = @($values | Sort-Object -Unique)
        if ($values.Count -ne $pactExpectedProbes.Count -or
            $uniqueValues.Count -ne $values.Count) {
            $failures.Add("$arrayName-cardinality")
        }

        foreach ($expectedProbe in $pactExpectedProbes) {
            if ($values -cnotcontains $expectedProbe) {
                $failures.Add("$arrayName-missing:$expectedProbe")
            }
        }
    }

    $domEvidence = Get-PactJsonProperty $RawResult 'domEvidence'
    if ($null -eq $domEvidence) {
        $failures.Add('dom-evidence-missing')
        return $failures.ToArray()
    }

    $expectedDomEvidence = [ordered]@{
        'dom-text' = 'Running'
        'dom-attribute' = '42'
        'dom-regex' = '123'
        'dom-missing' = $null
        'background-timer' = 'active'
    }
    foreach ($entry in $expectedDomEvidence.GetEnumerator()) {
        $property = $domEvidence.PSObject.Properties[$entry.Key]
        if ($null -eq $property) {
            $failures.Add("dom-missing-key:$($entry.Key)")
            continue
        }

        if ($null -eq $entry.Value) {
            if ($null -ne $property.Value) {
                $failures.Add("dom-value:$($entry.Key)")
            }
        }
        elseif (-not [string]::Equals(
                [string]$property.Value,
                [string]$entry.Value,
                [System.StringComparison]::Ordinal)) {
            $failures.Add("dom-value:$($entry.Key)")
        }
    }

    return $failures.ToArray()
}

function Get-PactEvidenceFailures {
    param(
        [Parameter(Mandatory)][object]$Evidence,
        [Parameter(Mandatory)][string]$ExpectedVersion,
        [Parameter(Mandatory)][string]$ExpectedHash,
        [Parameter(Mandatory)][string]$ExpectedCommit
    )

    $failures = [System.Collections.Generic.List[string]]::new()
    if ((Get-PactJsonProperty $Evidence 'schemaVersion') -ne 1) {
        $failures.Add('schema-version')
    }

    if (-not [string]::Equals(
            [string](Get-PactJsonProperty $Evidence 'decision'),
            'PASS',
            [System.StringComparison]::Ordinal)) {
        $failures.Add('decision')
    }

    if (-not [string]::Equals(
            [string](Get-PactJsonProperty $Evidence 'artifactVersion'),
            $ExpectedVersion,
            [System.StringComparison]::Ordinal)) {
        $failures.Add('artifact-version')
    }

    if (-not [string]::Equals(
            [string](Get-PactJsonProperty $Evidence 'testedExecutableSha256'),
            $ExpectedHash,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add('executable-hash')
    }

    if (-not [string]::Equals(
            [string](Get-PactJsonProperty $Evidence 'sourceCommit'),
            $ExpectedCommit,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        $failures.Add('source-commit')
    }

    $passed = @((Get-PactJsonProperty $Evidence 'passed'))
    if ($passed.Count -ne $pactExpectedProbes.Count -or
        @($passed | Sort-Object -Unique).Count -ne $passed.Count) {
        $failures.Add('passed-cardinality')
    }

    foreach ($expectedProbe in $pactExpectedProbes) {
        if ($passed -cnotcontains $expectedProbe) {
            $failures.Add("passed-missing:$expectedProbe")
        }
    }

    return $failures.ToArray()
}

function Assert-PactNoFailures {
    param(
        [Parameter(Mandatory)][string]$Context,
        [string[]]$Failures
    )

    if ($null -ne $Failures -and $Failures.Count -gt 0) {
        throw "$Context failed: $($Failures -join ', ')."
    }
}

function Remove-PactOwnedGateRoot {
    param([Parameter(Mandatory)][string]$Path)

    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            if (Test-Path -LiteralPath $Path) {
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            }

            return
        }
        catch [System.IO.IOException] {
            if ($attempt -eq 20) {
                Write-Warning "Native gate passed but temporary WebView data could not be removed: $Path"
                return
            }
        }
        catch [System.UnauthorizedAccessException] {
            if ($attempt -eq 20) {
                Write-Warning "Native gate passed but temporary WebView data could not be removed: $Path"
                return
            }
        }

        Start-Sleep -Milliseconds 250
    }
}

function New-PactPassingRawResult {
    $domEvidence = [ordered]@{
        'dom-text' = 'Running'
        'dom-attribute' = '42'
        'dom-regex' = '123'
        'dom-missing' = $null
        'background-timer' = 'active'
    }
    return [pscustomobject]@{
        required = @($pactExpectedProbes)
        passed = @($pactExpectedProbes)
        decision = 'PASS'
        domEvidence = [pscustomobject]$domEvidence
        error = $null
    }
}

function Copy-PactJsonObject {
    param([Parameter(Mandatory)][object]$InputObject)

    return $InputObject |
        ConvertTo-Json -Depth 20 |
        ConvertFrom-Json
}

function Invoke-PactGateSelfTest {
    $passing = New-PactPassingRawResult
    Assert-PactNoFailures 'passing raw result' (
        Get-PactRawProbeFailures $passing 0 $false)

    $nonzero = Get-PactRawProbeFailures $passing 3 $false
    if ($nonzero -cnotcontains 'process-exit:3') {
        throw 'Self-test did not classify a nonzero process exit.'
    }

    $missingDom = Copy-PactJsonObject $passing
    $missingDom.domEvidence.PSObject.Properties.Remove('dom-regex')
    if ((Get-PactRawProbeFailures $missingDom 0 $false) -cnotcontains 'dom-missing-key:dom-regex') {
        throw 'Self-test did not classify a missing DOM key.'
    }

    $inactiveTimer = Copy-PactJsonObject $passing
    $inactiveTimer.domEvidence.'background-timer' = 'inactive'
    if ((Get-PactRawProbeFailures $inactiveTimer 0 $false) -cnotcontains 'dom-value:background-timer') {
        throw 'Self-test did not classify insufficient background-timer evidence.'
    }

    $reportedError = Copy-PactJsonObject $passing
    $reportedError.error = 'probe failure'
    if (-not ((Get-PactRawProbeFailures $reportedError 0 $false) |
            Where-Object { $_ -like 'reported-error:*' })) {
        throw 'Self-test did not classify a reported probe error.'
    }

    $timeout = Get-PactRawProbeFailures $passing 0 $true
    if ($timeout -cnotcontains 'process-timeout') {
        throw 'Self-test did not classify a process timeout.'
    }

    $evidence = [pscustomobject]@{
        schemaVersion = 1
        decision = 'PASS'
        artifactVersion = 'test'
        testedExecutableSha256 = 'ABC'
        sourceCommit = '123'
        passed = @($pactExpectedProbes)
    }
    $staleHash = Get-PactEvidenceFailures $evidence 'test' 'DEF' '123'
    if ($staleHash -cnotcontains 'executable-hash') {
        throw 'Self-test did not classify a stale executable hash.'
    }

    Write-Output 'PASS: native WebView gate evidence contract self-test.'
}

if ($SelfTest) {
    if ($VerifyEvidence -or
        -not [string]::IsNullOrWhiteSpace($ApplicationPath) -or
        -not [string]::IsNullOrWhiteSpace($Version) -or
        -not [string]::IsNullOrWhiteSpace($EvidencePath)) {
        throw '-SelfTest cannot be combined with run or verification arguments.'
    }

    Invoke-PactGateSelfTest
    return
}

if ([string]::IsNullOrWhiteSpace($ApplicationPath) -or
    [string]::IsNullOrWhiteSpace($Version) -or
    [string]::IsNullOrWhiteSpace($EvidencePath)) {
    throw '-ApplicationPath, -Version, and -EvidencePath are required.'
}

$resolvedApplication = (Resolve-Path -LiteralPath $ApplicationPath -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $resolvedApplication -PathType Leaf) -or
    -not [string]::Equals(
        [System.IO.Path]::GetFileName($resolvedApplication),
        'Pact.App.Avalonia.exe',
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw '-ApplicationPath must resolve to Pact.App.Avalonia.exe.'
}

$applicationHash = (Get-FileHash -LiteralPath $resolvedApplication -Algorithm SHA256).Hash
$sourceCommit = if ([string]::IsNullOrWhiteSpace($ExpectedSourceCommit)) {
    Get-PactRepositoryCommit
}
else {
    $ExpectedSourceCommit.Trim()
}

if ($VerifyEvidence) {
    $resolvedEvidence = (Resolve-Path -LiteralPath $EvidencePath -ErrorAction Stop).Path
    $evidence = Get-Content -LiteralPath $resolvedEvidence -Raw | ConvertFrom-Json
    Assert-PactNoFailures 'native WebView evidence verification' (
        Get-PactEvidenceFailures $evidence $Version $applicationHash $sourceCommit)
    Write-Output "PASS: native WebView evidence verifies for version $Version."
    return
}

if (-not [Environment]::UserInteractive) {
    throw 'The native WebView gate requires an interactive Windows desktop.'
}

if ([System.Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
    throw 'The native WebView gate cannot run in Windows Session 0.'
}

$ownedRootParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$ownedRootName = [guid]::NewGuid().ToString('N')
$ownedRoot = [System.IO.Path]::GetFullPath((Join-Path $ownedRootParent $ownedRootName))
$rawResultPath = Join-Path $ownedRoot 'Temp\engine-probe.json'
$process = $null
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $rawResultPath) | Out-Null
    $settingsDirectory = Join-Path $ownedRoot 'Settings'
    New-Item -ItemType Directory -Path $settingsDirectory | Out-Null
    @{
        port = Get-PactFreeLoopbackPort
        enabled = $false
    } |
        ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $settingsDirectory 'agent-control.json') -Encoding utf8NoBOM

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedApplication
    # Match the supported desktop launch: do not lend the WinExe the gate's console
    # standard handles, which its pseudoconsole child could otherwise inherit.
    $startInfo.UseShellExecute = $true
    $startInfo.ArgumentList.Add('--data-root')
    $startInfo.ArgumentList.Add($ownedRoot)
    $startInfo.ArgumentList.Add('--engine-probe-output')
    $startInfo.ArgumentList.Add($rawResultPath)

    $process = [System.Diagnostics.Process]::Start($startInfo)
    if ($null -eq $process) {
        throw 'Failed to start the candidate executable.'
    }

    $timedOut = -not $process.WaitForExit(60000)
    if ($timedOut) {
        $process.Kill($true)
        [void]$process.WaitForExit(5000)
    }

    $exitCode = if ($timedOut) { 0 } else { $process.ExitCode }
    $rawResult = if (Test-Path -LiteralPath $rawResultPath -PathType Leaf) {
        Get-Content -LiteralPath $rawResultPath -Raw | ConvertFrom-Json
    }
    else {
        $null
    }
    Assert-PactNoFailures 'native WebView process probe' (
        Get-PactRawProbeFailures $rawResult $exitCode $timedOut)

    $sanitizedEvidence = [ordered]@{
        schemaVersion = 1
        executedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        artifactVersion = $Version
        testedExecutableSha256 = $applicationHash
        sourceCommit = $sourceCommit
        decision = 'PASS'
        passed = @($pactExpectedProbes)
    }
    $fullEvidencePath = [System.IO.Path]::GetFullPath($EvidencePath)
    $evidenceDirectory = Split-Path -Parent $fullEvidencePath
    if (-not [string]::IsNullOrWhiteSpace($evidenceDirectory)) {
        New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
    }

    $temporaryEvidencePath = "$fullEvidencePath.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        $sanitizedEvidence |
            ConvertTo-Json -Depth 10 |
            Set-Content -LiteralPath $temporaryEvidencePath -Encoding utf8NoBOM
        Move-Item -LiteralPath $temporaryEvidencePath -Destination $fullEvidencePath -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryEvidencePath) {
            Remove-Item -LiteralPath $temporaryEvidencePath -Force
        }
    }

    Write-Output "PASS: native WebView DOM monitoring verified for version $Version."
}
finally {
    if ($null -ne $process) {
        $process.Dispose()
    }

    $parsedRootName = [guid]::Empty
    $resolvedParent = [System.IO.Path]::GetFullPath((Split-Path -Parent $ownedRoot))
    if (-not [guid]::TryParseExact(
            (Split-Path -Leaf $ownedRoot),
            'N',
            [ref]$parsedRootName) -or
        -not [string]::Equals(
            $resolvedParent.TrimEnd('\'),
            $ownedRootParent.TrimEnd('\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean an unexpected native gate root: $ownedRoot"
    }

    if (Test-Path -LiteralPath $ownedRoot) {
        Remove-PactOwnedGateRoot $ownedRoot
    }
}
