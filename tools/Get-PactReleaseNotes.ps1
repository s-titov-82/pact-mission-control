[CmdletBinding()]
param(
	[Parameter(Mandatory)][string]$Version,
	[Parameter(Mandatory)][string]$ChangelogPath,
	[Parameter(Mandatory)][string]$OutputPath,
	[Parameter(Mandatory)][string]$RepositoryUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Release notes are the changelog entry the maintainer already wrote for this
# version. A generated commit list is not a substitute: it names branches and
# internal refactors rather than what changed for the person installing Setup.
$changelogFullPath = [IO.Path]::GetFullPath($ChangelogPath)
if (-not [IO.File]::Exists($changelogFullPath))
{
	throw "Changelog is missing: $changelogFullPath"
}

$lines = @([IO.File]::ReadAllLines($changelogFullPath))
$versionHeading = "## [$Version]"
$startIndex = -1
$endIndex = $lines.Count - 1
$previousVersion = $null
for ($index = 0; $index -lt $lines.Count; $index++)
{
	$line = $lines[$index]
	if ($startIndex -lt 0)
	{
		if ($line.StartsWith($versionHeading, [StringComparison]::Ordinal))
		{
			$startIndex = $index + 1
		}
		continue
	}

	if ($line -match '^## \[(?<version>[0-9]+\.[0-9]+\.[0-9]+)\]')
	{
		$previousVersion = $Matches.version
		$endIndex = $index - 1
		break
	}
}

if ($startIndex -lt 0)
{
	throw "CHANGELOG.md has no '## [$Version]' section; write the release notes before tagging."
}

$body = if ($endIndex -ge $startIndex)
{
	[string]::Join("`n", $lines[$startIndex..$endIndex]).Trim()
}
else
{
	''
}
if ([string]::IsNullOrWhiteSpace($body))
{
	throw "The '## [$Version]' changelog section is empty; write the release notes before tagging."
}

$trimmedRepositoryUrl = $RepositoryUrl.TrimEnd('/')
$sections = [Collections.Generic.List[string]]::new()
$sections.Add($body)
$sections.Add('---')
$sections.Add(
	"**Install.** Download ``pact-mission-control-$Version-win-x64-setup.exe`` below " +
	"and run it; the portable build is ``pact-mission-control-$Version-win-x64.zip``.")
$sections.Add(
	"**Verify.** ``SHA256SUMS.txt`` holds the checksums, and " +
	"[release verification]($trimmedRepositoryUrl/blob/v$Version/docs/release-verification.md) " +
	'covers the provenance and SBOM attestations.')
if ($null -ne $previousVersion)
{
	$sections.Add(
		"**Full changelog.** [v$previousVersion...v$Version]" +
		"($trimmedRepositoryUrl/compare/v$previousVersion...v$Version)")
}

$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFullPath)
if (-not [string]::IsNullOrEmpty($outputDirectory) -and
	-not [IO.Directory]::Exists($outputDirectory))
{
	$null = [IO.Directory]::CreateDirectory($outputDirectory)
}
[IO.File]::WriteAllText(
	$outputFullPath,
	([string]::Join("`n`n", $sections) + "`n"),
	[Text.UTF8Encoding]::new($false))

Write-Output "PASS: release notes for $Version written from the changelog entry."
