[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$ScriptPath,
	[Parameter(Mandatory)][string]$TemporaryRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath($TemporaryRoot)
if ([IO.Directory]::Exists($root))
{
	[IO.Directory]::Delete($root, $true)
}
$null = [IO.Directory]::CreateDirectory($root)
try
{
	$version = '1.2.3'
	$names = @(
		"pact-mission-control-$version-win-x64.zip",
		"pact-mission-control-$version-win-x64-setup.exe",
		'manifest.spdx.json')
	foreach ($name in $names)
	{
		[IO.File]::WriteAllText((Join-Path $root $name), $name)
	}

	& $ScriptPath -Version $version -ReleaseDirectory $root | Out-Null
	$checksumLines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt'))
	if ($checksumLines.Count -ne 3)
	{
		throw "Expected three checksum entries, found $($checksumLines.Count)."
	}
	foreach ($name in $names)
	{
		$expectedHash = (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash
		if (-not ($checksumLines -contains "$($expectedHash.ToLowerInvariant()) *$name"))
		{
			throw "Checksum entry is missing or incorrect: $name"
		}
	}

	[IO.File]::Delete((Join-Path $root $names[1]))
	try
	{
		& $ScriptPath -Version $version -ReleaseDirectory $root | Out-Null
		throw 'Missing Setup artifact was accepted.'
	}
	catch
	{
		if (-not $_.Exception.Message.Contains('Release artifact is missing'))
		{
			throw
		}
	}

	Write-Output 'PASS: release composition requires ZIP, Setup, and SPDX artifacts and checksums all three.'
}
finally
{
	if ([IO.Directory]::Exists($root))
	{
		[IO.Directory]::Delete($root, $true)
	}
}
