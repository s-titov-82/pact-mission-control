[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$Version,
	[Parameter(Mandatory)][string]$ReleaseDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$releaseRoot = [IO.Path]::GetFullPath($ReleaseDirectory)
$artifactNames = @(
	"pact-mission-control-$Version-win-x64.zip",
	"pact-mission-control-$Version-win-x64-setup.exe",
	'manifest.spdx.json')
foreach ($artifactName in $artifactNames)
{
	if (-not [IO.File]::Exists((Join-Path $releaseRoot $artifactName)))
	{
		throw "Release artifact is missing: $artifactName"
	}
}

$lines = foreach ($artifactName in $artifactNames)
{
	$hash = (Get-FileHash -LiteralPath (
		Join-Path $releaseRoot $artifactName) -Algorithm SHA256).Hash.ToLowerInvariant()
	"$hash *$artifactName"
}
[IO.File]::WriteAllText(
	(Join-Path $releaseRoot 'SHA256SUMS.txt'),
	([string]::Join("`n", $lines) + "`n"),
	[Text.UTF8Encoding]::new($false))

Write-Output "PASS: finalized release checksums for $($artifactNames.Count) artifacts."
