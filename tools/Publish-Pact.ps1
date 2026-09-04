[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$RepositoryUrl,
    [Parameter(Mandatory)]
    [ValidateSet('Signed', 'Unsigned')]
    [string]$AuthenticodeStatus,
    [ValidateSet('Prepare', 'Finalize', 'All')]
    [string]$Phase = 'All'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pactProductName = 'PACT:> Mission Control'
$pactSupplier = 'Sergei Titov'
$pactScriptDirectory = Split-Path -Parent $PSCommandPath
$pactRepositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $pactScriptDirectory '..'))
$pactPublishRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $pactRepositoryRoot 'artifacts/publish/win-x64'))
$pactReleaseParent = [System.IO.Path]::GetFullPath(
    (Join-Path $pactRepositoryRoot 'artifacts/release'))
$pactReleaseDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $pactReleaseParent $Version))
$pactArchiveName = "pact-mission-control-$Version-win-x64.zip"
$pactArchivePath = Join-Path $pactReleaseDirectory $pactArchiveName
$pactStandaloneSbomPath = Join-Path $pactReleaseDirectory 'manifest.spdx.json'
$pactReleaseChecksumsPath = Join-Path $pactReleaseDirectory 'SHA256SUMS.txt'
$pactGeneratedSbomPath = Join-Path $pactPublishRoot '_manifest/spdx_2.2/manifest.spdx.json'
$pactRuntimeComponentManifestPath = Join-Path $pactPublishRoot 'licenses/runtime-components.json'
$pactSourceRuntimeComponentManifestPath = Join-Path $pactRepositoryRoot 'third_party/runtime-components.json'

Import-Module -Name (Join-Path $pactScriptDirectory 'PactSbom.psm1') -Force

function Get-PactFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    return [Convert]::ToHexString(
        [System.Security.Cryptography.SHA256]::HashData(
            [System.IO.File]::ReadAllBytes($Path))
    ).ToLowerInvariant()
}

function Assert-PactPathBelow {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Description
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $fullRoot = [System.IO.Path]::GetFullPath($Root)
    $rootPrefix = $fullRoot.TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith(
            $rootPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description escapes its owned root: $fullPath"
    }
}

function Remove-PactOwnedDirectory {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Description
    )

    Assert-PactPathBelow -Path $Path -Root $Root -Description $Description
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Get-PactDeclaredVersion {
    [xml]$buildProperties = Get-Content -LiteralPath (
        Join-Path $pactRepositoryRoot 'Directory.Build.props') -Raw
    $values = @(
        $buildProperties.SelectNodes('/Project/PropertyGroup/VersionPrefix') |
            ForEach-Object { [string]$_.InnerText } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($values.Count -ne 1) {
        throw 'Directory.Build.props must declare exactly one VersionPrefix.'
    }

    return [string]$values[0]
}

function Assert-PactReleaseInputs {
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Version must be SemVer without a leading v: $Version"
    }

    $declaredVersion = Get-PactDeclaredVersion
    if ($Version -ne $declaredVersion) {
        throw "Version $Version does not match VersionPrefix $declaredVersion."
    }

    $repositoryUri = $null
    if (-not [System.Uri]::TryCreate(
            $RepositoryUrl,
            [System.UriKind]::Absolute,
            [ref]$repositoryUri) -or
        $repositoryUri.Scheme -ne 'https' -or
        $repositoryUri.Host -ne 'github.com' -or
        -not [string]::IsNullOrEmpty($repositoryUri.Query) -or
        -not [string]::IsNullOrEmpty($repositoryUri.Fragment) -or
        $repositoryUri.AbsolutePath.Trim('/').Split('/').Count -ne 2) {
        throw "RepositoryUrl must be an absolute HTTPS GitHub repository URL: $RepositoryUrl"
    }

    Assert-PactPathBelow `
        -Path $pactReleaseDirectory `
        -Root $pactReleaseParent `
        -Description 'Release directory'
    foreach ($releasePath in @(
            $pactArchivePath,
            $pactStandaloneSbomPath,
            $pactReleaseChecksumsPath)) {
        Assert-PactPathBelow `
            -Path $releasePath `
            -Root $pactReleaseDirectory `
            -Description 'Release output'
    }
}

