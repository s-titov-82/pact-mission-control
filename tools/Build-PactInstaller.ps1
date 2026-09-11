[CmdletBinding()]
param(
	[Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$')][string]$Version,
	[Parameter(Mandatory)][string]$PublishDirectory,
	[Parameter(Mandatory)][string]$OutputDirectory,
	[Parameter(Mandatory)][string]$CompilerPath,
	[string]$DependencyCacheDirectory,
	[ValidateSet('Signed', 'Unsigned')][string]$AuthenticodeStatus = 'Unsigned',
	[string]$SignToolCommand
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

trap
{
	[Console]::Out.WriteLine($_.Exception.Message)
	exit 1
}

function Resolve-FullPath
{
	param([Parameter(Mandatory)][string]$Path)
	return [IO.Path]::GetFullPath($Path)
}

function Assert-FileHash
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$ExpectedSha256,
		[Parameter(Mandatory)][string]$Description
	)

	$actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
	if ($actual -ne $ExpectedSha256.ToLowerInvariant())
	{
		throw "$Description SHA-256 mismatch. Expected $ExpectedSha256; actual $actual."
	}
}

function Assert-TrustedSignature
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$Publisher,
		[Parameter(Mandatory)][string]$Description
	)

	$signature = Get-AuthenticodeSignature -LiteralPath $Path
	if ($signature.Status -ne 'Valid')
	{
		throw "$Description Authenticode signature is $($signature.Status), not Valid."
	}
	if (-not $signature.SignerCertificate.Subject.Contains(
		$Publisher,
		[StringComparison]::OrdinalIgnoreCase))
	{
		throw "$Description signer '$($signature.SignerCertificate.Subject)' does not contain '$Publisher'."
	}
}

function Assert-X64ExecutableIdentity
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string]$ExpectedVersion,
		[Parameter(Mandatory)][string]$FileName
	)

	try
	{
		$stream = [IO.File]::OpenRead($Path)
		$reader = [IO.BinaryReader]::new($stream)
		try
		{
			if ($stream.Length -lt 70 -or $reader.ReadUInt16() -ne 0x5A4D)
			{
				throw 'DOS header is missing.'
			}
			$stream.Position = 0x3C
			$peOffset = $reader.ReadInt32()
			if ($peOffset -lt 0 -or $peOffset -gt ($stream.Length - 6))
			{
				throw 'PE header offset is outside the file.'
			}
			$stream.Position = $peOffset
			if ($reader.ReadUInt32() -ne 0x00004550)
			{
				throw 'PE signature is missing.'
			}
			$machine = $reader.ReadUInt16()
			if ($machine -ne 0x8664)
			{
				throw ('PE machine type is 0x{0:X4}, expected AMD64 (0x8664).' -f $machine)
			}
		}
		finally
		{
			$reader.Dispose()
			$stream.Dispose()
		}
	}
	catch
	{
		throw "$FileName is not a valid x64 PE application: $($_.Exception.Message)"
	}

	$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
	$productVersion = [string]$versionInfo.ProductVersion
	$semanticVersion = ($productVersion -split '\+', 2)[0]
	if ($semanticVersion -ne $ExpectedVersion)
	{
		throw "$FileName product version '$productVersion' does not match requested installer version '$ExpectedVersion'."
	}
}

function Get-OrDownloadLockedFile
{
	param(
		[Parameter(Mandatory)][pscustomobject]$Dependency,
		[Parameter(Mandatory)][string]$CacheDirectory
	)

	$path = Join-Path $CacheDirectory $Dependency.bootstrapperFileName
	if (-not [IO.File]::Exists($path))
	{
		$null = [IO.Directory]::CreateDirectory($CacheDirectory)
		$temporaryPath = "$path.download"
		try
		{
			Invoke-WebRequest -Uri $Dependency.bootstrapperUrl -OutFile $temporaryPath
			Assert-FileHash $temporaryPath $Dependency.bootstrapperSha256 'WebView2 bootstrapper'
			Assert-TrustedSignature $temporaryPath $Dependency.publisher 'WebView2 bootstrapper'
			Move-Item -LiteralPath $temporaryPath -Destination $path
		}
		finally
		{
			if ([IO.File]::Exists($temporaryPath))
			{
				Remove-Item -LiteralPath $temporaryPath -Force
			}
		}
	}

	Assert-FileHash $path $Dependency.bootstrapperSha256 'WebView2 bootstrapper'
	if ((Get-Item -LiteralPath $path).Length -ne $Dependency.bootstrapperBytes)
	{
		throw 'WebView2 bootstrapper size does not match installer/dependencies.lock.json.'
	}
	Assert-TrustedSignature $path $Dependency.publisher 'WebView2 bootstrapper'
	return $path
}

