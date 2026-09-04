[CmdletBinding()]
param(
	[string[]]$Path,
	[string]$RepositoryRoot,
	[switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-RepositoryRoot
{
	param([string]$RequestedRoot)

	if (-not [string]::IsNullOrWhiteSpace($RequestedRoot))
	{
		return [IO.Path]::GetFullPath($RequestedRoot)
	}

	$root = & git rev-parse --show-toplevel
	if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($root))
	{
		throw "Could not resolve the Git repository root."
	}

	return [IO.Path]::GetFullPath($root.Trim())
}

function Get-ExportIgnoreEvidence
{
	param(
		[Parameter(Mandatory)]
		[string]$RepositoryRoot,
		[Parameter(Mandatory)]
		[string[]]$Files
	)

	$startInfo = [Diagnostics.ProcessStartInfo]::new()
	$startInfo.FileName = "git"
	$startInfo.WorkingDirectory = $RepositoryRoot
	$startInfo.UseShellExecute = $false
	$startInfo.RedirectStandardInput = $true
	$startInfo.RedirectStandardOutput = $true
	$startInfo.RedirectStandardError = $true
	$startInfo.ArgumentList.Add("check-attr")
	$startInfo.ArgumentList.Add("--stdin")
	$startInfo.ArgumentList.Add("export-ignore")

	$process = [Diagnostics.Process]::new()
	$process.StartInfo = $startInfo
	try
	{
		if (-not $process.Start())
		{
			throw "Could not start git check-attr."
		}

		$outputTask = $process.StandardOutput.ReadToEndAsync()
		$errorTask = $process.StandardError.ReadToEndAsync()
		foreach ($path in $Files)
		{
			$process.StandardInput.Write($path)
			$process.StandardInput.Write("`n")
		}
		$process.StandardInput.Close()

		$process.WaitForExit()
		$output = $outputTask.GetAwaiter().GetResult()
		$errorOutput = $errorTask.GetAwaiter().GetResult()
		if ($process.ExitCode -ne 0)
		{
			throw "Could not read export-ignore attributes: $errorOutput"
		}

		return @(
			$output -split "`n" |
				ForEach-Object { $_.TrimEnd("`r") } |
				Where-Object { $_.Length -gt 0 }
		)
	}
	finally
	{
		$process.Dispose()
	}
}

function Get-PublicMarkdownFiles
{
	param(
		[Parameter(Mandatory)]
		[string]$RepositoryRoot,
		[string[]]$RequestedPaths
	)

	Push-Location $RepositoryRoot
	try
	{
		$files = @(
			& git ls-files --cached --others --exclude-standard -- "*.md" |
				ForEach-Object { $_.Replace("\", "/") }
		)
		if ($LASTEXITCODE -ne 0)
		{
			throw "Could not enumerate Markdown files."
		}

		$exportIgnored = [Collections.Generic.HashSet[string]]::new(
			[StringComparer]::OrdinalIgnoreCase)
		if ($files.Count -gt 0)
		{
			$attributeLines = @(Get-ExportIgnoreEvidence `
				-RepositoryRoot $RepositoryRoot `
				-Files $files)

			foreach ($line in $attributeLines)
			{
				if ($line -match "^(?<path>.*): export-ignore: set$")
				{
					$null = $exportIgnored.Add($Matches.path.Replace("\", "/"))
				}
			}
		}

		$selected = $files | Where-Object { -not $exportIgnored.Contains($_) }
		$filters = @(
			$RequestedPaths |
				Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
				ForEach-Object { $_ -split "," } |
				ForEach-Object { $_.Trim().TrimEnd("/", "\").Replace("\", "/") } |
				Where-Object { $_.Length -gt 0 }
		)
		if ($filters.Count -eq 0)
		{
			return @($selected)
		}

		return @(
			$selected | Where-Object {
				$candidate = $_
				$filters | Where-Object {
					$candidate.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
					$candidate.StartsWith(
						"$_/",
						[StringComparison]::OrdinalIgnoreCase)
				} | Select-Object -First 1
			}
		)
	}
	finally
	{
		Pop-Location
	}
}

function Get-MarkdownLinkTargets
{
	param(
		[Parameter(Mandatory)]
		[string]$FilePath
	)

	$content = [IO.File]::ReadAllText($FilePath)
	$visible = [Text.StringBuilder]::new()
	$fenceCharacter = $null
	$fenceLength = 0

	foreach ($line in [regex]::Split($content, "\r?\n"))
	{
		if ($null -eq $fenceCharacter)
		{
			if ($line -match '^\s*(?<fence>`{3,}|~{3,})')
			{
				$fenceCharacter = $Matches.fence[0]
				$fenceLength = $Matches.fence.Length
				continue
			}

			$null = $visible.AppendLine($line)
			continue
		}

		$closingFence = "^\s*" + [regex]::Escape(
			([string]$fenceCharacter) * $fenceLength) + [regex]::Escape(
				[string]$fenceCharacter) + "*\s*$"
		if ($line -match $closingFence)
		{
			$fenceCharacter = $null
			$fenceLength = 0
		}
	}

	$targets = [Collections.Generic.HashSet[string]]::new(
		[StringComparer]::Ordinal)
	$visibleText = $visible.ToString()
	$inlinePattern = "!?\[[^\]\r\n]*\]\(\s*(?<target><[^>\r\n]+>|[^)\r\n]+)\s*\)"
	foreach ($match in [regex]::Matches($visibleText, $inlinePattern))
	{
		$null = $targets.Add($match.Groups["target"].Value)
	}

	$definitionPattern =
		"(?m)^\s{0,3}\[[^\]\r\n]+\]:\s*(?<target><[^>\r\n]+>|\S+)"
	foreach ($match in [regex]::Matches($visibleText, $definitionPattern))
	{
		$null = $targets.Add($match.Groups["target"].Value)
	}

	return @($targets)
}

function Get-LinkPath
{
	param(
		[Parameter(Mandatory)]
		[string]$RawTarget
	)

	$target = $RawTarget.Trim()
	if ($target.StartsWith("<", [StringComparison]::Ordinal) -and
		$target.EndsWith(">", [StringComparison]::Ordinal))
	{
		return $target[1..($target.Length - 2)] -join ""
	}

	if ($target -match "^(?<path>\S+?)(?:\s+(?:`"[^`"]*`"|'[^']*'|\([^)]*\)))?$")
	{
		return $Matches.path
	}

	return ($target -split "\s+", 2)[0]
}

function Find-MissingMarkdownLinks
{
	param(
		[Parameter(Mandatory)]
		[string]$RepositoryRoot,
		[Parameter(Mandatory)]
		[AllowEmptyCollection()]
		[string[]]$Files
	)

	$root = [IO.Path]::GetFullPath($RepositoryRoot)
	$rootPrefix = $root.TrimEnd(
		[IO.Path]::DirectorySeparatorChar,
		[IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
	$missing = [Collections.Generic.List[object]]::new()

	foreach ($relativeFile in $Files)
	{
		$sourcePath = [IO.Path]::GetFullPath(
			[IO.Path]::Combine($root, $relativeFile.Replace(
				"/",
				[IO.Path]::DirectorySeparatorChar)))
		foreach ($rawTarget in Get-MarkdownLinkTargets -FilePath $sourcePath)
		{
			$linkPath = Get-LinkPath -RawTarget $rawTarget
			if ([string]::IsNullOrWhiteSpace($linkPath) -or
				$linkPath.StartsWith("#", [StringComparison]::Ordinal) -or
				$linkPath.StartsWith("//", [StringComparison]::Ordinal) -or
				$linkPath -match "^[A-Za-z][A-Za-z0-9+.-]*:")
			{
				continue
			}

			$separatorIndex = $linkPath.IndexOfAny([char[]]"?#")
			if ($separatorIndex -ge 0)
			{
				$linkPath = $linkPath.Substring(0, $separatorIndex)
			}
			if ([string]::IsNullOrWhiteSpace($linkPath))
			{
				continue
			}

			try
			{
				$decodedPath = [Uri]::UnescapeDataString($linkPath)
			}
			catch [UriFormatException]
			{
				$decodedPath = $linkPath
			}

			$localPath = $decodedPath.Replace(
				"/",
				[IO.Path]::DirectorySeparatorChar)
			if ($localPath.StartsWith(
				[IO.Path]::DirectorySeparatorChar,
				[StringComparison]::Ordinal))
			{
				$targetPath = [IO.Path]::GetFullPath(
					[IO.Path]::Combine($root, $localPath.TrimStart(
						[IO.Path]::DirectorySeparatorChar)))
			}
			else
			{
				$targetPath = [IO.Path]::GetFullPath(
					[IO.Path]::Combine(
						[IO.Path]::GetDirectoryName($sourcePath),
						$localPath))
			}

			$isInsideRepository =
				$targetPath.Equals($root, [StringComparison]::OrdinalIgnoreCase) -or
				$targetPath.StartsWith(
					$rootPrefix,
					[StringComparison]::OrdinalIgnoreCase)
			if (-not $isInsideRepository -or -not (Test-Path -LiteralPath $targetPath))
			{
				$missing.Add([pscustomobject]@{
						Source = $relativeFile.Replace("\", "/")
						Target = $rawTarget
					})
			}
		}
	}

	return @($missing)
}

function Test-MarkdownLinksFixture
{
	$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
		[IO.Path]::DirectorySeparatorChar)
	$fixtureRoot = [IO.Path]::Combine(
		$tempBase,
		"pact-markdown-links-" + [Guid]::NewGuid().ToString("N"))
	$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
	$tempPrefix = $tempBase + [IO.Path]::DirectorySeparatorChar
	if (-not $fixtureRoot.StartsWith(
		$tempPrefix,
		[StringComparison]::OrdinalIgnoreCase))
	{
		throw "Refusing to create a Markdown fixture outside the temporary root."
	}

	$null = [IO.Directory]::CreateDirectory($fixtureRoot)
	try
	{
		& git -C $fixtureRoot init --quiet
		if ($LASTEXITCODE -ne 0)
		{
			throw "Could not initialize the Markdown self-test repository."
		}

		[IO.File]::WriteAllText(
			[IO.Path]::Combine($fixtureRoot, "target.md"),
			"# Target")
		[IO.File]::WriteAllText(
			[IO.Path]::Combine($fixtureRoot, "target file.md"),
			"# Encoded target")
		[IO.File]::WriteAllText(
			[IO.Path]::Combine($fixtureRoot, "valid.md"),
			@"
[inline](target.md#section)
[encoded](target%20file.md?raw=1)
[reference][target]

[target]: target.md "Title"

~~~~text
[ignored](missing-inside-fence.md)
~~~~
"@)
		[IO.File]::WriteAllText(
			[IO.Path]::Combine($fixtureRoot, "broken.md"),
			"[broken](missing.md)")
		$privateRoot = [IO.Path]::Combine($fixtureRoot, "private")
		$null = [IO.Directory]::CreateDirectory($privateRoot)
		[IO.File]::WriteAllText(
			[IO.Path]::Combine($privateRoot, "history.md"),
			"[private broken link](missing-private.md)")
		[IO.File]::WriteAllText(
			[IO.Path]::Combine($fixtureRoot, ".gitattributes"),
			"/private/** export-ignore`n")

		$valid = @(Find-MissingMarkdownLinks `
			-RepositoryRoot $fixtureRoot `
			-Files @("valid.md"))
		if ($valid.Count -ne 0)
		{
			throw "The valid Markdown fixture produced missing-link results: $(
				$valid | ConvertTo-Json -Compress)"
		}

		$broken = @(Find-MissingMarkdownLinks `
			-RepositoryRoot $fixtureRoot `
			-Files @("broken.md"))
		if ($broken.Count -ne 1 -or $broken[0].Target -ne "missing.md")
		{
			throw "The broken Markdown fixture was not detected exactly once."
		}

		$commaSeparated = @(
			Get-PublicMarkdownFiles `
				-RepositoryRoot $fixtureRoot `
				-RequestedPaths @("valid.md,target.md"))
		if ((@($commaSeparated | Sort-Object) -join ",") -ne "target.md,valid.md")
		{
			throw "Comma-separated -Path values did not select both requested files."
		}

		$publicFiles = @(
			Get-PublicMarkdownFiles `
				-RepositoryRoot $fixtureRoot `
				-RequestedPaths @())
		if ($publicFiles -contains "private/history.md")
		{
			throw "Export-ignored Markdown was included in the public file set."
		}

		& pwsh -NoProfile -File $PSCommandPath `
			-RepositoryRoot $fixtureRoot `
			-Path "valid.md,target.md"
		if ($LASTEXITCODE -ne 0)
		{
			throw "The CLI rejected an explicit repository root."
		}

		return $true
	}
	finally
	{
		if ($fixtureRoot.StartsWith(
			$tempPrefix,
			[StringComparison]::OrdinalIgnoreCase) -and
			[IO.Directory]::Exists($fixtureRoot))
		{
			[IO.Directory]::Delete($fixtureRoot, $true)
		}
	}
}

if ($SelfTest)
{
	if (-not (Test-MarkdownLinksFixture))
	{
		throw "Markdown link self-test failed."
	}

	Write-Host "PASS: Markdown link validator self-test."
	return
}

$repositoryRoot = Get-RepositoryRoot -RequestedRoot $RepositoryRoot
$files = @(Get-PublicMarkdownFiles `
	-RepositoryRoot $repositoryRoot `
	-RequestedPaths @($Path))
$missingLinks = @(Find-MissingMarkdownLinks `
	-RepositoryRoot $repositoryRoot `
	-Files $files)

if ($missingLinks.Count -gt 0)
{
	foreach ($missing in $missingLinks)
	{
		[Console]::Error.WriteLine("$($missing.Source) -> $($missing.Target)")
	}

	exit 1
}

Write-Host "PASS: $($files.Count) Markdown files have valid relative links."
