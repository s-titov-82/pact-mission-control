[CmdletBinding()]
param(
    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pactScriptDirectory = Split-Path -Parent $PSCommandPath
$pactRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $pactScriptDirectory '..'))
$pactPackageRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $pactRepositoryRoot 'third_party/xterm'))
$pactAssetRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $pactRepositoryRoot 'src/Pact.App.Avalonia/WebAssets/vendor/xterm'))
$pactTemporaryRoot = $null

function Get-PactNormalizedUtf8Bytes {
    param([Parameter(Mandatory)][string]$Path)

    $text = [System.IO.File]::ReadAllText($Path)
    $normalized = $text.Replace("`r`n", "`n").Replace("`r", "`n")
    return [System.Text.UTF8Encoding]::new($false).GetBytes($normalized)
}

function Get-PactSha256 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData($Bytes)
    ).ToLowerInvariant()
}

function Write-PactNormalizedFile {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    $parent = Split-Path -Parent $Destination
    [System.IO.Directory]::CreateDirectory($parent) | Out-Null
    [System.IO.File]::WriteAllBytes(
        $Destination,
        (Get-PactNormalizedUtf8Bytes -Path $Source))
}

Push-Location $pactPackageRoot
try {
    & npm ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) {
        throw "npm ci failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

$pactMappings = @(
    @{
        Source = Join-Path $pactPackageRoot 'node_modules/@xterm/xterm/lib/xterm.js'
        Destination = Join-Path $pactAssetRoot 'xterm.js'
    }
    @{
        Source = Join-Path $pactPackageRoot 'node_modules/@xterm/xterm/css/xterm.css'
        Destination = Join-Path $pactAssetRoot 'xterm.css'
    }
    @{
        Source = Join-Path $pactPackageRoot 'node_modules/@xterm/addon-fit/lib/addon-fit.js'
        Destination = Join-Path $pactAssetRoot 'addon-fit.js'
    }
    @{
        Source = Join-Path $pactPackageRoot 'node_modules/@xterm/xterm/LICENSE'
        Destination = Join-Path $pactPackageRoot 'LICENSE.xterm.txt'
    }
    @{
        Source = Join-Path $pactPackageRoot 'node_modules/@xterm/addon-fit/LICENSE'
        Destination = Join-Path $pactPackageRoot 'LICENSE.addon-fit.txt'
    }
)

try {
    if ($Verify) {
        $pactTemporaryRoot = [System.IO.Path]::GetFullPath(
            (Join-Path ([System.IO.Path]::GetTempPath()) "Pact-xterm-$([Guid]::NewGuid().ToString('N'))"))
        $pactSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $pactTemporaryRoot.StartsWith(
                $pactSystemTemp,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to use an unexpected xterm verification directory: $pactTemporaryRoot"
        }

        [System.IO.Directory]::CreateDirectory($pactTemporaryRoot) | Out-Null
    }

    foreach ($mapping in $pactMappings) {
        $destination = [string]$mapping.Destination
        $outputPath = if ($Verify) {
            Join-Path $pactTemporaryRoot ([System.IO.Path]::GetFileName($destination))
        }
        else {
            $destination
        }

        Write-PactNormalizedFile -Source ([string]$mapping.Source) -Destination $outputPath
        [byte[]]$outputBytes = [System.IO.File]::ReadAllBytes($outputPath)

        if ($Verify) {
            if (-not [System.IO.File]::Exists($destination)) {
                throw "Vendored xterm output is missing: $destination"
            }

            [byte[]]$trackedBytes = Get-PactNormalizedUtf8Bytes -Path $destination
            if (-not [System.Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
                    $outputBytes,
                    $trackedBytes)) {
                throw "Vendored xterm output differs from the locked package: $destination"
            }
        }

        Write-Output "$(Get-PactSha256 -Bytes $outputBytes) *$destination"
    }

    $mode = if ($Verify) { 'verified' } else { 'synchronized' }
    Write-Output "PASS: xterm assets $mode from package-lock.json."
}
finally {
    if ($null -ne $pactTemporaryRoot -and
        [System.IO.Directory]::Exists($pactTemporaryRoot)) {
        [System.IO.Directory]::Delete($pactTemporaryRoot, $true)
    }
}