function Test-PactPreparedTree {
    if (-not (Test-Path -LiteralPath $pactPublishRoot -PathType Container)) {
        throw "Prepared publish tree does not exist: $pactPublishRoot"
    }

    $requiredNoticeFiles = @(
        'LICENSE'
        'THIRD-PARTY-NOTICES.md'
        'licenses/conpty-LICENSE.txt'
        'licenses/conpty-SHA256SUMS.txt'
        'licenses/xterm-LICENSE.txt'
        'licenses/xterm-addon-fit-LICENSE.txt'
        'licenses/runtime-components.json'
        'licenses/nuget/Avalonia.Angle.Windows.Natives-LICENSE.txt'
        'licenses/nuget/HtmlAgilityPack-LICENSE.txt'
        'licenses/nuget/Microsoft.Web.WebView2-LICENSE.txt'
        'licenses/nuget/Microsoft.Web.WebView2-NOTICE.txt'
        'licenses/nuget/MS-PL.txt'
    )
    foreach ($requiredNoticeFile in $requiredNoticeFiles) {
        $requiredNoticePath = Join-Path $pactPublishRoot $requiredNoticeFile
        if (-not (Test-Path -LiteralPath $requiredNoticePath -PathType Leaf) -or
            (Get-Item -LiteralPath $requiredNoticePath).Length -eq 0) {
            throw "Publish notice is missing or empty: $requiredNoticeFile"
        }
    }
    if ((Get-PactFileSha256 -Path $pactRuntimeComponentManifestPath) -ne
        (Get-PactFileSha256 -Path $pactSourceRuntimeComponentManifestPath)) {
        throw 'Published runtime component manifest differs from the repository source.'
    }

    $conptyManifestPath = Join-Path $pactRepositoryRoot 'third_party/conpty/SHA256SUMS.txt'
    $conptyManifestRoot = [System.IO.Path]::GetFullPath(
        (Split-Path -Parent $conptyManifestPath))
    $conptyManifestRootPrefix = $conptyManifestRoot +
        [System.IO.Path]::DirectorySeparatorChar
    $conptyEntries = @(
        Get-Content -LiteralPath $conptyManifestPath |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($conptyEntries.Count -ne 2) {
        throw 'ConPTY checksum manifest must contain exactly two non-empty entries.'
    }

    $expectedConptyNames = @('conpty.dll', 'OpenConsole.exe')
    $observedConptyNames = @()
    foreach ($conptyEntry in $conptyEntries) {
        if ($conptyEntry -notmatch '^(?<Hash>[0-9a-f]{64}) \*(?<Path>.+)$') {
            throw "Invalid ConPTY checksum entry: $conptyEntry"
        }

        $expectedHash = $Matches.Hash
        $relativeSource = $Matches.Path
        $sourcePath = [System.IO.Path]::GetFullPath(
            (Join-Path $conptyManifestRoot $relativeSource))
        if (-not $sourcePath.StartsWith(
                $conptyManifestRootPrefix,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "ConPTY checksum source escapes its manifest directory: $relativeSource"
        }

        $leafName = [System.IO.Path]::GetFileName($relativeSource)
        $observedConptyNames += $leafName
        $publishedPath = Join-Path $pactPublishRoot "conpty/$leafName"
        if (-not [System.IO.File]::Exists($sourcePath) -or
            -not [System.IO.File]::Exists($publishedPath)) {
            throw "ConPTY checksum target is missing: $leafName"
        }

        $sourceHash = Get-PactFileSha256 -Path $sourcePath
        $publishedHash = Get-PactFileSha256 -Path $publishedPath
        if ($sourceHash -ne $expectedHash -or
            $publishedHash -ne $expectedHash) {
            throw "ConPTY checksum mismatch: $leafName"
        }
    }

    if (@(Compare-Object $expectedConptyNames $observedConptyNames).Count -ne 0) {
        throw 'ConPTY checksum manifest does not name the expected binary pair.'
    }

    $symbols = @(Get-ChildItem -LiteralPath $pactPublishRoot -Recurse -File -Filter '*.pdb')
    if ($symbols.Count -gt 0) {
        throw "Publish contains PDB files: $($symbols.FullName -join ', ')"
    }

    $forbiddenRuntimeDirectories = @(
        Get-ChildItem -LiteralPath $pactPublishRoot -Recurse -Directory |
            Where-Object {
                ($_.Name -match '^(linux|osx)(-|$)') -or
                ($_.Name -in @('win-x86', 'win-arm64'))
            }
    )
    if ($forbiddenRuntimeDirectories.Count -gt 0) {
        throw "Publish contains unsupported runtime folders: $($forbiddenRuntimeDirectories.FullName -join ', ')"
    }

    $publishedFiles = @(Get-ChildItem -LiteralPath $pactPublishRoot -Recurse -File)
    [long]$publishedBytes = ($publishedFiles | Measure-Object -Property Length -Sum).Sum
    [long]$maximumBytes = 50MB
    if ($publishedBytes -gt $maximumBytes) {
        throw "Publish is $publishedBytes bytes, above the 50 MiB limit ($maximumBytes bytes)."
    }

    return [pscustomobject]@{
        FileCount = $publishedFiles.Count
        Bytes = $publishedBytes
    }
}

function Assert-PactAuthenticodeStatus {
    $ownedFiles = @(
        Get-ChildItem -LiteralPath $pactPublishRoot -Recurse -File |
            Where-Object {
                $_.Name.StartsWith(
                    'Pact.',
                    [System.StringComparison]::Ordinal) -and
                $_.Extension -in @('.dll', '.exe')
            })
    if ($ownedFiles.Count -lt 2) {
        throw 'Prepared publish tree does not contain the expected Pact-owned binaries.'
    }
    if (-not (Test-Path -LiteralPath (
                Join-Path $pactPublishRoot 'Pact.App.Avalonia.exe') -PathType Leaf)) {
        throw 'Prepared publish tree does not contain Pact.App.Avalonia.exe.'
    }

    foreach ($ownedFile in $ownedFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $ownedFile.FullName
        $expectedStatus = if ($AuthenticodeStatus -eq 'Signed') { 'Valid' } else { 'NotSigned' }
        if ([string]$signature.Status -ne $expectedStatus) {
            throw "Authenticode status for $($ownedFile.Name) is $($signature.Status), expected $expectedStatus."
        }
    }
}

function Get-PactSourceTimestamp {
    $epochText = [Environment]::GetEnvironmentVariable('SOURCE_DATE_EPOCH')
    if ([string]::IsNullOrWhiteSpace($epochText)) {
        $epochText = (& git -C $pactRepositoryRoot show -s --format=%ct HEAD | Select-Object -Last 1)
        if ($LASTEXITCODE -ne 0) {
            throw 'Unable to read the source commit timestamp.'
        }
    }

    [long]$epoch = 0
    if (-not [long]::TryParse($epochText, [ref]$epoch) -or $epoch -le 0) {
        throw "SOURCE_DATE_EPOCH must be a positive Unix timestamp: $epochText"
    }

    $timestamp = [DateTimeOffset]::FromUnixTimeSeconds($epoch)
    if ($timestamp.Year -lt 1980) {
        throw 'The source timestamp is too old for a ZIP entry.'
    }

    return $timestamp
}

function New-PactCanonicalZip {
    param([Parameter(Mandatory)][DateTimeOffset]$Timestamp)

    Add-Type -AssemblyName System.IO.Compression
    [System.IO.Directory]::CreateDirectory($pactReleaseDirectory) | Out-Null
    if (Test-Path -LiteralPath $pactArchivePath) {
        Remove-Item -LiteralPath $pactArchivePath -Force
    }

    $fileStream = [System.IO.File]::Open(
        $pactArchivePath,
        [System.IO.FileMode]::CreateNew,
        [System.IO.FileAccess]::ReadWrite,
        [System.IO.FileShare]::None)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new(
            $fileStream,
            [System.IO.Compression.ZipArchiveMode]::Create,
            $false)
        try {
            $relativePaths = [System.Collections.Generic.List[string]]::new()
            Get-ChildItem -LiteralPath $pactPublishRoot -Recurse -File |
                ForEach-Object {
                    $relativePaths.Add(
                        [System.IO.Path]::GetRelativePath(
                            $pactPublishRoot,
                            $_.FullName).Replace('\', '/'))
                }
            $relativePaths.Sort([System.StringComparer]::Ordinal)
            foreach ($relativePath in $relativePaths) {
                $entry = $archive.CreateEntry(
                    $relativePath,
                    [System.IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $Timestamp
                $input = [System.IO.File]::OpenRead(
                    (Join-Path $pactPublishRoot $relativePath))
                $output = $entry.Open()
                try {
                    $input.CopyTo($output)
                }
                finally {
                    $output.Dispose()
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

function Invoke-PactPrepare {
    Remove-PactOwnedDirectory `
        -Path $pactPublishRoot `
        -Root (Join-Path $pactRepositoryRoot 'artifacts/publish') `
        -Description 'Publish directory'
    Remove-PactOwnedDirectory `
        -Path $pactReleaseDirectory `
        -Root $pactReleaseParent `
        -Description 'Release directory'

    $restoreArguments = @(
        'restore'
        'Pact.slnx'
        '--disable-parallel'
        '--locked-mode'
    )
    $publishArguments = @(
        'publish'
        'src/Pact.App.Avalonia/Pact.App.Avalonia.csproj'
        '-c', 'Release'
        '--runtime', 'win-x64'
        '--self-contained', 'false'
        '--no-restore'
        '-m:2'
        '-nr:false'
        '-v', 'q'
        '-p:BuildInParallel=false'
        '-p:UsedAvaloniaProducts='
        "-p:Version=$Version"
        "-p:RepositoryUrl=$RepositoryUrl"
        '-p:ContinuousIntegrationBuild=true'
        '-o', $pactPublishRoot
    )

    Push-Location $pactRepositoryRoot
    try {
        & dotnet @restoreArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet restore failed with exit code $LASTEXITCODE."
        }

        & dotnet @publishArguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $summary = Test-PactPreparedTree
    $sizeMiB = [Math]::Round($summary.Bytes / 1MB, 2)
    Write-Output "PASS: prepared win-x64 publish contains $($summary.FileCount) files and is $sizeMiB MiB."
}

function Invoke-PactFinalize {
    $summary = Test-PactPreparedTree
    Assert-PactAuthenticodeStatus
    if (Test-Path -LiteralPath $pactGeneratedSbomPath) {
        throw 'Prepared tree already contains an SBOM; run Prepare before Finalize.'
    }

    Push-Location $pactRepositoryRoot
    try {
        & dotnet tool restore --disable-parallel
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool restore failed with exit code $LASTEXITCODE."
        }

        $sbomArguments = @(
            'tool', 'run', 'sbom-tool', 'generate'
            '-b', $pactPublishRoot
            '-bc', (Join-Path $pactRepositoryRoot 'src')
            '-pn', $pactProductName
            '-pv', $Version
            '-ps', $pactSupplier
            '-nsb', "$RepositoryUrl/sbom/$Version"
            '-pm', 'true'
            '-P', '2'
        )
        & dotnet @sbomArguments
        if ($LASTEXITCODE -ne 0) {
            throw "SBOM generation failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    Complete-PactSbom `
        -Path $pactGeneratedSbomPath `
        -PublishRoot $pactPublishRoot `
        -ManifestPath $pactSourceRuntimeComponentManifestPath `
        -ProductName $pactProductName `
        -ProductVersion $Version `
        -ProductLicense 'MIT' `
        -ProductDownloadLocation $RepositoryUrl | Out-Null
    [System.IO.Directory]::CreateDirectory($pactReleaseDirectory) | Out-Null
    [System.IO.File]::Copy(
        $pactGeneratedSbomPath,
        $pactStandaloneSbomPath,
        $true)
    New-PactCanonicalZip -Timestamp (Get-PactSourceTimestamp)

    $checksumLines = @(
        "$(Get-PactFileSha256 -Path $pactArchivePath) *$pactArchiveName"
        "$(Get-PactFileSha256 -Path $pactStandaloneSbomPath) *manifest.spdx.json"
    )
    [System.IO.File]::WriteAllText(
        $pactReleaseChecksumsPath,
        ($checksumLines -join "`n") + "`n",
        [System.Text.UTF8Encoding]::new($false))

    $sizeMiB = [Math]::Round($summary.Bytes / 1MB, 2)
    Write-Output "PASS: finalized $pactArchiveName with SPDX 2.2 and SHA-256 checksums ($sizeMiB MiB unpacked)."
}

Assert-PactReleaseInputs
if ($Phase -in @('Prepare', 'All')) {
    Invoke-PactPrepare
}
if ($Phase -in @('Finalize', 'All')) {
    Invoke-PactFinalize
}
