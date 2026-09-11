[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$BuildScriptPath,
	[Parameter(Mandatory)][string]$CompilerPath,
	[Parameter(Mandatory)][string]$DependencyCacheDirectory,
	[Parameter(Mandatory)][string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$resolvedBuildScriptPath = [IO.Path]::GetFullPath($BuildScriptPath)
$resolvedCompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$resolvedCacheDirectory = [IO.Path]::GetFullPath($DependencyCacheDirectory)
$resolvedTemporaryRoot = [IO.Path]::GetFullPath($TemporaryRoot)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$validAppHostPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/Pact.App.Avalonia/bin/Debug/net10.0-windows/win-x64/Pact.App.Avalonia.exe'))
$validUpdaterPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'src/Pact.Updater/bin/Debug/net10.0-windows/win-x64/Pact.Updater.exe'))
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props') -Raw
$declaredVersions = @(
	$buildProperties.SelectNodes('/Project/PropertyGroup/VersionPrefix') |
		ForEach-Object { [string]$_.InnerText } |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($declaredVersions.Count -ne 1)
{
	throw 'Directory.Build.props must declare exactly one VersionPrefix.'
}
$declaredVersion = $declaredVersions[0]
$artifactsPrefix = $artifactsRoot.TrimEnd(
	[IO.Path]::DirectorySeparatorChar,
	[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedTemporaryRoot.StartsWith(
		$artifactsPrefix,
		[StringComparison]::OrdinalIgnoreCase))
{
	throw 'TemporaryRoot must resolve below the repository artifacts directory.'
}

function Write-TextFile
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][AllowEmptyString()][string]$Value
	)

	$parent = [IO.Path]::GetDirectoryName($Path)
	$null = [IO.Directory]::CreateDirectory($parent)
	[IO.File]::WriteAllText($Path, $Value, [Text.UTF8Encoding]::new($false))
}

function Write-JsonFile
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][object]$Value
	)

	Write-TextFile -Path $Path -Value (($Value | ConvertTo-Json -Depth 10) + "`n")
}

function New-PublishFixture
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[string]$FrameworkName = 'Microsoft.NETCore.App',
		[string]$ProductVersion = $script:declaredVersion
	)

	$null = [IO.Directory]::CreateDirectory($Path)
	if (-not [IO.File]::Exists($validAppHostPath))
	{
		throw "The built x64 apphost fixture is missing: $validAppHostPath"
	}
	[IO.File]::Copy($validAppHostPath, (Join-Path $Path 'Pact.App.Avalonia.exe'))
	if (-not [IO.File]::Exists($validUpdaterPath))
	{
		throw "The built x64 updater fixture is missing: $validUpdaterPath"
	}
	[IO.File]::Copy($validUpdaterPath, (Join-Path $Path 'Pact.Updater.exe'))
	Write-TextFile (Join-Path $Path 'LICENSE') 'fixture license'
	Write-JsonFile (Join-Path $Path '_manifest/spdx_2.2/manifest.spdx.json') ([ordered]@{
		packages = @([ordered]@{
			name = 'PACT:> Mission Control'
			versionInfo = $ProductVersion
		})
	})
	Write-JsonFile (Join-Path $Path 'Pact.App.Avalonia.deps.json') ([ordered]@{
		runtimeTarget = [ordered]@{
			name = '.NETCoreApp,Version=v10.0/win-x64'
		}
	})
	Write-JsonFile (Join-Path $Path 'Pact.App.Avalonia.runtimeconfig.json') ([ordered]@{
		runtimeOptions = [ordered]@{
			tfm = 'net10.0'
			framework = [ordered]@{
				name = $FrameworkName
				version = '10.0.0'
			}
		}
	})
}

function Invoke-Build
{
	param(
		[Parameter(Mandatory)][string]$PublishDirectory,
		[Parameter(Mandatory)][string]$OutputDirectory,
		[string]$RequestedCompilerPath = $resolvedCompilerPath
	)

	$output = & pwsh -NoProfile -File $resolvedBuildScriptPath `
		-Version $declaredVersion `
		-PublishDirectory $PublishDirectory `
		-OutputDirectory $OutputDirectory `
		-CompilerPath $RequestedCompilerPath `
		-DependencyCacheDirectory $resolvedCacheDirectory `
		-AuthenticodeStatus Unsigned 2>&1
	return [pscustomobject]@{
		ExitCode = $LASTEXITCODE
		Output = [string]::Join("`n", @($output))
	}
}