function Get-TreeFingerprint
{
	param([Parameter(Mandatory)][string]$Root)

	$lines = Get-ChildItem -LiteralPath $Root -Recurse -File |
		ForEach-Object {
			$relativePath = [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/')
			$hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
			"$relativePath`t$hash"
		} |
		Sort-Object -CaseSensitive
	$bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $lines))
	return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
}

$repositoryRoot = Resolve-FullPath (Join-Path $PSScriptRoot '..')
$publishRoot = Resolve-FullPath $PublishDirectory
$outputRoot = Resolve-FullPath $OutputDirectory
$compiler = Resolve-FullPath $CompilerPath
if ([string]::IsNullOrWhiteSpace($DependencyCacheDirectory))
{
	$DependencyCacheDirectory = Join-Path $repositoryRoot 'artifacts/installer-dependencies'
}
$cacheRoot = Resolve-FullPath $DependencyCacheDirectory
$lockPath = Join-Path $repositoryRoot 'installer/dependencies.lock.json'
$scriptPath = Join-Path $repositoryRoot 'installer/Pact.iss'

if ($AuthenticodeStatus -eq 'Signed' -and [string]::IsNullOrWhiteSpace($SignToolCommand))
{
	throw 'SignToolCommand is required when AuthenticodeStatus is Signed.'
}
if ($AuthenticodeStatus -eq 'Unsigned' -and -not [string]::IsNullOrWhiteSpace($SignToolCommand))
{
	throw 'SignToolCommand must not be supplied when AuthenticodeStatus is Unsigned.'
}

if (-not [IO.File]::Exists((Join-Path $publishRoot 'Pact.App.Avalonia.exe')))
{
	throw "Pact.App.Avalonia.exe is missing from publish directory '$publishRoot'."
}
$applicationPath = Join-Path $publishRoot 'Pact.App.Avalonia.exe'
Assert-X64ExecutableIdentity `
	-Path $applicationPath `
	-ExpectedVersion $Version `
	-FileName 'Pact.App.Avalonia.exe'
$updaters = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File -Filter 'Pact.Updater.exe')
if ($updaters.Count -eq 0)
{
	throw "Pact.Updater.exe is missing from publish directory '$publishRoot'."
}
if ($updaters.Count -ne 1 -or
	([IO.Path]::GetRelativePath($publishRoot, $updaters[0].FullName)) -cne 'Pact.Updater.exe')
{
	throw "Publish directory must contain exactly one Pact.Updater.exe at its root; found $($updaters.Count)."
}
Assert-X64ExecutableIdentity `
	-Path $updaters[0].FullName `
	-ExpectedVersion $Version `
	-FileName 'Pact.Updater.exe'
foreach ($requiredRelativePath in @(
	'LICENSE',
	'_manifest/spdx_2.2/manifest.spdx.json',
	'Pact.App.Avalonia.deps.json',
	'Pact.App.Avalonia.runtimeconfig.json'))
{
	if (-not [IO.File]::Exists((Join-Path $publishRoot $requiredRelativePath)))
	{
		throw "$requiredRelativePath is missing from publish directory '$publishRoot'."
	}
}
if (-not [IO.File]::Exists($compiler))
{
	throw "Inno Setup compiler is missing: $compiler"
}

$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1)
{
	throw "Unsupported installer dependency lock schema '$($lock.schemaVersion)'."
}
$webView2MinimumVersion = $null
if (-not [version]::TryParse(
		[string]$lock.webView2.minimumVersion,
		[ref]$webView2MinimumVersion) -or
	$webView2MinimumVersion -lt [version]'86.0.616.0')
{
	throw 'WebView2 minimumVersion must be a valid supported four-part Runtime version.'
}

