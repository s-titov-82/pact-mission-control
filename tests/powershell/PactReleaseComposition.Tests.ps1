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
	$checksumPath = Join-Path $root 'SHA256SUMS.txt'
	$checksumBytes = [IO.File]::ReadAllBytes($checksumPath)
	if ($checksumBytes.Length -eq 0 -or $checksumBytes[-1] -ne 0x0A -or
		$checksumBytes.Contains([byte]0x0D))
	{
		throw 'Checksum manifest must use LF lines and end with a final LF.'
	}
	$checksumLines = @(Get-Content -LiteralPath $checksumPath)
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
	foreach ($line in $checksumLines)
	{
		if ($line -cnotmatch '^[0-9a-f]{64} \*[^/\\]+$')
		{
			throw "Checksum entry is not canonical: $line"
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