function Assert-FailsWith
{
	param(
		[Parameter(Mandatory)][pscustomobject]$Result,
		[Parameter(Mandatory)][string]$ExpectedText,
		[Parameter(Mandatory)][string]$Scenario
	)

	if ($Result.ExitCode -eq 0)
	{
		throw "$Scenario passed unexpectedly."
	}
	if ($Result.Output.IndexOf(
			$ExpectedText,
			[StringComparison]::OrdinalIgnoreCase) -lt 0)
	{
		throw "$Scenario failed for the wrong reason.`n$($Result.Output)"
	}
}

if (-not [IO.File]::Exists($resolvedBuildScriptPath))
{
	throw "Build script is missing: $resolvedBuildScriptPath"
}
if (-not [IO.File]::Exists($resolvedCompilerPath))
{
	throw "Compiler fixture is missing: $resolvedCompilerPath"
}

if ([IO.Directory]::Exists($resolvedTemporaryRoot))
{
	[IO.Directory]::Delete($resolvedTemporaryRoot, $true)
}
$null = [IO.Directory]::CreateDirectory($resolvedTemporaryRoot)

try
{
	$missingPayload = Invoke-Build `
		-PublishDirectory (Join-Path $resolvedTemporaryRoot 'missing') `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'missing-output')
	Assert-FailsWith `
		-Result $missingPayload `
		-ExpectedText 'Pact.App.Avalonia.exe is missing' `
		-Scenario 'Missing application payload'

	$unsupportedRoot = Join-Path $resolvedTemporaryRoot 'unsupported'
	New-PublishFixture $unsupportedRoot 'Microsoft.AspNetCore.App'
	$unsupported = Invoke-Build `
		-PublishDirectory $unsupportedRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'unsupported-output')
	Assert-FailsWith `
		-Result $unsupported `
		-ExpectedText 'Unsupported runtime framework' `
		-Scenario 'Unsupported runtime framework'

	$missingUpdaterRoot = Join-Path $resolvedTemporaryRoot 'missing-updater'
	New-PublishFixture $missingUpdaterRoot
	[IO.File]::Delete((Join-Path $missingUpdaterRoot 'Pact.Updater.exe'))
	$missingUpdater = Invoke-Build `
		-PublishDirectory $missingUpdaterRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'missing-updater-output')
	Assert-FailsWith `
		-Result $missingUpdater `
		-ExpectedText 'Pact.Updater.exe is missing' `
		-Scenario 'Missing updater payload'

	$multipleUpdaterRoot = Join-Path $resolvedTemporaryRoot 'multiple-updaters'
	New-PublishFixture $multipleUpdaterRoot
	$null = [IO.Directory]::CreateDirectory((Join-Path $multipleUpdaterRoot 'nested'))
	[IO.File]::Copy(
		$validUpdaterPath,
		(Join-Path $multipleUpdaterRoot 'nested/Pact.Updater.exe'))
	$multipleUpdaters = Invoke-Build `
		-PublishDirectory $multipleUpdaterRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'multiple-updaters-output')
	Assert-FailsWith `
		-Result $multipleUpdaters `
		-ExpectedText 'exactly one Pact.Updater.exe at its root' `
		-Scenario 'Multiple updater payloads'

	$invalidExecutableRoot = Join-Path $resolvedTemporaryRoot 'invalid-executable'
	New-PublishFixture $invalidExecutableRoot
	Write-TextFile (Join-Path $invalidExecutableRoot 'Pact.App.Avalonia.exe') 'not a PE application'
	$invalidExecutable = Invoke-Build `
		-PublishDirectory $invalidExecutableRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'invalid-executable-output')
	Assert-FailsWith `
		-Result $invalidExecutable `
		-ExpectedText 'not a valid x64 PE application' `
		-Scenario 'Invalid application executable'

	$wrongExecutableVersionRoot = Join-Path $resolvedTemporaryRoot 'wrong-executable-version'
	New-PublishFixture $wrongExecutableVersionRoot
	[IO.File]::Copy(
		$resolvedCompilerPath,
		(Join-Path $wrongExecutableVersionRoot 'Pact.App.Avalonia.exe'),
		$true)
	$wrongExecutableVersion = Invoke-Build `
		-PublishDirectory $wrongExecutableVersionRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'wrong-executable-version-output')
	Assert-FailsWith `
		-Result $wrongExecutableVersion `
		-ExpectedText 'product version' `
		-Scenario 'Mismatched application executable version'

	$wrongUpdaterVersionRoot = Join-Path $resolvedTemporaryRoot 'wrong-updater-version'
	New-PublishFixture $wrongUpdaterVersionRoot
	[IO.File]::Copy(
		$resolvedCompilerPath,
		(Join-Path $wrongUpdaterVersionRoot 'Pact.Updater.exe'),
		$true)
	$wrongUpdaterVersion = Invoke-Build `
		-PublishDirectory $wrongUpdaterVersionRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'wrong-updater-version-output')
	Assert-FailsWith `
		-Result $wrongUpdaterVersion `
		-ExpectedText 'Pact.Updater.exe product version' `
		-Scenario 'Mismatched updater executable version'

	$versionMismatchRoot = Join-Path $resolvedTemporaryRoot 'version-mismatch'
	New-PublishFixture $versionMismatchRoot 'Microsoft.NETCore.App' '9.9.9'
	$versionMismatch = Invoke-Build `
		-PublishDirectory $versionMismatchRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'version-mismatch-output')
	Assert-FailsWith `
		-Result $versionMismatch `
		-ExpectedText 'SBOM product version' `
		-Scenario 'Mismatched product version'

	$tamperedCompilerPath = Join-Path $resolvedTemporaryRoot 'ISCC.exe'
	[IO.File]::Copy($resolvedCompilerPath, $tamperedCompilerPath)
	[IO.File]::AppendAllText(
		$tamperedCompilerPath,
		'tampered',
		[Text.Encoding]::ASCII)
	$validRoot = Join-Path $resolvedTemporaryRoot 'valid'
	New-PublishFixture $validRoot
	$tamperedCompiler = Invoke-Build `
		-PublishDirectory $validRoot `
		-OutputDirectory (Join-Path $resolvedTemporaryRoot 'tampered-output') `
		-RequestedCompilerPath $tamperedCompilerPath
	Assert-FailsWith `
		-Result $tamperedCompiler `
		-ExpectedText 'Inno Setup compiler SHA-256 mismatch' `
		-Scenario 'Tampered compiler'

	$outputRoot = Join-Path $resolvedTemporaryRoot 'valid-output'
	$valid = Invoke-Build -PublishDirectory $validRoot -OutputDirectory $outputRoot
	if ($valid.ExitCode -ne 0)
	{
		throw "Valid installer fixture failed.`n$($valid.Output)"
	}
	$setupPath = Join-Path $outputRoot "pact-mission-control-$declaredVersion-win-x64-setup.exe"
	if (-not [IO.File]::Exists($setupPath))
	{
		throw "Valid installer fixture did not produce $setupPath."
	}
	if ((Get-AuthenticodeSignature -LiteralPath $setupPath).Status -ne 'NotSigned')
	{
		throw 'Unsigned fixture unexpectedly produced a signed Setup executable.'
	}
	$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($setupPath)
	if ($versionInfo.ProductVersion.Trim() -ne $declaredVersion)
	{
		throw "Setup ProductVersion is '$($versionInfo.ProductVersion)', expected $declaredVersion."
	}

	Write-Output 'PASS: Pact installer rejects invalid inputs and compiles an unsigned fixture with the pinned toolchain.'
}
finally
{
	if ([IO.Directory]::Exists($resolvedTemporaryRoot))
	{
		[IO.Directory]::Delete($resolvedTemporaryRoot, $true)
	}
}
