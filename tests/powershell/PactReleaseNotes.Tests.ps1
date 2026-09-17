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
	$repositoryUrl = 'https://example.test/owner/repo'
	$changelogPath = Join-Path $root 'CHANGELOG.md'
	$notesPath = Join-Path $root 'notes/release-notes.md'
	[IO.File]::WriteAllText($changelogPath, @'
# Changelog

## [1.3.0] - 2026-01-02

### Fixed

- The restart action resumes instead of starting over.

## [1.2.0] - 2026-01-01

### Added

- An older entry that must not leak into the newer notes.
'@)

	& $ScriptPath -Version '1.3.0' -ChangelogPath $changelogPath -OutputPath $notesPath -RepositoryUrl $repositoryUrl |
		Out-Null
	$notes = [IO.File]::ReadAllText($notesPath)
	if (-not $notes.Contains('The restart action resumes instead of starting over.'))
	{
		throw 'Release notes must carry the changelog entry for the released version.'
	}
	if ($notes.Contains('An older entry'))
	{
		throw 'Release notes must stop at the previous version heading.'
	}
	if (-not $notes.Contains("$repositoryUrl/compare/v1.2.0...v1.3.0"))
	{
		throw 'Release notes must link the comparison against the previous released version.'
	}
	if (-not $notes.Contains('pact-mission-control-1.3.0-win-x64-setup.exe') -or
		-not $notes.Contains("$repositoryUrl/blob/v1.3.0/docs/release-verification.md"))
	{
		throw 'Release notes must tell the reader what to download and how to verify it.'
	}
	$notesBytes = [IO.File]::ReadAllBytes($notesPath)
	if ($notesBytes.Length -eq 0 -or $notesBytes[-1] -ne 0x0A -or $notesBytes.Contains([byte]0x0D))
	{
		throw 'Release notes must use LF lines and end with a final LF.'
	}

	try
	{
		& $ScriptPath -Version '9.9.9' -ChangelogPath $changelogPath -OutputPath $notesPath -RepositoryUrl $repositoryUrl |
			Out-Null
		throw 'A version missing from the changelog was accepted.'
	}
	catch
	{
		if (-not $_.Exception.Message.Contains('has no'))
		{
			throw
		}
	}

	$emptySectionPath = Join-Path $root 'EMPTY.md'
	[IO.File]::WriteAllText($emptySectionPath, @'
# Changelog

## [1.3.0] - 2026-01-02

## [1.2.0] - 2026-01-01

### Added

- An older entry.
'@)
	try
	{
		& $ScriptPath -Version '1.3.0' -ChangelogPath $emptySectionPath -OutputPath $notesPath -RepositoryUrl $repositoryUrl |
			Out-Null
		throw 'An empty changelog section was accepted.'
	}
	catch
	{
		if (-not $_.Exception.Message.Contains('is empty'))
		{
			throw
		}
	}

	Write-Output 'PASS: release notes come from the changelog entry and refuse a missing or empty section.'
}
finally
{
	if ([IO.Directory]::Exists($root))
	{
		[IO.Directory]::Delete($root, $true)
	}
}
