[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$Version,
	[Parameter(Mandatory)][string]$SetupPath,
	[Parameter(Mandatory)][string]$ZipPath,
	[Parameter(Mandatory)][string]$WorkingDirectory,
	[Parameter(Mandatory)][ValidateSet('Signed', 'Unsigned')][string]$ExpectedAuthenticodeStatus
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:CI -ne 'true')
{
	throw 'Installer install/uninstall smoke is restricted to a disposable CI runner.'
}

$setup = [IO.Path]::GetFullPath($SetupPath)
$zip = [IO.Path]::GetFullPath($ZipPath)
$workRoot = [IO.Path]::GetFullPath($WorkingDirectory)
$runnerTemp = [IO.Path]::GetFullPath($env:RUNNER_TEMP)
$runnerPrefix = $runnerTemp.TrimEnd(
	[IO.Path]::DirectorySeparatorChar,
	[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $workRoot.StartsWith($runnerPrefix, [StringComparison]::OrdinalIgnoreCase))
{
	throw "WorkingDirectory must resolve below RUNNER_TEMP: $workRoot"
}

$installRootA = Join-Path $workRoot 'install-a'
$installRootB = Join-Path $workRoot 'install-b with spaces'
$expectedRoot = Join-Path $workRoot 'expected'
$setupLog = Join-Path $workRoot 'setup-a.log'
$rememberedUpgradeLog = Join-Path $workRoot 'upgrade-remembered-a.log'
$explicitUpgradeLog = Join-Path $workRoot 'upgrade-explicit-b.log'
$uninstallLog = Join-Path $workRoot 'uninstall.log'
$sentinelPath = Join-Path $installRootA 'user-sentinel.txt'
$profileRoot = Join-Path $env:APPDATA 'Pact'
$profileSentinelPath = Join-Path $profileRoot ('.installer-smoke-{0}.sentinel' -f [Guid]::NewGuid().ToString('N'))
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PactMissionControl_is1'
$defaultInstallRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs\Pact Mission Control'))
$uninstallerPath = $null

function Invoke-CheckedExecutable
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string[]]$Arguments,
		[Parameter(Mandatory)][string]$Description
	)

	$startInfo = [Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = $Path
	$startInfo.UseShellExecute = $false
	foreach ($argument in $Arguments)
	{
		$startInfo.ArgumentList.Add($argument)
	}
	$process = [Diagnostics.Process]::Start($startInfo)
	if ($null -eq $process)
	{
		throw "$Description did not start."
	}
	$process.WaitForExit()
	if ($process.ExitCode -ne 0)
	{
		$logArgument = $Arguments |
			Where-Object { $_.StartsWith('/LOG=', [StringComparison]::OrdinalIgnoreCase) } |
			Select-Object -First 1
		if ($null -ne $logArgument)
		{
			$logPath = $logArgument.Substring('/LOG='.Length).Trim('"')
			if ([IO.File]::Exists($logPath))
			{
				$logTail = Get-Content -LiteralPath $logPath -Tail 200 | Out-String
				Write-Output "$Description log tail:`n$logTail"
			}
		}
		throw "$Description failed with exit code $($process.ExitCode)."
	}
}

