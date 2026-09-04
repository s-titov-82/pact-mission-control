param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ReleaseDirectory,
    [Parameter(Mandatory)]
    [ValidateSet('Signed', 'Unsigned')]
    [string]$ExpectedAuthenticodeStatus
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pactProductName = 'PACT:> Mission Control'
$pactScriptDirectory = Split-Path -Parent $PSCommandPath
$pactRepositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $pactScriptDirectory '..'))
$pactTrustedComponentManifestPath = Join-Path `
    $pactRepositoryRoot `
    'third_party/runtime-components.json'
Import-Module -Name (Join-Path $pactScriptDirectory 'PactSbom.psm1') -Force

function Get-PactSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-PactSafeArchiveEntry {
    param([Parameter(Mandatory)][string]$EntryName)

    $normalized = $EntryName.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or
        $normalized.StartsWith('/', [System.StringComparison]::Ordinal) -or
        $normalized -match '^[A-Za-z]:' -or
        @($normalized.Split('/')).Contains('..')) {
        throw "Archive contains an unsafe entry: $EntryName"
    }
}

function Assert-PactChecksums {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$ExpectedNames
    )

    $lines = @(Get-Content -LiteralPath $Path | Where-Object {
            -not [string]::IsNullOrWhiteSpace($_)
        })
    if ($lines.Count -ne $ExpectedNames.Count) {
        throw "Checksum manifest must contain exactly $($ExpectedNames.Count) entries."
    }

    $seen = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($line in $lines) {
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*([^/\\]+)$') {
            throw "Invalid checksum line: $line"
        }

        $expectedHash = $Matches[1].ToLowerInvariant()
        $name = $Matches[2]
        if (-not $seen.Add($name)) {
            throw "Duplicate checksum entry: $name"
        }

        $filePath = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
            throw "Checksummed file is missing: $name"
        }

        $actualHash = Get-PactSha256 -Path $filePath
        if ($actualHash -ne $expectedHash) {
            throw "Checksum mismatch for $name."
        }
    }

    foreach ($expectedName in $ExpectedNames) {
        if (-not $seen.Contains($expectedName)) {
            throw "Checksum entry is missing: $expectedName"
        }
    }
}

function Assert-PactSpdx {
    param([Parameter(Mandatory)][string]$Path)

    $spdx = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ([string]$spdx.spdxVersion -ne 'SPDX-2.2') {
        throw "Unexpected SPDX version: $($spdx.spdxVersion)"
    }
    if ([string]$spdx.name -ne "PACT:> Mission Control $Version") {
        throw "Unexpected SPDX document name: $($spdx.name)"
    }

    $packages = @($spdx.packages)
    $requiredPackages = @{
        $pactProductName = $Version
        '@xterm/xterm' = '5.5.0'
        '@xterm/addon-fit' = '0.10.0'
        'Microsoft Windows Terminal ConPTY' = '1.25.260303002/win10-x64'
    }
    foreach ($packageName in $requiredPackages.Keys) {
        $matches = @($packages | Where-Object {
                [string]$_.name -eq $packageName -and
                [string]$_.versionInfo -eq $requiredPackages[$packageName]
            })
        if ($matches.Count -ne 1) {
            throw "SPDX must contain exactly one $packageName $($requiredPackages[$packageName]) package."
        }
    }

    $fileNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @($spdx.files)) {
        $fileNames.Add(([string]$file.fileName).TrimStart('.', '/').Replace('\', '/')) |
            Out-Null
    }
    foreach ($requiredFile in @(
            'Pact.App.Avalonia.exe',
            'conpty/conpty.dll',
            'conpty/OpenConsole.exe',
            'Web/vendor/xterm/xterm.js',
            'Web/vendor/xterm/addon-fit.js')) {
        if (-not $fileNames.Contains($requiredFile)) {
            throw "SPDX file inventory is missing $requiredFile."
        }
    }
}

function Assert-PactAuthenticode {
    param([Parameter(Mandatory)][string]$Root)

    $ownedFiles = @(
        Get-ChildItem -LiteralPath $Root -Recurse -File |
            Where-Object {
                $_.Name.StartsWith('Pact.', [System.StringComparison]::Ordinal) -and
                $_.Extension -in @('.dll', '.exe')
            })
    if ($ownedFiles.Count -eq 0) {
        throw 'No Pact-owned binaries were found.'
    }

    $expectedStatus = if ($ExpectedAuthenticodeStatus -eq 'Signed') {
        'Valid'
    }
    else {
        'NotSigned'
    }
    foreach ($ownedFile in $ownedFiles) {
        $status = [string](Get-AuthenticodeSignature -LiteralPath $ownedFile.FullName).Status
        if ($status -ne $expectedStatus) {
            throw "Authenticode status for $($ownedFile.Name) is $status, expected $expectedStatus."
        }
    }

    foreach ($upstreamPath in @(
            (Join-Path $Root 'conpty/conpty.dll'),
            (Join-Path $Root 'conpty/OpenConsole.exe'))) {
        $signature = Get-AuthenticodeSignature -LiteralPath $upstreamPath
        if ([string]$signature.Status -ne 'Valid' -or
            -not ([string]$signature.SignerCertificate.Subject).Contains(
                'Microsoft Corporation',
                [System.StringComparison]::Ordinal)) {
            throw "Bundled upstream binary does not have the expected valid Microsoft signature: $upstreamPath"
        }
    }
}

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw "Version must be SemVer without a leading v: $Version"
}