$runtimeConfigPath = Join-Path $publishRoot 'Pact.App.Avalonia.runtimeconfig.json'
$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw | ConvertFrom-Json
$framework = $runtimeConfig.runtimeOptions.framework
if ($null -eq $framework -or
	$framework.name -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App'))
{
	$name = if ($null -eq $framework) { '<missing>' } else { $framework.name }
	throw "Unsupported runtime framework '$name' in Pact.App.Avalonia.runtimeconfig.json."
}
if ($framework.name -ne $lock.dotnet.framework -or $framework.version -ne $lock.dotnet.minimumVersion)
{
	throw "Runtime config requires $($framework.name) $($framework.version), but installer/dependencies.lock.json declares $($lock.dotnet.framework) $($lock.dotnet.minimumVersion)."
}

$dependencyContext = Get-Content -LiteralPath (
	Join-Path $publishRoot 'Pact.App.Avalonia.deps.json') -Raw | ConvertFrom-Json
if (-not ([string]$dependencyContext.runtimeTarget.name).EndsWith(
		'/win-x64',
		[StringComparison]::Ordinal))
{
	throw "Unsupported publish runtime target '$($dependencyContext.runtimeTarget.name)'; expected win-x64."
}
$sbom = Get-Content -LiteralPath (
	Join-Path $publishRoot '_manifest/spdx_2.2/manifest.spdx.json') -Raw |
	ConvertFrom-Json
$productPackages = @($sbom.packages | Where-Object {
		[string]$_.name -eq 'PACT:> Mission Control'
	})
if ($productPackages.Count -ne 1 -or [string]$productPackages[0].versionInfo -ne $Version)
{
	$actualVersion = if ($productPackages.Count -eq 1) {
		[string]$productPackages[0].versionInfo
	} else {
		'<missing or ambiguous>'
	}
	throw "SBOM product version '$actualVersion' does not match requested installer version '$Version'."
}

Assert-FileHash $compiler $lock.innoSetup.compilerSha256 'Inno Setup compiler'
$compilerVersion = (& $compiler --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -ne $lock.innoSetup.version)
{
	throw "Inno Setup compiler version mismatch. Expected $($lock.innoSetup.version); actual '$compilerVersion'."
}

$webView2Bootstrapper = Get-OrDownloadLockedFile $lock.webView2 $cacheRoot
$publishFingerprint = Get-TreeFingerprint $publishRoot
$null = [IO.Directory]::CreateDirectory($outputRoot)
$outputBaseName = "pact-mission-control-$Version-win-x64-setup"
$expectedOutput = Join-Path $outputRoot "$outputBaseName.exe"
if ([IO.File]::Exists($expectedOutput))
{
	Remove-Item -LiteralPath $expectedOutput -Force
}

$arguments = [Collections.Generic.List[string]]::new()
$arguments.Add($scriptPath)
$arguments.Add("--output-dir=$outputRoot")
$arguments.Add("--output-filename=$outputBaseName")
$arguments.Add('--no-ide-signtools')
$arguments.Add("--define=PactVersion=$Version")
$arguments.Add("--define=PactPayloadRoot=$publishRoot")
$arguments.Add("--define=PactOutputDirectory=$outputRoot")
$arguments.Add("--define=PactOutputBaseFilename=$outputBaseName")
$arguments.Add("--define=PactWebView2BootstrapperPath=$webView2Bootstrapper")
$arguments.Add("--define=PactWebView2BootstrapperFileName=$($lock.webView2.bootstrapperFileName)")
$arguments.Add("--define=PactWebView2BootstrapperSha256=$($lock.webView2.bootstrapperSha256)")
$arguments.Add("--define=PactWebView2MinimumVersion=$($lock.webView2.minimumVersion)")
$arguments.Add("--define=PactDotNetFramework=$($lock.dotnet.framework)")
$arguments.Add("--define=PactDotNetMinimumVersion=$($lock.dotnet.minimumVersion)")
$arguments.Add("--define=PactDotNetMajorVersion=$(([version]$lock.dotnet.minimumVersion).Major)")
$arguments.Add("--define=PactDotNetInstallerFileName=$($lock.dotnet.installerFileName)")
$arguments.Add("--define=PactDotNetInstallerUrl=$($lock.dotnet.installerUrl)")
$arguments.Add("--define=PactDotNetInstallerSha256=$($lock.dotnet.installerSha256)")
$arguments.Add("--define=PactIconPath=$(Join-Path $repositoryRoot 'src/Pact.App.Avalonia/Assets/AppIcon.ico')")
$arguments.Add("--define=PactLicensePath=$(Join-Path $publishRoot 'LICENSE')")

if ($AuthenticodeStatus -eq 'Signed')
{
	$arguments.Add('--define=PactSigned=1')
	$arguments.Add("--signtool=pact=$SignToolCommand")
}
else
{
	$arguments.Add('--define=PactSigned=0')
	$arguments.Add('--no-signing')
}

& $compiler @arguments
if ($LASTEXITCODE -ne 0)
{
	throw "Inno Setup compiler failed with exit code $LASTEXITCODE."
}
if (-not [IO.File]::Exists($expectedOutput))
{
	throw "Inno Setup did not produce expected output '$expectedOutput'."
}
if ((Get-TreeFingerprint $publishRoot) -ne $publishFingerprint)
{
	throw 'Installer compilation changed the prepared publish tree.'
}

$setupSignature = Get-AuthenticodeSignature -LiteralPath $expectedOutput
if ($AuthenticodeStatus -eq 'Signed' -and $setupSignature.Status -ne 'Valid')
{
	throw "Setup Authenticode signature is $($setupSignature.Status), not Valid."
}
if ($AuthenticodeStatus -eq 'Unsigned' -and $setupSignature.Status -ne 'NotSigned')
{
	throw "Unsigned Setup has unexpected Authenticode status $($setupSignature.Status)."
}

Write-Output "PASS: created $expectedOutput"
