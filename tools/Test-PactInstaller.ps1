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

$installRoot = Join-Path $workRoot 'install'
$expectedRoot = Join-Path $workRoot 'expected'
$setupLog = Join-Path $workRoot 'setup.log'
$reinstallLog = Join-Path $workRoot 'reinstall.log'
$uninstallLog = Join-Path $workRoot 'uninstall.log'
$sentinelPath = Join-Path $installRoot 'user-sentinel.txt'
$profileRoot = Join-Path $env:APPDATA 'Pact'
$profileSentinelPath = Join-Path $profileRoot ('.installer-smoke-{0}.sentinel' -f [Guid]::NewGuid().ToString('N'))
$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PactMissionControl_is1'
$uninstallerPath = $null

function Invoke-CheckedExecutable
{
	param(
		[Parameter(Mandatory)][string]$Path,
		[Parameter(Mandatory)][string[]]$Arguments,
		[Parameter(Mandatory)][string]$Description
	)

	$process = Start-Process `
		-FilePath $Path `
		-ArgumentList $Arguments `
		-Wait `
		-PassThru
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
	foreach ($expectedFile in Get-ChildItem -LiteralPath $expectedRoot -Recurse -File)
	{
		$relativePath = [IO.Path]::GetRelativePath($expectedRoot, $expectedFile.FullName)
		$installedPath = Join-Path $installRoot $relativePath
		if (-not [IO.File]::Exists($installedPath))
		{
			throw "Installed payload is missing: $relativePath"
		}
		$expectedHash = (Get-FileHash -LiteralPath $expectedFile.FullName -Algorithm SHA256).Hash
		$actualHash = (Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash
		if ($actualHash -ne $expectedHash)
		{
			throw "Installed payload hash mismatch: $relativePath"
		}
	}

	$expectedPaths = @(
		Get-ChildItem -LiteralPath $expectedRoot -Recurse -File |
			ForEach-Object { [IO.Path]::GetRelativePath($expectedRoot, $_.FullName) })
	foreach ($installedFile in Get-ChildItem -LiteralPath $installRoot -Recurse -File)
	{
		$relativePath = [IO.Path]::GetRelativePath($installRoot, $installedFile.FullName)
		$isExpected = $expectedPaths -contains $relativePath
		$isInstallerOwned = $relativePath -match '^unins[0-9]{3}\.(exe|dat|msg)$'
		$isSentinel = $installedFile.FullName -eq $sentinelPath
		if (-not $isExpected -and -not $isInstallerOwned -and -not $isSentinel)
		{
			throw "Installation contains an unexpected file: $relativePath"
		}
	}
}

if ([IO.Directory]::Exists($workRoot))
{
	[IO.Directory]::Delete($workRoot, $true)
}
$null = [IO.Directory]::CreateDirectory($workRoot)
try
{
	[IO.Compression.ZipFile]::ExtractToDirectory($zip, $expectedRoot)
	Invoke-CheckedExecutable `
		-Path $setup `
		-Description 'Setup' `
		-Arguments @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/NOICONS',
			"/DIR=$installRoot",
			"/LOG=$setupLog")

	Assert-InstalledPayload

	$registration = Get-ItemProperty -LiteralPath $uninstallKey
	if ([string]$registration.DisplayVersion -ne $Version)
	{
		throw "Installed Apps registration has version '$($registration.DisplayVersion)', expected '$Version'."
	}
	$uninstallerPath = Join-Path $installRoot 'unins000.exe'
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

	[IO.File]::WriteAllText($sentinelPath, 'preserve')
	$null = [IO.Directory]::CreateDirectory($profileRoot)
	[IO.File]::WriteAllText($profileSentinelPath, 'preserve')
	Invoke-CheckedExecutable `
		-Path $setup `
		-Description 'Same-version reinstall' `
		-Arguments @(
			'/VERYSILENT',
			'/SUPPRESSMSGBOXES',
			'/NORESTART',
			'/NOICONS',
			"/DIR=$installRoot",
			"/LOG=$reinstallLog")
	Assert-InstalledPayload
	if (-not [IO.File]::Exists($sentinelPath) -or
		-not [IO.File]::Exists($profileSentinelPath))
	{
		throw 'Same-version reinstall removed a user-owned sentinel file.'
	}
	$uninstallerPath = Join-Path $installRoot 'unins000.exe'
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
		if ([IO.File]::Exists((Join-Path $installRoot $relativePath)))
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

	Write-Output 'PASS: Setup installed and reinstalled the exact ZIP payload; uninstall preserved user-owned files.'
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
}