$releaseRoot = [System.IO.Path]::GetFullPath($ReleaseDirectory)
if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
    throw "Release directory does not exist: $releaseRoot"
}

$archiveName = "pact-mission-control-$Version-win-x64.zip"
$expectedReleaseFiles = @($archiveName, 'manifest.spdx.json', 'SHA256SUMS.txt')
$actualReleaseFiles = @(
    Get-ChildItem -LiteralPath $releaseRoot -File |
        ForEach-Object Name |
        Sort-Object
)
if ([string]::Join("`n", $actualReleaseFiles) -ne
    [string]::Join("`n", ($expectedReleaseFiles | Sort-Object))) {
    throw "Release directory must contain exactly: $($expectedReleaseFiles -join ', ')."
}

$archivePath = Join-Path $releaseRoot $archiveName
$standaloneSpdxPath = Join-Path $releaseRoot 'manifest.spdx.json'
Assert-PactChecksums `
    -Path (Join-Path $releaseRoot 'SHA256SUMS.txt') `
    -Directory $releaseRoot `
    -ExpectedNames @($archiveName, 'manifest.spdx.json')
Assert-PactSpdx -Path $standaloneSpdxPath

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $entryNames = [System.Collections.Generic.List[string]]::new()
    $entryTimestamps = [System.Collections.Generic.HashSet[DateTimeOffset]]::new()
    foreach ($entry in $archive.Entries) {
        Assert-PactSafeArchiveEntry -EntryName $entry.FullName
        $entryNames.Add($entry.FullName)
        $entryTimestamps.Add($entry.LastWriteTime) | Out-Null
    }

    $sortedEntryNames = [System.Collections.Generic.List[string]]::new($entryNames)
    $sortedEntryNames.Sort([System.StringComparer]::Ordinal)
    for ($index = 0; $index -lt $entryNames.Count; $index++) {
        if ($entryNames[$index] -cne $sortedEntryNames[$index]) {
            throw "Archive entries are not in ordinal order at $($entryNames[$index])."
        }
    }
    if ($entryTimestamps.Count -ne 1) {
        throw 'Archive entries do not share one canonical source timestamp.'
    }
}
finally {
    $archive.Dispose()
}

$temporaryParent = [System.IO.Path]::GetTempPath()
$temporaryRoot = Join-Path $temporaryParent (
    'pact-publication-validation-' + [System.Guid]::NewGuid().ToString('N'))
$resolvedTemporaryParent = [System.IO.Path]::GetFullPath($temporaryParent)
$resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
$temporaryPrefix = $resolvedTemporaryParent.TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar) +
    [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedTemporaryRoot.StartsWith(
        $temporaryPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Temporary extraction path escapes the system temp directory: $resolvedTemporaryRoot"
}

try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory(
        $archivePath,
        $resolvedTemporaryRoot)

    foreach ($requiredPath in @(
            'LICENSE',
            'THIRD-PARTY-NOTICES.md',
            'licenses/conpty-LICENSE.txt',
            'licenses/conpty-SHA256SUMS.txt',
            'licenses/xterm-LICENSE.txt',
            'licenses/xterm-addon-fit-LICENSE.txt',
            'licenses/runtime-components.json',
            'licenses/runtime-packages.json',
            'licenses/nuget/Avalonia.Angle.Windows.Natives-LICENSE.txt',
            'licenses/nuget/HtmlAgilityPack-LICENSE.txt',
            'licenses/nuget/Microsoft.Web.WebView2-LICENSE.txt',
            'licenses/nuget/Microsoft.Web.WebView2-NOTICE.txt',
            'licenses/nuget/MS-PL.txt',
            'Pact.App.Avalonia.exe',
            'Microsoft.Web.WebView2.Core.dll',
            'conpty/conpty.dll',
            'conpty/OpenConsole.exe',
            'Web/terminalHost.js',
            'Web/vendor/xterm/xterm.js',
            'Web/vendor/xterm/xterm.css',
            'Web/vendor/xterm/addon-fit.js')) {
        if (-not (Test-Path -LiteralPath (
                    Join-Path $resolvedTemporaryRoot $requiredPath) -PathType Leaf)) {
            throw "Archive is missing required file: $requiredPath"
        }
    }

    $forbidden = @(
        Get-ChildItem -LiteralPath $resolvedTemporaryRoot -Recurse -File |
            Where-Object {
                $_.Extension -eq '.pdb' -or
                $_.FullName -match '[\\/](?:tests?|spikes?|docs[\\/]superpowers)[\\/]' -or
                $_.Extension -in @('.cs', '.csproj', '.sln', '.slnx') -or
                $_.FullName -match '[\\/]runtimes[\\/](?!win-x64[\\/])'
            })
    if ($forbidden.Count -ne 0) {
        throw "Archive contains forbidden development or foreign-runtime file: $($forbidden[0].FullName)"
    }

    $totalBytes = (
        Get-ChildItem -LiteralPath $resolvedTemporaryRoot -Recurse -File |
            Measure-Object -Property Length -Sum).Sum
    if ($totalBytes -gt 50MB) {
        throw "Unpacked archive is larger than 50 MiB: $totalBytes bytes."
    }

    $conptyManifest = Join-Path $resolvedTemporaryRoot 'licenses/conpty-SHA256SUMS.txt'
    foreach ($line in Get-Content -LiteralPath $conptyManifest) {
        if ($line -notmatch '^([0-9a-fA-F]{64}) \*(.+/([^/]+))$') {
            throw "Invalid bundled ConPTY checksum line: $line"
        }
        $expectedHash = $Matches[1].ToLowerInvariant()
        $binaryPath = Join-Path $resolvedTemporaryRoot ('conpty/' + $Matches[3])
        if ((Get-PactSha256 -Path $binaryPath) -ne $expectedHash) {
            throw "Bundled ConPTY checksum mismatch for $($Matches[3])."
        }
    }

    Assert-PactAuthenticode -Root $resolvedTemporaryRoot
    $archivedSpdxPath = Join-Path `
        $resolvedTemporaryRoot `
        '_manifest/spdx_2.2/manifest.spdx.json'
    $archivedComponentManifestPath = Join-Path `
        $resolvedTemporaryRoot `
        'licenses/runtime-components.json'
    if ((Get-PactSha256 -Path $archivedComponentManifestPath) -ne
        (Get-PactSha256 -Path $pactTrustedComponentManifestPath)) {
        throw 'Archived runtime component manifest differs from the repository source.'
    }
    Assert-PactSpdx -Path $archivedSpdxPath
    Assert-PactSbom `
        -Path $archivedSpdxPath `
        -PublishRoot $resolvedTemporaryRoot `
        -ManifestPath $pactTrustedComponentManifestPath `
        -ProductName $pactProductName `
        -ProductVersion $Version `
        -ProductLicense 'MIT' | Out-Null
    if ((Get-PactSha256 -Path $archivedSpdxPath) -ne
        (Get-PactSha256 -Path $standaloneSpdxPath)) {
        throw 'Standalone and archived SPDX manifests differ.'
    }

    $sizeMiB = [Math]::Round($totalBytes / 1MB, 2)
    Write-Output "PASS: publication $archiveName is complete, safe, checksum-valid, SPDX 2.2, and $sizeMiB MiB unpacked."
}
finally {
    if (Test-Path -LiteralPath $resolvedTemporaryRoot) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