function Assert-InstalledPayload
{
	param([Parameter(Mandatory)][string]$InstallRoot)

	foreach ($expectedFile in Get-ChildItem -LiteralPath $expectedRoot -Recurse -File)
	{
		$relativePath = [IO.Path]::GetRelativePath($expectedRoot, $expectedFile.FullName)
		$installedPath = Join-Path $InstallRoot $relativePath
		if (-not [IO.File]::Exists($installedPath))
		{
			throw "Installed payload is missing: $relativePath"
		}
		$expectedHash = (Get-FileHash -LiteralPath $expectedFile.FullName -Algorithm SHA256).Hash
		$actualHash = (Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash
		if ($actualHash -ne $expectedHash)
		{
			throw "Installed payload hash mismatch in '$InstallRoot': $relativePath (expected $expectedHash; actual $actualHash)."
		}
	}

	$expectedPaths = @(
		Get-ChildItem -LiteralPath $expectedRoot -Recurse -File |
			ForEach-Object { [IO.Path]::GetRelativePath($expectedRoot, $_.FullName) })
	foreach ($installedFile in Get-ChildItem -LiteralPath $InstallRoot -Recurse -File)
	{
		$relativePath = [IO.Path]::GetRelativePath($InstallRoot, $installedFile.FullName)
		$isExpected = $expectedPaths -contains $relativePath
		$isInstallerOwned = $relativePath -match '^unins[0-9]{3}\.(exe|dat|msg)$'
		$isSentinel = $installedFile.FullName -eq $sentinelPath
		if (-not $isExpected -and -not $isInstallerOwned -and -not $isSentinel)
		{
			throw "Installation contains an unexpected file: $relativePath"
		}
	}
}

function Assert-RegisteredInstallLocation
{
	param([Parameter(Mandatory)][string]$ExpectedRoot)

	$registration = Get-ItemProperty -LiteralPath $uninstallKey
	if ([string]$registration.DisplayVersion -ne $Version)
	{
		throw "Installed Apps registration has version '$($registration.DisplayVersion)', expected '$Version'."
	}
	$actualRoot = [IO.Path]::GetFullPath(([string]$registration.InstallLocation).TrimEnd('\'))
	if ($actualRoot -cne [IO.Path]::GetFullPath($ExpectedRoot))
	{
		throw "Installed Apps registration points to '$actualRoot', expected '$ExpectedRoot'."
	}
}

if ([IO.Directory]::Exists($workRoot))
{
	[IO.Directory]::Delete($workRoot, $true)
}
$defaultRootExisted = [IO.Directory]::Exists($defaultInstallRoot)
if ($defaultRootExisted -or (Test-Path -LiteralPath $uninstallKey))
{
	throw 'Installer smoke requires a disposable user profile with no existing Pact installation.'
}
$null = [IO.Directory]::CreateDirectory($workRoot)
try
{
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $expectedRoot)
	Invoke-CheckedExecutable `
		-Path $setup `
		-Description 'Initial custom-directory Setup' `
		-Arguments @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/NOICONS',
			"/DIR=$installRootA",
			"/LOG=$setupLog")

	Assert-InstalledPayload -InstallRoot $installRootA
	Assert-RegisteredInstallLocation -ExpectedRoot $installRootA
	$uninstallerPath = Join-Path $installRootA 'unins000.exe'
	if (-not [IO.File]::Exists($uninstallerPath))
	{
		throw 'Installed uninstaller is missing.'
	}
	$expectedSignature = if ($ExpectedAuthenticodeStatus -eq 'Signed') { 'Valid' } else { 'NotSigned' }
	$actualSignature = [string](Get-AuthenticodeSignature -LiteralPath $uninstallerPath).Status
	if ($actualSignature -ne $expectedSignature)
	{
		throw "Uninstaller Authenticode status is $actualSignature, expected $expectedSignature."
	}

	$rememberedProbe = Join-Path $installRootA 'Pact.App.Avalonia.runtimeconfig.json'
	[IO.File]::WriteAllText($rememberedProbe, 'stale-a')
	Invoke-CheckedExecutable `
		-Path $setup `
		-Description 'Upgrade using remembered custom directory' `
		-Arguments @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/NOICONS',
			"/LOG=$rememberedUpgradeLog")
	Assert-InstalledPayload -InstallRoot $installRootA
	Assert-RegisteredInstallLocation -ExpectedRoot $installRootA
	if ([IO.Directory]::Exists($defaultInstallRoot))
	{
		throw "Upgrade without /DIR created the hard-coded default directory: $defaultInstallRoot"
	}

	[IO.File]::WriteAllText($sentinelPath, 'preserve')
	$null = [IO.Directory]::CreateDirectory($profileRoot)
	[IO.File]::WriteAllText($profileSentinelPath, 'preserve')
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $installRootB)
	$explicitProbe = Join-Path $installRootB 'Pact.App.Avalonia.runtimeconfig.json'
	[IO.File]::WriteAllText($explicitProbe, 'stale-b')
	[IO.File]::WriteAllText($rememberedProbe, 'leave-a-untouched')
	Invoke-CheckedExecutable `
		-Path $setup `
		-Description 'Upgrade using explicit running directory' `
		-Arguments @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/NOICONS',
			"/DIR=$installRootB",
			"/LOG=$explicitUpgradeLog")
	Assert-InstalledPayload -InstallRoot $installRootB
	Assert-RegisteredInstallLocation -ExpectedRoot $installRootB
	if ([IO.File]::ReadAllText($rememberedProbe) -cne 'leave-a-untouched')
	{
		throw 'Explicit /DIR upgrade modified the remembered installation instead of the running directory.'
	}
	if (-not [IO.File]::Exists($sentinelPath) -or
		-not [IO.File]::Exists($profileSentinelPath))
	{
		throw 'Upgrade removed a user-owned sentinel file.'
	}
	$uninstallerPath = Join-Path $installRootB 'unins000.exe'
	$actualSignature = [string](Get-AuthenticodeSignature -LiteralPath $uninstallerPath).Status
	if ($actualSignature -ne $expectedSignature)
	{
		throw "Reinstalled uninstaller Authenticode status is $actualSignature, expected $expectedSignature."
	}
	Invoke-CheckedExecutable `
		-Path $uninstallerPath `
		-Description 'Uninstaller' `
		-Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog")

	foreach ($expectedFile in Get-ChildItem -LiteralPath $expectedRoot -Recurse -File)
	{
		$relativePath = [IO.Path]::GetRelativePath($expectedRoot, $expectedFile.FullName)
		if ([IO.File]::Exists((Join-Path $installRootB $relativePath)))
		{
			throw "Uninstall left a registered payload file: $relativePath"
		}
	}
	if (-not [IO.File]::Exists($sentinelPath) -or
		-not [IO.File]::Exists($profileSentinelPath))
	{
		throw 'Uninstall removed a user-owned sentinel file.'
	}
	if (Test-Path -LiteralPath $uninstallKey)
	{
		throw 'Uninstall left the Apps registration behind.'
	}

	Write-Output 'PASS: Setup preserved the remembered custom directory, explicit /DIR won, ZIP/updater bytes matched, and uninstall preserved user-owned files.'
}
finally
{
	if ($null -ne $uninstallerPath -and [IO.File]::Exists($uninstallerPath))
	{
		Start-Process `
			-FilePath $uninstallerPath `
			-ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
			-Wait |
			Out-Null
	}
	if ([IO.File]::Exists($profileSentinelPath))
	{
		[IO.File]::Delete($profileSentinelPath)
	}
	foreach ($cleanupRoot in @($installRootB, $installRootA))
	{
		if ([IO.Directory]::Exists($cleanupRoot))
		{
			[IO.Directory]::Delete($cleanupRoot, $true)
		}
	}
}
